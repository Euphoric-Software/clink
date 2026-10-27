#requires -Version 7
# Uploads a package (tools/package.ps1 output) and its signed manifest to an S3-compatible bucket (Cloudflare R2), contract v2.
#
# Bucket layout under <prefix> (must match ^clink(-staging)?$; CLINK_FEED_KEY_PREFIX, default "clink"):
#   releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-framework.exe, Clink-framework.exe.sha256, Clink-X.Y.Z-setup.exe}   write-once
#   dev/vX.Y.Z-dev.N/{same}                                                   write-once
#   stable.json(.sig), dev.json(.sig)                                         replaced on every publish
# Order: refuse a downgrade, versioned assets (write-once), HEAD-check their PUBLIC URLs, manifest, then signature.
#
# The manifest and .sig must already be in -PackageDir (tools/make-manifest.ps1, run with the signing key in its own step).
# With -Sign this script runs make-manifest.ps1 itself (needs -NotesUrl and CLINK_FEED_SIGNING_KEY) and drops the key from
# the environment as soon as signing is done.
# Env: R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT, R2_BUCKET. Optional purge: CLOUDFLARE_API_TOKEN + CLOUDFLARE_ZONE_ID.
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$PackageDir,
    [Parameter(Mandatory)] [string]$FeedJson,
    [string]$NotesUrl,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'feed-lib.ps1')

$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
$FeedJson = (Resolve-Path -LiteralPath $FeedJson).Path
$tag = "v$Version"
$folder = if ($Channel -eq 'stable') { "releases/$tag" } else { "dev/$tag" }

$cfg = Get-FeedConfig -Path $FeedJson -Channel $Channel
if (-not $cfg.Enabled) { throw "The feed is not enabled for the $Channel channel in feed.json ($($cfg.Reason))." }
$feed = $cfg.FeedUrl

foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($n))) { throw "Env var $n is not set (required because the feed is enabled for $Channel)." }
}
$prefix = if ($env:CLINK_FEED_KEY_PREFIX) { $env:CLINK_FEED_KEY_PREFIX.Trim('/') } else { 'clink' }
if ($prefix -cnotmatch '^clink(-staging)?$') { throw "Key prefix '$prefix' is not allowed (must be 'clink' or 'clink-staging')." }
$bucket = $env:R2_BUCKET
$endpoint = $env:R2_ENDPOINT

$env:AWS_ACCESS_KEY_ID = $env:R2_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:R2_SECRET_ACCESS_KEY
$env:AWS_DEFAULT_REGION = 'auto'
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
$env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'

