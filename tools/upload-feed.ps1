#requires -Version 7
# Uploads a package (tools/package.ps1 output) and its signed manifest to an S3-compatible bucket (Cloudflare R2), contract v2.
#
# The bucket and the base URL are per channel (docs/RELEASING.md, "Two buckets"): stable writes to the R2_STABLE_* bucket served at
# feedUrl, dev to the R2_DEV_* bucket served at devFeedUrl (or feedUrl when devFeedUrl is empty). The path of that base URL must end in
# "/<prefix>/"; this is checked before anything is written.
#
# Bucket layout under <prefix> (must match ^clink(-staging)?$; CLINK_FEED_KEY_PREFIX, default "clink"):
#   releases/vX.Y.Z/{Clink.exe, Clink.exe.sha256, Clink-framework.exe, Clink-framework.exe.sha256, Clink-Setup.exe}   write-once
#   dev/vX.Y.Z-dev.N/{same}                                                   write-once
#   stable.json(.sig), dev.json(.sig)                                         replaced on every publish
# Order: refuse a downgrade (the bucket's current manifest counts only after its signature verified against feed.json), versioned assets
# (write-once, compared by their real downloaded bytes), GET-and-hash their PUBLIC URLs, manifest, then signature.
# The decisions live in tools/lib/feed-publish-lib.ps1 (testable with a fake aws); this script only wires the real aws CLI and web client.
#
# The manifest and .sig must already be in -PackageDir (tools/make-manifest.ps1, run with the signing key in its own step).
# With -Sign this script runs make-manifest.ps1 itself (needs -NotesUrl and CLINK_FEED_SIGNING_KEY) and drops the key from
# the environment as soon as signing is done.
# -HistoryFloor: the highest published GitHub release of this channel that carried <channel>.json (the workflow computes it; empty = none).
# Env: R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT, R2_BUCKET. Optional purge: CLOUDFLARE_API_TOKEN + CLOUDFLARE_ZONE_ID.
#      Owner-only, temporary: CLINK_FEED_BUCKET_RESET_VERSION (see Resolve-FeedEffectiveFloor).
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$PackageDir,
    [Parameter(Mandatory)] [string]$FeedJson,
    [string]$NotesUrl,
    [string]$HistoryFloor,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'feed-lib.ps1')
. (Join-Path $PSScriptRoot 'lib\feed-publish-lib.ps1')

$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
$FeedJson = (Resolve-Path -LiteralPath $FeedJson).Path
$tag = "v$Version"
$folder = if ($Channel -eq 'stable') { "releases/$tag" } else { "dev/$tag" }

$cfg = Get-FeedConfig -Path $FeedJson -Channel $Channel
if (-not $cfg.Enabled) { throw "The feed is not enabled for the $Channel channel in feed.json ($($cfg.Reason))." }
# The channel's own base: stable uses feedUrl, dev uses devFeedUrl (or feedUrl when that is empty). The bucket this run writes to
# (R2_STABLE_* or R2_DEV_*) is the one served at this base.
$feed = $cfg.FeedUrl

foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($n))) { throw "Env var $n is not set (required because the feed is enabled for $Channel)." }
}
$prefix = if ($env:CLINK_FEED_KEY_PREFIX) { $env:CLINK_FEED_KEY_PREFIX.Trim('/') } else { 'clink' }
if ($prefix -cnotmatch '\Aclink(-staging)?\z') { throw "Key prefix '$prefix' is not allowed (must be 'clink' or 'clink-staging')." }
# Guard before ANY bucket access or manifest work: the key is <prefix>/<folder>/<file> but the public URL is base+<folder>/<file>, so the
# base's path must end in /<prefix>/ (otherwise the mismatch would only show at the public-URL check, after write-once objects were written).
Assert-FeedBaseMatchesPrefix -BaseUrl $feed -Prefix $prefix
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

$setup = 'Clink-Setup.exe'
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

# The real invokers (tools/lib/feed-publish-lib.ps1 holds the decisions; the script tests fake these two).
$awsInvoker = New-FeedAwsInvoker
$httpInvoker = New-FeedHttpInvoker

