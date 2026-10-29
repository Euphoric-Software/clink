# Tests for the release-side feed hardening: tools/lib/feed-publish-lib.ps1 (with a FAKE aws and a FAKE web client, and the real aws invoker
# against a fake aws.cmd placed first on PATH), the package.json binding rules
# of tools/make-manifest.ps1, tools/lib/dotnet-sdk-lib.ps1 and text assertions on the workflows. Standalone: exits 0 when everything
# passed, 1 otherwise. Windows PowerShell 5.1 and pwsh 7, no modules. Nothing here touches a network, a real bucket or a real key: every
# key pair is a throwaway made inside the test, and all files live in a scratch folder that is deleted at the end.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$workflowDir = Join-Path $repo '.github\workflows'
$hostExe = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-feedtests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

. (Join-Path $toolsDir 'feed-lib.ps1')
. (Join-Path $toolsDir 'lib\feed-publish-lib.ps1')
. (Join-Path $toolsDir 'lib\dotnet-sdk-lib.ps1')
$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "assertion failed: $Message" }
}

function Assert-Throws {
    # The body must throw and the message must match the pattern.
    param([scriptblock]$Body, [string]$Pattern, [string]$What)
    $message = $null
    try { & $Body } catch { $message = $_.Exception.Message }
    Assert-True ($null -ne $message) "$What : did not throw"
    Assert-True ($message -match $Pattern) "$What : message '$message' does not match '$Pattern'"
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:passed++
        Write-Host "PASS  $Name"
    } catch {
        $script:failed++
        Write-Host "FAIL  $Name : $($_.Exception.Message)"
    }
}

function Get-ScratchFolder {
    param([string]$Name)
    $path = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $path
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}

# One generator for the whole run: new System.Random objects created within the same clock tick would repeat each other's bytes.
$script:random = New-Object System.Random
function New-RandomBytes {
    param([int]$Length)
    $bytes = New-Object byte[] $Length
    $script:random.NextBytes($bytes)
    , $bytes
}

function New-TestKey {
    # A throwaway ECDSA P-256 key: Ec signs, Public is the Base64 DER SPKI that feed.json lists.
    $ec = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $q = $ec.ExportParameters($false).Q
    $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
    [pscustomobject]@{ Ec = $ec; Public = [Convert]::ToBase64String([byte[]]($header + $q.X + $q.Y)) }
}

function Get-SigText {
    param($Key, [byte[]]$Bytes)
    [Convert]::ToBase64String($Key.Ec.SignData($Bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256))
}

# ---- the fake aws: an in-memory bucket. Records every call. -----------------------------------------------------------------------
$script:store = @{}
$script:calls = New-Object System.Collections.ArrayList
$script:conditionalSupported = $true
function Reset-FakeBucket {
    $script:store = @{}
    $script:calls.Clear()
    $script:conditionalSupported = $true
}
$fakeAws = {
    param([string[]]$Arguments)
    [void]$script:calls.Add($Arguments -join ' ')
    if ($Arguments[0] -eq '--version') { return @{ Code = 0; Text = 'aws-cli/1.2.3 Python/3.8 Windows/10' } }
    $keyAt = [array]::IndexOf($Arguments, '--key')
    $key = if ($keyAt -ge 0) { $Arguments[$keyAt + 1] } else { '' }
    $op = "$($Arguments[0]) $($Arguments[1])"
    if ($op -eq 's3api get-object') {
        if ($script:store.ContainsKey($key)) {
            [System.IO.File]::WriteAllBytes($Arguments[$Arguments.Count - 1], [byte[]]$script:store[$key].Bytes)
            # Like the real CLI: a JSON description on stdout, including the (untrusted) metadata and a Content-Length.
            return @{ Code = 0; Text = ('{"ContentLength": 1, "Metadata": {"sha256": "' + $script:store[$key].Meta + '"}}') }
        }
        if ($key -like '*forbidden*') { return @{ Code = 254; Text = 'An error occurred (AccessDenied) when calling the GetObject operation: Access Denied' } }
        return @{ Code = 254; Text = 'An error occurred (NoSuchKey) when calling the GetObject operation: The specified key does not exist.' }
    }
    if ($op -eq 's3api put-object') {
        if (-not $script:conditionalSupported) { return @{ Code = 252; Text = "Unknown options: --if-none-match, *" } }
        if ($script:store.ContainsKey($key)) { return @{ Code = 254; Text = 'An error occurred (PreconditionFailed) when calling the PutObject operation (412): At least one of the pre-conditions you specified did not hold' } }
        $bodyAt = [array]::IndexOf($Arguments, '--body')
        $script:store[$key] = @{ Bytes = [System.IO.File]::ReadAllBytes($Arguments[$bodyAt + 1]); Meta = '' }
        return @{ Code = 0; Text = '' }
    }
    return @{ Code = 255; Text = "fake aws: unsupported call $($Arguments -join ' ')" }
}

function New-TestAsset {
    param([string]$Name, [byte[]]$Bytes, [string]$Folder = (Get-ScratchFolder 'assets'))
    $local = Join-Path $Folder $Name
    [System.IO.File]::WriteAllBytes($local, $Bytes)
    [pscustomobject]@{ Name = $Name; Local = $local; Sha256 = (Get-Sha256Hex $Bytes); Size = [int64]$Bytes.Length; Key = "clink/releases/v0.4.2/$Name"
        Url = "https://downloads.example/clink/releases/v0.4.2/$Name"; Type = 'application/octet-stream' }
}

function Publish-TestAsset {
    param($Asset)
    Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $Asset -CacheControl 'public, immutable' -ScratchDir (Get-ScratchFolder 'dl')
}

function Get-TestFloor {
    param($Keys)
    Get-FeedBucketFloor -Aws $fakeAws -Bucket 'b' -Prefix 'clink' -Channel 'stable' -Endpoint 'https://r2.example' -Keys $Keys -ScratchDir (Get-ScratchFolder 'dl')
}

function Set-StoredManifest {
    # Puts <prefix>/stable.json and .sig into the fake bucket; returns the manifest bytes.
    param($SigningKey, [string]$Version = '0.4.1', [string]$Channel = 'stable', [switch]$NoSig, [string]$SigOverride)
    $bytes = $utf8.GetBytes((@{ schema = 1; channel = $Channel; version = $Version } | ConvertTo-Json -Compress))
    $script:store['clink/stable.json'] = @{ Bytes = $bytes; Meta = '' }
    if (-not $NoSig) {
        $sig = if ($SigOverride) { $SigOverride } else { Get-SigText $SigningKey $bytes }
        $script:store['clink/stable.json.sig'] = @{ Bytes = $utf8.GetBytes($sig); Meta = '' }
    }
    , $bytes
}

