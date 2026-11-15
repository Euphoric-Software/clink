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

# Pinned signing inputs. The module is the exact nupkg the PowerShell Gallery serves for ArtifactSigning 0.1.8 (SHA-256 of the file;
# the gallery's own SHA-512 package hash matched when it was pinned). The two NuGet packages the module would otherwise fetch unpinned
# with Install-Package are pinned by SHA-512 (base64), the hash nuget.org publishes in its catalog entry for that id and version; they
# are placed where the module looks for them (%LOCALAPPDATA%\ArtifactSigning\<id>\<id>.<version>) so it skips its own install.
# The third package, the `sign` tool, is restored by the module with dotnet and checked by the nuget.config below.
# Update hashes and versions together, in a reviewed change.
$script:ArtifactSigningModuleUrl = 'https://www.powershellgallery.com/api/v2/package/ArtifactSigning/0.1.8'
$script:ArtifactSigningModuleSha256 = '3221344b8c627915d3870f23e80816f31a5d8c2bae1d7c0cdd6c9652f6c4e089'
$script:ArtifactSigningPackages = @(
    @{ Id = 'Microsoft.Windows.SDK.BuildTools'; Version = '10.0.26100.4188'; ContentPath = 'bin\10.0.26100.0\x64'; Sha512 = 'OkiRhdDr0ngD6+wm0Ezdu1mgV8ZuknXdHwjzeHVZjYSXi9bCkYkq9Ns/X/ab9BoHb+Tq/5/RmXbdNWtC0qHipQ==' }
    @{ Id = 'Microsoft.ArtifactSigning.Client'; Version = '1.0.128'; ContentPath = 'bin\x64'; Sha512 = 'mPBqaR9Pwvoi8Z3PhVZzPphgf775GjEsRTubB5jMkIja4KyzbjibVSoRtNIyAyR4W4VBwrUQkackwFvF31y/lQ==' }
)

function Get-ArtifactSigningCacheRoot {
    $root = $env:RUNNER_TEMP
    if ([string]::IsNullOrWhiteSpace($root)) { $root = [System.IO.Path]::GetTempPath() }
    return (Join-Path $root 'artifactsigning-pinned')
}

function Get-ArtifactSigningModuleBase { return (Join-Path (Get-ArtifactSigningCacheRoot) 'module\ArtifactSigning') }

function Save-PinnedPackage {
    # Download (or reuse a file already in the cache) and refuse anything whose hash is not the pinned one.
    param([string]$Url, [string]$Path, [string]$Algorithm, [string]$Expected, [bool]$Base64, [string]$What)
    if (-not (Test-Path -LiteralPath $Path)) { Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing -ErrorAction Stop }
    $hasher = [System.Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    $stream = [System.IO.File]::OpenRead($Path)
    try { $bytes = $hasher.ComputeHash($stream) } finally { $stream.Dispose(); $hasher.Dispose() }
    $actual = if ($Base64) { [Convert]::ToBase64String($bytes) } else { ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant() }
    if ($actual -cne $Expected) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        throw "$What package hash mismatch: got $actual, expected $Expected"
    }
}

function Expand-PinnedPackage {
    param([string]$Package, [string]$Destination)
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction Stop }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Package, $Destination)
}