# 2. Never go backwards: the bucket's current manifest for this channel must not be newer than this version. The manifest is only
#    believed as a floor after its .sig verified against the keys the app trusts (feed.json). The GitHub history floor (the highest
#    published release of the channel that carried the manifest, found by the workflow) is a second floor that an emptied or rolled-back
#    bucket cannot lower; with neither bucket file but a history floor the run is refused unless the owner's reset variable names that
#    exact version (docs/RELEASING.md, "Recovering a stuck feed").
$bucketFloor = Get-FeedBucketFloor -Aws $awsInvoker -Bucket $bucket -Prefix $prefix -Channel $Channel -Endpoint $endpoint -Keys $cfg.Keys
$floor = Resolve-FeedEffectiveFloor -BucketFloor $bucketFloor -HistoryFloor $HistoryFloor -ResetVersion $env:CLINK_FEED_BUCKET_RESET_VERSION -Version $Version -Channel $Channel
if ($floor) { Write-Host "effective $Channel floor: $floor" } else { Write-Host "no $Channel floor (first publish)" }
$immutable = 'public, max-age=31536000, immutable'
$short = 'public, max-age=60'

# 3. Versioned assets are write-once: the object in the bucket is downloaded and hashed; same bytes = skip, different = fail.
#    Conditional create (If-None-Match: *); an aws cli without it fails closed (no fallback). The real write-once guarantee is
#    still the R2 Bucket Lock rule on releases/ (docs/RELEASING.md).
foreach ($a in $assets) {
    [void](Publish-FeedVersionedAsset -Aws $awsInvoker -Bucket $bucket -Endpoint $endpoint -Asset $a -CacheControl $immutable)
}

# 4. The assets must be reachable at their PUBLIC URLs with the right bytes (downloaded and hashed) before the manifest points at them.
foreach ($a in $assets) { Assert-FeedPublicAsset -Http $httpInvoker -Asset $a }
# 5. Manifest, then signature.
foreach ($m in @(@($manifestPath, "$prefix/$Channel.json", 'application/json'), @($sigPath, "$prefix/$Channel.json.sig", 'text/plain'))) {
    $r = Invoke-FeedAws -Aws $awsInvoker -Arguments @('s3', 'cp', $m[0], "s3://$bucket/$($m[1])", '--endpoint-url', $endpoint, '--cache-control', $short, '--content-type', $m[2], '--only-show-errors')
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
    $r = Invoke-FeedAws -Aws $awsInvoker -Arguments @('s3', 'ls', "s3://$bucket/$prefix/dev/", '--endpoint-url', $endpoint)
    if ($r.Code -ne 0) { Write-Host "::warning::Could not list old dev folders: $($r.Text.Trim())" }
    else {
        $dirs = @($r.Text -split "`r?`n" | ForEach-Object { if ($_ -cmatch '\A\s*PRE (v[0-9]+\.[0-9]+\.[0-9]+-dev\.([0-9]+))/\s*\z') { [pscustomobject]@{ Name = $Matches[1]; N = [int]$Matches[2] } } } |
            Where-Object { $_.Name -ne $tag } | Sort-Object N -Descending)
        foreach ($d in ($dirs | Select-Object -Skip 9)) {   # 9 + the one just published = 10
            "pruning $($d.Name)"
            $r = Invoke-FeedAws -Aws $awsInvoker -Arguments @('s3', 'rm', "s3://$bucket/$prefix/dev/$($d.Name)/", '--recursive', '--endpoint-url', $endpoint, '--only-show-errors')
            if ($r.Code -ne 0) { Write-Host "::warning::Pruning $($d.Name) failed: $($r.Text.Trim())" }
        }
    }
}
# Prune failures above are only warnings, but the failed aws calls leave a non-zero $LASTEXITCODE. The runner appends an exit-code check
# to the step, which must not fail a publish that already succeeded (dev.json is live).
$global:LASTEXITCODE = 0
