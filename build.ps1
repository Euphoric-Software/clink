# Builds Clink. Uses the user-local .NET SDK (%USERPROFILE%\.dotnet) if there is no dotnet with an SDK on PATH.
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'dotnet'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (& dotnet --list-sdks 2>$null)) {
    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $local) { $dotnet = $local } else { throw "No .NET SDK found. Install .NET 10 SDK or run: https://dot.net/v1/dotnet-install.ps1 -Channel 10.0" }
}
$proj = Join-Path $root 'Clink\Clink.csproj'
if ($Publish) {
    & $dotnet publish $proj -c $Configuration -o (Join-Path $root 'dist')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    "Published to $(Join-Path $root 'dist')\Clink.exe"
} else {
    & $dotnet build $proj -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Run) {
    $exe = if ($Publish) { Join-Path $root 'dist\Clink.exe' } else { Join-Path $root "Clink\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Clink.exe" }
    Start-Process $exe
}