# 1. Manifest: make it (-Sign) or take the one already in the package, then verify it against feed.json and the package files.
if ($Sign) {
    if (-not $NotesUrl) { throw '-NotesUrl is required with -Sign.' }
    try {
        & (Join-Path $PSScriptRoot 'make-manifest.ps1') -Channel $Channel -Version $Version -Tag $tag -BaseUrl "$feed$folder/" `
            -NotesUrl $NotesUrl -PackageDir $PackageDir -FeedJson $FeedJson
    } finally { Remove-Item Env:\CLINK_FEED_SIGNING_KEY -ErrorAction SilentlyContinue }
}
$manifestPath = Join-Path $PackageDir "$Channel.json"
$sigPath = "$manifestPath.sig"
foreach ($f in $manifestPath, $sigPath) { if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Missing $f (run tools/make-manifest.ps1 first)." } }
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$sigText = [System.IO.File]::ReadAllText($sigPath).Trim()
if (-not (Test-FeedSignature -Bytes $manifestBytes -SigBase64 $sigText -Keys $cfg.Keys)) { throw "$Channel.json.sig does not verify against the $Channel keys in feed.json." }
$manifest = [System.Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if ($manifest.channel -ne $Channel -or $manifest.version -ne $Version -or $manifest.tag -ne $tag) { throw 'The manifest is for a different channel/version than requested.' }

$setup = "Clink-$Version-setup.exe"
$assets = @(
    [pscustomobject]@{ Name = 'Clink.exe'; Type = 'application/vnd.microsoft.portable-executable' }
    [pscustomobject]@{ Name = 'Clink.exe.sha256'; Type = 'text/plain' }
    [pscustomobject]@{ Name = 'Clink-framework.exe'; Type = 'application/vnd.microsoft.portable-executable' }
    [pscustomobject]@{ Name = 'Clink-framework.exe.sha256'; Type = 'text/plain' }
    [pscustomobject]@{ Name = $setup; Type = 'application/vnd.microsoft.portable-executable' }
)
foreach ($a in $assets) {
    $local = Join-Path $PackageDir $a.Name
    if (-not (Test-Path -LiteralPath $local -PathType Leaf)) { throw "Missing $local." }
    $a | Add-Member -NotePropertyName Local -NotePropertyValue $local
    $a | Add-Member -NotePropertyName Sha256 -NotePropertyValue (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLowerInvariant()
    $a | Add-Member -NotePropertyName Size -NotePropertyValue (Get-Item -LiteralPath $local).Length
    $a | Add-Member -NotePropertyName Key -NotePropertyValue "$prefix/$folder/$($a.Name)"
    $a | Add-Member -NotePropertyName Url -NotePropertyValue "$feed$folder/$($a.Name)"
}
foreach ($pair in @(@('exe', 'Clink.exe'), @('exeFramework', 'Clink-framework.exe'), @('installer', $setup))) {
    $entry = $manifest.assets.($pair[0])
    $a = $assets | Where-Object { $_.Name -eq $pair[1] }
    if ($entry.url -ne $a.Url -or $entry.sha256 -ne $a.Sha256 -or [int64]$entry.size -ne $a.Size) { throw "The manifest entry '$($pair[0])' does not match $($a.Name) in the package." }
}

function Invoke-Aws {
    # Runs aws, returns @{ Code; Text } (stdout+stderr), never throws.
    param([string[]]$Arguments)
    $text = (& aws @Arguments 2>&1 | Out-String)
    @{ Code = $LASTEXITCODE; Text = $text }
}

# 2. Never go backwards: the bucket's current manifest for this channel must not be newer than this version.
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("clink-current-" + [guid]::NewGuid().ToString('N') + '.json')
try {
    $r = Invoke-Aws @('s3api', 'get-object', '--bucket', $bucket, '--key', "$prefix/$Channel.json", '--endpoint-url', $endpoint, $tmp)
    if ($r.Code -eq 0) {
        $current = (Get-Content -Raw -LiteralPath $tmp | ConvertFrom-Json).version
        if ((Compare-FeedVersion $current $Version) -gt 0) { throw "The bucket's $Channel.json is already at $current, newer than $Version; refusing to publish an older version." }
        "current $Channel feed: $current"
    } elseif ($r.Text -match 'NoSuchKey|Not Found|\(404\)') {
        "no current $Channel.json in the bucket"
    } else { throw "Could not read the current $Channel.json: $($r.Text.Trim())" }
} finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

$immutable = 'public, max-age=31536000, immutable'
$short = 'public, max-age=60'

# 3. Versioned assets are write-once: same content (sha256 metadata) = skip, different = fail.
foreach ($a in $assets) {
    $r = Invoke-Aws @('s3api', 'head-object', '--bucket', $bucket, '--key', $a.Key, '--endpoint-url', $endpoint)
    if ($r.Code -eq 0) {
        $meta = ($r.Text | ConvertFrom-Json).Metadata
        $remote = if ($meta) { [string]$meta.sha256 } else { '' }
        if ($remote -eq $a.Sha256) { "exists, same sha256, skipped: $($a.Key)"; continue }
        throw "$($a.Key) already exists with different content (sha256 metadata '$remote' vs '$($a.Sha256)'). Versioned objects are write-once; bump the version."
    }
    if ($r.Text -notmatch 'Not Found|\(404\)|NoSuchKey') { throw "Could not check $($a.Key): $($r.Text.Trim())" }
    # Conditional create (If-None-Match: *) so a concurrent writer cannot be overwritten. Older aws cli versions do not know
    # --if-none-match: then fall back to a plain copy (the head-object check above still applies). The real write-once guarantee
    # is the R2 Bucket Lock rule on releases/ (docs/RELEASING.md).
    $r = Invoke-Aws @('s3api', 'put-object', '--bucket', $bucket, '--key', $a.Key, '--body', $a.Local, '--endpoint-url', $endpoint,
        '--cache-control', $immutable, '--content-type', $a.Type, '--metadata', "sha256=$($a.Sha256)", '--if-none-match', '*')
    if ($r.Code -ne 0 -and $r.Text -match '(?i)(Unknown options?|unrecognized arguments?):.*(if-none-match|\*)') {
        Write-Host '::warning::This aws cli does not support --if-none-match; falling back to an unconditional copy (head-object check only).'
        $r = Invoke-Aws @('s3', 'cp', $a.Local, "s3://$bucket/$($a.Key)", '--endpoint-url', $endpoint, '--cache-control', $immutable,
            '--content-type', $a.Type, '--metadata', "sha256=$($a.Sha256)", '--only-show-errors')
    } elseif ($r.Code -ne 0 -and $r.Text -match 'PreconditionFailed|\(412\)') {
        throw "$($a.Key) was created by someone else between the check and the upload (precondition failed). Versioned objects are write-once; re-run to compare the content."
    }
    if ($r.Code -ne 0) { throw "Upload failed: $($a.Key): $($r.Text.Trim())" }
    "uploaded $($a.Key)"
}

# 4. The assets must be reachable at their PUBLIC URLs with the right size before the manifest points at them.
foreach ($a in $assets) {
    $ok = $false; $last = ''
    for ($i = 1; $i -le 5 -and -not $ok; $i++) {
        try {
            $resp = Invoke-WebRequest -Method Head -Uri $a.Url -MaximumRedirection 0 -UseBasicParsing
            $len = [int64]($resp.Headers['Content-Length'] | Select-Object -First 1)
            if ($resp.StatusCode -eq 200 -and $len -eq $a.Size) { $ok = $true } else { $last = "status $($resp.StatusCode), Content-Length $len (expected $($a.Size))" }
        } catch { $last = $_.Exception.Message }
        if (-not $ok -and $i -lt 5) { Start-Sleep -Seconds 4 }
    }
    if (-not $ok) { throw "Public URL check failed for $($a.Url): $last" }
    "public URL ok: $($a.Url)"
}

# 5. Manifest, then signature.
foreach ($m in @(@($manifestPath, "$prefix/$Channel.json", 'application/json'), @($sigPath, "$prefix/$Channel.json.sig", 'text/plain'))) {
    $r = Invoke-Aws @('s3', 'cp', $m[0], "s3://$bucket/$($m[1])", '--endpoint-url', $endpoint, '--cache-control', $short, '--content-type', $m[2], '--only-show-errors')
    if ($r.Code -ne 0) { throw "Upload failed: $($m[1]): $($r.Text.Trim())" }
    "uploaded $($m[1])"
}

# 6. Optional cache purge of the two manifest URLs (non-fatal).
if ($env:CLOUDFLARE_API_TOKEN -and $env:CLOUDFLARE_ZONE_ID) {
    try {
        $body = @{ files = @("$feed$Channel.json", "$feed$Channel.json.sig") } | ConvertTo-Json -Compress
        Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$($env:CLOUDFLARE_ZONE_ID)/purge_cache" `
            -Headers @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" } -ContentType 'application/json' -Body $body | Out-Null
        'purged Cloudflare cache'
    } catch { Write-Host "::warning::Cloudflare cache purge failed: $($_.Exception.Message)" }
}

