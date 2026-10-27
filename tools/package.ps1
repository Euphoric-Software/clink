# Packages the two published flavours (tools/publish-flavours.ps1): Clink.exe (self-contained, the portable download),
# Clink-framework.exe (framework-dependent, what the installer ships), their .sha256 files ("<hash> *<name>"),
# Clink-Setup.exe (needs Inno Setup 6.3+; built from the FRAMEWORK exe) and package.json (minRuntime plus file/size/sha256
# per output, read by make-manifest.ps1), plus THIRD-PARTY-NOTICES.txt (copied to OutDir, listed in package.json, embedded in the
# installer). The installer's optional .NET Desktop Runtime task needs the runtime URL / SHA-256 / version from
# tools/resolve-dotnet-runtime.ps1 (-RuntimeInfoPath, or the three explicit parameters); they are validated here and are mandatory
# under -RequireInstaller.
param(
    [Parameter(Mandatory)] [string]$ExePath,            # self-contained exe (dist\sc\Clink.exe)
    [Parameter(Mandatory)] [string]$FrameworkExePath,   # framework-dependent exe (dist\fx\Clink.exe)
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$MinRuntime,   # X.Y.Z; default: Microsoft.WindowsDesktop.App version in the ONE Clink.runtimeconfig.json under Clink\bin_fx
    [string]$RuntimeConfigPath,   # explicit Clink.runtimeconfig.json to read the default minRuntime from (overrides the bin_fx search)
    [string]$FxBinDir,   # folder searched for Clink.runtimeconfig.json; default <repo>\Clink\bin_fx (the script tests point it at a fixture)
    [string]$RuntimeInfoPath,   # JSON as printed by tools/resolve-dotnet-runtime.ps1 (version, url, sha256, sha512, size)
    [string]$DotNetRuntimeUrl,       # explicit alternative to -RuntimeInfoPath (all three together)
    [string]$DotNetRuntimeSha256,
    [string]$DotNetRuntimeVersion,
    [string]$NoticesPath,   # default <repo>\THIRD-PARTY-NOTICES.txt; must exist
    [string]$IsccPath,
    [switch]$SkipInstaller,     # never build the installer, even when Inno Setup is installed (CI package dry run)
    [switch]$RequireInstaller,   # fail instead of skipping when Inno Setup is missing
    [switch]$SkipSetupSizeCheck, # TEST-ONLY (the script tests use a fake ISCC that writes a tiny file): skips the "setup exe > 1 MiB" check on the ISCC output
    [switch]$SkipVersionCheck    # TEST-ONLY (the script tests use fixture files without version info): skips the ProductVersion == Version check
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\dotnet-runtime-lib.ps1')

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "'$Version' is not a valid version (expected X.Y.Z or X.Y.Z-dev.N)" }
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "Clink.exe not found: $ExePath" }
if (-not (Test-Path -LiteralPath $FrameworkExePath -PathType Leaf)) { throw "Framework Clink.exe not found: $FrameworkExePath" }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$FrameworkExePath = (Resolve-Path -LiteralPath $FrameworkExePath).Path
if ($ExePath -eq $FrameworkExePath) { throw 'ExePath and FrameworkExePath are the same file.' }
$scLength = (Get-Item -LiteralPath $ExePath).Length
$fxLength = (Get-Item -LiteralPath $FrameworkExePath).Length
if ($scLength -le $fxLength) { throw "The self-contained exe ($scLength bytes) must be larger than the framework-dependent exe ($fxLength bytes): the inputs are swapped or the same flavour." }
if ((Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $FrameworkExePath -Algorithm SHA256).Hash) { throw 'The two exes are identical.' }

if (-not $SkipVersionCheck) {
    # The updater refuses an exe whose ProductVersion differs from the manifest version, so refuse it here, for both inputs.
    foreach ($pair in @(@('self-contained', $ExePath), @('framework-dependent', $FrameworkExePath))) {
        $product = ([string](Get-Item -LiteralPath $pair[1]).VersionInfo.ProductVersion -split '\+')[0]
        if ($product -ne $Version) { throw "The $($pair[0]) exe has ProductVersion '$product', expected '$Version' (publish with -Version $Version)." }
    }
}

