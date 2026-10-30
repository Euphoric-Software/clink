# No Azure access, real certificates, or signing operations: test the release gates.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\windows-signing-lib.ps1')
function Assert-Throws([scriptblock]$Body, [string]$Pattern) {
    try { & $Body } catch {
        if ($_.Exception.Message -match $Pattern) { return }
        throw
    }
    throw "Expected rejection matching $Pattern"
}
$keys = @('ARTIFACT_SIGNING_ENABLED', 'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'ARTIFACT_SIGNING_PUBLISHER')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $null) }
try {
    if (Get-WindowsSigningEnabled) { throw 'Unconfigured signing must stay disabled.' }
    $env:ARTIFACT_SIGNING_ENABLED = 'tru'
    Assert-Throws { Get-WindowsSigningEnabled } 'must be true, false, or unset'
    $env:ARTIFACT_SIGNING_ENABLED = 'true'
    Assert-Throws { Get-WindowsSigningEnabled } 'Missing Windows signing configuration'
    foreach ($key in @('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID')) { [Environment]::SetEnvironmentVariable($key, '11111111-1111-1111-1111-111111111111') }
    $env:ARTIFACT_SIGNING_ENDPOINT = 'https://eus.codesigning.azure.net'
    $env:ARTIFACT_SIGNING_ACCOUNT = 'ExampleSigning'
    $env:ARTIFACT_SIGNING_PROFILE = 'example-public'
    $env:ARTIFACT_SIGNING_PUBLISHER = 'Example Publisher LLC'
    if (-not (Get-WindowsSigningEnabled)) { throw 'Complete signing configuration was refused.' }
    $env:ARTIFACT_SIGNING_ENDPOINT = 'https://eus.codesigning.azure.net.attacker.example'
    Assert-Throws { Get-WindowsSigningEnabled } 'Invalid public Artifact Signing endpoint'
    $env:ARTIFACT_SIGNING_ENDPOINT = 'https://eus.codesigning.azure.net'
    $certificate = [pscustomobject]@{ Publisher = 'Example Publisher LLC' }
    $certificate | Add-Member ScriptMethod GetNameInfo { param($Type, $Issuer) $this.Publisher }
    $script:fakeSignature = [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $certificate; TimeStamperCertificate = $null }
    function Get-AuthenticodeSignature { param($LiteralPath, $ErrorAction) $script:fakeSignature }
    Assert-Throws { Assert-WindowsSignature -Path 'fixture.exe' } 'Missing valid timestamped'
    $script:fakeSignature.Status = 'Valid'
    Assert-Throws { Assert-WindowsSignature -Path 'fixture.exe' } 'Missing valid timestamped'
    $script:fakeSignature.TimeStamperCertificate = $certificate
    $certificate.Publisher = 'Someone Else LLC'
    Assert-Throws { Assert-WindowsSignature -Path 'fixture.exe' } 'Unexpected Windows publisher'
    $certificate.Publisher = 'Example Publisher LLC'
    Assert-WindowsSignature -Path 'fixture.exe'
    Write-Host 'Windows signing configuration, unsigned-file, timestamp and publisher gates passed.'
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
}
