# Tests for tools/lib/nuget-restore-lib.ps1: the fresh package folders of the release build jobs and the post-restore assertion over every
# project.assets.json (nuget.org the only source, no fallback folder, exactly the expected package folder, the repo nuget.config among the
# config files that were read, each package's .nupkg.metadata naming nuget.org). Standalone: exits 0 when everything passed, 1 otherwise.
# Windows PowerShell 5.1 and pwsh 7, no modules, no network: the assets files and package folders are made in a scratch folder.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-nugettests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
. (Join-Path $toolsDir 'lib\nuget-restore-lib.ps1')
$script:passed = 0
$script:failed = 0
$nugetOrg = 'https://api.nuget.org/v3/index.json'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "assertion failed: $Message" }
}
function Assert-Throws {
    param([scriptblock]$Body, [string]$Pattern, [string]$What)
    $message = $null
    try { & $Body } catch { $message = $_.Exception.Message }
    Assert-True ($null -ne $message) "$What : did not throw"
    Assert-True ($message -match $Pattern) "$What : message '$message' does not match '$Pattern'"
}
function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    try { & $Body; $script:passed++; Write-Host "PASS  $Name" }
    catch { $script:failed++; Write-Host "FAIL  $Name : $($_.Exception.Message)" }
}

function New-PackageFolder {
    # A package folder holding the two packages the fake assets name, each with a .nupkg.metadata of the given source.
    param([string]$Name, [string]$Source = $nugetOrg)
    $root = Join-Path $scratch $Name
    foreach ($rel in 'foo.bar\1.2.3', 'microsoft.netcore.app.runtime.win-x64\10.0.14') {
        $dir = Join-Path $root $rel
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir '.nupkg.metadata'), ('{"version":2,"contentHash":"abc","source":"' + $Source + '"}'), $utf8)
    }
    $root
}
function New-Assets {
    # A parsed project.assets.json in the shape NuGet writes. Parameters override one part each.
    param(
        [string]$PackagesRoot, [string]$RepoConfig, [string[]]$Sources = @($nugetOrg), $Fallback = $null, [string[]]$Folders, [string[]]$Configs,
        [string]$PackagesPath, [string]$DownloadVersion = '[10.0.14, 10.0.14]', [switch]$NoPackages
    )
    if (-not $Folders) { $Folders = @($PackagesRoot.TrimEnd('\') + '\') }
    if (-not $Configs) { $Configs = @($RepoConfig, 'C:\Users\nobody\AppData\Roaming\NuGet\NuGet.Config') }
    if (-not $PackagesPath) { $PackagesPath = $PackagesRoot.TrimEnd('\') + '\' }
    $srcObj = [ordered]@{}; foreach ($s in $Sources) { $srcObj[$s] = @{} }
    $folderObj = [ordered]@{}; foreach ($f in $Folders) { $folderObj[$f] = @{} }
    $restore = [ordered]@{ projectName = 'Clink'; packagesPath = $PackagesPath; configFilePaths = @($Configs); sources = $srcObj }
    if ($null -ne $Fallback) { $restore['fallbackFolders'] = $Fallback }
    $libraries = [ordered]@{ 'Foo.Bar/1.2.3' = @{ type = 'package'; path = 'foo.bar/1.2.3' }; 'Other/1.0.0' = @{ type = 'project' } }
    $download = @(@{ name = 'Microsoft.NETCore.App.Runtime.win-x64'; version = $DownloadVersion })
    if ($NoPackages) { $libraries = [ordered]@{}; $download = @() }
    $doc = [ordered]@{
        version = 3; libraries = $libraries; packageFolders = $folderObj
        project = [ordered]@{ restore = $restore; frameworks = [ordered]@{ 'net10.0-windows' = [ordered]@{ downloadDependencies = $download } } }
    }
    ($doc | ConvertTo-Json -Depth 10) | ConvertFrom-Json
}

try {
    $repoConfig = Join-Path $scratch 'repo\nuget.config'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $repoConfig) | Out-Null
    [System.IO.File]::WriteAllText($repoConfig, '<configuration />', $utf8)
    $pkgRoot = New-PackageFolder 'packages'

    function Get-Problems {
        param($Assets, [string]$Root = $pkgRoot, [switch]$SkipMeta)
        , @(Get-NuGetRestoreProblems -Assets $Assets -PackagesRoot $Root -RepoNuGetConfig $repoConfig -SkipPackageMetadata:$SkipMeta)
    }

    Invoke-Test 'Get-NuGetRestoreProblems: a restore from nuget.org into the expected package folder with the repo nuget.config has no problem' {
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig)
        Assert-True ($p.Count -eq 0) "problems for a good restore: $($p -join ' | ')"
        # Trailing separators, case and the config file's case do not matter (Windows paths).
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig.ToUpperInvariant() -Folders @($pkgRoot.ToUpperInvariant()) -PackagesPath $pkgRoot)
        Assert-True ($p.Count -eq 0) "case or trailing backslash made a difference: $($p -join ' | ')"
        # An empty fallbackFolders (array or object) is fine.
        Assert-True ((Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Fallback @())).Count -eq 0) 'an empty fallbackFolders array was refused'
    }

    Invoke-Test 'Get-NuGetRestoreProblems: the source must be exactly nuget.org (another source, an extra one, none)' {
        $good = $pkgRoot
        $p = Get-Problems (New-Assets -PackagesRoot $good -RepoConfig $repoConfig -Sources @('https://evil.example/v3/index.json'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'project\.restore\.sources') "another source: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $good -RepoConfig $repoConfig -Sources @($nugetOrg, 'https://evil.example/v3/index.json'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'project\.restore\.sources') "an extra source: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $good -RepoConfig $repoConfig -Sources @('https://API.nuget.org/v3/index.json'))
        Assert-True ($p.Count -eq 1) "the nuget.org URL in another case was accepted: $($p -join ' | ')"
        $assets = New-Assets -PackagesRoot $good -RepoConfig $repoConfig
        $assets.project.restore.PSObject.Properties.Remove('sources')
        Assert-True ((Get-Problems $assets).Count -ge 1) 'a restore record without sources was accepted'
    }

    Invoke-Test 'Get-NuGetRestoreProblems: no fallback folder, in any shape' {
        foreach ($fallback in @('C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\'), @('C:\a\', 'C:\b\')) {
            $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Fallback $fallback)
            Assert-True ($p.Count -eq 1 -and $p[0] -match 'fallbackFolders is not empty') "fallback $($fallback -join ','): $($p -join ' | ')"
        }
        $asObject = New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Fallback @{ 'C:\fb\' = @{} }
        Assert-True ((Get-Problems $asObject).Count -eq 1) 'a fallbackFolders object (as NuGet may write it) was accepted'
    }

    Invoke-Test 'Get-NuGetRestoreProblems: exactly one package folder, and it is the expected one' {
        $other = Join-Path $scratch 'other-packages'
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Folders @($other + '\'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'packageFolders') "another package folder: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Folders @($pkgRoot + '\', $other + '\'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'packageFolders') "two package folders: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -PackagesPath ($other + '\'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'packagesPath') "another packagesPath: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig) -Root $other
        Assert-True ($p.Count -ge 2) "a folder other than the expected NUGET_PACKAGES was accepted: $($p -join ' | ')"
    }

    Invoke-Test 'Get-NuGetRestoreProblems: the repo nuget.config must be among the config files that were read (--configfile / RestoreConfigFile bypass it)' {
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Configs @('C:\Users\nobody\AppData\Roaming\NuGet\NuGet.Config'))
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'does not include the repo nuget\.config' -and $p[0] -match '--configfile') "a restore that did not read the repo config: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Configs @('D:\elsewhere\nuget.config'))
        Assert-True ($p.Count -eq 1) "another nuget.config stood in for the repo one: $($p -join ' | ')"
        $assets = New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig
        $assets.project.restore.PSObject.Properties.Remove('configFilePaths')
        $p = Get-Problems $assets
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'configFilePaths is empty') "no config file paths: $($p -join ' | ')"
    }

    Invoke-Test 'Get-NuGetRestoreProblems: every used package needs a .nupkg.metadata that says nuget.org (libraries and exact-pinned download dependencies)' {
        $badSource = New-PackageFolder 'packages-badsource' -Source 'https://evil.example/v3/index.json'
        $p = Get-Problems (New-Assets -PackagesRoot $badSource -RepoConfig $repoConfig) -Root $badSource
        Assert-True ($p.Count -eq 2 -and ($p -join ' ') -match 'Foo\.Bar 1\.2\.3' -and ($p -join ' ') -match 'Microsoft\.NETCore\.App\.Runtime\.win-x64 10\.0\.14' -and ($p -join ' ') -match 'not from nuget\.org') "a package from another source: $($p -join ' | ')"
        $missing = New-PackageFolder 'packages-missing'
        Remove-Item -LiteralPath (Join-Path $missing 'foo.bar\1.2.3\.nupkg.metadata')
        $p = Get-Problems (New-Assets -PackagesRoot $missing -RepoConfig $repoConfig) -Root $missing
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'Foo\.Bar 1\.2\.3: no \.nupkg\.metadata') "a package without metadata: $($p -join ' | ')"
        $broken = New-PackageFolder 'packages-broken'
        [System.IO.File]::WriteAllText((Join-Path $broken 'foo.bar\1.2.3\.nupkg.metadata'), 'not json', $utf8)
        $p = Get-Problems (New-Assets -PackagesRoot $broken -RepoConfig $repoConfig) -Root $broken
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'not readable JSON') "unreadable metadata: $($p -join ' | ')"
        $nosource = New-PackageFolder 'packages-nosource'
        [System.IO.File]::WriteAllText((Join-Path $nosource 'foo.bar\1.2.3\.nupkg.metadata'), '{"version":2}', $utf8)
        $p = Get-Problems (New-Assets -PackagesRoot $nosource -RepoConfig $repoConfig) -Root $nosource
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'not from nuget\.org') "metadata without a source: $($p -join ' | ')"
        # An unpinned download dependency cannot be located, so it is a problem; no package at all is one too.
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -DownloadVersion '10.0.14')
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'not an exact \[x, x\] pin') "an unpinned download dependency: $($p -join ' | ')"
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -NoPackages)
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'no package at all') "an assets file without packages: $($p -join ' | ')"
        # The metadata check can be skipped (the pure part alone).
        Assert-True ((Get-Problems (New-Assets -PackagesRoot $badSource -RepoConfig $repoConfig) -Root $badSource -SkipMeta).Count -eq 0) '-SkipPackageMetadata still checked the packages'
        # The id and the version are lower-cased for the folder name, as NuGet does.
        $p = Get-Problems (New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig)
        Assert-True ($p.Count -eq 0) "lower-case package folders were not found: $($p -join ' | ')"
    }

    Invoke-Test 'Get-NuGetRestoreProblems: an assets file without project.restore is a problem, not an exception' {
        $p = Get-Problems ([pscustomobject]@{ version = 3 })
        Assert-True ($p.Count -eq 1 -and $p[0] -match 'no project\.restore') "no project.restore: $($p -join ' | ')"
    }

    Invoke-Test 'Assert-NuGetRestoreTrusted: reads every assets file, lists every problem, needs every file to exist, defaults NUGET_PACKAGES and the repo nuget.config' {
        $dir = Join-Path $scratch 'obj'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $good1 = Join-Path $dir 'good1.json'; $good2 = Join-Path $dir 'good2.json'; $bad = Join-Path $dir 'bad.json'; $notJson = Join-Path $dir 'notjson.json'
        [System.IO.File]::WriteAllText($good1, ((New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig) | ConvertTo-Json -Depth 10), $utf8)
        [System.IO.File]::WriteAllText($good2, ((New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig) | ConvertTo-Json -Depth 10), $utf8)
        [System.IO.File]::WriteAllText($bad, ((New-Assets -PackagesRoot $pkgRoot -RepoConfig $repoConfig -Fallback @('C:\fb\') -Sources @('https://evil.example/')) | ConvertTo-Json -Depth 10), $utf8)
        [System.IO.File]::WriteAllText($notJson, 'nope', $utf8)
        Assert-NuGetRestoreTrusted -AssetsFile $good1, $good2 -PackagesRoot $pkgRoot -RepoNuGetConfig $repoConfig 6>$null
        $msg = $null
        try { Assert-NuGetRestoreTrusted -AssetsFile $good1, $bad, $notJson, (Join-Path $dir 'missing.json') -PackagesRoot $pkgRoot -RepoNuGetConfig $repoConfig 6>$null } catch { $msg = $_.Exception.Message }
        Assert-True ($null -ne $msg -and $msg -match 'not trusted' -and $msg -match 'bad\.json: project\.restore\.sources' -and $msg -match 'bad\.json: project\.restore\.fallbackFolders' -and $msg -match 'notjson\.json: not valid JSON' -and $msg -match 'missing\.json: not found') "the failure lists not everything: $msg"
        Assert-True ($msg -notmatch 'good1\.json:') 'a good file was reported'
        # NUGET_PACKAGES is the default package folder; the repo nuget.config is found at the repo root of this checkout.
        $realConfig = @(Get-ChildItem -LiteralPath $repo -File | Where-Object { $_.Name -ieq 'nuget.config' })
        Assert-True ($realConfig.Count -eq 1) 'this checkout has no nuget.config at its root'
        $viaDefaults = Join-Path $dir 'defaults.json'
        [System.IO.File]::WriteAllText($viaDefaults, ((New-Assets -PackagesRoot $pkgRoot -RepoConfig $realConfig[0].FullName) | ConvertTo-Json -Depth 10), $utf8)
        $saved = $env:NUGET_PACKAGES
        try {
            $env:NUGET_PACKAGES = $pkgRoot
            Assert-NuGetRestoreTrusted -AssetsFile $viaDefaults 6>$null
            $env:NUGET_PACKAGES = Join-Path $scratch 'some-other-folder'
            Assert-Throws { Assert-NuGetRestoreTrusted -AssetsFile $viaDefaults 6>$null } 'packageFolders' 'NUGET_PACKAGES pointing elsewhere'
        } finally { $env:NUGET_PACKAGES = $saved }
    }

    Invoke-Test 'New-FreshNuGetFolders: makes <temp>\nuget-<run>-<attempt>\{packages,http-cache}, refuses an existing folder, a bad id and a missing temp folder' {
        $tmp = Join-Path $scratch 'runner-temp'
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        $f = New-FreshNuGetFolders -TempRoot $tmp -RunId '12345' -RunAttempt '2'
        Assert-True ($f.Packages -ceq (Join-Path $tmp 'nuget-12345-2\packages') -and $f.HttpCache -ceq (Join-Path $tmp 'nuget-12345-2\http-cache')) "paths: $($f.Packages) $($f.HttpCache)"
        Assert-True ((Test-Path -LiteralPath $f.Packages -PathType Container) -and (Test-Path -LiteralPath $f.HttpCache -PathType Container)) 'the folders were not created'
        Assert-True (@(Get-ChildItem -LiteralPath $f.Packages -Force).Count -eq 0) 'the package folder is not empty'
        Assert-Throws { New-FreshNuGetFolders -TempRoot $tmp -RunId '12345' -RunAttempt '2' } 'already exists' 'a folder that exists (a rerun of the same attempt, a leftover)'
        $f2 = New-FreshNuGetFolders -TempRoot $tmp -RunId '12345' -RunAttempt '3'
        Assert-True ($f2.Packages -cne $f.Packages) 'two attempts share a folder'
        foreach ($bad in @('', 'abc', '1; rm', '..\x', '1 2', '-1')) {
            Assert-Throws { New-FreshNuGetFolders -TempRoot $tmp -RunId $bad -RunAttempt '1' } 'not numeric' "run id '$bad'"
            Assert-Throws { New-FreshNuGetFolders -TempRoot $tmp -RunId '1' -RunAttempt $bad } 'not numeric' "attempt '$bad'"
        }
        Assert-Throws { New-FreshNuGetFolders -TempRoot (Join-Path $scratch 'no-such-temp') -RunId '1' -RunAttempt '1' } 'does not exist' 'a missing temp folder'
    }

    Invoke-Test 'the lib is BOM-free ASCII' {
        foreach ($p in (Join-Path $toolsDir 'lib\nuget-restore-lib.ps1'), $PSCommandPath) {
            $bytes = [System.IO.File]::ReadAllBytes($p)
            Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "$p has a BOM"
            Assert-True (-not ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1)) "$p has non-ASCII bytes"
        }
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