if (-not $MinRuntime) {
    if ($RuntimeConfigPath) {
        if (-not (Test-Path -LiteralPath $RuntimeConfigPath -PathType Leaf)) { throw "Runtime config not found: $RuntimeConfigPath" }
        $configPath = (Resolve-Path -LiteralPath $RuntimeConfigPath).Path
    } else {
        if (-not $FxBinDir) { $FxBinDir = Join-Path $root 'Clink\bin_fx' }
        $configs = @(Get-ChildItem -LiteralPath $FxBinDir -Recurse -Filter 'Clink.runtimeconfig.json' -File -ErrorAction SilentlyContinue)
        if ($configs.Count -eq 0) { throw "No Clink.runtimeconfig.json found under $FxBinDir to read minRuntime from; run tools/publish-flavours.ps1 or pass -MinRuntime." }
        if ($configs.Count -gt 1) { throw "More than one Clink.runtimeconfig.json under ${FxBinDir}: $(($configs | ForEach-Object { $_.FullName }) -join '; '). Delete the stale one or pass -RuntimeConfigPath." }
        $configPath = $configs[0].FullName
    }
    $runtimeConfig = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
    $desktop = @($runtimeConfig.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' })
    if ($desktop.Count -ne 1) { throw "$configPath does not reference Microsoft.WindowsDesktop.App exactly once." }
    $MinRuntime = [string]$desktop[0].version
}
if ($MinRuntime -notmatch '^\d+\.\d+\.\d+$') { throw "minRuntime '$MinRuntime' is not X.Y.Z" }

if ($SkipInstaller -and $RequireInstaller) { throw '-SkipInstaller and -RequireInstaller exclude each other.' }

# Runtime inputs for the installer's runtime task. Validated here, before anything is written: the version is a plain x.y.z at or
# above the committed floor of dotnet-runtime-lib.ps1, the URL is exactly the builds.dotnet.microsoft.com URL built for that version,
# the SHA-256 is 64 hex characters, none contains a quote, space or control character.
$explicitRuntime = $DotNetRuntimeUrl -or $DotNetRuntimeSha256 -or $DotNetRuntimeVersion
if ($RuntimeInfoPath -and $explicitRuntime) { throw '-RuntimeInfoPath and the explicit -DotNetRuntime* parameters exclude each other.' }
$runtime = $null
if ($RuntimeInfoPath) {
    if (-not (Test-Path -LiteralPath $RuntimeInfoPath -PathType Leaf)) { throw "Runtime info file not found: $RuntimeInfoPath" }
    $info = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $RuntimeInfoPath).Path) | ConvertFrom-Json
    foreach ($field in 'version', 'url', 'sha256') {
        if (-not $info.PSObject.Properties[$field] -or -not [string]$info.$field) { throw "Runtime info file $RuntimeInfoPath has no '$field' (it must be the output of tools/resolve-dotnet-runtime.ps1 without -NoDownload)." }
    }
    $runtime = [pscustomobject]@{ Version = [string]$info.version; Url = [string]$info.url; Sha256 = [string]$info.sha256 }
} elseif ($explicitRuntime) {
    if (-not ($DotNetRuntimeUrl -and $DotNetRuntimeSha256 -and $DotNetRuntimeVersion)) { throw 'Pass -DotNetRuntimeUrl, -DotNetRuntimeSha256 and -DotNetRuntimeVersion together (or use -RuntimeInfoPath).' }
    $runtime = [pscustomobject]@{ Version = $DotNetRuntimeVersion; Url = $DotNetRuntimeUrl; Sha256 = $DotNetRuntimeSha256 }
}
if ($runtime) {
    foreach ($pair in @(@('version', $runtime.Version), @('url', $runtime.Url), @('sha256', $runtime.Sha256))) { Assert-SafeOutputValue -Name $pair[0] -Value $pair[1] }
    if ($runtime.Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "Runtime version '$($runtime.Version)' is not x.y.z." }
    if ([version]$runtime.Version -lt [version]$script:MinimumRuntimeVersion) { throw "Runtime version $($runtime.Version) is below the committed minimum $($script:MinimumRuntimeVersion) (tools/lib/dotnet-runtime-lib.ps1)." }
    [void](Test-DotNetRuntimeUrl -Url $runtime.Url)
    $expectedUrl = Get-ExpectedDotNetRuntimeUrl -Version $runtime.Version
    if ($runtime.Url -cne $expectedUrl) { throw "Runtime URL '$($runtime.Url)' is not the expected '$expectedUrl'." }
    if ($runtime.Sha256 -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw "Runtime SHA-256 '$($runtime.Sha256)' is not 64 hex characters." }
    $runtime.Sha256 = $runtime.Sha256.ToLowerInvariant()
} elseif ($RequireInstaller) {
    throw '-RequireInstaller needs the .NET runtime inputs: pass -RuntimeInfoPath (output of tools/resolve-dotnet-runtime.ps1) or -DotNetRuntimeUrl, -DotNetRuntimeSha256 and -DotNetRuntimeVersion.'
}

# The third-party notices file (shipped beside the exes, listed in package.json, embedded in the installer).
if (-not $NoticesPath) { $NoticesPath = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }
if (-not (Test-Path -LiteralPath $NoticesPath -PathType Leaf)) { throw "Third-party notices file not found: $NoticesPath (tools/notices/make-notices.ps1 -Generate writes it)." }
$NoticesPath = (Resolve-Path -LiteralPath $NoticesPath).Path
if ((Get-Item -LiteralPath $NoticesPath).Length -le 0) { throw "Third-party notices file is empty: $NoticesPath" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

# Locate ISCC.
$iscc = $null
if ($SkipInstaller) {
    Write-Host 'Installer skipped (-SkipInstaller).'
} elseif ($IsccPath) {
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw "ISCC not found: $IsccPath" }
    $iscc = $IsccPath
} else {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    if ($candidates) { $iscc = @($candidates)[0] }
    else { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
}
if (-not $iscc -and $RequireInstaller) { throw 'Inno Setup 6 (ISCC.exe) not found and -RequireInstaller was given.' }

# 1. Both exes + sha256 files ("<hash> *<name>\n", the line format the updaters read).
$exeOut = Join-Path $OutDir 'Clink.exe'
$fxOut = Join-Path $OutDir 'Clink-framework.exe'
if ($ExePath -ne $exeOut) { Copy-Item -LiteralPath $ExePath -Destination $exeOut -Force }
if ($FrameworkExePath -ne $fxOut) { Copy-Item -LiteralPath $FrameworkExePath -Destination $fxOut -Force }
function Write-Sha256File([string]$Name) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutDir $Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $OutDir "$Name.sha256"), "$hash *$Name`n", $utf8)
}
foreach ($name in 'Clink.exe', 'Clink-framework.exe') { Write-Sha256File $name }

