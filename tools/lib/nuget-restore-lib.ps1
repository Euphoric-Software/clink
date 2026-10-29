# NuGet trust checks for the release builds (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7.
# nuget.config (repo root) makes nuget.org the only source, clears the fallback folders and requires signed packages. Those settings can
# be bypassed from outside the file, which a text check of nuget.config would not notice:
#   - `dotnet restore --configfile` / the RestoreConfigFile property replace the whole config hierarchy (the repo nuget.config is not read),
#   - the MSBuild properties RestoreSources, RestoreFallbackFolders and RestoreAdditionalProjectFallbackFolders and the environment variable
#     NUGET_FALLBACK_PACKAGES are not undone by <clear/>,
#   - a package folder that already holds packages (a runner image, a cache) is trusted without any signature check.
# So the build jobs (1) start from a fresh package folder (New-FreshNuGetFolders) and (2) after the restores read what NuGet itself
# recorded in every project.assets.json (Assert-NuGetRestoreTrusted): the one allowed source, no fallback folder, exactly the expected
# package folder, the repo nuget.config among the config files that were read, and every restored package's .nupkg.metadata naming nuget.org.

$script:NuGetOrgSource = 'https://api.nuget.org/v3/index.json'

function New-FreshNuGetFolders {
    # Creates <TempRoot>\nuget-<RunId>-<RunAttempt>\{packages, http-cache}. Refuses a folder that already exists (a fresh one or nothing).
    # The workflow writes NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH to GITHUB_ENV from the result.
    param([Parameter(Mandatory)] [string]$TempRoot, [Parameter(Mandatory)] [AllowEmptyString()] [string]$RunId, [Parameter(Mandatory)] [AllowEmptyString()] [string]$RunAttempt)
    if ($RunId -cnotmatch '\A[0-9]+\z' -or $RunAttempt -cnotmatch '\A[0-9]+\z') { throw "The run id '$RunId' or attempt '$RunAttempt' is not numeric." }
    if (-not (Test-Path -LiteralPath $TempRoot -PathType Container)) { throw "The temp folder $TempRoot does not exist." }
    $root = Join-Path $TempRoot "nuget-$RunId-$RunAttempt"
    if (Test-Path -LiteralPath $root) { throw "$root already exists; the NuGet folders of a run must start empty." }
    $packages = Join-Path $root 'packages'
    $http = Join-Path $root 'http-cache'
    [void](New-Item -ItemType Directory -Path $packages, $http)
    [pscustomobject]@{ Packages = $packages; HttpCache = $http }
}

