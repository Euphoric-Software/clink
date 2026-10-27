#requires -Version 7
# Uploads a package (tools/package.ps1 output) and its signed manifest to an S3-compatible bucket (Cloudflare R2), in the
# order that keeps the feed consistent: immutable assets first, stable "latest/" copies next, manifest + signature last.
# Credentials come from env: R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT, R2_BUCKET (and CLINK_FEED_SIGNING_KEY via
# make-manifest.ps1). CLINK_FEED_URL is the public base URL (ends with '/'); CLINK_FEED_KEY_PREFIX is the bucket key prefix
# that URL maps to (default "clink"; use "clink-staging" for a dry run). Optional purge: CLOUDFLARE_API_TOKEN + CLOUDFLARE_ZONE_ID.
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$PackageDir,
    [Parameter(Mandatory)] [string]$NotesUrl
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET', 'CLINK_FEED_URL') {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($n))) { throw "Env var $n is not set." }
}
$feed = $env:CLINK_FEED_URL
if (-not $feed.EndsWith('/')) { $feed += '/' }
$prefix = if ($env:CLINK_FEED_KEY_PREFIX) { $env:CLINK_FEED_KEY_PREFIX.Trim('/') } else { 'clink' }
$tag = "v$Version"
$folder = if ($Channel -eq 'stable') { "releases/$tag" } else { "dev/$tag" }

$env:AWS_ACCESS_KEY_ID = $env:R2_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:R2_SECRET_ACCESS_KEY
$env:AWS_DEFAULT_REGION = 'auto'
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
$env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'

# 1. Manifest (written locally first; uploaded last).
& (Join-Path $PSScriptRoot 'make-manifest.ps1') -Channel $Channel -Version $Version -Tag $tag `
    -BaseUrl "$feed$folder/" -NotesUrl $NotesUrl -PackageDir $PackageDir
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'make-manifest failed' }

function Send-Object([string]$Local, [string]$Key, [string]$CacheControl, [string]$ContentType) {
    & aws s3 cp $Local "s3://$($env:R2_BUCKET)/$Key" --endpoint-url $env:R2_ENDPOINT --cache-control $CacheControl --content-type $ContentType --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Upload failed: $Key" }
    "uploaded $Key"
}
$immutable = 'public, max-age=31536000, immutable'
$short = 'public, max-age=60'
$exe = 'application/vnd.microsoft.portable-executable'

# 2. Immutable versioned assets.
$setup = "Clink-$Version-setup.exe"
$zip = "Clink-$Version-portable.zip"
Send-Object (Join-Path $PackageDir 'Clink.exe') "$prefix/$folder/Clink.exe" $immutable $exe
Send-Object (Join-Path $PackageDir 'Clink.exe.sha256') "$prefix/$folder/Clink.exe.sha256" $immutable 'text/plain'
Send-Object (Join-Path $PackageDir $setup) "$prefix/$folder/$setup" $immutable $exe
Send-Object (Join-Path $PackageDir $zip) "$prefix/$folder/$zip" $immutable 'application/zip'

# 3. Stable only: stable download links for the website.
$purge = @()
if ($Channel -eq 'stable') {
    Send-Object (Join-Path $PackageDir $setup) "$prefix/latest/Clink-setup.exe" $short $exe
    Send-Object (Join-Path $PackageDir $zip) "$prefix/latest/Clink-portable.zip" $short 'application/zip'
    $purge += "${feed}latest/Clink-setup.exe", "${feed}latest/Clink-portable.zip"
}

# 4. Manifest and signature last: clients never see a manifest whose assets are missing.
Send-Object (Join-Path $PackageDir "$Channel.json") "$prefix/$Channel.json" $short 'application/json'
Send-Object (Join-Path $PackageDir "$Channel.json.sig") "$prefix/$Channel.json.sig" $short 'text/plain'
$purge += "$feed$Channel.json", "$feed$Channel.json.sig"

# 5. Optional cache purge.
if ($env:CLOUDFLARE_API_TOKEN -and $env:CLOUDFLARE_ZONE_ID) {
    try {
        $body = @{ files = $purge } | ConvertTo-Json -Compress
        Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$($env:CLOUDFLARE_ZONE_ID)/purge_cache" `
            -Headers @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" } -ContentType 'application/json' -Body $body | Out-Null
        'purged Cloudflare cache'
    } catch { Write-Host "::warning::Cloudflare cache purge failed: $($_.Exception.Message)" }
}

# 6. Dev only: keep the 10 newest dev folders in the bucket.
if ($Channel -eq 'dev') {
    try {
        $listing = & aws s3 ls "s3://$($env:R2_BUCKET)/$prefix/dev/" --endpoint-url $env:R2_ENDPOINT
        if ($LASTEXITCODE -ne 0) { throw 'list failed' }
        $dirs = @($listing | ForEach-Object { if ($_ -match 'PRE (v\d+\.\d+\.\d+-dev\.(\d+))/') { [pscustomobject]@{ Name = $Matches[1]; N = [int]$Matches[2] } } } |
            Sort-Object N -Descending)
        foreach ($d in ($dirs | Select-Object -Skip 10)) {
            "pruning $($d.Name)"
            & aws s3 rm "s3://$($env:R2_BUCKET)/$prefix/dev/$($d.Name)/" --recursive --endpoint-url $env:R2_ENDPOINT --only-show-errors
        }
    } catch { Write-Host "::warning::Pruning old dev folders failed: $($_.Exception.Message)" }
}
