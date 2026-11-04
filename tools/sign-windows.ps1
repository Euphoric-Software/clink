param([Parameter(Mandatory)] [string]$Path)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\windows-signing-lib.ps1')
Invoke-WindowsInstallerSigning -Path $Path