# 1b. The notices file next to them (the installer embeds this copy, so the release asset and the installed file are the same bytes).
$noticesOut = Join-Path $OutDir 'THIRD-PARTY-NOTICES.txt'
if ($NoticesPath -ne $noticesOut) { Copy-Item -LiteralPath $NoticesPath -Destination $noticesOut -Force }

# 2. Installer (from the framework-dependent exe; it installs the .NET Desktop Runtime when missing).
$setupName = 'Clink-Setup.exe'
$setupPath = Join-Path $OutDir $setupName
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
if (Test-Path -LiteralPath "$setupPath.sha256") { Remove-Item -LiteralPath "$setupPath.sha256" -Force }
if ($iscc) {
    $numeric = ($Version -replace '-.*$', '') + '.0'
    $iss = Join-Path $root 'installer\Clink.iss'
    $isccArguments = @("/DAppVersion=$Version", "/DAppFileVersion=$numeric", "/DSourceExe=$fxOut", "/DOutDir=$OutDir", "/DNoticesFile=$noticesOut")
    if ($runtime) {
        $isccArguments += "/DDotNetRuntimeUrl=$($runtime.Url)", "/DDotNetRuntimeSha256=$($runtime.Sha256)", "/DDotNetRuntimeVersion=$($runtime.Version)"
    } else {
        Write-Warning 'No .NET runtime inputs (-RuntimeInfoPath): the installer is built WITHOUT the runtime task (Setup only warns when the runtime is missing).'
    }
    # No /Qp (it would suppress warnings). stdout is captured: only warning lines are echoed on success, everything on failure.
    # PowerShell 7.3+ may turn a non-zero native exit into a terminating error under ErrorActionPreference=Stop: switch that off here
    # so the echo-then-throw path below always runs (the variable is unknown to, and harmless on, Windows PowerShell 5.1).
    $previousNativePreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try { $isccOutput = @(& $iscc @isccArguments $iss); $isccExit = $LASTEXITCODE }
    finally { $PSNativeCommandUseErrorActionPreference = $previousNativePreference }
    foreach ($line in $isccOutput) { if ([string]$line -match '^\s*(Line \d+:\s*)?Warning:') { Write-Output "ISCC: $line" } }
    Write-Output "ISCC exit code: $isccExit"
    if ($isccExit -ne 0) {
        $isccOutput | ForEach-Object { Write-Output "ISCC: $_" }
        throw "ISCC failed (exit code $isccExit)"
    }
    if (-not (Test-Path -LiteralPath $setupPath)) { throw "ISCC did not produce $setupPath" }
    # A real setup exe embeds the framework exe (megabytes); an empty or stub file means ISCC went wrong without failing.
    if (-not $SkipSetupSizeCheck) {
        $setupLength = (Get-Item -LiteralPath $setupPath).Length
        if ($setupLength -le 1048576) { throw "The setup exe $setupPath is only $setupLength bytes (expected more than 1 MiB): ISCC output is not a real installer." }
    }
    Write-Sha256File $setupName
} else {
    if (-not $SkipInstaller) { Write-Warning 'Inno Setup 6 (ISCC.exe) not found: installer NOT built.' }
}

# 3. package.json summary.
$files = [ordered]@{}
$kinds = [ordered]@{ exe = 'Clink.exe'; sha256file = 'Clink.exe.sha256'; exeFramework = 'Clink-framework.exe'; sha256fileFramework = 'Clink-framework.exe.sha256'; installer = $setupName; sha256fileInstaller = "$setupName.sha256"; notices = 'THIRD-PARTY-NOTICES.txt' }
foreach ($k in $kinds.Keys) {
    $p = Join-Path $OutDir $kinds[$k]
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $files[$k] = [ordered]@{
        file   = $kinds[$k]
        size   = (Get-Item -LiteralPath $p).Length
        sha256 = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$summary = [ordered]@{ version = $Version; minRuntime = $MinRuntime; files = $files }
[System.IO.File]::WriteAllText((Join-Path $OutDir 'package.json'), ($summary | ConvertTo-Json -Depth 5), $utf8)
$files.Values | ForEach-Object { '{0,-34} {1,12:N0} bytes' -f $_.file, $_.size }
'package.json written'
