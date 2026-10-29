# Decision logic of tools/upload-feed.ps1 (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7, no modules.
# Every function that talks to the bucket takes an injectable invoker so the script tests can fake S3/R2:
#   -Aws  [scriptblock] param([string[]]$Arguments) -> @{ Code = <int exit code>; Text = <stdout+stderr> }   (New-FeedAwsInvoker builds the real one)
#   -Http [scriptblock] param([string]$Url, [string]$OutFile) -> @{ Status = <int> }, writes the response body to $OutFile (throws on a transport error)
#                                                                  (New-FeedHttpInvoker builds the real one)
# Trust model: nothing the bucket SAYS about an object (user metadata, size headers, ETag) is believed. Objects are downloaded
# and hashed. The bucket's current manifest is only used as the downgrade floor after its signature verified against the same
# keys the app embeds (Clink/Assets/feed.json). The GitHub release history gives a second floor that an emptied or rolled-back
# bucket cannot lower. Everything fails closed. No security decision here depends on a variable that could be unset: patterns are
# inline literals.

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'feed-lib.ps1')

$script:FeedRecoveryHint = "or a previous publish was interrupted between the manifest and its .sig; see RELEASING.md 'Recovering a stuck feed'"

function New-FeedAwsInvoker {
    # The real aws invoker. Runs `aws` and returns @{ Code; Text } (stdout+stderr as text). It throws only when the aws executable
    # cannot be started at all; a non-zero exit code is an answer, not an exception.
    {
        param([string[]]$Arguments)
        $ErrorActionPreference = 'Continue'              # native stderr must not become a terminating error under the caller's 'Stop'
        $PSNativeCommandUseErrorActionPreference = $false
        $lines = @(& aws @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
        @{ Code = $code; Text = ($lines -join "`n") }
    }
}

function New-FeedHttpInvoker {
    # The real public-URL fetch: GET without following redirects, the body goes to $OutFile. A non-2xx status or a transport error
    # throws (and counts as a failed attempt). 600 s per request; works on Windows PowerShell 5.1 and PowerShell 7.
    {
        param([string]$Url, [string]$OutFile)
        $ProgressPreference = 'SilentlyContinue'
        $resp = Invoke-WebRequest -Method Get -Uri $Url -OutFile $OutFile -PassThru -MaximumRedirection 0 -UseBasicParsing -TimeoutSec 600
        # Response headers as a table with LOWER-CASE names and one string per header (Windows PowerShell 5.1 and pwsh 7 shape them differently).
        $headers = @{}
        if ($resp.Headers) {
            foreach ($k in @($resp.Headers.Keys)) { $headers[([string]$k).ToLowerInvariant()] = (@($resp.Headers[$k]) -join ', ') }
        }
        @{ Status = [int]$resp.StatusCode; Headers = $headers }
    }
}

function Invoke-FeedAws {
    # Calls the invoker and normalises its answer to a single @{ Code; Text }.
    param([Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string[]]$Arguments)
    $answer = @(& $Aws -Arguments $Arguments) | Select-Object -Last 1
    if ($null -eq $answer -or $null -eq $answer.Code) { throw 'The aws invoker returned no result.' }
    @{ Code = [int]$answer.Code; Text = [string]$answer.Text }
}

function Get-FeedKeyPrefix {
    # The bucket key prefix of a run: CLINK_FEED_KEY_PREFIX (slashes trimmed), default "clink"; only "clink" and "clink-staging" are
    # allowed. One function for the workflows' early check ("Read feed configuration") and upload-feed.ps1, so they cannot differ.
    param([AllowNull()] [AllowEmptyString()] [string]$Value)
    $prefix = if ($Value) { $Value.Trim('/') } else { 'clink' }
    if ($prefix -cnotmatch '\Aclink(-staging)?\z') { throw "Key prefix '$prefix' is not allowed (must be 'clink' or 'clink-staging')." }
    $prefix
}

function Assert-FeedBaseMatchesPrefix {
    # upload-feed writes the key <Prefix>/<folder>/<file> but checks the public URL <BaseUrl><folder>/<file> (and the manifests'
    # URLs are built the same way), so the path of the channel's base URL must be EXACTLY "/<Prefix>/" (the host is mapped to the
    # bucket root; a longer path such as "/some/clink/" would need the keys "some/clink/..." and would not match). A mismatch would
    # otherwise show up only at the public-URL check, after the write-once objects were already written.
    # Called twice: by the workflows' "Read feed configuration" step (before anything is signed or drafted) and first thing in
    # upload-feed.ps1 (before the first bucket access). Case-sensitive. BaseUrl is the channel's base from Get-FeedConfig (validated, ends with '/').
    param([Parameter(Mandatory)] [string]$BaseUrl, [Parameter(Mandatory)] [string]$Prefix)
    if ($Prefix -cnotmatch '\A[A-Za-z0-9-]+\z') { throw "Key prefix '$Prefix' is not a plain name." }
    $m = [regex]::Match($BaseUrl, '\Ahttps://[^/?#@\\\s]+(?<path>/[^?#@\\\s]*)?\z')
    if (-not $m.Success) { throw "Feed base URL '$BaseUrl' is not an https URL with a host." }
    $path = if ($m.Groups['path'].Success) { $m.Groups['path'].Value } else { '/' }
    if ($path -cne "/$Prefix/") {
        throw "The path of the feed base URL '$BaseUrl' is '$path', which is not '/$Prefix/'. Objects are written under the key prefix '$Prefix' but read back at the public URL, so the feedUrl/devFeedUrl path must be exactly '/$Prefix/' on a host mapped to the bucket root (docs/RELEASING.md). Nothing was signed or uploaded by this step."
    }
}

function Get-FeedFileFacts {
    # SHA-256 (lower case hex) and size of the bytes that are really in the file.
    param([Parameter(Mandatory)] [string]$Path)
    [pscustomobject]@{
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        Size   = [int64](New-Object System.IO.FileInfo $Path).Length
    }
}

function Get-FeedRemoteObject {
    # GETs <Key> to a scratch file and hashes it. Found = $false only for a clear "no such key"; any other error throws.
    # Bytes is filled only with -ReadBytes (manifests are small; the exes are not read into memory); a file over MaxReadBytes
    # (1 MB) is refused BEFORE it is read.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Key,
        [Parameter(Mandatory)] [string]$Endpoint, [string]$ScratchDir = [System.IO.Path]::GetTempPath(), [switch]$ReadBytes,
        [int64]$MaxReadBytes = 1048576
    )
    $tmp = Join-Path $ScratchDir ('clink-feed-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $r = Invoke-FeedAws -Aws $Aws -Arguments @('s3api', 'get-object', '--bucket', $Bucket, '--key', $Key, '--endpoint-url', $Endpoint, $tmp)
        if ($r.Code -eq 0) {
            if (-not (Test-Path -LiteralPath $tmp -PathType Leaf)) { throw "aws reported success for $Key but wrote no file." }
            $facts = Get-FeedFileFacts -Path $tmp
            if ($ReadBytes -and $facts.Size -gt $MaxReadBytes) { throw "$Key in the bucket is $($facts.Size) bytes, larger than the $MaxReadBytes byte limit for a manifest or signature; refusing to read it." }
            $bytes = if ($ReadBytes) { [System.IO.File]::ReadAllBytes($tmp) } else { $null }
            return [pscustomobject]@{ Found = $true; Sha256 = $facts.Sha256; Size = $facts.Size; Bytes = $bytes }
        }
        # Inline pattern on purpose: a variable that is unset would make -match fail open here.
        if ($r.Text -match 'NoSuchKey|Not Found|\(404\)') { return [pscustomobject]@{ Found = $false; Sha256 = ''; Size = [int64]0; Bytes = $null } }
        throw "Could not read ${Key}: $($r.Text.Trim())"
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

function Get-FeedBucketFloor {
    # NEW-4. Returns the version of the bucket's current <Channel>.json to use as the downgrade floor, or $null for the very first
    # publish (no manifest and no signature in the bucket). The manifest's signature (<Channel>.json.sig) must verify under Keys
    # (the channel's keys of Clink/Assets/feed.json, the ones the app trusts); a missing or invalid signature, a manifest for another
    # channel or with an unreadable version throws, so a planted or tampered manifest can neither raise nor lower the floor.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Prefix,
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel, [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Keys, [string]$ScratchDir = [System.IO.Path]::GetTempPath()
    )
    $manifestKey = "$Prefix/$Channel.json"
    $manifest = Get-FeedRemoteObject -Aws $Aws -Bucket $Bucket -Key $manifestKey -Endpoint $Endpoint -ScratchDir $ScratchDir -ReadBytes
    $sig = Get-FeedRemoteObject -Aws $Aws -Bucket $Bucket -Key "$manifestKey.sig" -Endpoint $Endpoint -ScratchDir $ScratchDir -ReadBytes
    if (-not $manifest.Found) {
        if ($sig.Found) { throw "The bucket has $manifestKey.sig but no $manifestKey; refusing to publish over an inconsistent feed. Investigate before publishing ($script:FeedRecoveryHint)." }
        Write-Host "no current $Channel.json in the bucket (first publish for this channel, unless the GitHub history says otherwise)"
        return $null
    }
    if (-not $sig.Found) { throw "The bucket's $manifestKey has no $manifestKey.sig; refusing to use it as the downgrade floor. Investigate the bucket before publishing ($script:FeedRecoveryHint)." }
    $sigText = [System.Text.Encoding]::ASCII.GetString($sig.Bytes).Trim()
    if (-not (Test-FeedSignature -Bytes $manifest.Bytes -SigBase64 $sigText -Keys $Keys)) {
        throw "The bucket's $manifestKey does not verify against the $Channel keys in feed.json (planted, tampered, signed by a key that was removed from feed.json, $script:FeedRecoveryHint). Refusing to use it as the downgrade floor."
    }
    $parsed = [System.Text.Encoding]::UTF8.GetString($manifest.Bytes) | ConvertFrom-Json
    if ($parsed.channel -cne $Channel) { throw "The bucket's $manifestKey is signed but names channel '$($parsed.channel)'." }
    $version = [string]$parsed.version
    [void](Get-VersionKey $version)   # throws on anything that is not X.Y.Z or X.Y.Z-dev.N
    Write-Host "current $Channel feed: $version (signature verified)"
    $version
}

function Get-FeedHistoryFloor {
    # Security M1. The highest earlier NON-DRAFT release of the channel that carried <Channel>.json, from the GitHub release listing,
    # or $null when there is none. Listing: one line per release, tab separated: tag name, draft flag (true|false), asset names
    # joined with commas, author login (gh api ... --jq '.[] | [.tag_name, (.draft | tostring), ([.assets[].name] | join(",")),
    # (.author.login | tostring)] | @tsv').
    # Drafts are ignored (a failed run leaves one; it never reached users). L-4: only releases whose author is exactly github-actions[bot]
    # (the release workflows' token) count; a release a person created by hand is no evidence of what the feed shipped. A release that
    # carried the manifest but whose tag is not a feed version is an error, never silently skipped.
    param(
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [AllowNull()] [AllowEmptyCollection()] [string[]]$Listing = @()
    )
    $best = $null
    foreach ($raw in @($Listing)) {
        $line = ([string]$raw).TrimEnd("`r", "`n")
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split("`t")
        if ($parts.Count -ne 4) { throw "Unexpected release listing line (expected tag, draft flag, asset names and author login separated by tabs): '$line'" }
        if ($parts[1] -ceq 'true') { continue }
        if ($parts[1] -cne 'false') { throw "Unexpected draft flag '$($parts[1])' in the release listing." }
        if ($parts[3] -cne 'github-actions[bot]') { continue }
        if (-not ($parts[2].Split(',') -ccontains "$Channel.json")) { continue }
        $tag = $parts[0]
        if ($tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?\z') { throw "Release '$tag' carried $Channel.json but its tag is not a feed version (vX.Y.Z or vX.Y.Z-dev.N); investigate before publishing." }
        $candidate = $tag.Substring(1)
        if ($null -eq $best -or (Compare-FeedVersion $candidate $best) -gt 0) { $best = $candidate }
    }
    $best
}

function Resolve-FeedEffectiveFloor {
    # Security M1. effective floor = max(verified bucket floor, GitHub history floor).
    #  - The bucket has neither file ($BucketFloor is $null) but a published release carried the manifest: REFUSED, unless the owner set
    #    CLINK_FEED_BUCKET_RESET_VERSION (ResetVersion) to exactly that release's version. Owner-only, temporary, reviewed.
    #  - Version must not be below the bucket floor (equal re-publishes) and must be strictly above the history floor.
    # Returns the effective floor version or $null.
    param(
        [AllowNull()] [AllowEmptyString()] [string]$BucketFloor, [AllowNull()] [AllowEmptyString()] [string]$HistoryFloor,
        [AllowNull()] [AllowEmptyString()] [string]$ResetVersion, [Parameter(Mandatory)] [string]$Version, [Parameter(Mandatory)] [string]$Channel
    )
    [void](Get-VersionKey $Version)
    $bucket = [string]$BucketFloor
    $history = [string]$HistoryFloor
    $reset = ([string]$ResetVersion).Trim()
    if ($bucket) { [void](Get-VersionKey $bucket) }
    if ($history) { [void](Get-VersionKey $history) }
    if ($reset) { [void](Get-VersionKey $reset) }
    if (-not $bucket -and $history) {
        if (-not $reset -or $reset -cne $history) {
            throw "The bucket has no $Channel.json and no $Channel.json.sig, but the published GitHub release v$history carried $Channel.json, so the feed was on. Refusing to treat this as a first publish (an emptied or replaced bucket is a replay target); $script:FeedRecoveryHint. If the bucket was reset on purpose, the owner sets the repository variable CLINK_FEED_BUCKET_RESET_VERSION (Settings > Secrets and variables > Actions > Variables) to $history for this one publish and removes it afterwards."
        }
        Write-Host "::warning::CLINK_FEED_BUCKET_RESET_VERSION=$reset accepted: the bucket has no $Channel.json, floor taken from the GitHub history. Remove the variable after this publish."
    } elseif ($reset) {
        Write-Host '::warning::CLINK_FEED_BUCKET_RESET_VERSION is set but not needed for this publish; remove it.'
    }
    $effective = if (-not $bucket) { $history } elseif (-not $history) { $bucket } elseif ((Compare-FeedVersion $history $bucket) -gt 0) { $history } else { $bucket }
    if ($bucket -and $history -and (Compare-FeedVersion $history $bucket) -gt 0) {
        Write-Host "::warning::The bucket's $Channel.json ($bucket) is older than the published release v$history; using v$history as the floor."
    }
    if ($bucket) { Assert-FeedNoDowngrade -Floor $bucket -Version $Version -Channel $Channel }
    if ($history -and (Compare-FeedVersion $history $Version) -ge 0) {
        throw "The GitHub release history shows v$history already carried $Channel.json; $Version must be newer than that. Refusing to publish."
    }
    if ($effective) { $effective } else { $null }
}

function Assert-FeedNoDowngrade {
    param([AllowNull()] [AllowEmptyString()] [string]$Floor, [Parameter(Mandatory)] [string]$Version, [Parameter(Mandatory)] [string]$Channel)
    if ($Floor -and (Compare-FeedVersion $Floor $Version) -gt 0) { throw "The bucket's $Channel.json is already at $Floor, newer than $Version; refusing to publish an older version." }
}

function Publish-FeedVersionedAsset {
    # Versioned objects are write-once. Existing object: download it and compare the REAL sha256 and size (never the metadata or
    # a size header): equal = skipped, different = throws. Missing: conditional create (If-None-Match: *). There is NO fallback for an
    # aws cli that lacks --if-none-match: that fails closed and logs `aws --version`. Returns 'skipped' or 'uploaded'.
    # Asset: Key, Local, Sha256, Size, Type.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] $Asset, [Parameter(Mandatory)] [string]$CacheControl, [string]$ScratchDir = [System.IO.Path]::GetTempPath()
    )
    $state = Get-FeedVersionedAssetState -Aws $Aws -Bucket $Bucket -Endpoint $Endpoint -Asset $Asset -ScratchDir $ScratchDir
    if ($state -ceq 'same') { return 'skipped' }
    Send-FeedVersionedAsset -Aws $Aws -Bucket $Bucket -Endpoint $Endpoint -Asset $Asset -CacheControl $CacheControl
}

function Get-FeedVersionedAssetState {
    # The read-only half of Publish-FeedVersionedAsset: 'missing' (no such key), 'same' (the object's REAL downloaded bytes have the
    # expected sha256 and size; logged as skipped) or THROWS "already exists with different content". Writes nothing.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] $Asset, [string]$ScratchDir = [System.IO.Path]::GetTempPath()
    )
    $remote = Get-FeedRemoteObject -Aws $Aws -Bucket $Bucket -Key $Asset.Key -Endpoint $Endpoint -ScratchDir $ScratchDir
    if (-not $remote.Found) { return 'missing' }
    if ($remote.Sha256 -eq $Asset.Sha256 -and $remote.Size -eq [int64]$Asset.Size) { Write-Host "exists, same bytes (sha256 $($remote.Sha256)), skipped: $($Asset.Key)"; return 'same' }
    throw "$($Asset.Key) already exists with different content (actual sha256 $($remote.Sha256), $($remote.Size) bytes; expected $($Asset.Sha256), $($Asset.Size) bytes). Versioned objects are write-once: publish a newer version."
}

function Publish-FeedVersionedAssets {
    # L-5. All versioned objects of a release (binaries, .sha256 files, notes.txt) in two passes: FIRST every key is checked
    # (Get-FeedVersionedAssetState: missing / same bytes / different = throw), and only when NONE conflicts are the missing ones written
    # (conditional create, Send-FeedVersionedAsset). A conflict on any asset, the last one included, therefore leaves the bucket untouched.
    # Returns one 'skipped' / 'uploaded' per asset, in order.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] [object[]]$Assets, [Parameter(Mandatory)] [string]$CacheControl, [string]$ScratchDir = [System.IO.Path]::GetTempPath()
    )
    $states = @(foreach ($a in $Assets) { Get-FeedVersionedAssetState -Aws $Aws -Bucket $Bucket -Endpoint $Endpoint -Asset $a -ScratchDir $ScratchDir })
    if ($states.Count -ne $Assets.Count) { throw 'The versioned-asset preflight returned no state for every asset.' }
    Write-Host "preflight ok: $(@($states | Where-Object { $_ -ceq 'missing' }).Count) to upload, $(@($states | Where-Object { $_ -ceq 'same' }).Count) already there with the same bytes"
    for ($i = 0; $i -lt $Assets.Count; $i++) {
        if ($states[$i] -ceq 'same') { 'skipped' }
        else { Send-FeedVersionedAsset -Aws $Aws -Bucket $Bucket -Endpoint $Endpoint -Asset $Assets[$i] -CacheControl $CacheControl }
    }
}