function Install-ArtifactSigningModule {
    # Replaces Install-Module: every input is verified against its pinned hash, then the module is imported by path. Safe to call in
    # every process (the cache is re-hashed and re-extracted each time): the signer runs in a fresh process under ISCC.
    $cache = Get-ArtifactSigningCacheRoot
    New-Item -ItemType Directory -Force -Path $cache -ErrorAction Stop | Out-Null
    # The two packages first: the module's own Install-Package fetch is skipped only when these folders exist.
    $local = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($local)) { throw 'LOCALAPPDATA is not set.' }
    foreach ($package in $script:ArtifactSigningPackages) {
        $file = Join-Path $cache ('{0}.{1}.nupkg' -f $package.Id, $package.Version)
        $url = 'https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg' -f $package.Id.ToLowerInvariant(), $package.Version.ToLowerInvariant()
        Save-PinnedPackage -Url $url -Path $file -Algorithm 'SHA512' -Expected $package.Sha512 -Base64 $true -What $package.Id
        $idFolder = Join-Path (Join-Path $local 'ArtifactSigning') $package.Id
        # Anything already there (an older version, a runner leftover) is replaced by the verified files.
        if (Test-Path -LiteralPath $idFolder) { Remove-Item -LiteralPath $idFolder -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Force -Path $idFolder -ErrorAction Stop | Out-Null
        $install = Join-Path $idFolder ('{0}.{1}' -f $package.Id, $package.Version)
        Expand-PinnedPackage -Package $file -Destination $install
        if (-not (Test-Path -LiteralPath (Join-Path $install $package.ContentPath) -PathType Container)) { throw "$($package.Id) lost its expected content folder $($package.ContentPath)." }
    }
    $moduleFile = Join-Path $cache 'ArtifactSigning.0.1.8.nupkg'
    Save-PinnedPackage -Url $script:ArtifactSigningModuleUrl -Path $moduleFile -Algorithm 'SHA256' -Expected $script:ArtifactSigningModuleSha256 -Base64 $false -What 'ArtifactSigning'
    $base = Get-ArtifactSigningModuleBase
    Expand-PinnedPackage -Package $moduleFile -Destination $base
    Import-Module (Join-Path $base 'ArtifactSigning.psd1') -ErrorAction Stop
}

function Assert-ArtifactSigningModuleLoaded {
    # The verified module must be the loaded one. A by-name import would search PSModulePath, which holds nothing verified.
    $base = Get-ArtifactSigningModuleBase
    $loaded = @(Get-Module -Name ArtifactSigning | Where-Object { $_.Version -eq [version]'0.1.8' -and $_.ModuleBase -ieq $base })
    if ($loaded.Count -eq 0) { Install-ArtifactSigningModule }
    $loaded = @(Get-Module -Name ArtifactSigning | Where-Object { $_.Version -eq [version]'0.1.8' -and $_.ModuleBase -ieq $base })
    if ($loaded.Count -eq 0) { throw 'The verified ArtifactSigning module is not loaded.' }
}

function Get-ArtifactSigningNuGetConfig {
    # For the module's `dotnet tool install` restore: nuget.org only, every package must carry a valid signature, and the
    # only trusted signer is the nuget.org repository countersignature (CN=NuGet.org Repository by Microsoft), and only for packages
    # nuget.org lists as owned by Microsoft or SignTeam (the owners of the `sign` tool). No machine-level fallback folder is used.
    return @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <config>
    <add key="signatureValidationMode" value="require" />
  </config>
  <trustedSigners>
    <clear />
    <repository name="nuget.org" serviceIndex="https://api.nuget.org/v3/index.json">
      <certificate fingerprint="1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
      <owners>Microsoft;SignTeam</owners>
    </repository>
  </trustedSigners>
</configuration>
'@
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
    # The release installs the same pinned module as Microsoft's signing action.
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
    # Invoke-ArtifactSigning installs its tools with dotnet --add-source. Do not
    # inherit the app's NuGet source mapping (or relax it) for that independent restore.
    $directory = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-signing-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\global.json') -Destination (Join-Path $directory 'global.json') -ErrorAction Stop
    # Signatures required, only the nuget.org repository signer trusted, for the module's own tool restore.
    [System.IO.File]::WriteAllText((Join-Path $directory 'nuget.config'), (Get-ArtifactSigningNuGetConfig), [System.Text.UTF8Encoding]::new($false))
    Push-Location -LiteralPath $directory -ErrorAction Stop
    try {
        Assert-ArtifactSigningModuleLoaded
        Invoke-ArtifactSigning @parameters
    } finally { Pop-Location }
    Assert-WindowsSignature -Path $parameters.Files
}

function Get-WindowsInstallerSignTool {
    # Inno's $q and $f placeholders preserve paths with spaces. This is passed as
    # one ISCC argument; no shell interpolation of the command is involved.
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
    $script = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\sign-windows.ps1'))
    return ('$q{0}$q -NoProfile -NonInteractive -File $q{1}$q $f' -f $pwsh, $script)
}