function ConvertTo-NuGetFolderKey {
    # Windows paths compare ignoring case and a trailing separator.
    param([AllowNull()] [AllowEmptyString()] [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $Path.TrimEnd('\', '/').ToLowerInvariant() }
    $full.TrimEnd('\', '/').ToLowerInvariant()
}

function Get-NuGetPackageIdentities {
    # @{ Id; Version } of every package the restore used: the "package" libraries and the exact-pinned downloadDependencies (runtime packs).
    # Problems (an unpinned download dependency) go to the list.
    param([Parameter(Mandatory)] $Assets, [Parameter(Mandatory)] $Problems)
    $found = New-Object System.Collections.Generic.List[object]
    if ($null -ne $Assets.libraries) {
        foreach ($lib in $Assets.libraries.PSObject.Properties) {
            if ([string]$lib.Value.type -cne 'package') { continue }
            $slash = $lib.Name.IndexOf('/')
            if ($slash -lt 1) { $Problems.Add("library '$($lib.Name)' is not <id>/<version>"); continue }
            $found.Add([pscustomobject]@{ Id = $lib.Name.Substring(0, $slash); Version = $lib.Name.Substring($slash + 1) })
        }
    }
    if ($null -ne $Assets.project -and $null -ne $Assets.project.frameworks) {
        foreach ($fw in $Assets.project.frameworks.PSObject.Properties) {
            if ($null -eq $fw.Value.downloadDependencies) { continue }
            foreach ($dep in @($fw.Value.downloadDependencies)) {
                if ([string]$dep.version -cmatch '\A\[([^,\s\]]+),\s*([^,\s\]]+)\]\z' -and $Matches[1] -ceq $Matches[2]) {
                    $found.Add([pscustomobject]@{ Id = [string]$dep.name; Version = $Matches[1] })
                } else { $Problems.Add("downloadDependency '$($dep.name)' has the version '$($dep.version)', which is not an exact [x, x] pin") }
            }
        }
    }
    $found.ToArray()
}

function Get-NuGetRestoreProblems {
    # Pure and testable: the list of everything wrong with one parsed project.assets.json. Empty = trusted.
    #   - project.restore.sources is exactly the one nuget.org v3 index
    #   - no fallback folder
    #   - exactly one package folder (packageFolders and project.restore.packagesPath), equal to PackagesRoot
    #   - project.restore.configFilePaths names the repo nuget.config
    #   - every package that was used has a .nupkg.metadata in PackagesRoot whose source is nuget.org
    param(
        [Parameter(Mandatory)] $Assets, [Parameter(Mandatory)] [string]$PackagesRoot, [Parameter(Mandatory)] [string]$RepoNuGetConfig,
        [switch]$SkipPackageMetadata
    )
    $problems = New-Object System.Collections.Generic.List[string]
    $restore = if ($null -ne $Assets.project) { $Assets.project.restore } else { $null }
    if ($null -eq $restore) { $problems.Add('the assets file has no project.restore section'); return $problems.ToArray() }

    $sources = @(if ($null -ne $restore.sources) { $restore.sources.PSObject.Properties.Name })
    if ($sources.Count -ne 1 -or -not ($sources[0] -ceq $script:NuGetOrgSource)) {
        $problems.Add("project.restore.sources is '$($sources -join ', ')', expected exactly $script:NuGetOrgSource")
    }

    $fallback = $restore.PSObject.Properties['fallbackFolders']
    if ($null -ne $fallback -and $null -ne $fallback.Value) {
        $items = if ($fallback.Value -is [System.Array]) { @($fallback.Value) } elseif ($fallback.Value -is [string]) { @($fallback.Value) } else { @($fallback.Value.PSObject.Properties.Name) }
        if ($items.Count -gt 0) { $problems.Add("project.restore.fallbackFolders is not empty: $($items -join ', ')") }
    }

    $expectedFolder = ConvertTo-NuGetFolderKey $PackagesRoot
    $folders = @(if ($null -ne $Assets.packageFolders) { $Assets.packageFolders.PSObject.Properties.Name })
    if ($folders.Count -ne 1 -or (ConvertTo-NuGetFolderKey $folders[0]) -cne $expectedFolder) {
        $problems.Add("packageFolders is '$($folders -join ', ')', expected exactly the package folder $PackagesRoot")
    }
    if ((ConvertTo-NuGetFolderKey ([string]$restore.packagesPath)) -cne $expectedFolder) {
        $problems.Add("project.restore.packagesPath is '$($restore.packagesPath)', expected $PackagesRoot")
    }

    $configs = @(if ($null -ne $restore.configFilePaths) { @($restore.configFilePaths) })
    $repoKey = ConvertTo-NuGetFolderKey $RepoNuGetConfig
    if ($configs.Count -eq 0) { $problems.Add('project.restore.configFilePaths is empty (no config file was read)') }
    elseif (-not (@($configs | ForEach-Object { ConvertTo-NuGetFolderKey ([string]$_) }) -ccontains $repoKey)) {
        $problems.Add("project.restore.configFilePaths does not include the repo nuget.config ($RepoNuGetConfig); it lists: $($configs -join ', '). A restore with --configfile or RestoreConfigFile bypasses it.")
    }

    if (-not $SkipPackageMetadata) {
        $identities = @(Get-NuGetPackageIdentities -Assets $Assets -Problems $problems)
        if ($identities.Count -eq 0) { $problems.Add('the assets file lists no package at all') }
        foreach ($pkg in $identities) {
            $meta = Join-Path $PackagesRoot ($pkg.Id.ToLowerInvariant() + '\' + $pkg.Version.ToLowerInvariant() + '\.nupkg.metadata')
            if (-not (Test-Path -LiteralPath $meta -PathType Leaf)) { $problems.Add("$($pkg.Id) $($pkg.Version): no .nupkg.metadata in the package folder ($meta)"); continue }
            try { $source = [string]((Get-Content -Raw -LiteralPath $meta | ConvertFrom-Json).source) }
            catch { $problems.Add("$($pkg.Id) $($pkg.Version): .nupkg.metadata is not readable JSON"); continue }
            if (-not ($source -ceq $script:NuGetOrgSource)) { $problems.Add("$($pkg.Id) $($pkg.Version): .nupkg.metadata says the package came from '$source', not from nuget.org") }
        }
    }
    $problems.ToArray()
}

function Assert-NuGetRestoreTrusted {
    # Throws, listing every problem, unless every assets file passes Get-NuGetRestoreProblems. Run after the restores of the build job:
    # release.yml: obj_sc, obj_fx; release-dev.yml also obj (the plain build); build.yml's package dry run: obj_sc, obj_fx.
    # PackagesRoot defaults to NUGET_PACKAGES (set by the "Fresh NuGet folders" step), else %USERPROFILE%\.nuget\packages. RepoNuGetConfig
    # defaults to the nuget.config at the repo root (found by name, any case).
    param(
        [Parameter(Mandatory)] [string[]]$AssetsFile, [string]$PackagesRoot, [string]$RepoNuGetConfig
    )
    if (-not $PackagesRoot) { $PackagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' } }
    if (-not $RepoNuGetConfig) {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $hit = @(Get-ChildItem -LiteralPath $repoRoot -File | Where-Object { $_.Name.Equals('nuget.config', [System.StringComparison]::OrdinalIgnoreCase) })
        if ($hit.Count -ne 1) { throw "No nuget.config at the repo root ($repoRoot)." }
        $RepoNuGetConfig = $hit[0].FullName
    }
    $all = New-Object System.Collections.Generic.List[string]
    foreach ($file in $AssetsFile) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $all.Add("${file}: not found (was the project restored?)"); continue }
        try { $assets = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json } catch { $all.Add("${file}: not valid JSON"); continue }
        foreach ($p in @(Get-NuGetRestoreProblems -Assets $assets -PackagesRoot $PackagesRoot -RepoNuGetConfig $RepoNuGetConfig)) { $all.Add("${file}: $p") }
        Write-Host "checked the NuGet restore record $file"
    }
    if ($all.Count -gt 0) { throw "The NuGet restore is not trusted:`n  $($all -join "`n  ")" }
    Write-Host "NuGet restore trusted: nuget.org only, no fallback folder, package folder $PackagesRoot, $($AssetsFile.Count) assets file(s)"
}
