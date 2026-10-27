# Writes <Channel>.json and <Channel>.json.sig (contract v2) into -PackageDir from package.json (see package.ps1).
# The signing key (PKCS#8 PEM, or the PEM file as one-line Base64) comes from env var CLINK_FEED_SIGNING_KEY (removed from the
# environment as soon as it is read).
# The manifest bytes are signed exactly as written (UTF-8, no BOM): ECDSA P-256 / SHA-256, IEEE P1363 r||s, Base64 on one line.
# Before anything is written the signature is verified against the channel's keys in -FeedJson (Clink/Assets/feed.json); a key
# that is not listed there (wrong key, rotated key, empty list) fails the run.
# BaseUrl is the folder URL holding the assets (ends with '/') and must be under feedUrl,
# e.g. https://cdn.example/clink/releases/v0.4.2/
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$BaseUrl,
    [Parameter(Mandatory)] [string]$NotesUrl,
    [Parameter(Mandatory)] [string]$PackageDir,
    [Parameter(Mandatory)] [string]$FeedJson
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'feed-lib.ps1')

$pem = $env:CLINK_FEED_SIGNING_KEY
Remove-Item Env:\CLINK_FEED_SIGNING_KEY -ErrorAction SilentlyContinue

if ($Version -match '^v') { throw 'Version must not start with "v".' }
if ($Tag -ne "v$Version") { throw "Tag '$Tag' must be 'v$Version'." }
if ($Channel -eq 'dev' -and $Version -notmatch '-dev\.\d+$') { throw 'A dev manifest needs a version like X.Y.Z-dev.N.' }
if ($Channel -eq 'stable' -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A stable manifest needs a version like X.Y.Z.' }
foreach ($u in $BaseUrl, $NotesUrl) { if ($u -notmatch '^https://') { throw "URL must be https: $u" } }
if (-not $BaseUrl.EndsWith('/')) { $BaseUrl += '/' }

$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
$FeedJson = (Resolve-Path -LiteralPath $FeedJson).Path
$cfg = Get-FeedConfig -Path $FeedJson -Channel $Channel
if (-not $cfg.Enabled) { throw "The feed is not enabled for the $Channel channel in feed.json ($($cfg.Reason)); refusing to sign." }
if (-not $BaseUrl.StartsWith($cfg.FeedUrl, [System.StringComparison]::Ordinal)) { throw "BaseUrl $BaseUrl is not under feedUrl $($cfg.FeedUrl)." }

if ([string]::IsNullOrWhiteSpace($pem)) { throw 'Env var CLINK_FEED_SIGNING_KEY is empty or not set.' }
if ($pem -notmatch 'BEGIN PRIVATE KEY') {
    # Also accept the whole PEM file as one line of Base64 (easier to store as a secret without newline mangling).
    $decoded = $null
    try { $decoded = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($pem -replace '\s', ''))) } catch { }
    if (-not $decoded -or $decoded -notmatch 'BEGIN PRIVATE KEY') { throw 'CLINK_FEED_SIGNING_KEY must be a PKCS#8 PEM ("BEGIN PRIVATE KEY") or that PEM file Base64-encoded on one line.' }
    $pem = $decoded
}
$pkgFile = Join-Path $PackageDir 'package.json'
if (-not (Test-Path -LiteralPath $pkgFile)) { throw "package.json not found in $PackageDir (run tools/package.ps1 first)." }
$pkg = Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json
if ($pkg.version -ne $Version) { throw "package.json is for version $($pkg.version), not $Version." }

# Update clients parse minRuntime as a version (X.Y.Z); it comes from package.json (read from the framework build's runtimeconfig).
$minRuntime = [string]$pkg.minRuntime
if ($minRuntime -notmatch '^\d+\.\d+\.\d+$') { throw "package.json minRuntime '$minRuntime' is not X.Y.Z (run tools/package.ps1 from this version)." }

