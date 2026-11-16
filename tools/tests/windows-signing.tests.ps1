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
    # ---- supply-chain pins: module and package hashes, verified module really loads, signature-checked tool restore, dev switch, dependabot
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $pinWork = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-signing-pin-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $pinWork | Out-Null
    $savedEnv = @{ RUNNER_TEMP = $env:RUNNER_TEMP; LOCALAPPDATA = $env:LOCALAPPDATA }
    $savedModuleSha = $script:ArtifactSigningModuleSha256
    $savedPackages = $script:ArtifactSigningPackages
    try {
        $realHash = $script:ArtifactSigningModuleSha256
        if ($realHash -cnotmatch '\A[0-9a-f]{64}\z') { throw 'The pinned ArtifactSigning SHA-256 is missing or malformed.' }
        foreach ($package in $script:ArtifactSigningPackages) {
            if ($package.Sha512 -cnotmatch '\A[A-Za-z0-9+/]{86}==\z') { throw "The pinned SHA-512 of $($package.Id) is missing or malformed." }
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        function New-TestZip {
            param([string]$Name, [hashtable]$Files)
            $dir = Join-Path $pinWork ('src-' + $Name)
            New-Item -ItemType Directory -Path $dir | Out-Null
            foreach ($key in $Files.Keys) {
                $target = Join-Path $dir $key
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                [System.IO.File]::WriteAllText($target, $Files[$key])
            }
            $zip = Join-Path $pinWork ($Name + '.nupkg')
            [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip)
            return $zip
        }
        $psd1 = "@{ RootModule = 'ArtifactSigning.psm1'; ModuleVersion = '0.1.8'; GUID = '6f1c6a1e-5b1e-4f0e-9f57-0f6c5b7f0a11'; FunctionsToExport = @('Invoke-ArtifactSigning') }"
        $psm1 = 'function Invoke-ArtifactSigning { [System.IO.File]::WriteAllText($env:CLINK_T_MARKER, (''{0}|{1}'' -f (Test-Path -LiteralPath nuget.config), (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA ''ArtifactSigning\Test.One\Test.One.1.0.0\bin\x64\a.txt'')))) }'
        $moduleZip = New-TestZip 'module' @{ 'ArtifactSigning.psd1' = $psd1; 'ArtifactSigning.psm1' = $psm1 }
        $oneZip = New-TestZip 'one' @{ 'bin/x64/a.txt' = 'one' }
        $twoZip = New-TestZip 'two' @{ 'bin/10.0.26100.0/x64/b.txt' = 'two' }
        $badZip = Join-Path $pinWork 'bad.nupkg'
        [System.IO.File]::WriteAllBytes($badZip, [byte[]](1, 2, 3, 4))
        function Get-TestSha512 { param([string]$Path) $h = [System.Security.Cryptography.SHA512]::Create(); try { [Convert]::ToBase64String($h.ComputeHash([System.IO.File]::ReadAllBytes($Path))) } finally { $h.Dispose() } }
        $script:ArtifactSigningModuleSha256 = (Get-FileHash -LiteralPath $moduleZip -Algorithm SHA256).Hash.ToLowerInvariant()
        $script:ArtifactSigningPackages = @(
            @{ Id = 'Test.One'; Version = '1.0.0'; ContentPath = 'bin\x64'; Sha512 = (Get-TestSha512 $oneZip) }
            @{ Id = 'Test.Two'; Version = '2.0.0'; ContentPath = 'bin\10.0.26100.0\x64'; Sha512 = (Get-TestSha512 $twoZip) }
        )
        $twoUrl = 'https://api.nuget.org/v3-flatcontainer/test.two/2.0.0/test.two.2.0.0.nupkg'
        # Network mock: no real download, no file:// URIs (PowerShell 7 rejects that scheme).
        $script:web = @{
            $script:ArtifactSigningModuleUrl = $moduleZip
            'https://api.nuget.org/v3-flatcontainer/test.one/1.0.0/test.one.1.0.0.nupkg' = $oneZip
            $twoUrl = $twoZip
        }
        function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing, $ErrorAction) Copy-Item -LiteralPath $script:web[$Uri] -Destination $OutFile }
        $script:imported = $null
        function Import-Module { param($Name, $ErrorAction) $script:imported = $Name }
        function Set-TestRoots { param([string]$Name) $env:RUNNER_TEMP = Join-Path $pinWork ('rt-' + $Name); $env:LOCALAPPDATA = Join-Path $pinWork ('la-' + $Name); New-Item -ItemType Directory -Force -Path $env:RUNNER_TEMP, $env:LOCALAPPDATA | Out-Null }

        # 1. wrong module bytes: refused, nothing imported
        Set-TestRoots 'm'
        $script:web[$script:ArtifactSigningModuleUrl] = $badZip
        Assert-Throws { Install-ArtifactSigningModule } 'ArtifactSigning package hash mismatch'
        if ($null -ne $script:imported) { throw 'A module package with the wrong hash was imported.' }
        $script:web[$script:ArtifactSigningModuleUrl] = $moduleZip
        # 2. wrong NuGet package bytes: refused
        Set-TestRoots 'p'
        $script:web[$twoUrl] = $badZip
        Assert-Throws { Install-ArtifactSigningModule } 'Test.Two package hash mismatch'
        if ($null -ne $script:imported) { throw 'A module was imported after a package hash mismatch.' }
        $script:web[$twoUrl] = $twoZip
        # 3. good inputs: packages placed where the module looks (a stale folder is replaced), module imported by path from the verified base
        Set-TestRoots 'g'
        $stale = Join-Path $env:LOCALAPPDATA 'ArtifactSigning\Test.One\Test.One.0.9.9'
        New-Item -ItemType Directory -Path $stale | Out-Null
        Install-ArtifactSigningModule
        if (Test-Path -LiteralPath $stale) { throw 'A stale unverified package folder was kept.' }
        foreach ($rel in @('ArtifactSigning\Test.One\Test.One.1.0.0\bin\x64\a.txt', 'ArtifactSigning\Test.Two\Test.Two.2.0.0\bin\10.0.26100.0\x64\b.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA $rel))) { throw "The verified package file is missing: $rel" }
        }
        if ($script:imported -cne (Join-Path (Get-ArtifactSigningModuleBase) 'ArtifactSigning.psd1') -or -not (Test-Path -LiteralPath $script:imported)) { throw 'The verified module was not imported by path from the verified folder.' }

        # 4. a fresh process (what ISCC starts) with Import-Module NOT mocked: the signing entry point loads the verified module itself
        Set-TestRoots 'fresh'
        $child = Join-Path $pinWork 'child.ps1'
        [System.IO.File]::WriteAllText($child, @'
$ErrorActionPreference = 'Stop'
. (Join-Path $env:CLINK_T_REPO 'tools\lib\windows-signing-lib.ps1')
function Get-TestSha512 { param([string]$Path) $h = [System.Security.Cryptography.SHA512]::Create(); try { [Convert]::ToBase64String($h.ComputeHash([System.IO.File]::ReadAllBytes($Path))) } finally { $h.Dispose() } }
$script:ArtifactSigningModuleSha256 = (Get-FileHash -LiteralPath $env:CLINK_T_MODULE -Algorithm SHA256).Hash.ToLowerInvariant()
$script:ArtifactSigningPackages = @(
    @{ Id = 'Test.One'; Version = '1.0.0'; ContentPath = 'bin\x64'; Sha512 = (Get-TestSha512 $env:CLINK_T_ONE) }
    @{ Id = 'Test.Two'; Version = '2.0.0'; ContentPath = 'bin\10.0.26100.0\x64'; Sha512 = (Get-TestSha512 $env:CLINK_T_TWO) }
)
$script:web = @{
    $script:ArtifactSigningModuleUrl = $env:CLINK_T_MODULE
    'https://api.nuget.org/v3-flatcontainer/test.one/1.0.0/test.one.1.0.0.nupkg' = $env:CLINK_T_ONE
    'https://api.nuget.org/v3-flatcontainer/test.two/2.0.0/test.two.2.0.0.nupkg' = $env:CLINK_T_TWO
}
function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing, $ErrorAction) Copy-Item -LiteralPath $script:web[$Uri] -Destination $OutFile }
$certificate = [pscustomobject]@{ Publisher = 'Example Publisher LLC' }
$certificate | Add-Member ScriptMethod GetNameInfo { param($Type, $Issuer) $this.Publisher }
function Get-AuthenticodeSignature { param($LiteralPath, $ErrorAction) [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $certificate; TimeStamperCertificate = $certificate } }
$fixture = Join-Path $env:CLINK_T_WORK 'fixture.exe'
[System.IO.File]::WriteAllText($fixture, 'synthetic executable')
if (Get-Module -Name ArtifactSigning) { throw 'The module was loaded before the signing entry point ran.' }
Invoke-WindowsInstallerSigning -Path $fixture
$loaded = @(Get-Module -Name ArtifactSigning)
if ($loaded.Count -ne 1 -or $loaded[0].ModuleBase -cne (Get-ArtifactSigningModuleBase)) { throw 'The verified module is not the loaded one.' }
Write-Output ('child-ok|' + [System.IO.File]::ReadAllText($env:CLINK_T_MARKER))
'@)
        $saved2 = @{}
        $childEnv = @{ CLINK_T_REPO = $repoRoot; CLINK_T_MODULE = $moduleZip; CLINK_T_ONE = $oneZip; CLINK_T_TWO = $twoZip; CLINK_T_WORK = $pinWork; CLINK_T_MARKER = (Join-Path $pinWork 'marker.txt')
            ARTIFACT_SIGNING_ENABLED = 'true'; AZURE_CLIENT_ID = '11111111-2222-3333-4444-555555555555'; AZURE_TENANT_ID = '11111111-2222-3333-4444-555555555555'; AZURE_SUBSCRIPTION_ID = '11111111-2222-3333-4444-555555555555'
            ARTIFACT_SIGNING_ENDPOINT = 'https://eus.codesigning.azure.net'; ARTIFACT_SIGNING_ACCOUNT = 'ExampleSigning'; ARTIFACT_SIGNING_PROFILE = 'example-public'; ARTIFACT_SIGNING_PUBLISHER = 'Example Publisher LLC' }
        foreach ($key in $childEnv.Keys) { $saved2[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $childEnv[$key]) }
        try {
            $hostExe = (Get-Process -Id $PID).Path
            $out = (& $hostExe -NoProfile -ExecutionPolicy Bypass -File $child 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0 -or $out -notmatch 'child-ok\|True\|True') { throw "The fresh-process signing run failed (exit $LASTEXITCODE): $out" }
        } finally { foreach ($key in $saved2.Keys) { [Environment]::SetEnvironmentVariable($key, $saved2[$key]) } }
    } finally {
        foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name]) }
        $script:ArtifactSigningModuleSha256 = $savedModuleSha
        $script:ArtifactSigningPackages = $savedPackages
        Remove-Item -LiteralPath 'function:Import-Module', 'function:Invoke-WebRequest' -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $pinWork -Recurse -Force
    }
    $config = Get-ArtifactSigningNuGetConfig
    foreach ($needle in @('<clear />', '<owners>Microsoft;SignTeam</owners>', '<fallbackPackageFolders>', 'signatureValidationMode" value="require"', '<repository name="nuget.org"', 'allowUntrustedRoot="false"', 'https://api.nuget.org/v3/index.json')) {
        if (-not $config.Contains($needle)) { throw "The signing nuget.config lost: $needle" }
    }
    [xml]$null = $config
    $release = [System.IO.File]::ReadAllText((Join-Path $repoRoot '.github\workflows\release.yml'))
    $dev = [System.IO.File]::ReadAllText((Join-Path $repoRoot '.github\workflows\release-dev.yml'))
    foreach ($text in @($release, $dev)) {
        if ($text -match '(?m)^\s*Install-Module\b') { throw 'A workflow installs the signing module without the hash pin.' }
        if ($text -notmatch '(?m)^\s*Install-ArtifactSigningModule\s*$') { throw 'A workflow does not call Install-ArtifactSigningModule.' }
    }
    if ($dev -notmatch '(?m)^\s*ARTIFACT_SIGNING_ENABLED: \$\{\{ vars\.ARTIFACT_SIGNING_DEV_ENABLED \}\}\s*$') { throw 'release-dev.yml must read ARTIFACT_SIGNING_DEV_ENABLED.' }
    if ($dev -match 'vars\.ARTIFACT_SIGNING_ENABLED') { throw 'release-dev.yml still reads the shared stable signing switch.' }
    if ($release -notmatch '(?m)^\s*ARTIFACT_SIGNING_ENABLED: \$\{\{ vars\.ARTIFACT_SIGNING_ENABLED \}\}\s*$') { throw 'release.yml lost the stable signing switch.' }
    $dependabot = [System.IO.File]::ReadAllText((Join-Path $repoRoot '.github\dependabot.yml'))
    if ($dependabot -notmatch '(?m)^\s*- package-ecosystem: nuget\s*$') { throw 'dependabot.yml has no nuget ecosystem.' }
    $fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-signing-fixture-' + [guid]::NewGuid().ToString('N') + '.exe')
    [System.IO.File]::WriteAllText($fixture, 'synthetic executable')
    $originalLocation = (Get-Location).Path
    $script:failSigning = $false
    function Assert-ArtifactSigningModuleLoaded { }
    function Invoke-ArtifactSigning {
        param($Files)
        if ((Get-Location).Path -eq $originalLocation -or -not (Test-Path -LiteralPath 'nuget.config') -or (Get-Content -LiteralPath 'nuget.config' -Raw) -cne (Get-ArtifactSigningNuGetConfig) -or -not (Test-Path -LiteralPath 'global.json')) { throw 'Signing tools inherited the app restore context.' }
        if (-not [System.IO.Path]::IsPathRooted($Files) -or -not (Test-Path -LiteralPath $Files)) { throw 'The signing input lost its absolute path.' }
        if ($script:failSigning) { throw 'synthetic signing failure' }
    }
    try {
        Invoke-WindowsInstallerSigning -Path $fixture
        if ((Get-Location).Path -cne $originalLocation) { throw 'Signing did not restore the working directory.' }
        $script:failSigning = $true
        Assert-Throws { Invoke-WindowsInstallerSigning -Path $fixture } 'synthetic signing failure'
        if ((Get-Location).Path -cne $originalLocation) { throw 'Signing failure did not restore the working directory.' }
    } finally { Remove-Item -LiteralPath $fixture -Force }
    Write-Host 'Windows signing configuration, unsigned-file, timestamp and publisher gates passed.'
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
}
