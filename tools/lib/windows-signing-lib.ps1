# Windows publisher signing is independent of the update-feed signature.
# Repository variables contain public Azure identifiers, never client secrets.
function Get-WindowsSigningEnabled {
    $mode = $env:ARTIFACT_SIGNING_ENABLED
    if ([string]::IsNullOrEmpty($mode) -or $mode -ceq 'false') { return $false }
    if ($mode -cne 'true') { throw 'ARTIFACT_SIGNING_ENABLED must be true, false, or unset.' }
    foreach ($name in @('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID', 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'ARTIFACT_SIGNING_PUBLISHER')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing Windows signing configuration: $name" }
    }
    foreach ($name in @('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID')) {
        if ([Environment]::GetEnvironmentVariable($name) -cnotmatch '\A[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\z') { throw "Invalid Azure identifier: $name" }
    }
    if ($env:ARTIFACT_SIGNING_ENDPOINT -cnotmatch '\Ahttps://(brs|cus|eus|jpe|krc|ncus|neu|plc|scus|swn|wcus|weu|wus|wus2|wus3)\.codesigning\.azure\.net/?\z') { throw 'Invalid public Artifact Signing endpoint.' }
    if ($env:ARTIFACT_SIGNING_ACCOUNT -cnotmatch '\A[A-Za-z][A-Za-z0-9-]{1,22}[A-Za-z0-9]\z') { throw 'Invalid Artifact Signing account name.' }
    if ($env:ARTIFACT_SIGNING_PROFILE -cnotmatch '\A[A-Za-z][A-Za-z0-9-]{3,98}[A-Za-z0-9]\z') { throw 'Invalid Artifact Signing profile name.' }
    return $true
}

function Assert-WindowsSignature {
    param([Parameter(Mandatory)] [string[]]$Path)
    if ([string]::IsNullOrWhiteSpace($env:ARTIFACT_SIGNING_PUBLISHER)) { throw 'Missing expected Windows publisher.' }
    foreach ($file in $Path) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file -ErrorAction Stop
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) { throw "Missing valid timestamped Windows signature: $file" }
        $publisher = $signature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        if ($publisher -cne $env:ARTIFACT_SIGNING_PUBLISHER) { throw "Unexpected Windows publisher: $file" }
        Write-Host "Verified Windows signature: $file"
    }
}

function Invoke-WindowsInstallerSigning {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Get-WindowsSigningEnabled)) { throw 'Installer signing was called while Windows signing is disabled.' }
    # The pinned artifact-signing-action installs this module before packaging.
    $parameters = @{
        Endpoint = $env:ARTIFACT_SIGNING_ENDPOINT
        CodeSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT
        CertificateProfileName = $env:ARTIFACT_SIGNING_PROFILE
        Files = (Resolve-Path -LiteralPath $Path).Path
        FileDigest = 'SHA256'
        TimestampRfc3161 = 'http://timestamp.acs.microsoft.com'
        TimestampDigest = 'SHA256'
        ExcludeEnvironmentCredential = $true
        ExcludeWorkloadIdentityCredential = $true
        ExcludeManagedIdentityCredential = $true
        ExcludeSharedTokenCacheCredential = $true
        ExcludeVisualStudioCredential = $true
        ExcludeVisualStudioCodeCredential = $true
        ExcludeAzureCliCredential = $false
        ExcludeAzurePowerShellCredential = $true
        ExcludeAzureDeveloperCliCredential = $true
        ExcludeInteractiveBrowserCredential = $true
    }
    Invoke-ArtifactSigning @parameters
    Assert-WindowsSignature -Path $Path
}