# Bind package.json to what the manifest advertises (the publish job also verifies package.json's hash against the build job's output).
# The names are compared case-sensitively and exactly: they become URLs and file paths. Runtime major: the app targets
# net10.0-windows (Clink/Clink.csproj TargetFramework), so minRuntime (the Microsoft.WindowsDesktop.App version) must be 10.x.y; raise it
# together with the TargetFramework.
$expectedRuntimeMajor = 10
foreach ($pair in @(@('exe', 'Clink.exe'), @('exeFramework', 'Clink-framework.exe'), @('installer', "Clink-$Version-setup.exe"))) {
    $entry = $pkg.files.($pair[0])
    if (-not $entry) { throw "package.json has no '$($pair[0])' entry (installer not built?)." }
    if (([string]$entry.file) -cne $pair[1]) { throw "package.json files.$($pair[0]).file is '$($entry.file)', expected '$($pair[1])'." }
}
if (([string]$pkg.files.exe.sha256) -ceq ([string]$pkg.files.exeFramework.sha256)) { throw 'package.json lists the same sha256 for exe and exeFramework: the two flavours must differ.' }
if ([int]($minRuntime.Split('.')[0]) -ne $expectedRuntimeMajor) { throw "package.json minRuntime '$minRuntime' is not a $expectedRuntimeMajor.x.y runtime (the app targets net$expectedRuntimeMajor.0)." }

function New-AssetEntry($info, [string]$what) {
    if (-not $info) { throw "package.json has no '$what' entry (installer not built?)." }
    if ($info.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw "Bad sha256 for $what." }
    $path = Join-Path $PackageDir $info.file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$($info.file) is missing from $PackageDir." }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $info.sha256) { throw "$($info.file) does not match package.json (sha256 differs)." }
    [ordered]@{ url = $BaseUrl + $info.file; size = [int64](Get-Item -LiteralPath $path).Length; sha256 = $info.sha256 }
}
$manifest = [ordered]@{
    schema     = 1
    channel    = $Channel
    version    = $Version
    tag        = $Tag
    published  = [DateTime]::Now.ToUniversalTime().ToString('o', [System.Globalization.CultureInfo]::InvariantCulture)
    notesUrl   = $NotesUrl
    minRuntime = $minRuntime
    assets     = [ordered]@{
        exe          = New-AssetEntry $pkg.files.exe 'exe'
        exeFramework = New-AssetEntry $pkg.files.exeFramework 'exeFramework'
        installer    = New-AssetEntry $pkg.files.installer 'installer'
    }
}
$json = $manifest | ConvertTo-Json -Depth 6
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)

# CNG instead of ECDsa.ImportFromPem so this also runs on Windows PowerShell 5.1 (CI uses pwsh 7; both work).
$b64 = ($pem -replace '-----[^-]+-----', '' -replace '\s', '')
$pem = $null
$cngKey = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($b64), [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
$b64 = $null
$ecdsa = $null
try {
    $ecdsa = [System.Security.Cryptography.ECDsaCng]::new($cngKey)
    if ($ecdsa.KeySize -ne 256) { throw 'Signing key must be ECDSA P-256.' }
    $sig = $ecdsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
} finally { if ($ecdsa) { $ecdsa.Dispose() }; $cngKey.Dispose() }
if ($sig.Length -ne 64) { throw "Unexpected signature length $($sig.Length)." }
$sigText = [Convert]::ToBase64String($sig)

if (-not (Test-FeedSignature -Bytes $bytes -SigBase64 $sigText -Keys $cfg.Keys)) {
    throw "The signature does not verify against the $Channel keys in feed.json: the signing key is not one of them. Nothing was written."
}

$jsonPath = Join-Path $PackageDir "$Channel.json"
[System.IO.File]::WriteAllBytes($jsonPath, $bytes)
[System.IO.File]::WriteAllBytes("$jsonPath.sig", [System.Text.UTF8Encoding]::new($false).GetBytes($sigText))
"Wrote $jsonPath and $jsonPath.sig (signature verified against feed.json)"
