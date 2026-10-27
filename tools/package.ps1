# Packages a published Clink.exe: Clink.exe, Clink.exe.sha256, Clink-<ver>-setup.exe (needs Inno Setup 6),
# Clink-<ver>-portable.zip and package.json (file/size/sha256 per output, read by make-manifest.ps1).
param(
    [Parameter(Mandatory)] [string]$ExePath,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$IsccPath,
    [switch]$RequireInstaller   # fail instead of skipping when Inno Setup is missing
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "'$Version' is not a valid version (expected X.Y.Z or X.Y.Z-dev.N)" }
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "Clink.exe not found: $ExePath" }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)

# Locate ISCC.
$iscc = $null
if ($IsccPath) {
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

# 1. Clink.exe + sha256 (format identical to release.yml: "<hash> *Clink.exe\n").
$exeOut = Join-Path $OutDir 'Clink.exe'
if ($ExePath -ne $exeOut) { Copy-Item -LiteralPath $ExePath -Destination $exeOut -Force }
$hash = (Get-FileHash -LiteralPath $exeOut -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $OutDir 'Clink.exe.sha256'), "$hash *Clink.exe`n", $utf8)

# 2. Portable zip: Clink.exe + Clink.portable marker at the zip root.
$zipName = "Clink-$Version-portable.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("clink-portable-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $exeOut -Destination (Join-Path $stage 'Clink.exe')
    [System.IO.File]::WriteAllText((Join-Path $stage 'Clink.portable'),
        "Clink portable mode: settings and clips stay beside Clink.exe. Delete this file to use the normal per-user locations.`r`n", $utf8)
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# 3. Installer.
$setupName = "Clink-$Version-setup.exe"
$setupPath = Join-Path $OutDir $setupName
if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
if ($iscc) {
    $numeric = ($Version -replace '-.*$', '') + '.0'
    $iss = Join-Path $root 'installer\Clink.iss'
    & $iscc "/DAppVersion=$Version" "/DAppFileVersion=$numeric" "/DSourceExe=$exeOut" "/DOutDir=$OutDir" /Qp $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath $setupPath)) { throw "ISCC did not produce $setupPath" }
} else {
    Write-Warning 'Inno Setup 6 (ISCC.exe) not found: installer NOT built.'
}

# 4. package.json summary.
$files = [ordered]@{}
$kinds = [ordered]@{ exe = 'Clink.exe'; sha256file = 'Clink.exe.sha256'; installer = $setupName; portable = $zipName }
foreach ($k in $kinds.Keys) {
    $p = Join-Path $OutDir $kinds[$k]
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $files[$k] = [ordered]@{
        file   = $kinds[$k]
        size   = (Get-Item -LiteralPath $p).Length
        sha256 = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$summary = [ordered]@{ version = $Version; files = $files }
[System.IO.File]::WriteAllText((Join-Path $OutDir 'package.json'), ($summary | ConvertTo-Json -Depth 5), $utf8)
$files.Values | ForEach-Object { '{0,-34} {1,12:N0} bytes' -f $_.file, $_.size }
'package.json written'
