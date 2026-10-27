# Packages the two published flavours (tools/publish-flavours.ps1): Clink.exe (self-contained, the portable download),
# Clink-framework.exe (framework-dependent, what the installer ships), their .sha256 files ("<hash> *<name>"),
# Clink-<ver>-setup.exe (needs Inno Setup 6.3+; built from the FRAMEWORK exe) and package.json (minRuntime plus file/size/sha256
# per output, read by make-manifest.ps1).
param(
    [Parameter(Mandatory)] [string]$ExePath,            # self-contained exe (dist\sc\Clink.exe)
    [Parameter(Mandatory)] [string]$FrameworkExePath,   # framework-dependent exe (dist\fx\Clink.exe)
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$MinRuntime,   # X.Y.Z; default: Microsoft.WindowsDesktop.App version in the ONE Clink.runtimeconfig.json under Clink\bin_fx
    [string]$RuntimeConfigPath,   # explicit Clink.runtimeconfig.json to read the default minRuntime from (overrides the bin_fx search)
    [string]$FxBinDir,   # folder searched for Clink.runtimeconfig.json; default <repo>\Clink\bin_fx (the script tests point it at a fixture)
    [string]$IsccPath,
    [switch]$SkipInstaller,     # never build the installer, even when Inno Setup is installed (CI package dry run)
    [switch]$RequireInstaller,   # fail instead of skipping when Inno Setup is missing
    [switch]$SkipVersionCheck    # TEST-ONLY (the script tests use fixture files without version info): skips the ProductVersion == Version check
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

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
    if ($desktop.Count -ne 1) { throw "$configPath does not reference Microsoft.WindowsDesktop.App exactly once." }    $MinRuntime = [string]$desktop[0].version
}
if ($MinRuntime -notmatch '^\d+\.\d+\.\d+$') { throw "minRuntime '$MinRuntime' is not X.Y.Z" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

# Locate ISCC.
$iscc = $null
if ($SkipInstaller -and $RequireInstaller) { throw '-SkipInstaller and -RequireInstaller exclude each other.' }
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
foreach ($name in 'Clink.exe', 'Clink-framework.exe') {
    $hash = (Get-FileHash -LiteralPath (Join-Path $OutDir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $OutDir "$name.sha256"), "$hash *$name`n", $utf8)
}

# 2. Installer (from the framework-dependent exe; it installs the .NET Desktop Runtime when missing).
$setupName = "Clink-$Version-setup.exe"
$setupPath = Join-Path $OutDir $setupName
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
if ($iscc) {
    $numeric = ($Version -replace '-.*$', '') + '.0'
    $iss = Join-Path $root 'installer\Clink.iss'
    & $iscc "/DAppVersion=$Version" "/DAppFileVersion=$numeric" "/DSourceExe=$fxOut" "/DOutDir=$OutDir" /Qp $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $setupPath)) { throw "ISCC did not produce $setupPath" }
} else {
    if (-not $SkipInstaller) { Write-Warning 'Inno Setup 6 (ISCC.exe) not found: installer NOT built.' }
}

# 3. package.json summary.
$files = [ordered]@{}
$kinds = [ordered]@{ exe = 'Clink.exe'; sha256file = 'Clink.exe.sha256'; exeFramework = 'Clink-framework.exe'; sha256fileFramework = 'Clink-framework.exe.sha256'; installer = $setupName }
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
