param([Parameter(Mandatory)] [string]$Path)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\windows-signing-lib.ps1')
if ($env:GITHUB_ACTIONS -cne 'true' -or -not $env:RUNNER_TEMP) { throw 'Installer verification requires a fresh GitHub Actions runner.' }
if (-not (Get-WindowsSigningEnabled)) { throw 'Installer verification requires signing.' }
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7D4A9E3B-5C21-4F6A-9B08-2E1C6A5D3F74}_is1'
foreach ($hive in [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine) {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
    try { if ($registry.OpenSubKey($key)) { throw 'An existing Clink installation must not be used by this test.' } }
    finally { $registry.Dispose() }
}
foreach ($directory in (Join-Path $env:LOCALAPPDATA 'Clink'), (Join-Path $env:APPDATA 'Clink'), (Join-Path $env:LOCALAPPDATA 'Programs\Clink')) {
    if (Test-Path -LiteralPath $directory) { throw 'An existing Clink data directory must not be used by this test.' }
}
$fixture = Join-Path $env:RUNNER_TEMP ('clink-signed-install-' + [guid]::NewGuid().ToString('N'))
function Invoke-Setup([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { throw 'Installer operation timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer operation failed: $($process.ExitCode)" }
}
Assert-WindowsSignature -Path $Path
Invoke-Setup (Resolve-Path -LiteralPath $Path).Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', '/TASKS=""', ('/DIR="' + $fixture + '"'))
$uninstaller = Join-Path $fixture 'unins000.exe'
try {
    Assert-WindowsSignature -Path (Join-Path $fixture 'Clink.exe'), $uninstaller
} finally {
    if (Test-Path -LiteralPath $uninstaller) { Invoke-Setup $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') }
}
if (Test-Path -LiteralPath (Join-Path $fixture 'Clink.exe')) { throw 'Uninstall did not remove the test application.' }
Write-Host 'Signed setup, installed application and embedded uninstaller verified.'