function Send-FeedVersionedAsset {
    # The write half: conditional create (If-None-Match: *) of one versioned object that the caller found missing. Returns 'uploaded'.
    param(
        [Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string]$Bucket, [Parameter(Mandatory)] [string]$Endpoint,
        [Parameter(Mandatory)] $Asset, [Parameter(Mandatory)] [string]$CacheControl
    )
    $r = Invoke-FeedAws -Aws $Aws -Arguments @('s3api', 'put-object', '--bucket', $Bucket, '--key', $Asset.Key, '--body', $Asset.Local, '--endpoint-url', $Endpoint,
        '--cache-control', $CacheControl, '--content-type', $Asset.Type, '--metadata', "sha256=$($Asset.Sha256)", '--if-none-match', '*')
    if ($r.Code -ne 0) {
        if ($r.Text -match 'PreconditionFailed|\(412\)') {
            throw "$($Asset.Key) was created by someone else between the check and the upload (precondition failed). Versioned objects are write-once; investigate the bucket (who else writes to it?) and publish a newer version."
        }
        if ($r.Text -match '(?i)(unknown options?|unrecognized arguments?|invalid choice|usage:)' -and $r.Text -match '(?i)if-none-match|\*') {
            $v = Invoke-FeedAws -Aws $Aws -Arguments @('--version')
            Write-Host "aws --version: $($v.Text.Trim())"
            throw "This aws cli does not support --if-none-match; refusing to upload $($Asset.Key) without a conditional create (no fallback). Use a current AWS CLI v2."
        }
        throw "Upload failed: $($Asset.Key): $($r.Text.Trim())"
    }
    Write-Host "uploaded $($Asset.Key)"
    'uploaded'
}

