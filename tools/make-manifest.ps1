# Writes <Channel>.json and <Channel>.json.sig (contract v1) into -PackageDir from package.json (see package.ps1).
# The signing key (PKCS#8 PEM) comes from env var CLINK_FEED_SIGNING_KEY. The manifest bytes are written once
# (UTF-8, no BOM) and exactly those bytes are signed: ECDSA P-256 / SHA-256, IEEE P1363 r||s, Base64 on one line.
# BaseUrl is the folder URL holding the assets (ends with '/'), e.g. https://cdn.example/clink/releases/v0.4.2/
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$BaseUrl,
    [Parameter(Mandatory)] [string]$NotesUrl,
    [Parameter(Mandatory)] [string]$PackageDir
)
$ErrorActionPreference = 'Stop'

if ($Version -match '^v') { throw 'Version must not start with "v".' }
if ($Tag -ne "v$Version") { throw "Tag '$Tag' must be 'v$Version'." }
if ($Channel -eq 'dev' -and $Version -notmatch '-dev\.\d+$') { throw 'A dev manifest needs a version like X.Y.Z-dev.N.' }
if ($Channel -eq 'stable' -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable manifest needs a version like X.Y.Z.' }
foreach ($u in $BaseUrl, $NotesUrl) { if ($u -notmatch '^https://') { throw "URL must be https: $u" } }
if (-not $BaseUrl.EndsWith('/')) { $BaseUrl += '/' }

$pem = $env:CLINK_FEED_SIGNING_KEY
if ([string]::IsNullOrWhiteSpace($pem)) { throw 'Env var CLINK_FEED_SIGNING_KEY is empty or not set.' }
$pkgFile = Join-Path $PackageDir 'package.json'
if (-not (Test-Path -LiteralPath $pkgFile)) { throw "package.json not found in $PackageDir (run tools/package.ps1 first)." }
$pkg = Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json
if ($pkg.version -ne $Version) { throw "package.json is for version $($pkg.version), not $Version." }

function New-AssetEntry($info, [string]$what) {
    if (-not $info) { throw "package.json has no '$what' entry (installer not built?)." }
    if ($info.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "Bad sha256 for $what." }
    [ordered]@{ url = $BaseUrl + $info.file; size = [int64]$info.size; sha256 = $info.sha256 }
}
$manifest = [ordered]@{
    schema     = 1
    channel    = $Channel
    version    = $Version
    tag        = $Tag
    published  = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    notesUrl   = $NotesUrl
    minRuntime = '10.0'
    assets     = [ordered]@{
        exe       = New-AssetEntry $pkg.files.exe 'exe'
        installer = New-AssetEntry $pkg.files.installer 'installer'
        portable  = New-AssetEntry $pkg.files.portable 'portable'
    }
}
$json = $manifest | ConvertTo-Json -Depth 6
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)

# CNG instead of ECDsa.ImportFromPem so this also runs on Windows PowerShell 5.1 (CI uses pwsh 7; both work).
$b64 = ($pem -replace '-----[^-]+-----', '' -replace '\s', '')
if ($pem -notmatch 'BEGIN PRIVATE KEY') { throw 'CLINK_FEED_SIGNING_KEY must be a PKCS#8 PEM ("BEGIN PRIVATE KEY").' }
$cngKey = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($b64), [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
$ecdsa = $null
try {
    $ecdsa = [System.Security.Cryptography.ECDsaCng]::new($cngKey)
    if ($ecdsa.KeySize -ne 256) { throw 'Signing key must be ECDSA P-256.' }
    $sig = $ecdsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
} finally { if ($ecdsa) { $ecdsa.Dispose() }; $cngKey.Dispose() }
if ($sig.Length -ne 64) { throw "Unexpected signature length $($sig.Length)." }

$jsonPath = Join-Path $PackageDir "$Channel.json"
[System.IO.File]::WriteAllBytes($jsonPath, $bytes)
[System.IO.File]::WriteAllBytes("$jsonPath.sig", [System.Text.UTF8Encoding]::new($false).GetBytes([Convert]::ToBase64String($sig)))
"Wrote $jsonPath and $jsonPath.sig"