# 7. Dev only: keep the 10 newest dev folders, deleting strictly under <prefix>/dev/<vX.Y.Z-dev.N>/.
if ($Channel -eq 'dev') {
    $r = Invoke-Aws @('s3', 'ls', "s3://$bucket/$prefix/dev/", '--endpoint-url', $endpoint)
    if ($r.Code -ne 0) { Write-Host "::warning::Could not list old dev folders: $($r.Text.Trim())" }
    else {
        $dirs = @($r.Text -split "`r?`n" | ForEach-Object { if ($_ -match '^\s*PRE (v\d+\.\d+\.\d+-dev\.(\d+))/\s*$') { [pscustomobject]@{ Name = $Matches[1]; N = [int]$Matches[2] } } } |
            Where-Object { $_.Name -ne $tag } | Sort-Object N -Descending)
        foreach ($d in ($dirs | Select-Object -Skip 9)) {   # 9 + the one just published = 10
            "pruning $($d.Name)"
            $r = Invoke-Aws @('s3', 'rm', "s3://$bucket/$prefix/dev/$($d.Name)/", '--recursive', '--endpoint-url', $endpoint, '--only-show-errors')
            if ($r.Code -ne 0) { Write-Host "::warning::Pruning $($d.Name) failed: $($r.Text.Trim())" }
        }
    }
}
