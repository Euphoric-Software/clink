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
        @{ Status = [int]$resp.StatusCode }
    }
}

function Invoke-FeedAws {
    # Calls the invoker and normalises its answer to a single @{ Code; Text }.
    param([Parameter(Mandatory)] [scriptblock]$Aws, [Parameter(Mandatory)] [string[]]$Arguments)
    $answer = @(& $Aws -Arguments $Arguments) | Select-Object -Last 1
    if ($null -eq $answer -or $null -eq $answer.Code) { throw 'The aws invoker returned no result.' }
    @{ Code = [int]$answer.Code; Text = [string]$answer.Text }
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
    # joined with commas (gh api ... --jq '.[] | [.tag_name, (.draft | tostring), ([.assets[].name] | join(","))] | @tsv').
    # Drafts are ignored (a failed run leaves one; it never reached users). A release that carried the manifest but whose tag is not
    # a feed version is an error, never silently skipped.
    param(
        [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
        [AllowNull()] [AllowEmptyCollection()] [string[]]$Listing = @()
    )
    $best = $null
    foreach ($raw in @($Listing)) {
        $line = ([string]$raw).TrimEnd("`r", "`n")
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split("`t")
        if ($parts.Count -ne 3) { throw "Unexpected release listing line (expected tag, draft flag and asset names separated by tabs): '$line'" }
        if ($parts[1] -ceq 'true') { continue }
        if ($parts[1] -cne 'false') { throw "Unexpected draft flag '$($parts[1])' in the release listing." }
        if (-not ($parts[2].Split(',') -ccontains "$Channel.json")) { continue }
        $tag = $parts[0]
        if ($tag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?$') { throw "Release '$tag' carried $Channel.json but its tag is not a feed version (vX.Y.Z or vX.Y.Z-dev.N); investigate before publishing." }
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
            throw "The bucket has no $Channel.json and no $Channel.json.sig, but the published GitHub release v$history carried $Channel.json, so the feed was on. Refusing to treat this as a first publish (an emptied or replaced bucket is a replay target); $script:FeedRecoveryHint. If the bucket was reset on purpose, the owner sets the environment variable CLINK_FEED_BUCKET_RESET_VERSION to $history for this one publish and removes it afterwards."
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
    $remote = Get-FeedRemoteObject -Aws $Aws -Bucket $Bucket -Key $Asset.Key -Endpoint $Endpoint -ScratchDir $ScratchDir
    if ($remote.Found) {
        if ($remote.Sha256 -eq $Asset.Sha256 -and $remote.Size -eq [int64]$Asset.Size) { Write-Host "exists, same bytes (sha256 $($remote.Sha256)), skipped: $($Asset.Key)"; return 'skipped' }
        throw "$($Asset.Key) already exists with different content (actual sha256 $($remote.Sha256), $($remote.Size) bytes; expected $($Asset.Sha256), $($Asset.Size) bytes). Versioned objects are write-once; bump the version."
    }
    $r = Invoke-FeedAws -Aws $Aws -Arguments @('s3api', 'put-object', '--bucket', $Bucket, '--key', $Asset.Key, '--body', $Asset.Local, '--endpoint-url', $Endpoint,
        '--cache-control', $CacheControl, '--content-type', $Asset.Type, '--metadata', "sha256=$($Asset.Sha256)", '--if-none-match', '*')
    if ($r.Code -ne 0) {
        if ($r.Text -match 'PreconditionFailed|\(412\)') {
            throw "$($Asset.Key) was created by someone else between the check and the upload (precondition failed). Versioned objects are write-once; re-run to compare the content."
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
    # header is believed). Retries for propagation. Asset: Url, Sha256, Size.
    param(
        [Parameter(Mandatory)] [scriptblock]$Http, [Parameter(Mandatory)] $Asset,
        [int]$Attempts = 5, [int]$DelaySeconds = 4, [string]$ScratchDir = [System.IO.Path]::GetTempPath()
    )
    $last = ''
    for ($i = 1; $i -le $Attempts; $i++) {
        $tmp = Join-Path $ScratchDir ('clink-feed-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $resp = @(& $Http -Url $Asset.Url -OutFile $tmp) | Select-Object -Last 1
            $status = if ($resp -and $null -ne $resp.Status) { [int]$resp.Status } else { 0 }
            if ($status -ne 200) { $last = "status $status" }
            elseif (-not (Test-Path -LiteralPath $tmp -PathType Leaf)) { $last = 'no body was received' }
            else {
                $facts = Get-FeedFileFacts -Path $tmp
                if ($facts.Sha256 -eq $Asset.Sha256 -and $facts.Size -eq [int64]$Asset.Size) { Write-Host "public URL ok: $($Asset.Url)"; return }
                $last = "served bytes differ: sha256 $($facts.Sha256), $($facts.Size) bytes (expected $($Asset.Sha256), $($Asset.Size) bytes)"
            }
        } catch { $last = $_.Exception.Message }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        if ($i -lt $Attempts -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }
    throw "Public URL check failed for $($Asset.Url): $last"
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
