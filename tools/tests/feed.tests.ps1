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
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink-0.4.2-setup.exe'), (New-RandomBytes 3000))
        $files = [ordered]@{}
        foreach ($pair in @(@('exe', 'Clink.exe'), @('exeFramework', 'Clink-framework.exe'), @('installer', 'Clink-0.4.2-setup.exe'))) {
            $path = Join-Path $dir $pair[1]
            $files[$pair[0]] = [ordered]@{ file = $pair[1]; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $pkg = [pscustomobject]@{ version = '0.4.2'; minRuntime = '10.0.14'; files = [pscustomobject]$files }
        if ($Mutate) { & $Mutate $pkg $dir }
        [System.IO.File]::WriteAllText((Join-Path $dir 'package.json'), ($pkg | ConvertTo-Json -Depth 6), $utf8)
        $dir
    }
    function Invoke-MakeManifest {
        param([string]$Dir)
        $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $manifestScript, '-Channel', 'stable', '-Version', '0.4.2', '-Tag', 'v0.4.2',
            '-BaseUrl', 'https://downloads.example/clink/releases/v0.4.2/', '-NotesUrl', 'https://example.org/notes', '-PackageDir', $Dir, '-FeedJson', $feedJson)
        $quoted = $all | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
        $id = [guid]::NewGuid().ToString('N')
        $outFile = Join-Path $scratch "out-$id.txt"
        $errFile = Join-Path $scratch "err-$id.txt"
        [Environment]::SetEnvironmentVariable('CLINK_FEED_SIGNING_KEY', $keyPem[0].Private)
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
    Invoke-Test 'make-manifest: files.installer.file other than Clink-<Version>-setup.exe is refused' {
        Assert-ManifestRefused 'setup-version' { param($p) $p.files.installer.file = 'Clink-0.4.3-setup.exe' } "files\.installer\.file is 'Clink-0\.4\.3-setup\.exe', expected 'Clink-0\.4\.2-setup\.exe'"
        Assert-ManifestRefused 'setup-case' { param($p) $p.files.installer.file = 'clink-0.4.2-setup.exe' } 'files\.installer\.file'
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

    # ------------------------------------------------------------------ L5: SDK identity
    Invoke-Test 'dotnet-sdk-lib: only Valid + an exact observed Microsoft subject + an allow-listed root + the pinned version pass' {
        $good = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        $root = '8F43288AD272F3103B6FB1428485EA3014C0BCFE'
        function Get-Refusal { param($Status = 'Valid', $Subject = $good, $Root = $root, $Reported = '10.0.402', $Pinned = '10.0.402') Get-DotNetSdkRefusal -SignatureStatus $Status -SignerSubject $Subject -RootThumbprint $Root -ReportedVersion $Reported -PinnedVersion $Pinned }
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
        Assert-True ($null -ne (Get-Refusal -Reported '10.0.403')) 'accepted another SDK version'
        Assert-True ($null -ne (Get-Refusal -Reported '')) 'accepted an empty version'
        Assert-True ($null -ne (Get-Refusal -Pinned '')) 'accepted an empty pin'
        Assert-True ($null -ne (Get-DotNetSdkRefusal -SignatureStatus 'Valid' -SignerSubject $good -RootThumbprint $root -ReportedVersion '10.0.402' -PinnedVersion '10.0.402' -AllowedSubjects @())) 'an empty subject allow-list accepted'
        Assert-True ($null -ne (Get-DotNetSdkRefusal -SignatureStatus 'Valid' -SignerSubject $good -RootThumbprint $root -ReportedVersion '10.0.402' -PinnedVersion '10.0.402' -AllowedRootThumbprints @())) 'an empty root allow-list accepted'
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

    Invoke-Test 'RELEASING.md documents the allow-list, the secret names, the one-way feed and the new checks' {
        $doc = [System.IO.File]::ReadAllText((Join-Path $repo 'docs\RELEASING.md'))
        foreach ($needle in 'v[0-9].[0-9].[0-9]', 'v[0-9].[0-9].[0-9][0-9]', 'v[0-9].[0-9][0-9].[0-9]', 'v[0-9].[0-9][0-9].[0-9][0-9]', 'CLINK_FEED_STABLE_SIGNING_KEY', 'CLINK_FEED_DEV_SIGNING_KEY',
            'R2_STABLE_BUCKET', 'R2_DEV_BUCKET', 'The feed cannot be switched off', 'pkg_sha256', 'feed.tests.ps1', 'dotnet-sdk-lib.ps1', '--if-none-match',
            'feed-publish-lib.ps1', 'Recovering a stuck feed', 'CLINK_FEED_BUCKET_RESET_VERSION', 'OWNER DECISION', 'dotnet host (muxer', 'Tag-type rules', 'branch ruleset', 'download-and-hash') {
            Assert-True ($doc.Contains($needle)) "RELEASING.md lacks '$needle'"
        }
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
