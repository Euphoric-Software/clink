# Builds Clink. Uses the user-local .NET SDK (%USERPROFILE%\.dotnet) if there is no dotnet with an SDK on PATH.
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Package,   # publish, then run tools/package.ps1 into dist/package (Clink-Setup.exe needs Inno Setup 6.3+)
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'dotnet'
# "dotnet --version" run in the repo root honours global.json: it fails when the pinned SDK is not installed.
function Test-DotNetSdk {
    param([string]$Exe)
    Push-Location -LiteralPath $root
    try {
        $output = & $Exe --version 2>$null
        return ($LASTEXITCODE -eq 0 -and [bool]$output)
    } catch { return $false } finally { Pop-Location }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-DotNetSdk 'dotnet')) {
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if ((Test-Path $local) -and (Test-DotNetSdk $local)) { $dotnet = $local } else { throw "No .NET SDK satisfying global.json found. Install the pinned SDK (see global.json), e.g. dotnet-install.ps1 -Version <x>." }
}
$proj = Join-Path $root 'Clink\Clink.csproj'
if ($Package) {
    $Publish = $true
    if ($Configuration -ne 'Release') { Write-Warning 'Packaging a Debug build: release packages should be built with -Configuration Release (the default).' }
}
if ($Publish) {
    # Both flavours, separate obj/bin: dist\sc\Clink.exe (self-contained) and dist\fx\Clink.exe (framework-dependent).
    & (Join-Path $root 'tools\publish-flavours.ps1') -Configuration $Configuration -DotNet $dotnet
    "Published to $(Join-Path $root 'dist')\sc and \fx"
    if ($Package) {
        $ver = ([xml](Get-Content -Raw (Join-Path $root 'Clink\Clink.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
        & (Join-Path $root 'tools\package.ps1') -ExePath (Join-Path $root 'dist\sc\Clink.exe') -FrameworkExePath (Join-Path $root 'dist\fx\Clink.exe') -Version $ver -OutDir (Join-Path $root 'dist\package')
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
} else {
    # Repo root as the working directory, so global.json (the pinned SDK) applies whatever the caller's current directory is.
    Push-Location -LiteralPath $root
    try { & $dotnet build $proj -c $Configuration } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Run) {
    $exe = if ($Publish) { Join-Path $root 'dist\sc\Clink.exe' } else { Join-Path $root "Clink\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Clink.exe" }
    Start-Process $exe
}