try {
    $keyA = New-TestKey
    $keyB = New-TestKey

    # ------------------------------------------------------------------ NEW-4: the downgrade floor needs a verified signature
    Invoke-Test 'floor: no manifest and no signature in the bucket is the first publish (allowed, $null)' {
        Reset-FakeBucket
        $floor = Get-TestFloor @($keyA.Public)
        Assert-True ($null -eq $floor) "floor was '$floor'"
    }
    Invoke-Test 'floor: a manifest signed by a listed key gives its version as the floor (any key of the list works)' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyB '0.4.1')
        $floor = Get-TestFloor @($keyA.Public, $keyB.Public)
        Assert-True ($floor -eq '0.4.1') "floor was '$floor'"
    }
    Invoke-Test 'floor: a manifest without a signature in the bucket is refused' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1' -NoSig)
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'no .*\.sig' 'missing signature'
    }
    Invoke-Test 'floor: a manifest signed by a key that feed.json does not list is refused' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyB '9.9.9')
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'does not verify' 'foreign key'
    }
    Invoke-Test 'floor: a tampered manifest (valid signature of other bytes) is refused' {
        Reset-FakeBucket
        $good = Set-StoredManifest $keyA '0.4.1'
        $tampered = $utf8.GetBytes((@{ schema = 1; channel = 'stable'; version = '0.0.1' } | ConvertTo-Json -Compress))
        $script:store['clink/stable.json'] = @{ Bytes = $tampered; Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'does not verify' 'tampered manifest'
    }
    Invoke-Test 'floor: garbage or empty signature text is refused (not an exception of another kind)' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1' -SigOverride 'not-base64!!')
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'does not verify' 'garbage signature'
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1' -SigOverride ([Convert]::ToBase64String((New-Object byte[] 10))))
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'does not verify' 'short signature'
    }
    Invoke-Test 'floor: an empty key list verifies nothing (refused)' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1')
        Assert-Throws { Get-TestFloor @() } 'does not verify' 'no keys'
    }
    Invoke-Test 'floor: a signature without a manifest is an inconsistent feed (refused)' {
        Reset-FakeBucket
        $script:store['clink/stable.json.sig'] = @{ Bytes = $utf8.GetBytes('AAAA'); Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'no clink/stable\.json' 'orphan signature'
    }
    Invoke-Test 'floor: a signed manifest for another channel, or with a bad version, is refused' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1' -Channel 'dev')
        Assert-Throws { Get-TestFloor @($keyA.Public) } "names channel 'dev'" 'wrong channel'
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA 'latest')
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'Not a feed version' 'bad version'
    }
    Invoke-Test 'floor: any other read error (access denied) is refused, never mistaken for a first publish' {
        Reset-FakeBucket
        Assert-Throws {
            Get-FeedBucketFloor -Aws $fakeAws -Bucket 'b' -Prefix 'clink-forbidden' -Channel 'stable' -Endpoint 'https://r2.example' -Keys @($keyA.Public) -ScratchDir (Get-ScratchFolder 'dl')
        } 'Could not read' 'access denied'
    }
    Invoke-Test 'floor: a bucket manifest or signature over 1 MB is refused before it is read; exactly 1 MB is still read' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1')
        $script:store['clink/stable.json'] = @{ Bytes = (New-RandomBytes (1048576 + 1)); Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'larger than the 1048576 byte limit' 'oversized manifest'
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1')
        $script:store['clink/stable.json.sig'] = @{ Bytes = (New-RandomBytes (1048576 + 1)); Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'larger than the 1048576 byte limit' 'oversized signature'
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1')
        $script:store['clink/stable.json'] = @{ Bytes = (New-RandomBytes 1048576); Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } 'does not verify' 'a manifest of exactly 1 MB is read (and then fails verification)'
    }
    Invoke-Test 'write-once: a versioned asset over 1 MB is NOT size-limited (it is hashed, never read into memory)' {
        Reset-FakeBucket
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 100)
        $script:store[$asset.Key] = @{ Bytes = (New-RandomBytes (1048576 + 5)); Meta = '' }
        Assert-Throws { Publish-TestAsset $asset } 'already exists with different content' 'big existing asset'
    }
    Invoke-Test 'floor: the not-found answer is recognised as NoSuchKey, "Not Found" and "(404)" only; a 403 or any other text is an error' {
        Reset-FakeBucket
        $dl = Get-ScratchFolder 'dl'
        foreach ($text in 'An error occurred (NoSuchKey) when calling the GetObject operation: The specified key does not exist.',
            'An error occurred (404) when calling the GetObject operation: Not Found', 'Not Found', 'HTTP (404)') {
            $answer = { param([string[]]$Arguments) @{ Code = 254; Text = $script:text } }
            $script:text = $text
            $r = Get-FeedRemoteObject -Aws $answer -Bucket 'b' -Key 'k' -Endpoint 'https://r2.example' -ScratchDir $dl
            Assert-True (-not $r.Found) "not treated as missing: $text"
        }
        foreach ($text in 'An error occurred (403) when calling the GetObject operation: Forbidden', 'An error occurred (AccessDenied)', 'connection reset', '') {
            $script:text = $text
            $answer = { param([string[]]$Arguments) @{ Code = 254; Text = $script:text } }
            Assert-Throws { Get-FeedRemoteObject -Aws $answer -Bucket 'b' -Key 'k' -Endpoint 'https://r2.example' -ScratchDir $dl } 'Could not read' "treated as missing: '$text'"
        }
    }
    Invoke-Test 'floor: every refusal that can come from an interrupted publish names the recovery section' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1' -NoSig)
        Assert-Throws { Get-TestFloor @($keyA.Public) } "interrupted between the manifest and its \.sig; see RELEASING\.md 'Recovering a stuck feed'" 'missing sig'
        Reset-FakeBucket
        $script:store['clink/stable.json.sig'] = @{ Bytes = $utf8.GetBytes('AAAA'); Meta = '' }
        Assert-Throws { Get-TestFloor @($keyA.Public) } "Recovering a stuck feed" 'orphan sig'
        Reset-FakeBucket
        [void](Set-StoredManifest $keyB '0.4.1')
        Assert-Throws { Get-TestFloor @($keyA.Public) } "Recovering a stuck feed" 'unverifiable'
    }
    Invoke-Test 'downgrade: a higher verified floor refuses, an equal or lower one passes' {
        Assert-Throws { Assert-FeedNoDowngrade -Floor '0.5.0' -Version '0.4.2' -Channel 'stable' } 'newer than' 'downgrade'
        Assert-Throws { Assert-FeedNoDowngrade -Floor '0.4.2' -Version '0.4.2-dev.7' -Channel 'dev' } 'newer than' 'final over dev'
        Assert-FeedNoDowngrade -Floor '0.4.2' -Version '0.4.2' -Channel 'stable'
        Assert-FeedNoDowngrade -Floor '0.4.1' -Version '0.4.2' -Channel 'stable'
        Assert-FeedNoDowngrade -Floor $null -Version '0.4.2' -Channel 'stable'
    }

    # ------------------------------------------------------------------ Security M1: the GitHub-history floor
    function Get-TestListing {
        # One line per release: tag, draft flag, comma-joined asset names (what the workflow's gh api --jq @tsv prints).
        param([object[]]$Releases)
        @($Releases | ForEach-Object { $_.Tag + "`t" + ($(if ($_.Draft) { 'true' } else { 'false' })) + "`t" + ($_.Assets -join ',') })
    }
    $historyStable = @(
        @{ Tag = 'v0.4.0'; Draft = $false; Assets = @('Clink.exe', 'stable.json', 'stable.json.sig') }
        @{ Tag = 'v0.4.1'; Draft = $false; Assets = @('Clink.exe', 'stable.json', 'stable.json.sig') }
        @{ Tag = 'v0.4.2-dev.9'; Draft = $false; Assets = @('Clink.exe', 'dev.json', 'dev.json.sig') }
        @{ Tag = 'v0.9.0'; Draft = $true; Assets = @('Clink.exe', 'stable.json', 'stable.json.sig') }
        @{ Tag = 'v0.3.0'; Draft = $false; Assets = @('Clink.exe') }
    )
    function Get-TestEffectiveFloor {
        # The publish decision with the fake bucket: bucket floor (verified), history floor (from a listing), owner override, version.
        param([object[]]$Releases, [string]$Version, [string]$Reset = '', [string]$Channel = 'stable')
        $bucketFloor = Get-TestFloor @($keyA.Public)
        $historyFloor = Get-FeedHistoryFloor -Channel $Channel -Listing (Get-TestListing $Releases)
        Resolve-FeedEffectiveFloor -BucketFloor $bucketFloor -HistoryFloor $historyFloor -ResetVersion $Reset -Version $Version -Channel $Channel
    }
    Invoke-Test 'history floor: the highest NON-DRAFT release of the channel that carried <channel>.json (drafts, other channels, plain releases ignored)' {
        $stable = Get-FeedHistoryFloor -Channel stable -Listing (Get-TestListing $historyStable)
        Assert-True ($stable -eq '0.4.1') "stable floor '$stable' (the draft v0.9.0 must not count)"
        $dev = Get-FeedHistoryFloor -Channel dev -Listing (Get-TestListing $historyStable)
        Assert-True ($dev -eq '0.4.2-dev.9') "dev floor '$dev'"
        Assert-True ($null -eq (Get-FeedHistoryFloor -Channel stable -Listing @())) 'an empty listing has a floor'
        Assert-True ($null -eq (Get-FeedHistoryFloor -Channel stable)) 'no listing has a floor'
        $numeric = Get-TestListing @(@{ Tag = 'v0.9.0'; Draft = $false; Assets = @('stable.json') }, @{ Tag = 'v0.10.0'; Draft = $false; Assets = @('stable.json') }, @{ Tag = 'v0.2.0'; Draft = $false; Assets = @('stable.json') })
        Assert-True ((Get-FeedHistoryFloor -Channel stable -Listing $numeric) -eq '0.10.0') 'versions are not compared numerically'
        Assert-True ($null -eq (Get-FeedHistoryFloor -Channel stable -Listing (Get-TestListing @(@{ Tag = 'v1.0.0'; Draft = $false; Assets = @('stable.json.sig', 'Stable.json', 'xstable.json') })))) 'a look-alike asset name counted'
    }
    Invoke-Test 'history floor: a draft-only history is ignored (a failed run leaves a draft; it never reached users)' {
        $drafts = @(@{ Tag = 'v0.4.2'; Draft = $true; Assets = @('Clink.exe', 'stable.json', 'stable.json.sig') })
        Assert-True ($null -eq (Get-FeedHistoryFloor -Channel stable -Listing (Get-TestListing $drafts))) 'a draft counted'
        Reset-FakeBucket
        $floor = Get-TestEffectiveFloor $drafts '0.4.2'
        Assert-True ($null -eq $floor) "floor '$floor'"
    }
    Invoke-Test 'history floor: a malformed line, a draft flag other than true/false, or a non-version tag that carried the manifest is an error' {
        Assert-Throws { Get-FeedHistoryFloor -Channel stable -Listing @('v1.0.0 false stable.json') } 'Unexpected release listing line' 'no tabs'
        Assert-Throws { Get-FeedHistoryFloor -Channel stable -Listing @("v1.0.0`tmaybe`tstable.json") } 'Unexpected draft flag' 'bad flag'
        Assert-Throws { Get-FeedHistoryFloor -Channel stable -Listing @("nightly`tfalse`tstable.json") } 'not a feed version' 'odd tag'
        Assert-Throws { Get-FeedHistoryFloor -Channel stable -Listing @("v1.0.0-rc.1`tfalse`tstable.json") } 'not a feed version' 'rc tag'
        Assert-True ($null -eq (Get-FeedHistoryFloor -Channel stable -Listing @("nightly`tfalse`tClink.exe"))) 'an odd tag without the manifest should be ignored'
    }
    Invoke-Test 'M1: an EMPTY bucket while the history shows a published stable.json is refused (not a first publish)' {
        Reset-FakeBucket
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.2' } 'Refusing to treat this as a first publish.*Recovering a stuck feed' 'empty bucket'
    }
    Invoke-Test 'M1: the same with CLINK_FEED_BUCKET_RESET_VERSION equal to the history floor is allowed; another value, or an unparsable one, is refused' {
        Reset-FakeBucket
        $floor = Get-TestEffectiveFloor $historyStable '0.4.2' '0.4.1'
        Assert-True ($floor -eq '0.4.1') "effective floor '$floor'"
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.2' '0.4.0' } 'Refusing to treat this' 'older reset value'
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.2' '0.9.9' } 'Refusing to treat this' 'newer reset value'
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.2' 'yes' } 'Not a feed version' 'garbage reset value'
        Assert-True ((Get-TestEffectiveFloor $historyStable '0.4.2' ' 0.4.1 ') -eq '0.4.1') 'surrounding blanks of the variable value are trimmed'
    }
    Invoke-Test 'M1: even with the override, a version at or below the history floor is refused' {
        Reset-FakeBucket
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.1' '0.4.1' } 'must be newer' 'equal to the floor'
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.0' '0.4.1' } 'must be newer' 'tag below the floor'
    }
    Invoke-Test 'M1: a version below the GitHub floor is refused even when the bucket holds an OLDER signed manifest (rolled-back bucket)' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.0')
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.1' } 'must be newer' 'replayed older manifest, same version as history'
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.0' } 'newer|older' 'tag below the floor'
        $floor = Get-TestEffectiveFloor $historyStable '0.4.2'
        Assert-True ($floor -eq '0.4.1') "effective floor '$floor' (the higher of bucket 0.4.0 and history 0.4.1)"
    }
    Invoke-Test 'M1: a bucket floor above the history floor stays the floor' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.5.0')
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.2' } 'already at 0\.5\.0' 'bucket ahead'
        $floor = Get-TestEffectiveFloor $historyStable '0.5.1'
        Assert-True ($floor -eq '0.5.0') "effective floor '$floor'"
    }
    Invoke-Test 'M1: the override is ignored (and logged) when the bucket has a manifest; it never lowers any floor' {
        Reset-FakeBucket
        [void](Set-StoredManifest $keyA '0.4.1')
        Assert-Throws { Get-TestEffectiveFloor $historyStable '0.4.1' '0.4.1' } 'must be newer' 'override does not allow a re-publish'
        $floor = Get-TestEffectiveFloor $historyStable '0.4.2' '0.4.1'
        Assert-True ($floor -eq '0.4.1') "effective floor '$floor'"
    }
    Invoke-Test 'M1: no history and an empty bucket is the real first publish (allowed); no history with a bucket manifest is the old behaviour' {
        Reset-FakeBucket
        Assert-True ($null -eq (Get-TestEffectiveFloor @() '0.4.2')) 'first publish refused'
        [void](Set-StoredManifest $keyA '0.4.1')
        Assert-True ((Get-TestEffectiveFloor @() '0.4.2') -eq '0.4.1') 'bucket floor lost'
        Assert-Throws { Get-TestEffectiveFloor @() '0.4.0' } 'already at 0\.4\.1' 'downgrade'
    }

    # ------------------------------------------------------------------ the REAL aws invoker, against a fake aws.cmd first on PATH
    function Use-FakeAwsCmd {
        # Writes aws.cmd into a scratch folder, puts the folder first on PATH for the body, restores PATH afterwards.
        param([string]$Name, [string[]]$Lines, [scriptblock]$Body)
        $dir = Get-ScratchFolder "fakeaws-$Name"
        [System.IO.File]::WriteAllText((Join-Path $dir 'aws.cmd'), (($Lines -join "`r`n") + "`r`n"), [System.Text.Encoding]::ASCII)
        $oldPath = $env:PATH
        $env:PATH = "$dir;$oldPath"
        try { & $Body } finally { $env:PATH = $oldPath }
    }
    Invoke-Test 'real aws invoker: exit 254 + "(NoSuchKey)" on stderr is an answer (Code 254), a missing key, even under ErrorActionPreference Stop' {
        $cmd = @('@echo off', 'echo An error occurred (NoSuchKey) when calling the GetObject operation: The specified key does not exist. 1>&2', 'exit /b 254')
        Use-FakeAwsCmd 'nosuchkey' $cmd {
            $aws = New-FeedAwsInvoker
            $raw = Invoke-FeedAws -Aws $aws -Arguments @('s3api', 'get-object', '--key', 'k')
            Assert-True ($raw.Code -eq 254) "code $($raw.Code)"
            Assert-True ($raw.Text -match 'NoSuchKey') "text '$($raw.Text)'"
            $r = Get-FeedRemoteObject -Aws $aws -Bucket 'b' -Key 'clink/stable.json' -Endpoint 'https://r2.example' -ScratchDir (Get-ScratchFolder 'dl')
            Assert-True (-not $r.Found) 'NoSuchKey was not a missing key'
            $floor = Get-FeedBucketFloor -Aws $aws -Bucket 'b' -Prefix 'clink' -Channel stable -Endpoint 'https://r2.example' -Keys @($keyA.Public) -ScratchDir (Get-ScratchFolder 'dl')
            Assert-True ($null -eq $floor) 'an empty bucket was not a first publish'
        }
    }
    Invoke-Test 'real aws invoker: exit 252 "Unknown options: --if-none-match" refuses the upload, logs aws --version, no plain copy' {
        $cmd = @('@echo off', 'if "%1"=="--version" goto ver', 'if "%2"=="get-object" goto get', 'if "%2"=="put-object" goto put',
            'echo unexpected call %* 1>&2', 'exit /b 1', ':ver', 'echo aws-cli/9.9.9 fake', 'echo called>>%~dp0version.log', 'exit /b 0', ':get',
            'echo An error occurred (NoSuchKey) when calling the GetObject operation 1>&2', 'exit /b 254', ':put',
            'echo aws: error: Unknown options: --if-none-match, * 1>&2', 'exit /b 252')
        Use-FakeAwsCmd 'oldcli' $cmd {
            $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 300)
            $aws = New-FeedAwsInvoker
            Assert-Throws {
                Publish-FeedVersionedAsset -Aws $aws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $asset -CacheControl 'x' -ScratchDir (Get-ScratchFolder 'dl')
            } 'does not support --if-none-match' 'old aws cli through the real invoker'
            Assert-True (Test-Path -LiteralPath (Join-Path (Get-ScratchFolder 'fakeaws-oldcli') 'version.log')) 'aws --version was not run'
        }
    }
    Invoke-Test 'real aws invoker: exit 0 with no output file is refused ("wrote no file"), never read as an empty object' {
        Use-FakeAwsCmd 'nofile' @('@echo off', 'exit /b 0') {
            $aws = New-FeedAwsInvoker
            Assert-Throws { Get-FeedRemoteObject -Aws $aws -Bucket 'b' -Key 'clink/stable.json' -Endpoint 'https://r2.example' -ScratchDir (Get-ScratchFolder 'dl') } 'wrote no file' 'success without a file'
        }
    }
    Invoke-Test 'real aws invoker: any other failure (AccessDenied, exit 1) is an error, not a missing key' {
        Use-FakeAwsCmd 'denied' @('@echo off', 'echo An error occurred (AccessDenied) when calling the GetObject operation: Access Denied 1>&2', 'exit /b 254') {
            $aws = New-FeedAwsInvoker
            Assert-Throws { Get-FeedRemoteObject -Aws $aws -Bucket 'b' -Key 'k' -Endpoint 'https://r2.example' -ScratchDir (Get-ScratchFolder 'dl') } 'Could not read.*AccessDenied' 'access denied'
        }
    }

    # ------------------------------------------------------------------ NEW-1: write-once compares the ACTUAL bytes
    Invoke-Test 'write-once: an existing object with a LYING x-amz-meta-sha256 is refused (the real bytes differ)' {
        Reset-FakeBucket
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 4096)
        $script:store[$asset.Key] = @{ Bytes = (New-RandomBytes 4096); Meta = $asset.Sha256 }   # same size, other bytes, metadata claims ours
        Assert-Throws { Publish-TestAsset $asset } 'already exists with different content' 'lying metadata'
        Assert-True (-not ($script:calls -match 'put-object')) 'a put-object was attempted'
        Assert-True (-not ($script:calls -match 'head-object')) 'head-object (metadata/Content-Length) was used'
    }
    Invoke-Test 'write-once: an existing object of a different size (hash differs too) is refused' {
        Reset-FakeBucket
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 4096)
        $script:store[$asset.Key] = @{ Bytes = (New-RandomBytes 100); Meta = $asset.Sha256 }
        Assert-Throws { Publish-TestAsset $asset } 'already exists with different content' 'size mismatch'
    }
    Invoke-Test 'write-once: identical bytes are skipped without any upload, whatever the metadata says' {
        Reset-FakeBucket
        $bytes = New-RandomBytes 4096
        $asset = New-TestAsset 'Clink.exe' $bytes
        $script:store[$asset.Key] = @{ Bytes = $bytes; Meta = 'wrong-on-purpose' }
        $result = Publish-TestAsset $asset
        Assert-True ($result -eq 'skipped') "result $result"
        Assert-True (-not ($script:calls -match 'put-object')) 'a put-object was attempted for identical bytes'
    }
    Invoke-Test 'write-once: a missing object is created with a conditional put (--if-none-match *) and the bytes land' {
        Reset-FakeBucket
        $bytes = New-RandomBytes 2048
        $asset = New-TestAsset 'Clink-framework.exe' $bytes
        $result = Publish-TestAsset $asset
        Assert-True ($result -eq 'uploaded') "result $result"
        $put = @($script:calls | Where-Object { $_ -match 'put-object' })
        Assert-True ($put.Count -eq 1 -and $put[0] -match '--if-none-match \*$') "put call: $put"
        Assert-True ((Get-Sha256Hex ([byte[]]$script:store[$asset.Key].Bytes)) -eq $asset.Sha256) 'stored bytes differ'
    }
    Invoke-Test 'write-once: a precondition failure on put (a concurrent writer) is refused' {
        Reset-FakeBucket
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 512)
        # The object appears between the GET and the PUT.
        $racing = {
            param([string[]]$Arguments)
            if ($Arguments[1] -eq 'get-object') { return @{ Code = 254; Text = 'An error occurred (NoSuchKey) when calling the GetObject operation' } }
            return @{ Code = 254; Text = 'An error occurred (PreconditionFailed) (412)' }
        }
        Assert-Throws {
            Publish-FeedVersionedAsset -Aws $racing -Bucket 'b' -Endpoint 'https://r2.example' -Asset $asset -CacheControl 'x' -ScratchDir (Get-ScratchFolder 'dl')
        } 'created by someone else' 'precondition failed'
    }

    # ------------------------------------------------------------------ NEW-5: no fallback without --if-none-match
    Invoke-Test 'if-none-match unsupported: the upload is refused, aws --version is logged, and no plain copy is ever attempted' {
        Reset-FakeBucket
        $script:conditionalSupported = $false
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 512)
        $logged = $null
        Assert-Throws { $script:logged = Publish-TestAsset $asset 6>&1 | Out-String } 'does not support --if-none-match' 'old aws cli'
        Assert-True (@($script:calls | Where-Object { $_ -eq '--version' }).Count -eq 1) 'aws --version was not run'
        Assert-True (-not ($script:calls -match 's3 cp')) 'a fallback copy was attempted'
        Assert-True (-not $script:store.ContainsKey($asset.Key)) 'the object was written anyway'
    }
    Invoke-Test 'a generic upload failure is refused with the aws message' {
        Reset-FakeBucket
        $asset = New-TestAsset 'Clink.exe' (New-RandomBytes 512)
        $broken = {
            param([string[]]$Arguments)
            if ($Arguments[1] -eq 'get-object') { return @{ Code = 254; Text = '(NoSuchKey)' } }
            return @{ Code = 1; Text = 'boom: connection reset' }
        }
        Assert-Throws {
            Publish-FeedVersionedAsset -Aws $broken -Bucket 'b' -Endpoint 'https://r2.example' -Asset $asset -CacheControl 'x' -ScratchDir (Get-ScratchFolder 'dl')
        } 'Upload failed.*boom' 'generic failure'
    }

    # ------------------------------------------------------------------ NEW-1: public URL check hashes the served bytes
    Invoke-Test 'public: right bytes pass; wrong bytes with the right size, wrong size, a 404 and a transport error are refused' {
        $bytes = New-RandomBytes 1000
        $asset = New-TestAsset 'Clink.exe' $bytes
        $serving = $null
        $http = { param([string]$Url, [string]$OutFile) if ($script:serving.Throw) { throw 'connection refused' }; if ($script:serving.Bytes) { [System.IO.File]::WriteAllBytes($OutFile, [byte[]]$script:serving.Bytes) }; @{ Status = $script:serving.Status } }
        $dl = Get-ScratchFolder 'dl'
        $script:serving = @{ Status = 200; Bytes = $bytes }
        Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl
        $script:serving = @{ Status = 200; Bytes = (New-RandomBytes 1000) }
        Assert-Throws { Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 2 -DelaySeconds 0 -ScratchDir $dl } 'served bytes differ' 'same size, other bytes'
        $script:serving = @{ Status = 200; Bytes = (New-RandomBytes 999) }
        Assert-Throws { Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'served bytes differ' 'other size'
        $script:serving = @{ Status = 404; Bytes = $null }
        Assert-Throws { Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'status 404' '404'
        $script:serving = @{ Status = 200; Bytes = $null }
        Assert-Throws { Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'no body' 'no body'
        $script:serving = @{ Throw = $true }
        Assert-Throws { Assert-FeedPublicAsset -Http $http -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'connection refused' 'transport error'
        Assert-True (@(Get-ChildItem -LiteralPath $dl -Filter 'clink-feed-*.tmp').Count -eq 0) 'scratch files were left behind'
    }

    Invoke-Test 'public: a first failed attempt followed by a good one passes (retry), and a 404 variant keeps retrying until the attempts run out' {
        $bytes = New-RandomBytes 500
        $asset = New-TestAsset 'Clink.exe' $bytes
        $script:attempt = 0
        $flaky = { param([string]$Url, [string]$OutFile) $script:attempt++; if ($script:attempt -eq 1) { return @{ Status = 503 } }; [System.IO.File]::WriteAllBytes($OutFile, [byte[]]$bytes); @{ Status = 200 } }
        Assert-FeedPublicAsset -Http $flaky -Asset $asset -Attempts 3 -DelaySeconds 0 -ScratchDir (Get-ScratchFolder 'dl')
        Assert-True ($script:attempt -eq 2) "attempts: $($script:attempt)"
        $script:attempt = 0
        $never = { param([string]$Url, [string]$OutFile) $script:attempt++; throw 'The remote server returned an error: (404) Not Found.' }
        Assert-Throws { Assert-FeedPublicAsset -Http $never -Asset $asset -Attempts 3 -DelaySeconds 0 -ScratchDir (Get-ScratchFolder 'dl') } '\(404\) Not Found' 'persistent 404'
        Assert-True ($script:attempt -eq 3) "attempts: $($script:attempt)"
    }

    # ------------------------------------------------------------------ NEW-3: a feed that was on stays on
    Invoke-Test 'feed history: an earlier stable.json (or dev.json) refuses a feed-less publish of that channel only' {
        Assert-Throws { Assert-FeedNeverWasOn -Channel stable -AssetNames @('Clink.exe', 'stable.json', 'stable.json.sig') } 'cannot be switched off' 'stable'
        Assert-Throws { Assert-FeedNeverWasOn -Channel dev -AssetNames @('Clink.exe', 'dev.json') } 'cannot be switched off' 'dev'
        Assert-FeedNeverWasOn -Channel stable -AssetNames @('Clink.exe', 'Clink.exe.sha256', 'dev.json', 'dev.json.sig', 'THIRD-PARTY-NOTICES.txt')
        Assert-FeedNeverWasOn -Channel dev -AssetNames @('stable.json')
        Assert-FeedNeverWasOn -Channel stable -AssetNames @()
        Assert-FeedNeverWasOn -Channel stable
    }

    # ------------------------------------------------------------------ L1: make-manifest binds package.json
    $keyPem = @()
    foreach ($i in 0..1) {
        # PKCS#8 for make-manifest: generate through CNG with a plaintext-exportable key (throwaway).
        $cp = New-Object System.Security.Cryptography.CngKeyCreationParameters
        $cp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
        $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $cp)
        try {
            $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
            $blob = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        } finally { $cng.Dispose() }
        $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
        $keyPem += [pscustomobject]@{
            Private = "-----BEGIN PRIVATE KEY-----`n" + [Convert]::ToBase64String($pkcs8) + "`n-----END PRIVATE KEY-----`n"
            Public  = [Convert]::ToBase64String([byte[]]($header + $blob[8..71]))
        }
    }
    $feedJson = Join-Path $scratch 'feed.json'
    [System.IO.File]::WriteAllText($feedJson, ('{"schema":1,"feedUrl":"https://downloads.example/clink/","stableKeys":["' + $keyPem[0].Public + '"],"devKeys":["' + $keyPem[1].Public + '"]}'), $utf8)
    $manifestScript = Join-Path $toolsDir 'make-manifest.ps1'

    function New-PackageDir {
        # A package folder with the three binaries and a package.json that make-manifest accepts; Mutate edits the parsed package.json (and may touch files).
        param([string]$Name, [scriptblock]$Mutate)
        $dir = Get-ScratchFolder "pkg-$Name"
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink.exe'), (New-RandomBytes 5000))
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink-framework.exe'), (New-RandomBytes 2000))
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink-Setup.exe'), (New-RandomBytes 3000))
        $files = [ordered]@{}
        foreach ($pair in @(@('exe', 'Clink.exe'), @('exeFramework', 'Clink-framework.exe'), @('installer', 'Clink-Setup.exe'))) {
            $path = Join-Path $dir $pair[1]
            $files[$pair[0]] = [ordered]@{ file = $pair[1]; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $pkg = [pscustomobject]@{ version = '0.4.2'; minRuntime = '10.0.14'; files = [pscustomobject]$files }
        if ($Mutate) { & $Mutate $pkg $dir }
        [System.IO.File]::WriteAllText((Join-Path $dir 'package.json'), ($pkg | ConvertTo-Json -Depth 6), $utf8)
        $dir
    }
    function Invoke-MakeManifest {
        # Defaults: the stable manifest of 0.4.2 under feedUrl. Channel/Version/BaseUrl/Feed/KeyIndex override them (the dev key is $keyPem[1]).
        param([string]$Dir, [string]$Channel = 'stable', [string]$Version = '0.4.2', [string]$BaseUrl = 'https://downloads.example/clink/releases/v0.4.2/',
            [string]$Feed = $feedJson, [int]$KeyIndex = 0)
        $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $manifestScript, '-Channel', $Channel, '-Version', $Version, '-Tag', "v$Version",
            '-BaseUrl', $BaseUrl, '-NotesUrl', 'https://example.org/notes', '-PackageDir', $Dir, '-FeedJson', $Feed)
        $quoted = $all | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
        $id = [guid]::NewGuid().ToString('N')
        $outFile = Join-Path $scratch "out-$id.txt"
        $errFile = Join-Path $scratch "err-$id.txt"
        [Environment]::SetEnvironmentVariable('CLINK_FEED_SIGNING_KEY', $keyPem[$KeyIndex].Private)
        try {
            $run = Start-Process -FilePath $hostExe -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        } finally { [Environment]::SetEnvironmentVariable('CLINK_FEED_SIGNING_KEY', $null) }
        [pscustomobject]@{ Code = $run.ExitCode; Text = [System.IO.File]::ReadAllText($outFile) + [System.IO.File]::ReadAllText($errFile) }
    }
    function Assert-ManifestRefused {
        param([string]$Name, [scriptblock]$Mutate, [string]$Pattern)
        $dir = New-PackageDir $Name $Mutate
        $r = Invoke-MakeManifest $dir
        Assert-True ($r.Code -ne 0) "$Name : make-manifest accepted the package"
        Assert-True ($r.Text -match $Pattern) "$Name : message does not match '$Pattern': $($r.Text)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $dir 'stable.json'))) "$Name : a manifest was written"
    }

    Invoke-Test 'make-manifest: a package that follows every binding rule is accepted (control)' {
        $dir = New-PackageDir 'ok'
        $r = Invoke-MakeManifest $dir
        Assert-True ($r.Code -eq 0) "refused a good package: $($r.Text)"
        Assert-True (Test-Path -LiteralPath (Join-Path $dir 'stable.json.sig')) 'no signature'
    }
    Invoke-Test 'make-manifest: files.exe.file other than exactly Clink.exe (case, path, name) is refused' {
        Assert-ManifestRefused 'exe-case' { param($p) $p.files.exe.file = 'clink.exe' } "files\.exe\.file is 'clink\.exe', expected 'Clink\.exe'"
        Assert-ManifestRefused 'exe-path' { param($p) $p.files.exe.file = '..\Clink.exe' } 'files\.exe\.file'
        Assert-ManifestRefused 'exe-name' { param($p) $p.files.exe.file = 'Clink-framework.exe' } 'files\.exe\.file'
    }
    Invoke-Test 'make-manifest: files.exeFramework.file other than exactly Clink-framework.exe is refused' {
        Assert-ManifestRefused 'fx-name' { param($p) $p.files.exeFramework.file = 'Clink.exe' } "files\.exeFramework\.file is 'Clink\.exe', expected 'Clink-framework\.exe'"
        Assert-ManifestRefused 'fx-case' { param($p) $p.files.exeFramework.file = 'CLINK-FRAMEWORK.EXE' } 'files\.exeFramework\.file'
    }
    Invoke-Test 'make-manifest: files.installer.file other than exactly Clink-Setup.exe is refused' {
        Assert-ManifestRefused 'setup-version' { param($p) $p.files.installer.file = 'Clink-0.4.2-setup.exe' } "files\.installer\.file is 'Clink-0\.4\.2-setup\.exe', expected 'Clink-Setup\.exe'"
        Assert-ManifestRefused 'setup-case' { param($p) $p.files.installer.file = 'clink-setup.exe' } 'files\.installer\.file'
    }
    Invoke-Test 'make-manifest: identical exe and exeFramework hashes are refused' {
        Assert-ManifestRefused 'same-hash' {
            param($p, $dir)
            Copy-Item -LiteralPath (Join-Path $dir 'Clink.exe') -Destination (Join-Path $dir 'Clink-framework.exe') -Force
            $p.files.exeFramework.sha256 = $p.files.exe.sha256
            $p.files.exeFramework.size = $p.files.exe.size
        } 'same sha256 for exe and exeFramework'
    }
    Invoke-Test 'make-manifest: a minRuntime whose major is not 10 is refused (9.x and 11.x)' {
        Assert-ManifestRefused 'rt9' { param($p) $p.minRuntime = '9.0.8' } "minRuntime '9\.0\.8' is not a 10\.x\.y runtime"
        Assert-ManifestRefused 'rt11' { param($p) $p.minRuntime = '11.0.0' } "minRuntime '11\.0\.0' is not a 10\.x\.y runtime"
    }
    Invoke-Test 'make-manifest: a missing installer entry and a file that differs from package.json are still refused' {
        Assert-ManifestRefused 'no-installer' { param($p) $p.files.PSObject.Properties.Remove('installer') } "no 'installer' entry"
        Assert-ManifestRefused 'hash-differs' { param($p, $dir) [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink.exe'), (New-RandomBytes 5001)) } 'does not match package.json'
    }

    # ------------------------------------------------------------------ devFeedUrl (B2): Get-FeedConfig, make-manifest and the base/prefix guard
    function New-FeedJsonFile {
        # A feed.json in the scratch folder. $Extra is raw JSON spliced in before the key lists (for example '"devFeedUrl":"https://x/clink/",').
        param([string]$Name, [string]$Extra = '', [string]$FeedUrl = 'https://dl.example/clink/', [string[]]$Stable = @($keyPem[0].Public), [string[]]$Dev = @($keyPem[1].Public))
        $path = Join-Path $scratch "$Name.json"
        $sk = (@($Stable) | ForEach-Object { '"' + $_ + '"' }) -join ','
        $dk = (@($Dev) | ForEach-Object { '"' + $_ + '"' }) -join ','
        [System.IO.File]::WriteAllText($path, ('{"schema":1,"feedUrl":"' + $FeedUrl + '",' + $Extra + '"stableKeys":[' + $sk + '],"devKeys":[' + $dk + ']}'), $utf8)
        $path
    }
    $stableBase = 'https://dl.example/clink/'
    $devBase = 'https://dl-dev.example/clink/'

    Invoke-Test 'devFeedUrl: absent or "" means the dev channel uses feedUrl; stable always uses feedUrl' {
        foreach ($extra in '', '"devFeedUrl":"",', '"devFeedUrl":"   ",') {
            $f = New-FeedJsonFile 'cfg-nodev' -Extra $extra
            $d = Get-FeedConfig -Path $f -Channel dev
            $s = Get-FeedConfig -Path $f -Channel stable
            Assert-True ($d.Enabled -and $d.Mode -eq 'on' -and $d.FeedUrl -ceq $stableBase) "dev base was '$($d.FeedUrl)' for [$extra]"
            Assert-True ($s.FeedUrl -ceq $stableBase) "stable base was '$($s.FeedUrl)' for [$extra]"
            Assert-True ($d.DevFeedUrl -ceq '' -and $d.ConfiguredFeedUrl -ceq $stableBase) 'the as-written properties are wrong'
            Assert-True ($d.Keys.Count -eq 1 -and $d.Keys[0] -ceq $keyPem[1].Public -and $s.Keys[0] -ceq $keyPem[0].Public) 'keys are not per channel'
        }
    }
    Invoke-Test 'devFeedUrl: a non-empty devFeedUrl is the dev base; stable stays on feedUrl; keys stay per channel; it may equal feedUrl; it is trimmed' {
        $f = New-FeedJsonFile 'cfg-dev' -Extra ('"devFeedUrl":"' + $devBase + '",')
        $d = Get-FeedConfig -Path $f -Channel dev
        $s = Get-FeedConfig -Path $f -Channel stable
        Assert-True ($d.FeedUrl -ceq $devBase -and $d.DevFeedUrl -ceq $devBase -and $d.ConfiguredFeedUrl -ceq $stableBase) "dev: base '$($d.FeedUrl)'"
        Assert-True ($s.FeedUrl -ceq $stableBase -and $s.DevFeedUrl -ceq $devBase) "stable: base '$($s.FeedUrl)'"
        Assert-True ($d.Keys[0] -ceq $keyPem[1].Public -and $s.Keys[0] -ceq $keyPem[0].Public) 'keys crossed'
        $same = Get-FeedConfig -Path (New-FeedJsonFile 'cfg-same' -Extra ('"devFeedUrl":"' + $stableBase + '",')) -Channel dev
        Assert-True ($same.FeedUrl -ceq $stableBase) 'devFeedUrl equal to feedUrl was refused or changed'
        $trim = Get-FeedConfig -Path (New-FeedJsonFile 'cfg-trim' -Extra ('"devFeedUrl":"  ' + $devBase + '  ",')) -Channel dev
        Assert-True ($trim.FeedUrl -ceq $devBase) 'devFeedUrl was not trimmed'
    }
    Invoke-Test 'devFeedUrl: it follows exactly the feedUrl rules (the same bad URLs are refused for both, the same good ones accepted)' {
        $bad = 'http://h.example/clink/', 'https://h.example/clink', 'https://u@h.example/clink/', 'https://h.example/clink/?x=1', 'https://h.example/clink/#f', 'https://h.example\clink/',
            'h.example/clink/', 'https:///clink/', 'https://h.example/cl ink/', 'ftp://h.example/clink/'
        foreach ($u in $bad) {
            $j = $u.Replace('\', '\\')   # JSON text of the URL
            Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-badf' -FeedUrl $j) -Channel stable) } 'feedUrl must be https' "feedUrl $u"
            Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-badd' -Extra ('"devFeedUrl":"' + $j + '",')) -Channel dev) } 'devFeedUrl must be empty or follow' "devFeedUrl $u (dev)"
            Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-badd' -Extra ('"devFeedUrl":"' + $j + '",')) -Channel stable) } 'devFeedUrl must be empty or follow' "devFeedUrl $u (stable)"
        }
        foreach ($u in 'https://h.example/', 'https://h.example/a/b/', 'https://h.example:8443/clink/', 'https://pub-1.r2.dev/clink-staging/') {
            $a = Get-FeedConfig -Path (New-FeedJsonFile 'cfg-goodf' -FeedUrl $u) -Channel stable
            $b = Get-FeedConfig -Path (New-FeedJsonFile 'cfg-goodd' -Extra ('"devFeedUrl":"' + $u + '",')) -Channel dev
            Assert-True ($a.FeedUrl -ceq $u -and $b.FeedUrl -ceq $u) "a good URL was refused: $u"
        }
    }
    Invoke-Test 'devFeedUrl: a non-string devFeedUrl, a devFeedUrl without feedUrl, or one beside an otherwise empty file is misconfigured (fail closed); an empty file stays off' {
        foreach ($raw in '5', 'true', '["https://h.example/clink/"]', '{"a":1}') {
            Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-type' -Extra ('"devFeedUrl":' + $raw + ',')) -Channel dev) } 'devFeedUrl must be a string' "devFeedUrl $raw"
        }
        # feedUrl empty but devFeedUrl set: misconfigured, with or without keys.
        Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-nofeed' -FeedUrl '' -Extra ('"devFeedUrl":"' + $devBase + '",')) -Channel dev) } 'feedUrl is empty' 'devFeedUrl with keys but no feedUrl'
        Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-nofeed2' -FeedUrl '' -Stable @() -Dev @() -Extra ('"devFeedUrl":"' + $devBase + '",')) -Channel stable) } 'feedUrl is empty' 'devFeedUrl alone'
        # The previous rules still hold.
        Assert-Throws { [void](Get-FeedConfig -Path (New-FeedJsonFile 'cfg-nodevkeys' -Dev @() -Extra ('"devFeedUrl":"' + $devBase + '",')) -Channel dev) } 'devKeys is empty' 'devFeedUrl with empty devKeys'
        # Fully empty is off, with or without an empty devFeedUrl.
        foreach ($extra in '', '"devFeedUrl":"",') {
            $off = Get-FeedConfig -Path (New-FeedJsonFile 'cfg-off' -FeedUrl '' -Stable @() -Dev @() -Extra $extra) -Channel dev
            Assert-True ((-not $off.Enabled) -and $off.Mode -eq 'off' -and $off.FeedUrl -ceq '') "an empty file is not off (extra [$extra])"
        }
        # The committed file must keep parsing (it is empty today: off).
        $committed = Get-FeedConfig -Path (Join-Path $repo 'Clink\Assets\feed.json') -Channel dev
        Assert-True ($committed.Mode -eq 'off' -or $committed.Mode -eq 'on') 'the committed feed.json does not parse'
    }

    Invoke-Test 'devFeedUrl: make-manifest binds a manifest to its OWN channel base (dev under devFeedUrl, stable under feedUrl)' {
        $split = New-FeedJsonFile 'mm-split' -Extra ('"devFeedUrl":"' + $devBase + '",')
        $devDir = New-PackageDir 'dev-ok' { param($p) $p.version = '0.4.2-dev.1' }
        $r = Invoke-MakeManifest $devDir -Channel dev -Version '0.4.2-dev.1' -BaseUrl ($devBase + 'dev/v0.4.2-dev.1/') -Feed $split -KeyIndex 1
        Assert-True ($r.Code -eq 0) "a dev manifest under devFeedUrl was refused: $($r.Text)"
        $manifest = [System.IO.File]::ReadAllText((Join-Path $devDir 'dev.json')) | ConvertFrom-Json
        Assert-True ($manifest.assets.exe.url -ceq ($devBase + 'dev/v0.4.2-dev.1/Clink.exe')) "the binary URL is $($manifest.assets.exe.url)"
        Assert-True (Test-Path -LiteralPath (Join-Path $devDir 'dev.json.sig')) 'no dev signature'

        $d2 = New-PackageDir 'dev-wrongbase' { param($p) $p.version = '0.4.2-dev.1' }
        $r = Invoke-MakeManifest $d2 -Channel dev -Version '0.4.2-dev.1' -BaseUrl ($stableBase + 'dev/v0.4.2-dev.1/') -Feed $split -KeyIndex 1
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'is not under the dev feed base') "a dev manifest under feedUrl was accepted: $($r.Text)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $d2 'dev.json'))) 'a manifest was written for the wrong base'

        $s1 = New-PackageDir 'stable-ok'
        $r = Invoke-MakeManifest $s1 -Channel stable -BaseUrl ($stableBase + 'releases/v0.4.2/') -Feed $split
        Assert-True ($r.Code -eq 0) "a stable manifest under feedUrl was refused: $($r.Text)"
        $s2 = New-PackageDir 'stable-wrongbase'
        $r = Invoke-MakeManifest $s2 -Channel stable -BaseUrl ($devBase + 'releases/v0.4.2/') -Feed $split
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'is not under the stable feed base') "a stable manifest under devFeedUrl was accepted: $($r.Text)"

        # Without devFeedUrl the dev channel uses feedUrl.
        $d3 = New-PackageDir 'dev-nodev' { param($p) $p.version = '0.4.2-dev.1' }
        $r = Invoke-MakeManifest $d3 -Channel dev -Version '0.4.2-dev.1' -BaseUrl 'https://downloads.example/clink/dev/v0.4.2-dev.1/' -KeyIndex 1
        Assert-True ($r.Code -eq 0) "a dev manifest under feedUrl (no devFeedUrl) was refused: $($r.Text)"
    }

    Invoke-Test 'N6: make-manifest anchors versions at both ends (case-sensitive) and ASCII digits only' {
        $dir = New-PackageDir 'n6'
        foreach ($v in '0.4.2', 'x0.4.2-dev.1', '0.4.2-dev.1x', '0.4.2-DEV.1', '0.4.2-dev.', '0.4.2-dev') {
            $r = Invoke-MakeManifest $dir -Channel dev -Version $v -BaseUrl 'https://downloads.example/clink/dev/x/' -KeyIndex 1
            Assert-True ($r.Code -ne 0 -and $r.Text -match 'needs a version like X\.Y\.Z-dev\.N') "dev version '$v' was not refused as a dev version: $($r.Text)"
        }
        foreach ($v in '0.4.2-dev.1', 'x0.4.2', '0.4.2x', '0.4') {
            $r = Invoke-MakeManifest $dir -Channel stable -Version $v
            Assert-True ($r.Code -ne 0 -and $r.Text -match 'needs a version like X\.Y\.Z\.') "stable version '$v' was not refused: $($r.Text)"
        }
        Assert-ManifestRefused 'rt-prefix' { param($p) $p.minRuntime = 'x10.0.14' } "minRuntime 'x10\.0\.14' is not X\.Y\.Z"
        Assert-ManifestRefused 'rt-suffix' { param($p) $p.minRuntime = '10.0.14-preview' } "minRuntime '10\.0\.14-preview' is not X\.Y\.Z"
        Assert-True (-not ([System.IO.File]::ReadAllText((Join-Path $toolsDir 'make-manifest.ps1')) -match '\\d')) 'make-manifest.ps1 still uses \d'
    }

    Invoke-Test 'guard: the channel base path must end in /<prefix>/ (accepts the matching layouts, refuses every mismatch before any write)' {
        Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl.example/clink/' -Prefix 'clink'
        Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl-dev.example/clink/' -Prefix 'clink'
        Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl.example/some/clink/' -Prefix 'clink'
        Assert-FeedBaseMatchesPrefix -BaseUrl 'https://pub-1.r2.dev/clink-staging/' -Prefix 'clink-staging'
        foreach ($case in @(
                @('https://dl.example/', 'clink'), @('https://dl.example', 'clink'), @('https://dl.example/clink-staging/', 'clink'), @('https://dl.example/xclink/', 'clink'),
                @('https://dl.example/Clink/', 'clink'), @('https://dl.example/clink/dev/', 'clink'), @('https://dl.example/clink/', 'clink-staging'), @('https://dl.example/other/', 'clink'),
                @('http://dl.example/clink/', 'clink'), @('https://dl.example/clink/?x=1', 'clink'), @('https://dl.example/clink//', 'clink'))) {
            Assert-Throws { Assert-FeedBaseMatchesPrefix -BaseUrl $case[0] -Prefix $case[1] } '(does not end in|not an https URL)' "base $($case[0]) prefix $($case[1])"
        }
        Assert-Throws { Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl.example/clink/' -Prefix 'clink/x' } 'not a plain name' 'prefix with a slash'
        Assert-Throws { Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl.example/clink/' -Prefix '..' } 'not a plain name' 'prefix ..'
        Assert-Throws { Assert-FeedBaseMatchesPrefix -BaseUrl 'https://dl.example/' -Prefix 'clink' } 'Nothing was written' 'the message says nothing was written'
        # Both channels of a B2 feed.json pass with the normal prefix.
        $split = New-FeedJsonFile 'guard-split' -Extra ('"devFeedUrl":"' + $devBase + '",')
        foreach ($ch in 'stable', 'dev') { Assert-FeedBaseMatchesPrefix -BaseUrl (Get-FeedConfig -Path $split -Channel $ch).FeedUrl -Prefix 'clink' }
    }
    Invoke-Test 'guard: upload-feed.ps1 runs it right after the prefix check, before the signing, any aws invoker and every bucket call; the channel base feeds the URLs' {
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        $guard = $text.IndexOf('Assert-FeedBaseMatchesPrefix -BaseUrl $feed -Prefix $prefix')
        Assert-True ($guard -gt 0) 'upload-feed.ps1 does not call the guard with the channel base and the prefix'
        Assert-True ($text.IndexOf('$prefix -cnotmatch') -lt $guard) 'the guard runs before the prefix is validated'
        foreach ($later in "Join-Path `$PSScriptRoot 'make-manifest.ps1'", 'New-FeedAwsInvoker', 'Get-FeedBucketFloor', 'Publish-FeedVersionedAsset', "'s3'", 'Invoke-FeedAws') {
            $at = $text.IndexOf($later)
            Assert-True ($at -gt $guard) "'$later' appears before the base/prefix guard"
        }
        Assert-True ($text.Contains('$feed = $cfg.FeedUrl') -and -not $text.Contains('ConfiguredFeedUrl') -and -not $text.Contains('.DevFeedUrl')) 'upload-feed.ps1 does not take the channel base from Get-FeedConfig'
        Assert-True ($text.Contains('"$feed$Channel.json"') -and $text.Contains('"$feed$folder/$($a.Name)"') -and $text.Contains('-BaseUrl "$feed$folder/"')) 'the purge, the public URLs or the manifest base do not use the channel base'
        $mm = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'make-manifest.ps1'))
        Assert-True ($mm.Contains('$BaseUrl.StartsWith($cfg.FeedUrl') -and -not $mm.Contains('ConfiguredFeedUrl')) 'make-manifest.ps1 does not bind to the channel base'
        # Each workflow reads the base of ITS channel.
        foreach ($pair in @(@('release.yml', 'stable'), @('release-dev.yml', 'dev'))) {
            $wf = [System.IO.File]::ReadAllText((Join-Path $workflowDir $pair[0]))
            Assert-True ($wf.Contains("Get-FeedConfig -Path Clink/Assets/feed.json -Channel $($pair[1])") -and $wf.Contains('feed_url=$($cfg.FeedUrl)')) "$($pair[0]): feed_url is not the $($pair[1]) channel's base"
            Assert-True ($wf -notmatch 'ConfiguredFeedUrl|\.DevFeedUrl') "$($pair[0]) reads a feed URL other than the channel's base"
        }
    }
    Invoke-Test 'guard: running upload-feed.ps1 with a base whose path does not end in /<prefix>/ fails before any bucket call (pwsh 7 only; a fake aws on PATH records every call)' {
        if ($PSVersionTable.PSVersion.Major -lt 7) { Write-Host '      (skipped: upload-feed.ps1 needs PowerShell 7; CI runs it)'; return }
        $bin = Get-ScratchFolder 'guard-bin'
        $awsLog = Join-Path $scratch 'guard-aws.log'
        Remove-Item -LiteralPath $awsLog -ErrorAction SilentlyContinue
        [System.IO.File]::WriteAllText((Join-Path $bin 'aws.cmd'), "@echo off`r`necho %* >> `"$awsLog`"`r`nexit /b 1`r`n", [System.Text.Encoding]::ASCII)
        $pkg = Get-ScratchFolder 'guard-pkg'
        $savedPath = $env:PATH
        $env:PATH = "$bin;$savedPath"
        foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') { [Environment]::SetEnvironmentVariable($n, 'x-' + $n) }
        [Environment]::SetEnvironmentVariable('R2_ENDPOINT', 'https://example.invalid')
        [Environment]::SetEnvironmentVariable('CLINK_FEED_KEY_PREFIX', $null)
        try {
            $upload = Join-Path $toolsDir 'upload-feed.ps1'
            $run = {
                param([string]$FeedFile, [string]$Channel, [string]$Version)
                $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $upload, '-Channel', $Channel, '-Version', $Version, '-PackageDir', $pkg, '-FeedJson', $FeedFile, '-HistoryFloor', '')
                $quoted = $all | ForEach-Object { if ($_ -match '\s' -or $_ -eq '') { '"' + $_ + '"' } else { $_ } }
                $id = [guid]::NewGuid().ToString('N')
                $o = Join-Path $scratch "g-out-$id.txt"; $e = Join-Path $scratch "g-err-$id.txt"
                $p = Start-Process -FilePath $hostExe -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError $e
                [pscustomobject]@{ Code = $p.ExitCode; Text = [System.IO.File]::ReadAllText($o) + [System.IO.File]::ReadAllText($e) }
            }
            # Wrong path for the default prefix 'clink': refused by the guard, aws never called.
            $bad = & $run (New-FeedJsonFile 'guard-bad' -FeedUrl 'https://dl.example/other/') 'stable' '0.4.2'
            Assert-True ($bad.Code -ne 0 -and $bad.Text -match "does not end in '/clink/'" -and $bad.Text -match 'Nothing was written') "the wrong stable base was not refused by the guard: $($bad.Text)"
            $devBad = & $run (New-FeedJsonFile 'guard-bad-dev' -Extra '"devFeedUrl":"https://dl-dev.example/clinkx/",') 'dev' '0.4.2-dev.1'
            Assert-True ($devBad.Code -ne 0 -and $devBad.Text -match "does not end in '/clink/'") "the wrong dev base was not refused by the guard: $($devBad.Text)"
            # A good base gets past the guard (and then stops at the missing manifest, still without a bucket call).
            $ok = & $run (New-FeedJsonFile 'guard-ok' -Extra '"devFeedUrl":"https://dl-dev.example/clink/",') 'dev' '0.4.2-dev.1'
            Assert-True ($ok.Code -ne 0 -and $ok.Text -notmatch 'does not end in' -and $ok.Text -match 'Missing') "the good dev base did not get past the guard: $($ok.Text)"
            Assert-True (-not (Test-Path -LiteralPath $awsLog)) "aws was called: $([System.IO.File]::ReadAllText($awsLog))"
        } finally {
            $env:PATH = $savedPath
            foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') { [Environment]::SetEnvironmentVariable($n, $null) }
        }
    }

    # ------------------------------------------------------------------ L5: SDK identity
    Invoke-Test 'dotnet-sdk-lib: only Valid + an exact observed Microsoft subject + an allow-listed root + the pinned version pass' {
        $good = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        $root = '8F43288AD272F3103B6FB1428485EA3014C0BCFE'
        function Get-Refusal { param($Status = 'Valid', $Subject = $good, $Root = $root, $Reported = '10.0.403', $Pinned = '10.0.403') Get-DotNetSdkRefusal -SignatureStatus $Status -SignerSubject $Subject -RootThumbprint $Root -ReportedVersion $Reported -PinnedVersion $Pinned }
        Assert-True ($null -eq (Get-Refusal)) 'refused a good SDK'
        Assert-True ($null -ne (Get-Refusal -Status 'NotSigned' -Subject '' -Root '')) 'accepted an unsigned dotnet'
        Assert-True ($null -ne (Get-Refusal -Status 'HashMismatch')) 'accepted a hash mismatch'
        Assert-True ($null -ne (Get-Refusal -Subject 'CN=Evil, O=Microsoft Corporation, L=Redmond, S=Washington, C=US')) 'accepted another CN'
        Assert-True ($null -ne (Get-Refusal -Subject 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US')) 'accepted the unobserved Microsoft Corporation subject'
        Assert-True ($null -ne (Get-Refusal -Subject ($good.ToLowerInvariant()))) 'subject comparison is not case-sensitive'
        Assert-True ($null -ne (Get-Refusal -Subject "$good, OU=x")) 'accepted a longer subject'
        Assert-True ($null -ne (Get-Refusal -Root '')) 'accepted an empty root thumbprint'
        Assert-True ($null -ne (Get-Refusal -Root '0000000000000000000000000000000000000000')) 'accepted another root'
        Assert-True ($null -ne (Get-Refusal -Root ($root.ToLowerInvariant()))) 'root comparison is not case-sensitive'
        Assert-True ($null -ne (Get-Refusal -Reported '10.0.402')) 'accepted another SDK version'
        Assert-True ($null -ne (Get-Refusal -Reported '10.0.404')) 'accepted a higher SDK version'
        Assert-True ($null -ne (Get-Refusal -Reported '10.0.4030')) 'accepted a version that merely starts with the pin'
        Assert-True ($null -ne (Get-Refusal -Reported '10.0.403-rc.1')) 'accepted a prerelease of the pin'
        Assert-True ($null -ne (Get-Refusal -Reported '')) 'accepted an empty version'
        Assert-True ($null -ne (Get-Refusal -Pinned '')) 'accepted an empty pin'
        Assert-True ($null -ne (Get-DotNetSdkRefusal -SignatureStatus 'Valid' -SignerSubject $good -RootThumbprint $root -ReportedVersion '10.0.403' -PinnedVersion '10.0.403' -AllowedSubjects @())) 'an empty subject allow-list accepted'
        Assert-True ($null -ne (Get-DotNetSdkRefusal -SignatureStatus 'Valid' -SignerSubject $good -RootThumbprint $root -ReportedVersion '10.0.403' -PinnedVersion '10.0.403' -AllowedRootThumbprints @())) 'an empty root allow-list accepted'
        Assert-True ($script:AllowedDotNetSdkSignerSubjects.Count -eq 1 -and $script:AllowedDotNetSdkRootThumbprints.Count -eq 1) 'the allow-lists hold more than the observed values'
    }
    Invoke-Test 'dotnet-sdk-lib: the check reads the signature, chain root and version of the resolved dotnet (DOTNET_NOLOGO, last non-empty line)' {
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\dotnet-sdk-lib.ps1'))
        foreach ($needle in 'Get-AuthenticodeSignature', '(Get-Command dotnet', '-ceq', 'global.json', '--version', 'X509Chain', 'Test-SignerRootThumbprint', "DOTNET_NOLOGO = '1'", 'last non-empty line') { Assert-True ($text.Contains($needle)) "dotnet-sdk-lib.ps1 lacks $needle" }
        Assert-True (-not $text.Contains('CN=Microsoft Corporation, O=')) 'dotnet-sdk-lib.ps1 lists an unobserved subject'
    }
    # ------------------------------------------------------------------ upload-feed.ps1 wiring (text; it needs pwsh 7 and a bucket)
    Invoke-Test 'upload-feed.ps1 uses the library, has no fallback copy for the assets, and trusts neither metadata nor Content-Length' {
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\feed-publish-lib.ps1'))
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'lib\feed-lib.ps1'))) 'the old tools/lib/feed-lib.ps1 still exists'
        Assert-True ($text.StartsWith('#requires -Version 7')) 'upload-feed.ps1 lost #requires -Version 7'
        foreach ($needle in "lib\feed-publish-lib.ps1", 'Get-FeedBucketFloor', 'Resolve-FeedEffectiveFloor', '$HistoryFloor', 'CLINK_FEED_BUCKET_RESET_VERSION', 'Publish-FeedVersionedAsset', 'Assert-FeedPublicAsset',
            '$awsInvoker = New-FeedAwsInvoker', '$httpInvoker = New-FeedHttpInvoker', "'Clink-framework.exe'", "'exeFramework'") {
            Assert-True ($text.Contains($needle)) "upload-feed.ps1 lacks $needle"
        }
        foreach ($bad in 'head-object', 'Content-Length', '.Metadata', 'falling back', 'Invoke-Aws ', 'Invoke-WebRequest', '& aws') { Assert-True (-not $text.Contains($bad)) "upload-feed.ps1 still contains $bad" }
        foreach ($bad in 'head-object', 'Content-Length', '.Metadata', 'falling back') { Assert-True (-not $lib.Contains($bad)) "feed-publish-lib.ps1 contains $bad" }
        Assert-True ($lib -notmatch "'s3', 'cp'") 'feed-publish-lib.ps1 has a plain copy fallback'
        Assert-True ($text -match "(?s)Get-FeedBucketFloor.*Publish-FeedVersionedAsset.*Assert-FeedPublicAsset.*'s3', 'cp'") 'order is not floor, assets, public check, manifest'
        Assert-True ($lib -match "(?m)^\. \(Join-Path \(Split-Path -Parent \`$PSScriptRoot\) 'feed-lib\.ps1'\)\s*$") 'the lib does not dot-source tools/feed-lib.ps1 unconditionally'
        Assert-True ($lib -notmatch 'Get-Command Test-FeedSignature') 'the dot-source of tools/feed-lib.ps1 is conditional'
    }
    Invoke-Test 'upload-feed.ps1 resets $LASTEXITCODE after the dev prune block (prune failures are warnings; the runner appends an exit-code check)' {
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        $start = $text.IndexOf("# 7. Dev only")
        Assert-True ($start -ge 0) 'upload-feed.ps1 has no step 7 (dev prune)'
        $block = $text.Substring($start)
        $reset = $block.IndexOf('$global:LASTEXITCODE = 0')
        Assert-True ($reset -gt $block.LastIndexOf("'--only-show-errors')")) 'the $LASTEXITCODE reset does not follow the prune block'
        Assert-True ($block.Substring($reset + 1) -notmatch "Invoke-FeedAws") 'an aws call follows the $LASTEXITCODE reset'
        # Behaviour: run the real step 7 text with an aws invoker whose ls/rm fail and leave a non-zero $LASTEXITCODE.
        $script:pruneCalls = @()
        $failingAws = {
            param([string[]]$Arguments)
            $script:pruneCalls += ,($Arguments -join ' ')
            $global:LASTEXITCODE = 1
            if ($Arguments[1] -eq 'ls') { return @{ Code = 1; Text = 'boom: listing' } }
            return @{ Code = 1; Text = 'boom: rm' }
        }
        $listing = ((1..12) | ForEach-Object { "                           PRE v0.4.2-dev.$_/" }) -join "`n"
        $listingAws = {
            param([string[]]$Arguments)
            $script:pruneCalls += ,($Arguments -join ' ')
            $global:LASTEXITCODE = 1
            if ($Arguments[1] -eq 'ls') { return @{ Code = 0; Text = $listing } }
            return @{ Code = 1; Text = 'boom: rm' }
        }
        foreach ($invoker in $failingAws, $listingAws) {
            $script:pruneCalls = @()
            $global:LASTEXITCODE = 0
            $Channel = 'dev'; $tag = 'v0.4.2-dev.13'; $bucket = 'b'; $prefix = 'clink'; $endpoint = 'https://example.invalid'
            $awsInvoker = $invoker
            . ([scriptblock]::Create($block)) 6>$null
            Assert-True ($script:pruneCalls.Count -ge 1) 'the fake aws invoker was never called by the prune block'
            Assert-True ($global:LASTEXITCODE -eq 0) "`$LASTEXITCODE is $($global:LASTEXITCODE) after the prune block"
        }
        Assert-True (@($script:pruneCalls | Where-Object { $_ -like 's3 rm *' }).Count -ge 1) 'the prune rm path was not exercised'
    }
    Invoke-Test 'm1: no security decision in the lib matches against a variable (an unset one would make -match pass)' {
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\feed-publish-lib.ps1'))
        Assert-True ($lib -notmatch '(?i)-(c|i)?(not)?match\s+\$') 'a -match/-notmatch against a variable remains in feed-publish-lib.ps1'
        Assert-True (-not $lib.Contains('$script:FeedNotFoundPattern')) 'FeedNotFoundPattern is back'
        Assert-True ($lib.Contains("-match 'NoSuchKey|Not Found|\(404\)'")) 'the not-found pattern is not inline'
    }
    Invoke-Test 'm2/m9: the real http invoker has a 600 s timeout and silent progress; the invokers live in the lib; the aws invoker comment is accurate' {
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\feed-publish-lib.ps1'))
        foreach ($needle in 'function New-FeedAwsInvoker', 'function New-FeedHttpInvoker', '-TimeoutSec 600', "`$ProgressPreference = 'SilentlyContinue'", '-MaximumRedirection 0') { Assert-True ($lib.Contains($needle)) "feed-publish-lib.ps1 lacks $needle" }
        Assert-True (-not $lib.Contains('never throws')) 'the inaccurate "never throws" comment is still there'
        $aws = New-FeedAwsInvoker
        $http = New-FeedHttpInvoker
        Assert-True ($aws -is [scriptblock] -and $http -is [scriptblock]) 'the invoker factories do not return script blocks'
    }
    Invoke-Test 'L1: version parsing accepts only ASCII digits (Unicode digits are refused)' {
        $arabic = ([string][char]0x0661) + '.' + ([string][char]0x0662) + '.' + ([string][char]0x0663)
        Assert-Throws { Get-VersionKey $arabic } 'Not a feed version' 'Arabic-Indic digits'
        Assert-Throws { Get-VersionKey "1.2.3`n" } 'Not a feed version' 'trailing newline'
        Assert-Throws { Get-VersionKey '1.2.3-DEV.1' } 'Not a feed version' 'upper case dev'
        Assert-True ((Get-VersionKey '10.20.30-dev.4').N -eq 4) 'a good version is refused'
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'feed-lib.ps1'))
        Assert-True (-not ($lib -match '\\d')) 'tools/feed-lib.ps1 still uses \d'
    }
    # ------------------------------------------------------------------ workflows
    function Get-RunBlock {
        param([string]$Text)
        $lines = $Text -split "`r?`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^(\s*)(?:- )?run:\s*(.*)$') {
                $indent = $Matches[1].Length
                $rest = $Matches[2]
                if ($rest -match '^[|>][-+]?\s*$') {
                    $body = New-Object System.Collections.Generic.List[string]
                    for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                        $line = $lines[$j]
                        if ($line.Trim().Length -gt 0 -and ($line.Length - $line.TrimStart().Length) -le $indent) { break }
                        $body.Add($line)
                    }
                    ($body -join "`n")
                } else { $rest }
            }
        }
    }
    function Read-Workflow { param([string]$Name) [System.IO.File]::ReadAllText((Join-Path $workflowDir $Name)) }
    function Split-Jobs {
        param([string]$Text)
        $b = $Text.IndexOf("`n  build:"); $p = $Text.IndexOf("`n  publish:")
        Assert-True ($b -ge 0 -and $p -gt $b) 'workflow has no build and publish job'
        [pscustomobject]@{ Head = $Text.Substring(0, $b); Build = $Text.Substring($b, $p - $b); Publish = $Text.Substring($p) }
    }
    $allWorkflows = @(Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml')

    Invoke-Test 'workflows: every uses: is pinned to 40 hex, no run: script has a ${{ }} expression, no permission was widened' {
        $uses = 0; $blocks = 0
        foreach ($wf in $allWorkflows) {
            $text = [System.IO.File]::ReadAllText($wf.FullName)
            foreach ($line in ($text -split "`r?`n")) {
                if ($line -match '^\s*(?:- )?uses:\s*(\S+)') {
                    $uses++
                    Assert-True ($Matches[1] -cmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$') "$($wf.Name): not pinned: $($line.Trim())"
                }
            }
            foreach ($block in @(Get-RunBlock $text)) {
                $blocks++
                Assert-True (-not $block.Contains('${{')) "$($wf.Name): a run: script has a template expression"
            }
            Assert-True ($text -notmatch '(?m)^\s*(id-token|actions|packages|pull-requests|checks|issues|deployments|statuses|security-events|attestations|discussions|pages):\s*write') "$($wf.Name): a write permission beyond contents was added"
            Assert-True ($text -notmatch 'permissions:\s*write-all') "$($wf.Name): write-all"
        }
        Assert-True ($uses -ge 10 -and $blocks -ge 20) "only $uses uses: and $blocks run: scripts found (parser broken?)"
        $rel = Read-Workflow 'release.yml'
        Assert-True ([regex]::Matches($rel, '(?m)^\s*contents:\s*write').Count -eq 1) 'release.yml: contents: write appears other than once (publish job only)'
        Assert-True ([regex]::Matches((Read-Workflow 'release-dev.yml'), '(?m)^\s*contents:\s*write').Count -eq 1) 'release-dev.yml: contents: write appears other than once'
    }

    Invoke-Test 'N3(b): release.yml has no workflow_dispatch and no branch trigger or branch release path; only vX.Y.Z tags ship' {
        $text = Read-Workflow 'release.yml'
        Assert-True (-not $text.Contains('workflow_dispatch')) 'release.yml still has workflow_dispatch'
        Assert-True ($text -notmatch 'refs/heads') 'release.yml still has a refs/heads path'
        Assert-True ($text -notmatch '(?m)^\s+branches:') 'release.yml has a branches trigger'
        Assert-True ($text -notmatch '(?i)manual run') 'release.yml still talks about manual runs'
        $trigger = $text.Substring($text.IndexOf("`non:"), $text.IndexOf("`npermissions:") - $text.IndexOf("`non:"))
        Assert-True ($trigger -match "(?s)^\s*on:\s+push:\s+tags:\s+- 'v\*\.\*\.\*'\s+- '!v\*-dev\.\*'[^\n]*\s*$") "the on: block is not just the stable tag push: $trigger"
        $jobs = Split-Jobs $text
        Assert-True ($jobs.Build.Contains("-cnotmatch '^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$'")) 'the build job does not require a final-release tag'
        Assert-True ($jobs.Publish.Contains("-cnotmatch '^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$'")) 'the publish guard does not require a final-release tag'
        Assert-True ($text -notmatch '\\d') 'release.yml still has a \d in a guard'
        Assert-True ($text -notmatch '(?i)prerelease:\s*\$\{\{' -and $text -cnotmatch 'PRERELEASE' -and $text -notmatch 'outputs\.prerelease' -and $text -cnotmatch 'prerelease=') 'release.yml still has the unreachable PRERELEASE branch'
        $arabic = ([string][char]0x0661) + '.' + ([string][char]0x0662) + '.' + ([string][char]0x0663)
        foreach ($ref in "refs/tags/v$arabic", 'refs/tags/v1.2.3-dev.1', 'refs/tags/v1.2', 'refs/tags/v1.2.3x', 'refs/heads/main') {
            Assert-True ($ref -cnotmatch '^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$') "the tag guard accepts $ref"
        }
        Assert-True (("refs/tags/v$arabic") -cnotmatch '^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$') 'the tag guard accepts Arabic-Indic digits'
        Assert-True ('refs/tags/v1.2.3' -cmatch '^refs/tags/v[0-9]+\.[0-9]+\.[0-9]+$') 'the tag guard refuses a good tag'
        $dev = Read-Workflow 'release-dev.yml'
        Assert-True ($dev -notmatch '\\d') 'release-dev.yml still has a \d'
        Assert-True ($dev.Contains("'^[0-9]+\.[0-9]+\.[0-9]+-dev\.[0-9]+$'")) 'release-dev.yml lost the dev version guard'
        Assert-True ($jobs.Publish.Contains('merge-base --is-ancestor')) 'the publish guard lost the commit-on-main check'
    }

    Invoke-Test 'NEW-6: per-channel secret names map to the env names the scripts read (stable and dev never cross)' {
        $map = @{ 'release.yml' = @('STABLE', 'R2_STABLE_', 'DEV'); 'release-dev.yml' = @('DEV', 'R2_DEV_', 'STABLE') }
        foreach ($name in $map.Keys) {
            $text = Read-Workflow $name
            $ch = $map[$name][0]; $other = $map[$name][2]
            foreach ($pair in @(@('R2_ACCESS_KEY_ID', "R2_${ch}_ACCESS_KEY_ID"), @('R2_SECRET_ACCESS_KEY', "R2_${ch}_SECRET_ACCESS_KEY"), @('R2_ENDPOINT', "R2_${ch}_ENDPOINT"), @('R2_BUCKET', "R2_${ch}_BUCKET"), @('CLINK_FEED_SIGNING_KEY', "CLINK_FEED_${ch}_SIGNING_KEY"))) {
                $line = $pair[0] + ': ${{ secrets.' + $pair[1] + ' }}'
                Assert-True ($text.Contains($line)) "$name lacks '$line'"
            }
            Assert-True ($text -notmatch ('secrets\.R2_' + $other + '_')) "$name uses the $other R2 secrets"
            Assert-True ($text -notmatch ('secrets\.CLINK_FEED_' + $other + '_SIGNING_KEY')) "$name uses the $other signing key"
            Assert-True ($text -notmatch 'secrets\.R2_(ACCESS_KEY_ID|SECRET_ACCESS_KEY|ENDPOINT|BUCKET)\b') "$name still reads a channel-less R2 secret"
            Assert-True ($text -notmatch 'secrets\.CLINK_FEED_SIGNING_KEY') "$name still reads the channel-less signing key"
            Assert-True ((Split-Jobs $text).Build -notmatch 'secrets\.') "$name build job references secrets"
        }
        # The scripts keep their env contract.
        $upload = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        foreach ($n in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') { Assert-True ($upload.Contains($n)) "upload-feed.ps1 no longer reads $n" }
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $toolsDir 'make-manifest.ps1')).Contains('$env:CLINK_FEED_SIGNING_KEY')) 'make-manifest.ps1 no longer reads CLINK_FEED_SIGNING_KEY'
    }

    Invoke-Test 'L1: pkg_sha256 flows from the package step through a job output into a publish check before the manifest is made' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = Read-Workflow $name
            $jobs = Split-Jobs $text
            Assert-True ($text.Contains('pkg_sha256: ${{ steps.package.outputs.pkg_sha256 }}')) "$name build job lacks the pkg_sha256 output"
            Assert-True ($jobs.Build.Contains('pkg_sha256=$((Get-FileHash dist/package/package.json -Algorithm SHA256).Hash.ToLowerInvariant())')) "$name package step does not hash package.json"
            Assert-True ($jobs.Publish.Contains('PKG_SHA256: ${{ needs.build.outputs.pkg_sha256 }}')) "$name publish job lacks PKG_SHA256"
            $check = $jobs.Publish.IndexOf('dist/package/package.json -Algorithm SHA256')
            Assert-True ($check -gt 0 -and $jobs.Publish.Contains('-ne $env:PKG_SHA256')) "$name publish job does not compare package.json with PKG_SHA256"
            Assert-True ($check -lt $jobs.Publish.IndexOf('make-manifest.ps1')) "$name verifies package.json after the manifest is made"
            Assert-True ($check -gt $jobs.Publish.IndexOf('Download package artifact')) "$name verifies package.json before the artifact is downloaded"
        }
    }

    Invoke-Test 'NEW-3: both publish jobs refuse a feed-less publish once a release carried <channel>.json (read-only gh api, paginated)' {
        foreach ($pair in @(@('release.yml', 'stable'), @('release-dev.yml', 'dev'))) {
            $text = Read-Workflow $pair[0]
            $jobs = Split-Jobs $text
            $at = $jobs.Publish.IndexOf('- name: Guard - a feed that was on stays on')
            Assert-True ($at -gt 0) "$($pair[0]): the publish job lacks the step"
            Assert-True ($jobs.Build.IndexOf('stays on') -lt 0) "$($pair[0]): the step is in the build job"
            $end = $jobs.Publish.IndexOf("`n      # Fail before", $at)
            Assert-True ($end -gt $at) "$($pair[0]): cannot delimit the guard step"
            $step = $jobs.Publish.Substring($at, $end - $at)
            foreach ($needle in "if: steps.feed.outputs.enabled != 'true'", 'gh api "repos/$env:GITHUB_REPOSITORY/releases?per_page=100" --paginate', "--jq '.[].assets[].name'", ". ./tools/lib/feed-publish-lib.ps1", "Assert-FeedNeverWasOn -Channel $($pair[1]) -AssetNames `$names") {
                Assert-True ($step.Contains($needle)) "$($pair[0]): step lacks $needle"
            }
            Assert-True ($step -notmatch '(?i)-X\s*(POST|PUT|PATCH|DELETE)|--method|gh release (create|edit|delete|upload)') "$($pair[0]): the guard step is not read-only"
            Assert-True ($step -cnotmatch '\s-(f|F)\s|--field|--raw-field|--input|-X|--method') "$($pair[0]): the guard step sends a body or a method override"
            # Security M1: the history-floor step is a second read-only listing, with the tag, draft flag and asset names.
            $hAt = $jobs.Publish.IndexOf('- name: Find the GitHub history floor')
            Assert-True ($hAt -gt 0) "$($pair[0]): the publish job lacks the history floor step"
            $hEnd = $jobs.Publish.IndexOf("`n      # ", $hAt)
            Assert-True ($hEnd -gt $hAt) "$($pair[0]): cannot delimit the history floor step"
            $hist = $jobs.Publish.Substring($hAt, $hEnd - $hAt)
            foreach ($needle in "if: steps.feed.outputs.enabled == 'true'", 'id: history', 'gh api "repos/$env:GITHUB_REPOSITORY/releases?per_page=100" --paginate', '.tag_name, (.draft | tostring)', '.assets[].name', '@tsv',
                ". ./tools/lib/feed-publish-lib.ps1", "Get-FeedHistoryFloor -Channel $($pair[1]) -Listing `$listing", 'floor=$floor') {
                Assert-True ($hist.Contains($needle)) "$($pair[0]): history step lacks $needle"
            }
            Assert-True ($hist -cnotmatch '\s-(f|F)\s|--field|--raw-field|--input|-X|--method|gh release (create|edit|delete|upload)') "$($pair[0]): the history step is not GET-only"
            Assert-True ($hAt -gt $jobs.Publish.IndexOf('id: feed') -and $hAt -lt $jobs.Publish.IndexOf('Create GitHub release (draft)')) "$($pair[0]): the history step is misplaced"
            $upload = $jobs.Publish.Substring($jobs.Publish.IndexOf('- name: Publish to the update feed'))
            $uploadStep = $upload.Substring(0, $upload.IndexOf("`n      - name:", 10))
            foreach ($needle in 'HISTORY_FLOOR: ${{ steps.history.outputs.floor }}', 'CLINK_FEED_BUCKET_RESET_VERSION: ${{ vars.CLINK_FEED_BUCKET_RESET_VERSION }}', '-HistoryFloor $env:HISTORY_FLOOR', 'timeout-minutes: 30') {
                Assert-True ($uploadStep.Contains($needle)) "$($pair[0]): the publish step lacks $needle"
            }
            Assert-True ($uploadStep -notmatch 'secrets\.CLINK_FEED_BUCKET_RESET') "$($pair[0]): the reset override is a secret"
            Assert-True ($at -gt $jobs.Publish.IndexOf('id: feed')) "$($pair[0]): the guard runs before the feed configuration is read"
            Assert-True ($at -lt $jobs.Publish.IndexOf('Create GitHub release (draft)')) "$($pair[0]): the guard runs after the draft release is created"
            Assert-True ($at -lt $jobs.Publish.IndexOf('Check bucket secrets')) "$($pair[0]): the guard runs after the secrets are referenced"
        }
    }

    Invoke-Test 'L5: every actions/setup-dotnet is followed by the SDK Authenticode and version step, in all three workflows' {
        $total = 0
        foreach ($wf in $allWorkflows) {
            $text = [System.IO.File]::ReadAllText($wf.FullName)
            $setups = [regex]::Matches($text, 'actions/setup-dotnet@[0-9a-f]{40}').Count
            $verified = [regex]::Matches($text, "(?s)actions/setup-dotnet@[0-9a-f]{40}[^\n]*\n\s+with:\n\s+global-json-file: global\.json\n\s*\n(?:\s*#[^\n]*\n)*\s*- name: Verify the \.NET SDK[^\n]*\n\s+shell: pwsh\n\s+run: \|\n\s+\. \./tools/lib/dotnet-sdk-lib\.ps1\n\s+Assert-DotNetSdk -GlobalJsonPath global\.json").Count
            Assert-True ($setups -ge 1) "$($wf.Name) has no setup-dotnet"
            Assert-True ($setups -eq $verified) "$($wf.Name): $setups setup-dotnet but $verified are followed by the verification step"
            $total += $setups
        }
        Assert-True ($total -ge 4) "only $total setup-dotnet occurrences (build.yml has two jobs)"
        Assert-True ((Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml').Count -eq 3) 'a workflow was added: extend this test'
    }

    # ------------------------------------------------------------------ release workflow hardening (L2, L3, L4, prune, Node 24, NITs)
    function Get-Steps {
        # The steps of a job text: each starts at a "      - name:" line and runs to the next one (comments between steps stay at the tail of the previous step).
        param([string]$JobText)
        @([regex]::Split($JobText, '(?m)^(?=      - name: )') | Where-Object { $_.StartsWith('      - name: ') })
    }
    function Get-Step {
        param([string]$JobText, [string]$Name)
        $found = @(Get-Steps $JobText | Where-Object { $_.StartsWith("      - name: $Name") })
        Assert-True ($found.Count -eq 1) "expected exactly one step starting with '$Name', found $($found.Count)"
        $found[0]
    }
    function Get-StepRun {
        param([string]$StepText)
        $blocks = @(Get-RunBlock $StepText)
        Assert-True ($blocks.Count -eq 1) "expected one run: block in the step, found $($blocks.Count)"
        $blocks[0]
    }
    function Test-StepCallsGh {
        param([string]$StepText)
        [bool](@(Get-RunBlock $StepText) | Where-Object { $_ -match '(?<![\w.-])gh\s+(api|release)\b' })
    }
    $tokenLine = 'GH_TOKEN: ${{ github.token }}'

    Invoke-Test 'L4: the write token is step-scoped in both publish jobs: only steps that call gh name GH_TOKEN, the secret/aws/upload steps never do' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $jobs = Split-Jobs (Read-Workflow $name)
            $firstStep = $jobs.Publish.IndexOf('      - name: ')
            $header = (($jobs.Publish.Substring(0, $firstStep) -split "`r?`n") | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"
            Assert-True ($header -notmatch 'GH_TOKEN' -and $header -notmatch 'github\.token') "$name : the publish job header still has a job-wide token"
            Assert-True ($header.Contains('GH_REPO: ${{ github.repository }}') -and $header.Contains('contents: write')) "$name : the publish header lost GH_REPO or the permission"
            $withToken = 0
            foreach ($step in (Get-Steps $jobs.Publish)) {
                $title = ($step -split "`n")[0].Trim()
                $callsGh = Test-StepCallsGh $step
                $hasToken = $step.Contains($tokenLine)
                if ($callsGh) { Assert-True $hasToken "$name : '$title' calls gh without naming GH_TOKEN" }
                if ($hasToken) { Assert-True $callsGh "$name : '$title' names GH_TOKEN but calls no gh"; $withToken++ }
                if ($step -match 'secrets\.' -or $step -match 'upload-feed\.ps1|make-manifest\.ps1|aws ') {
                    Assert-True ((-not $hasToken) -and ($step -notmatch 'github\.token|GH_TOKEN')) "$name : '$title' holds secrets or runs aws/upload-feed and also sees the write token"
                }
            }
            Assert-True ($withToken -ge 5) "$name : only $withToken steps name the token (parser broken?)"
            foreach ($secretStep in 'Check bucket secrets', 'Make and sign the manifest', 'Publish to the update feed') {
                $s = Get-Step $jobs.Publish $secretStep
                Assert-True ($s -notmatch 'GH_TOKEN|github\.token') "$name : '$secretStep' sees the write token"
            }
        }
    }

    Invoke-Test 'L2: release-dev.yml refuses an existing tag (HTTP 404 only), ties the draft to GITHUB_SHA and checks the tag after publishing' {
        $dev = Read-Workflow 'release-dev.yml'
        $pub = (Split-Jobs $dev).Publish
        $guard = Get-Step $pub 'Guard - the tag does not exist yet'
        $run = Get-StepRun $guard
        foreach ($needle in 'git/ref/tags/v$env:VERSION', '$refCode -eq 0) { throw', "-cnotmatch '\(HTTP 404\)'", '$global:LASTEXITCODE = 0') { Assert-True ($run.Contains($needle)) "the tag guard lacks $needle" }
        Assert-True ($pub.IndexOf('Guard - the tag does not exist yet') -lt $pub.IndexOf('Create GitHub release (draft)') -and $pub.IndexOf('Guard - the tag does not exist yet') -lt $pub.IndexOf('Download package artifact')) 'the tag guard runs late'
        Assert-True ($run.IndexOf('$global:LASTEXITCODE = 0') -gt $run.LastIndexOf('& gh api')) 'a gh call follows the LASTEXITCODE reset in the tag guard'
        foreach ($sample in @('gh: Not Found (HTTP 404)', "x`n(HTTP 404)`n")) { Assert-True ($sample -cmatch '\(HTTP 404\)') '404 text is not recognised' }
        foreach ($sample in @('gh: Forbidden (HTTP 403)', 'HTTP 500', '')) { Assert-True ($sample -cnotmatch '\(HTTP 404\)') "'$sample' is taken for a 404" }
        $verify = Get-StepRun (Get-Step $pub 'Verify the release assets')
        Assert-True ($verify.Contains('$builtCommit = [string]$release.target_commitish') -and $verify.Contains('-ceq $env:GITHUB_SHA')) 'the verify step does not compare target_commitish with GITHUB_SHA'
        Assert-True ($dev -match 'target_commitish: \$\{\{ github\.sha \}\}') 'the draft is no longer created at github.sha'
        $order = @('Create GitHub release (draft)', 'Verify the release assets', 'Publish to the update feed', 'Publish the GitHub release', 'Verify the published tag', 'Prune old dev builds') | ForEach-Object { $pub.IndexOf("- name: $_") }
        Assert-True (($order | Where-Object { $_ -lt 0 }).Count -eq 0) 'a publish step is missing'
        for ($i = 1; $i -lt $order.Count; $i++) { Assert-True ($order[$i] -gt $order[$i - 1]) 'the publish steps are out of order' }
        $after = Get-StepRun (Get-Step $pub 'Verify the published tag')
        foreach ($needle in 'git/ref/tags/v$env:VERSION', "-ceq 'commit'", '$ref.object.sha -ceq $env:GITHUB_SHA') { Assert-True ($after.Contains($needle)) "the post-publish tag check lacks $needle" }
        Assert-True (-not (Read-Workflow 'release.yml').Contains('the tag does not exist yet')) 'release.yml (the tag is made by the owner) got the dev tag guard'
    }

    Invoke-Test 'L3: both verify steps fail on a missing digest and on any asset outside the required list (run against a fake gh)' {
        $savedEnv = @{}
        foreach ($n in 'GITHUB_REPOSITORY', 'RELEASE_ID', 'VERSION', 'GITHUB_SHA') { $savedEnv[$n] = [Environment]::GetEnvironmentVariable($n) }
        $work = Get-ScratchFolder 'verify-work'
        $pkgDir = Join-Path $work 'dist\package'
        New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null
        $names = 'Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'Clink-Setup.exe', 'Clink-Setup.exe.sha256', 'THIRD-PARTY-NOTICES.txt'
        foreach ($n in $names) { [System.IO.File]::WriteAllBytes((Join-Path $pkgDir $n), (New-RandomBytes 300)) }
        $sha = 'a' * 40
        function New-ReleaseJson {
            param([string]$Version, [bool]$Prerelease, [string]$Commit, [scriptblock]$Mutate)
            $assets = foreach ($n in $names) {
                $f = Join-Path $pkgDir $n
                [pscustomobject]@{ name = $n; state = 'uploaded'; size = (Get-Item -LiteralPath $f).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
            $rel = [pscustomobject]@{ draft = $true; tag_name = "v$Version"; prerelease = $Prerelease; target_commitish = $Commit; assets = @($assets) }
            if ($Mutate) { & $Mutate $rel }
            $rel | ConvertTo-Json -Depth 5
        }
        try {
            [Environment]::SetEnvironmentVariable('GITHUB_REPOSITORY', 'o/r'); [Environment]::SetEnvironmentVariable('RELEASE_ID', '123'); [Environment]::SetEnvironmentVariable('GITHUB_SHA', $sha)
            Push-Location $work
            foreach ($case in @(@('release.yml', '0.4.2', $false), @('release-dev.yml', '0.4.2-dev.1', $true))) {
                $wfName = $case[0]; $version = $case[1]; $pre = $case[2]
                [Environment]::SetEnvironmentVariable('VERSION', $version)
                $block = Get-StepRun (Get-Step (Split-Jobs (Read-Workflow $wfName)).Publish 'Verify the release assets')
                $script:fakeJson = ''
                function gh { $global:LASTEXITCODE = 0; $script:fakeJson }
                $runIt = { . ([scriptblock]::Create($block)) 6>$null | Out-Null }
                $script:fakeJson = New-ReleaseJson $version $pre $sha
                & $runIt   # the control: a complete, correct release passes
                $script:fakeJson = New-ReleaseJson $version $pre $sha { param($r) $r.assets[2].digest = $null }
                Assert-Throws $runIt 'no digest for asset Clink-framework\.exe' "$wfName : a missing digest"
                $script:fakeJson = New-ReleaseJson $version $pre $sha { param($r) $r.assets[0].digest = 'sha256:' + ('0' * 64) }
                Assert-Throws $runIt 'has digest sha256:0+ on GitHub' "$wfName : a wrong digest"
                $script:fakeJson = New-ReleaseJson $version $pre $sha { param($r) $r.assets = @($r.assets) + [pscustomobject]@{ name = 'evil.dll'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) } }
                Assert-Throws $runIt 'unexpected assets: evil\.dll' "$wfName : an extra asset"
                $script:fakeJson = New-ReleaseJson $version $pre $sha { param($r) $r.assets = @($r.assets) + [pscustomobject]@{ name = 'clink.exe'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) } }
                Assert-Throws $runIt 'unexpected assets: clink\.exe' "$wfName : a case variant of a required name"
                $script:fakeJson = New-ReleaseJson $version $pre $sha { param($r) $r.assets = @($r.assets | Select-Object -First 6) }
                Assert-Throws $runIt 'lacks exactly one asset named THIRD-PARTY-NOTICES\.txt' "$wfName : a missing asset"
                if ($wfName -eq 'release-dev.yml') {
                    $script:fakeJson = New-ReleaseJson $version $pre ('b' * 40)
                    Assert-Throws $runIt 'not at the built commit' "$wfName : a draft at another commit"
                    $script:fakeJson = New-ReleaseJson $version $pre 'dev'
                    Assert-Throws $runIt 'not at the built commit' "$wfName : a draft whose target is a branch name"
                }
            }
        } finally {
            Pop-Location
            foreach ($n in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($n, $savedEnv[$n]) }
        }
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $step = Get-Step (Split-Jobs (Read-Workflow $name)).Publish 'Verify the release assets'
            Assert-True ($step -notmatch '::warning::') "$name : the verify step still downgrades a missing digest to a warning"
        }
    }

    Invoke-Test 'prune (a)(b): the dev prune keeps the 10 newest PUBLISHED dev releases, pages through all releases, skips drafts and look-alikes, and handles immutable tags (run against a fake gh)' {
        $dev = Read-Workflow 'release-dev.yml'
        $step = Get-Step (Split-Jobs $dev).Publish 'Prune old dev builds'
        Assert-True ($step.Contains('if: success()') -and $step.Contains('continue-on-error: true') -and $step -notmatch '!cancelled') 'the prune step is not "if: success()" with continue-on-error'
        $block = Get-StepRun $step
        Assert-True ($block.Contains('--paginate') -and -not $block.Contains('--cleanup-tag') -and $block.Contains("-cmatch '\Av[0-9]+\.[0-9]+\.[0-9]+-dev\.([0-9]+)\z'")) 'the prune script lacks pagination or the anchored case-sensitive match, or still uses --cleanup-tag'
        $savedRepo = $env:GITHUB_REPOSITORY
        $env:GITHUB_REPOSITORY = 'o/r'
        $lines = New-Object System.Collections.ArrayList
        foreach ($n in 1..13) { [void]$lines.Add("v0.4.2-dev.$n`tfalse") }            # 13 published dev releases: dev.1-3 are to go
        [void]$lines.Add("v0.4.2-dev.40`ttrue")                                        # a draft from a failed run: not counted, not deleted
        [void]$lines.Add("v0.4.1`tfalse"); [void]$lines.Add("v0.4.2-dev.5x`tfalse"); [void]$lines.Add("V0.4.2-dev.1`tfalse"); [void]$lines.Add("v0.4.2-dev.1`tmaybe")
        $script:ghCalls = New-Object System.Collections.ArrayList
        $script:ghListing = $lines.ToArray()
        $script:ghFailRelease = @(); $script:ghFailTag = @()
        function gh {
            [void]$script:ghCalls.Add(($args -join ' '))
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'api' -and $args -contains '--paginate') { return $script:ghListing }
            if ($args[0] -eq 'release' -and $args[1] -eq 'delete') { if ($script:ghFailRelease -contains $args[2]) { $global:LASTEXITCODE = 1 }; return }
            if ($args[0] -eq 'api' -and $args[1] -eq '-X' -and $args[2] -eq 'DELETE') {
                $tag = ([string]$args[3]).Split('/')[-1]
                if ($script:ghFailTag -contains $tag) { $global:LASTEXITCODE = 1; return "gh: Tag is protected by an immutable release (HTTP 422)" }
                return
            }
            $global:LASTEXITCODE = 99
        }
        $runIt = { $out = . ([scriptblock]::Create($block)); $out }
        try {
            # Normal run: the three oldest published releases are deleted (release first, then the tag), nothing else.
            $global:LASTEXITCODE = 0
            [void](& $runIt)
            $deleted = @($script:ghCalls | Where-Object { $_ -like 'release delete *' })
            Assert-True ($deleted.Count -eq 3) "expected 3 release deletions, got $($deleted.Count): $($deleted -join ' | ')"
            foreach ($n in 1..3) { Assert-True ($deleted -contains "release delete v0.4.2-dev.$n --yes --repo o/r") "dev.$n was not deleted: $($deleted -join ' | ')" }
            Assert-True ($script:ghCalls[0] -like 'api repos/o/r/releases?per_page=100 --paginate*') "the listing call is $($script:ghCalls[0])"
            $tagCalls = @($script:ghCalls | Where-Object { $_ -like 'api -X DELETE *' })
            Assert-True ($tagCalls.Count -eq 3 -and $tagCalls[0] -eq 'api -X DELETE repos/o/r/git/refs/tags/v0.4.2-dev.3') "tag deletions: $($tagCalls -join ' | ')"
            Assert-True ($global:LASTEXITCODE -eq 0) "LASTEXITCODE is $($global:LASTEXITCODE) after a clean prune"
            # Immutable releases: the tag cannot be removed. A warning, the loop goes on, the step still succeeds.
            $script:ghCalls.Clear(); $script:ghFailTag = @('v0.4.2-dev.2'); $global:LASTEXITCODE = 0
            $out = (& $runIt) -join "`n"
            Assert-True (@($script:ghCalls | Where-Object { $_ -like 'release delete *' }).Count -eq 3) 'a protected tag stopped the prune'
            Assert-True ($out -match '::warning::The tag v0\.4\.2-dev\.2 was not removed') "no warning for the protected tag: $out"
            Assert-True ($global:LASTEXITCODE -eq 0) 'a protected tag left a non-zero LASTEXITCODE'
            # A release that cannot be deleted: counted, the others still go, its tag is left alone, the step fails at the end.
            $script:ghCalls.Clear(); $script:ghFailTag = @(); $script:ghFailRelease = @('v0.4.2-dev.2'); $global:LASTEXITCODE = 0
            Assert-Throws { [void](& $runIt) } '1 old dev release\(s\) could not be deleted' 'a failed release delete'
            Assert-True (@($script:ghCalls | Where-Object { $_ -like 'release delete *' }).Count -eq 3) 'a failed delete stopped the prune'
            Assert-True (-not ($script:ghCalls -contains 'api -X DELETE repos/o/r/git/refs/tags/v0.4.2-dev.2')) 'the tag of a release that was not deleted was removed'
            # Ten or fewer published dev releases: nothing happens (13 listed, but only drafts/look-alikes beyond the 10 newest published ones).
            $script:ghCalls.Clear(); $script:ghFailRelease = @(); $script:ghListing = @(1..10 | ForEach-Object { "v0.4.2-dev.$_`tfalse" }) + @("v0.4.2-dev.77`ttrue", "v0.4.2-dev.78`ttrue")
            [void](& $runIt)
            Assert-True (@($script:ghCalls | Where-Object { $_ -like 'release delete *' }).Count -eq 0) 'drafts were counted among the 10 kept (or deleted)'
            # A failed listing fails the step.
            function gh { $global:LASTEXITCODE = 1 }
            Assert-Throws { [void](& $runIt) } 'Could not list the releases' 'a failed listing'
        } finally { $env:GITHUB_REPOSITORY = $savedRepo }
    }

    Invoke-Test 'main hard-code: release.yml checks origin/main literally, with no default-branch lookup' {
        $text = Read-Workflow 'release.yml'
        Assert-True ($text -notmatch '(?i)default_branch|DEFAULT_BRANCH|\$default\b') 'release.yml still reads the default branch'
        $guard = Get-Step (Split-Jobs $text).Publish 'Guard - stable tag, commit on main, release not yet there'
        Assert-True ($guard.Contains('git merge-base --is-ancestor $env:GITHUB_SHA origin/main') -and $guard.Contains('is not on origin/main')) 'the guard does not test origin/main literally'
    }

    Invoke-Test '(iv): the stable latest-release step and the dev version step log which releases/latest path they took (200 or 404)' {
        $decide = Get-StepRun (Get-Step (Split-Jobs (Read-Workflow 'release.yml')).Publish 'Decide whether this release becomes latest')
        $version = Get-StepRun (Get-Step (Split-Jobs (Read-Workflow 'release-dev.yml')).Build 'Determine version')
        foreach ($run in $decide, $version) {
            Assert-True ($run.Contains('(200 path)') -and $run.Contains('(404 path)')) 'a releases/latest step does not log its path'
        }
    }

    Invoke-Test 'Node 24: every action is pinned to a release line whose action.yml says node24 (no Node 20 pin left), pinned by 40-hex SHA with a version comment' {
        $minMajor = @{ 'actions/checkout' = 5; 'actions/setup-dotnet' = 5; 'softprops/action-gh-release' = 3; 'actions/cache' = 5; 'actions/upload-artifact' = 6; 'actions/download-artifact' = 6 }
        $nodeTwenty = '11bd71901bbe5b1630ceea73d27597364c9af683', '67a3573c9a986a3f9c594539f4ab511d57bb3ce9', 'da05d552573ad5aba039eaac05058a918a7bf631', '5a3ec84eff668545956fd18022155c47e93e2684'
        $seen = 0
        foreach ($wf in $allWorkflows) {
            foreach ($line in ([System.IO.File]::ReadAllText($wf.FullName) -split "`r?`n")) {
                if ($line -match '^\s*(?:- )?uses:\s*(\S+)@([0-9a-f]{40})\s+#\s*v(\d+)\.(\d+)\.(\d+)\s*$') {
                    $seen++
                    $repoName = $Matches[1]
                    Assert-True ($minMajor.ContainsKey($repoName)) "$($wf.Name): unknown action $repoName (add it to this test with its first node24 major)"
                    Assert-True ([int]$Matches[3] -ge $minMajor[$repoName]) "$($wf.Name): $repoName v$($Matches[3]) predates its node24 line"
                    Assert-True ($nodeTwenty -cnotcontains $Matches[2]) "$($wf.Name): $repoName is pinned to a Node 20 commit"
                } elseif ($line -match '^\s*(?:- )?uses:') { throw "$($wf.Name): a uses: line without a SHA pin and a '# vX.Y.Z' comment: $($line.Trim())" }
            }
        }
        Assert-True ($seen -ge 12) "only $seen pinned actions found"
    }

    Invoke-Test 'NITs: the dev build logs the same Media Foundation files as build.yml (mfh264enc, msmpeg2vdec); the release workflows never carry a test-only switch or the dummy runtime hash' {
        foreach ($name in 'build.yml', 'release-dev.yml') {
            $text = Read-Workflow $name
            Assert-True ($text.Contains("'mfplat.dll', 'mfmp4srcsnk.dll', 'msauddecmft.dll', 'mfh264enc.dll', 'msmpeg2vdec.dll'")) "$name does not log the five Media Foundation files"
        }
        $dev = Read-Workflow 'release-dev.yml'
        Assert-True ($dev.IndexOf('- name: Windows and Media Foundation versions') -lt $dev.IndexOf('- name: Unit tests')) 'the version log runs after the unit tests'
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = Read-Workflow $name
            foreach ($bad in '-SkipSetupSizeCheck', '-SkipVersionCheck', '-AllowMissingRuntimePacks', '-SkipInstaller', "'0' * 64", ('0' * 64)) { Assert-True (-not $text.Contains($bad)) "$name contains $bad (test-only / dry-run switch)" }
        }
        $build = Read-Workflow 'build.yml'
        Assert-True ($build -notmatch '\\d') 'build.yml still uses \d'
        Assert-True ($build.Contains("-cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z'")) 'build.yml dry-run version check is not anchored'
    }

    Invoke-Test 'N4: the history floor takes only whole tags (a trailing newline or a prefix does not pass)' {
        Assert-Throws { Get-FeedHistoryFloor -Channel dev -Listing @("v0.4.2-dev.1`n`tfalse`tdev.json") } 'not a feed version' 'trailing newline inside the tag field'
        Assert-Throws { Get-FeedHistoryFloor -Channel dev -Listing @("xv0.4.2-dev.1`tfalse`tdev.json") } 'not a feed version' 'prefix'
        Assert-Throws { Get-FeedHistoryFloor -Channel dev -Listing @("v0.4.2-dev.1x`tfalse`tdev.json") } 'not a feed version' 'suffix'
        Assert-Throws { Get-FeedHistoryFloor -Channel dev -Listing @("v0.4.2-DEV.1`tfalse`tdev.json") } 'not a feed version' 'upper case'
        Assert-True ((Get-FeedHistoryFloor -Channel dev -Listing @("v0.4.2-dev.1`tfalse`tdev.json")) -ceq '0.4.2-dev.1') 'a good tag is refused'
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\feed-publish-lib.ps1'))
        Assert-True ($lib.Contains("'\Av[0-9]+\.[0-9]+\.[0-9]+(-dev\.[0-9]+)?\z'")) 'the lib history regex is not \A..\z'
        $up = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        Assert-True ($up.Contains("-cmatch '\A\s*PRE (v[0-9]+\.[0-9]+\.[0-9]+-dev\.([0-9]+))/\s*\z'")) 'the bucket prune pattern is not anchored and case-sensitive'
    }

    Invoke-Test 'RELEASING.md documents the allow-list, the secret names, the one-way feed and the new checks' {
        $doc = [System.IO.File]::ReadAllText((Join-Path $repo 'docs\RELEASING.md'))
        foreach ($needle in 'v[0-9].[0-9].[0-9]', 'v[0-9].[0-9].[0-9][0-9]', 'v[0-9].[0-9][0-9].[0-9]', 'v[0-9].[0-9][0-9].[0-9][0-9]', 'CLINK_FEED_STABLE_SIGNING_KEY', 'CLINK_FEED_DEV_SIGNING_KEY',
            'R2_STABLE_BUCKET', 'R2_DEV_BUCKET', 'The feed cannot be switched off', 'pkg_sha256', 'feed.tests.ps1', 'dotnet-sdk-lib.ps1', '--if-none-match',
            'feed-publish-lib.ps1', 'Recovering a stuck feed', 'CLINK_FEED_BUCKET_RESET_VERSION', 'dotnet host (muxer', 'Tag-type rules', 'branch ruleset', 'download-and-hash') {
            Assert-True ($doc.Contains($needle)) "RELEASING.md lacks '$needle'"
        }
        # B2 (two buckets): the layout, hosts, tokens, locks, CORS, no-transform rule, purge token and the staging dry run are described.
        foreach ($needle in 'devFeedUrl', '`clink-stable`', '`clink-dev`', 'dl.euphoric.software', 'dl-dev.euphoric.software', 'Assert-FeedBaseMatchesPrefix', 'Bucket Lock',
            'INDEFINITE', '30 days', 'CORS on `clink-stable` ONLY', 'https://euphoric.software', 'No-transform rule', 'Zone Cache Purge', 'CLOUDFLARE_API_TOKEN', 'PUBLIC fork', 'personal account',
            'Stale drafts', 'main-dev-safety', 'stable-tags', 'dev-tags', 'no-v-branches', 'Node 24', 'runs.using') {
            Assert-True ($doc.Contains($needle)) "RELEASING.md lacks '$needle'"
        }
        # The contradictions the B2 decision removed.
        Assert-True ($doc -cnotmatch 'dedicated Clink bucket') 'RELEASING.md still says "a dedicated Clink bucket"'
        Assert-True ($doc -cnotmatch 'contract stays single') 'RELEASING.md still says the feedUrl contract stays single'
        Assert-True ($doc -cnotmatch 'https://www\.euphoric\.software') 'RELEASING.md still lists the unneeded www CORS origin'
        Assert-True ($doc -cnotmatch '"devKeys":\s*\[\s*\]') 'RELEASING.md still has the example with empty devKeys (misconfigured by its own rule)'
        Assert-True ($doc -cnotmatch 'OWNER DECISION') 'RELEASING.md still has the open bucket-layout owner decision'
        Assert-True ($doc -cnotmatch 'Branch protection on `main` and `dev` \(pull request') 'RELEASING.md still asks for pull-request branch protection (solo flow)'
        # The example feed.json in the contract section must itself be a valid ON configuration with devFeedUrl.
        $exampleAt = $doc.IndexOf('"devFeedUrl": "https://dl-dev.example/clink/"')
        Assert-True ($exampleAt -gt 0) 'RELEASING.md lacks the devFeedUrl example'
        Assert-True ($doc -notmatch 'manual run on ``?main') 'RELEASING.md still describes a manual stable run on main'
        Assert-True ($doc -cnotmatch '\\n(?=\s|$)') 'RELEASING.md contains a literal \n where a line break was meant'
        Assert-True ($doc -cnotmatch 'tools/lib/feed-lib\.ps1') 'RELEASING.md still names the old tools/lib/feed-lib.ps1'
        Assert-True ($doc -cnotmatch 'head-object') 'RELEASING.md still mentions head-object'
        Assert-True ($doc -cnotmatch '\.[ ]{2,}- Dev only') 'RELEASING.md has a bullet glued to the previous sentence'
        $keys = $doc.Substring($doc.IndexOf("`n## Keys"), $doc.IndexOf("`n## Dry runs") - $doc.IndexOf("`n## Keys"))
        Assert-True ($keys -cnotmatch '(?<![A-Z_])CLINK_FEED_SIGNING_KEY') 'the Keys section names the bare CLINK_FEED_SIGNING_KEY'
        $recover = $doc.Substring($doc.IndexOf('### Recovering a stuck feed'))
        foreach ($needle in 'Torn pair', 'DRAFT release', 'Leaked or removed key', 'sets the environment variable', 'removes the variable', 'FIRST feed-on run', 'owner-only, temporary, reviewed') { Assert-True ($recover.Contains($needle)) "the recovery section lacks '$needle'" }
        $newKey = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'new-feed-key.ps1'))
        Assert-True ($newKey -cnotmatch '(?<![A-Z_])CLINK_FEED_SIGNING_KEY') 'new-feed-key.ps1 names the bare CLINK_FEED_SIGNING_KEY'
        foreach ($needle in 'CLINK_FEED_STABLE_SIGNING_KEY', 'CLINK_FEED_DEV_SIGNING_KEY') { Assert-True ($newKey.Contains($needle)) "new-feed-key.ps1 lacks $needle" }
        # Each allow-list pattern must match final tags and never a -dev. tag (GitHub environment patterns are fnmatch-like: [0-9] is one digit, '.' literal).
        $patterns = @('v[0-9].[0-9].[0-9]', 'v[0-9].[0-9].[0-9][0-9]', 'v[0-9].[0-9][0-9].[0-9]', 'v[0-9].[0-9][0-9].[0-9][0-9]')
        $regexes = $patterns | ForEach-Object { '^' + ($_ -replace '\.', '\.') + '$' }
        foreach ($tag in 'v0.4.2', 'v0.4.12', 'v0.14.2', 'v1.20.30') { Assert-True (@($regexes | Where-Object { $tag -cmatch $_ }).Count -ge 1) "no pattern matches $tag" }
        foreach ($tag in 'v0.4.2-dev.1', 'v0.4.12-dev.10', 'v0.14.2-dev.3', 'v1.20.30-dev.99', 'v0.4.2-rc', 'v0.4.2x') { Assert-True (@($regexes | Where-Object { $tag -cmatch $_ }).Count -eq 0) "a pattern matches $tag" }
    }
} finally {
    foreach ($k in @($keyA, $keyB)) { if ($k) { $k.Ec.Dispose() } }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