function Assert-FeedPublicAsset {
    # The asset must be served at its PUBLIC url with status 200 and the expected bytes: the body is downloaded and hashed (no size
    # header is believed). Retries for propagation. Asset: Url, Sha256, Size (and Name).
    # notes.txt (Name 'notes.txt') is also checked for HOW it is served, from the headers the invoker returns (Headers, lower-case names):
    #   Content-Type must be exactly text/plain; charset=utf-8 (R2 stores what the upload set, so this is reliable: a failure, retried);
    #   X-Content-Type-Options must be nosniff. That one comes from a Cloudflare Transform Rule the owner adds (docs/RELEASING.md, setup
    #   step 1.7), not from R2, so a missing header is a ::warning:: and not a failure (it cannot be relied on before the rule exists).
    #   -RequireNoSniff turns that warning into a failure.
    param(
        [Parameter(Mandatory)] [scriptblock]$Http, [Parameter(Mandatory)] $Asset,
        [int]$Attempts = 5, [int]$DelaySeconds = 4, [string]$ScratchDir = [System.IO.Path]::GetTempPath(), [switch]$RequireNoSniff
    )
    $last = ''
    $isNotes = ([string]$Asset.Name -ceq 'notes.txt')
    for ($i = 1; $i -le $Attempts; $i++) {
        $tmp = Join-Path $ScratchDir ('clink-feed-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $resp = @(& $Http -Url $Asset.Url -OutFile $tmp) | Select-Object -Last 1
            $status = if ($resp -and $null -ne $resp.Status) { [int]$resp.Status } else { 0 }
            if ($status -ne 200) { $last = "status $status" }
            elseif (-not (Test-Path -LiteralPath $tmp -PathType Leaf)) { $last = 'no body was received' }
            else {
                $facts = Get-FeedFileFacts -Path $tmp
                if ($facts.Sha256 -eq $Asset.Sha256 -and $facts.Size -eq [int64]$Asset.Size) {
                    $problem = ''
                    if ($isNotes) {
                        $headers = @{}
                        if ($resp.Headers) { foreach ($k in @($resp.Headers.Keys)) { $headers[([string]$k).ToLowerInvariant()] = (@($resp.Headers[$k]) -join ', ') } }
                        $type = ([string]$headers['content-type']).Trim()
                        $sniff = ([string]$headers['x-content-type-options']).Trim()
                        if (-not ($type -ieq 'text/plain; charset=utf-8')) { $problem = "served with Content-Type '$type', expected 'text/plain; charset=utf-8'" }
                        elseif (-not ($sniff -ieq 'nosniff')) {
                            if ($RequireNoSniff) { $problem = "served without 'X-Content-Type-Options: nosniff' (got '$sniff')" }
                            else { Write-Host "::warning::$($Asset.Url) is served without 'X-Content-Type-Options: nosniff' (got '$sniff'). Add the Cloudflare Transform Rule (Modify Response Header) for both download hosts (docs/RELEASING.md, setup step 1.7)." }
                        }
                    }
                    if (-not $problem) { Write-Host "public URL ok: $($Asset.Url)"; return }
                    $last = $problem
                } else {
                    $last = "served bytes differ: sha256 $($facts.Sha256), $($facts.Size) bytes (expected $($Asset.Sha256), $($Asset.Size) bytes)"
                }
            }
        } catch { $last = $_.Exception.Message }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        if ($i -lt $Attempts -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }
    throw "Public URL check failed for $($Asset.Url): $last"
}

function Assert-FeedPublishedManifest {
    # Reads <BaseUrl><Channel>.json and <BaseUrl><Channel>.json.sig back over the PUBLIC host (through the same Http invoker as
    # Assert-FeedPublicAsset) and requires, for the pair together:
    #   - status 200 and bytes EXACTLY equal to the local files (ManifestPath, SigPath);
    #   - the served signature verifies the served manifest under Keys (the channel's keys of feed.json);
    #   - Content-Type application/json (.json) / text/plain (.sig), each with an optional "; charset=..." (the upload sets the bare type);
    #   - Cache-Control contains max-age=60 (the upload sets "public, max-age=60"; max-age=600 does not count);
    #   - X-Content-Type-Options nosniff: like notes.txt that header comes from a Cloudflare Transform Rule, so a missing one is a
    #     ::warning::, and -RequireNoSniff makes it a failure.
    # Retries (propagation, transient errors) like Assert-FeedPublicAsset; throws when the last attempt still fails.
    param(
        [Parameter(Mandatory)] [scriptblock]$Http, [Parameter(Mandatory)] [string]$BaseUrl, [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [Parameter(Mandatory)] [string]$ManifestPath, [Parameter(Mandatory)] [string]$SigPath, [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Keys,
        [int]$Attempts = 5, [int]$DelaySeconds = 4, [string]$ScratchDir = [System.IO.Path]::GetTempPath(), [switch]$RequireNoSniff
    )
    $targets = @(
        [pscustomobject]@{ Url = "$BaseUrl$Channel.json"; Local = $ManifestPath; Type = 'application/json' }
        [pscustomobject]@{ Url = "$BaseUrl$Channel.json.sig"; Local = $SigPath; Type = 'text/plain' }
    )
    $last = ''
    for ($i = 1; $i -le $Attempts; $i++) {
        $problem = ''
        $served = @{}
        $warnings = New-Object System.Collections.ArrayList
        foreach ($t in $targets) {
            $tmp = Join-Path $ScratchDir ('clink-feed-' + [guid]::NewGuid().ToString('N') + '.tmp')
            try {
                $resp = @(& $Http -Url $t.Url -OutFile $tmp) | Select-Object -Last 1
                $status = if ($resp -and $null -ne $resp.Status) { [int]$resp.Status } else { 0 }
                if ($status -ne 200) { $problem = "$($t.Url): status $status" }
                elseif (-not (Test-Path -LiteralPath $tmp -PathType Leaf)) { $problem = "$($t.Url): no body was received" }
                else {
                    $body = [System.IO.File]::ReadAllBytes($tmp)
                    $expected = [System.IO.File]::ReadAllBytes($t.Local)
                    $headers = @{}
                    if ($resp.Headers) { foreach ($k in @($resp.Headers.Keys)) { $headers[([string]$k).ToLowerInvariant()] = (@($resp.Headers[$k]) -join ', ') } }
                    $type = ([string]$headers['content-type']).Trim()
                    $cache = ([string]$headers['cache-control']).Trim()
                    $sniff = ([string]$headers['x-content-type-options']).Trim()
                    if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$body, [byte[]]$expected)) { $problem = "$($t.Url): served bytes differ from the local file ($($body.Length) bytes served, $($expected.Length) expected)" }
                    elseif ($type -cnotmatch ('(?i)\A' + [regex]::Escape($t.Type) + '(\s*;\s*charset=[A-Za-z0-9._-]+)?\z')) { $problem = "$($t.Url): served with Content-Type '$type', expected '$($t.Type)'" }
                    elseif ($cache -notmatch '(?i)(\A|[\s,])max-age=60(\s*,|\s*\z)') { $problem = "$($t.Url): served with Cache-Control '$cache', expected it to contain max-age=60" }
                    elseif (-not ($sniff -ieq 'nosniff')) {
                        if ($RequireNoSniff) { $problem = "$($t.Url): served without 'X-Content-Type-Options: nosniff' (got '$sniff')" }
                        else { [void]$warnings.Add("::warning::$($t.Url) is served without 'X-Content-Type-Options: nosniff' (got '$sniff'). Add the Cloudflare Transform Rule (Modify Response Header) for both download hosts (docs/RELEASING.md, setup step 1.7).") }
                    }
                    if (-not $problem) { $served[$t.Type] = $body }
                }
            } catch { $problem = "$($t.Url): $($_.Exception.Message)" }
            finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
            if ($problem) { break }
        }
        if (-not $problem) {
            $sigText = [System.Text.Encoding]::UTF8.GetString($served['text/plain']).Trim()
            if (-not (Test-FeedSignature -Bytes ([byte[]]$served['application/json']) -SigBase64 $sigText -Keys $Keys)) { $problem = "the served $Channel.json.sig does not verify the served $Channel.json against the $Channel keys in feed.json" }
        }
        if (-not $problem) {
            foreach ($w in $warnings) { Write-Host $w }
            Write-Host "live manifest ok: $BaseUrl$Channel.json (+ .sig)"
            return
        }
        $last = $problem
        if ($i -lt $Attempts -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }
    throw "Live manifest check failed for $BaseUrl$Channel.json: $last"
}

function Assert-FeedFloorSeed {
    # M1. A version that becomes an MSBuild property value (-p:ClinkMinStable=<v> / -p:ClinkMinDev=<v>): X.Y.Z or X.Y.Z-dev.N and NOTHING
    # else (a ';' would add a second MSBuild property, a newline or a quote would break the command line). '' means "no seed" and passes.
    # Stable seeds are X.Y.Z only. Returns the value. Anchored with \A..\z (not ^..$, which lets a trailing newline through).
    param([AllowNull()] [AllowEmptyString()] [string]$Value, [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel)
    $v = [string]$Value
    if ($v.Length -eq 0) { return '' }
    if ($v -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?\z') { throw "The $Channel floor seed is not a version (X.Y.Z or X.Y.Z-dev.N); refusing to pass it to the build." }
    if ($Channel -ceq 'stable' -and $v.Contains('-dev.')) { throw 'The stable floor seed is a dev version; refusing to pass it to the build.' }
    $v
}

function Get-FeedFloorSeeds {
    # M1. The embedded floor seeds of a build: the newest PUBLISHED stable and the newest PUBLISHED dev release of the repository, found with
    # Get-FeedHistoryFloor on the release listing (same tab-separated format). Each is validated (Assert-FeedFloorSeed); '' when there is none
    # (the first release, the build.yml dry run, a local build: no seed). Returns @{ Stable; Dev }.
    param([AllowNull()] [AllowEmptyCollection()] [string[]]$Listing = @())
    $stable = Get-FeedHistoryFloor -Channel stable -Listing $Listing
    $dev = Get-FeedHistoryFloor -Channel dev -Listing $Listing
    @{ Stable = (Assert-FeedFloorSeed -Value $stable -Channel stable); Dev = (Assert-FeedFloorSeed -Value $dev -Channel dev) }
}

function Assert-FeedSeedBelowVersion {
    # The seed of the channel being built must be STRICTLY lower than the version being built (Compare-FeedVersion). An equal or higher seed
    # means the binary would treat its own version as a downgrade (a stale tag, a hand-made release, a wrong <Version>): fail with a clear
    # message before anything is built. '' and $null mean "no seed" and pass.
    param(
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [AllowNull()] [AllowEmptyString()] [string]$Seed, [Parameter(Mandatory)] [string]$Version
    )
    $s = [string]$Seed
    if ($s.Length -eq 0) { return }
    if ((Compare-FeedVersion $s $Version) -ge 0) {
        throw "The embedded $Channel floor seed '$s' is not lower than the version being built, '$Version'. A binary would treat its own version as a downgrade. Check <Version> in Clink/Clink.csproj and the releases of the $Channel channel (a hand-made or newer release exists)."
    }
}

function Assert-FeedFloorMatchesSeed {
    # L-4. The publish job's history floor for the channel must equal the seed the build job embedded for that channel (both come from
    # Get-FeedHistoryFloor over the release listing, minutes apart). A difference means a release of the channel was created, published or
    # deleted between the two steps, so the binaries about to ship carry a stale or wrong floor seed: refuse, nothing has been signed or
    # drafted yet. '' and $null are the same (no floor, no seed).
    param(
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [AllowNull()] [AllowEmptyString()] [string]$Floor, [AllowNull()] [AllowEmptyString()] [string]$Seed
    )
    $f = [string]$Floor
    $s = [string]$Seed
    if ($f -cne $s) {
        throw "The $Channel history floor is now '$f' but the build job embedded the $Channel floor seed '$s': a $Channel release was created, published or deleted while this run was building. Refusing to publish binaries with a stale seed. Nothing was signed, drafted or uploaded: use 'Re-run all jobs' (the build reads the listing again) once no other $Channel release is in progress."
    }
}

function Assert-FeedR2Endpoint {
    # L7. The R2 S3 endpoint secret must be exactly https://<32 hex>.r2.cloudflarestorage.com (or .eu. for the EU jurisdiction): no bucket name,
    # path, port, query or user info. The value is never part of the message.
    param([AllowNull()] [AllowEmptyString()] [string]$Value, [string]$Name = 'R2_ENDPOINT')
    if (([string]$Value) -cnotmatch '\Ahttps://[0-9a-f]{32}\.(eu\.)?r2\.cloudflarestorage\.com\z') {
        throw "$Name is not https://<32 hex account id>.r2.cloudflarestorage.com (or .eu.r2...; no bucket name, no path, no trailing slash). The value is not printed."
    }
}

function Assert-FeedNeverWasOn {
    # NEW-3. Used by the publish jobs when the feed is NOT enabled for the channel: a feed, once on, cannot be switched off, because
    # clients that already trust the feed would see the bucket's manifest go stale (and a stale one is a replay target). Throws when any
    # earlier release carried <Channel>.json. AssetNames: every asset name of every release (drafts included) of the repository.
    param([Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel, [AllowEmptyCollection()] [string[]]$AssetNames = @())
    $hit = @($AssetNames | Where-Object { $_ -ceq "$Channel.json" })
    if ($hit.Count -gt 0) {
        throw "An earlier release of this repository carried $Channel.json, so the $Channel feed was on. It cannot be switched off: restore feedUrl and the keys in Clink/Assets/feed.json (docs/RELEASING.md, `"The feed cannot be switched off`")."
    }
}
