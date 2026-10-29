# Shared helpers for the update feed scripts (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7.
# Trust anchors live in Clink/Assets/feed.json (embedded in Clink.exe):
#   {"schema":1,"feedUrl":"https://.../clink/" or "","devFeedUrl":"https://.../clink/" (optional),"stableKeys":["<Base64 DER SPKI P-256>",...],"devKeys":[...]}
# feedUrl is the base of the stable channel (stable.json, its .sig and the stable binaries). devFeedUrl is the optional base of the dev
# channel (dev.json, its .sig and the dev binaries); absent or "" means the dev channel uses feedUrl.

# The %-escape pattern below (an escaped unreserved character: ALPHA, DIGIT, - . _ ~, which URI normalisation turns back into the plain
# character) is the one of UpdateFeed.s_escapedUnreserved in the client. It is an inline, case-sensitive literal on purpose.

# One validator for both URLs, a port of FeedTrust.ValidFeedUrl in Clink/Services/UpdateFeed.cs. Returns @{ Url; Why }: Url is the
# normalised base (Uri.AbsoluteUri) or $null, Why says what is wrong.
#   1. trim; empty is refused
#   2. an absolute https URL with a host
#   3. no '?' or '#' (also no query or fragment), no user info
#   4. the text and the absolute path both end in '/'
#   5. CANONICAL ONLY (stricter than the client, which normalises): the trimmed text must already equal Uri.AbsoluteUri (lower-case
#      host and scheme, default port dropped, dot segments removed, nothing needing escaping) and must hold no %-escape of an
#      unreserved character. The client would silently turn a non-canonical URL into another one; refusing it here keeps the
#      published URLs and the client's resolved bases from differing.
function Resolve-FeedBaseUrl {
    param([AllowNull()] [AllowEmptyString()] [string]$Text)
    $t = ([string]$Text).Trim()
    if ($t.Length -eq 0) { return [pscustomobject]@{ Url = $null; Why = 'empty' } }
    $u = $null
    if (-not [Uri]::TryCreate($t, [UriKind]::Absolute, [ref]$u)) { return [pscustomobject]@{ Url = $null; Why = 'not an absolute URL' } }
    if ($u.Scheme -cne 'https') { return [pscustomobject]@{ Url = $null; Why = 'not https' } }
    if ([string]::IsNullOrEmpty($u.Host)) { return [pscustomobject]@{ Url = $null; Why = 'no host' } }
    if ($t.Contains('?') -or $t.Contains('#') -or $u.Query.Length -gt 0 -or $u.Fragment.Length -gt 0) { return [pscustomobject]@{ Url = $null; Why = 'has a query or fragment' } }
    if ($u.UserInfo.Length -gt 0) { return [pscustomobject]@{ Url = $null; Why = 'has user info' } }
    if (-not $t.EndsWith('/') -or -not $u.AbsolutePath.EndsWith('/')) { return [pscustomobject]@{ Url = $null; Why = "must end with '/'" } }
    if ($t -cmatch '%(?:3[0-9]|[46][1-9A-Fa-f]|[57][0-9Aa]|2[Dd]|2[Ee]|5[Ff]|7[Ee])' -or -not $u.AbsoluteUri.Equals($t, [System.StringComparison]::Ordinal)) {
        return [pscustomobject]@{ Url = $null; Why = "not in canonical form (write it as '$($u.AbsoluteUri)': lower-case host, no default port, no dot segments, no escaped plain characters)" }
    }
    [pscustomobject]@{ Url = $u.AbsoluteUri; Why = '' }
}

function Test-FeedUrlShape {
    param([AllowEmptyString()] [string]$Url)
    $null -ne (Resolve-FeedBaseUrl -Text $Url).Url
}

# Port of UpdateFeed.IsUnder: $true when Url is an https URL strictly inside Base (a validated feed base, ends in '/'). Used by
# make-manifest.ps1 so that it never signs a URL that every client refuses: no '//' (empty segment), no %-escape of an unreserved
# character, no '.' or '..' segment (also encoded), no backslash (also %5C), no query, fragment or user info, same host and port.
function Test-FeedAssetUrlUnder {
    param([AllowNull()] [AllowEmptyString()] [string]$Url, [AllowNull()] [AllowEmptyString()] [string]$Base)
    $u = $null; $b = $null
    if (-not [Uri]::TryCreate([string]$Url, [UriKind]::Absolute, [ref]$u) -or -not [Uri]::TryCreate([string]$Base, [UriKind]::Absolute, [ref]$b)) { return $false }
    if ($Url -cmatch '%(?:3[0-9]|[46][1-9A-Fa-f]|[57][0-9Aa]|2[Dd]|2[Ee]|5[Ff]|7[Ee])') { return $false }
    if ($u.Scheme -cne 'https' -or $u.UserInfo.Length -gt 0 -or $u.Query.Length -gt 0 -or $u.Fragment.Length -gt 0 -or $Url.Contains('?') -or $Url.Contains('#')) { return $false }
    if ($u.Scheme -cne $b.Scheme -or $u.Port -ne $b.Port -or -not $u.IdnHost.ToLowerInvariant().Equals($b.IdnHost.ToLowerInvariant(), [System.StringComparison]::Ordinal)) { return $false }
    $basePath = $b.AbsolutePath
    if (-not $basePath.EndsWith('/')) { $basePath += '/' }
    if (-not $u.AbsolutePath.StartsWith($basePath, [System.StringComparison]::Ordinal) -or $u.AbsolutePath.Length -eq $basePath.Length) { return $false }
    $rest = [Uri]::UnescapeDataString($u.AbsolutePath.Substring($basePath.Length))
    if ($rest.Contains('\')) { return $false }
    # Stricter than the client: a literal backslash, or a literal "." or ".." segment, in the TEXT is refused too (Uri would turn them into
    # a slash / normalise them away, so the client would fetch another URL than the one written in the manifest).
    if ($Url.Contains('\')) { return $false }
    $rawPath = [regex]::Replace($Url, '(?i)\Ahttps://[^/]*', '')
    foreach ($segment in $rawPath.Split('/')) { if ($segment -ceq '..' -or $segment -ceq '.') { return $false } }
    foreach ($segment in $rest.Split('/')) { if ($segment -ceq '..' -or $segment -ceq '.' -or $segment -ceq '') { return $false } }
    return $true
}

# The value of the property named EXACTLY $Name (ordinal), or $null. (PowerShell's own member access is case-insensitive.)
function Get-FeedJsonMember {
    param($Object, [string]$Name)
    # The unary comma keeps a one-element array an array (the pipeline would unroll it into its element).
    foreach ($p in $Object.PSObject.Properties) { if ($p.Name.Equals($Name, [System.StringComparison]::Ordinal)) { return , $p.Value } }
    return $null
}

# Reads feed.json and validates it like the client (FeedTrust.Create / Parse in Clink/Services/UpdateFeed.cs; contract v2):
#   - fully empty (feedUrl "" or blank, devFeedUrl "" or absent, both key lists empty)  -> feed OFF (Enabled = $false, Mode = 'off')
#   - feedUrl valid AND stableKeys and devKeys both non-empty, every key a valid P-256 SPKI, lists disjoint -> ON;
#     a devFeedUrl that is not "" (a blank one counts as listed and is invalid) must pass the same URL validation as feedUrl, feedUrl
#     must be non-blank, and two different bases must not be nested (one inside the other)
#   - anything else that names a URL or a key (partial, malformed, overlapping, a wrong type, a bad schema)  -> THROWS (the client would be
#     Misconfigured). The message lists every problem.
# Extra rules on this side only, so the two sides cannot silently differ: URLs must be canonical (Resolve-FeedBaseUrl) and a property
# whose name matches a known key case-insensitively but not exactly (FeedUrl, StableKeys) is refused. schema must be the integer 1.
# Both channels are enabled together. Returns the data for -Channel plus Mode:
#   FeedUrl = the BASE URL OF -Channel (stable: feedUrl; dev: devFeedUrl, or feedUrl when devFeedUrl is empty), normalised; '' when off.
#   Every script that builds, checks or purges URLs uses this one.
#   ConfiguredFeedUrl = feedUrl (normalised); DevFeedUrl = devFeedUrl (normalised, '' when absent).
function Get-FeedConfig {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "feed.json not found: $Path" }
    $cfg = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ($null -eq $cfg -or $cfg -isnot [pscustomobject]) { throw 'feed.json is misconfigured: it is not a JSON object' }
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($name in @($cfg.PSObject.Properties.Name)) {
        foreach ($known in 'schema', 'feedUrl', 'devFeedUrl', 'stableKeys', 'devKeys') {
            if ($name.Equals($known, [System.StringComparison]::OrdinalIgnoreCase) -and -not $name.Equals($known, [System.StringComparison]::Ordinal)) {
                $problems.Add("property '$name' is spelled differently from '$known' (property names are case-sensitive)")
            }
        }
    }
    $schema = Get-FeedJsonMember $cfg 'schema'
    if (($schema -isnot [int] -and $schema -isnot [long]) -or $schema -ne 1) { $problems.Add('unsupported schema (expected the integer 1)') }

    $urlText = ''
    $rawUrl = Get-FeedJsonMember $cfg 'feedUrl'
    if ($rawUrl -is [string]) { $urlText = $rawUrl } elseif ($null -ne $rawUrl) { $problems.Add('feedUrl is not a string') }
    $devText = ''
    $rawDev = Get-FeedJsonMember $cfg 'devFeedUrl'
    if ($rawDev -is [string]) { $devText = $rawDev } elseif ($null -ne $rawDev) { $problems.Add('devFeedUrl is not a string') }

    $url = $null
    $urlListed = -not [string]::IsNullOrWhiteSpace($urlText)
    if ($urlListed) {
        $r = Resolve-FeedBaseUrl -Text $urlText
        $url = $r.Url
        if ($null -eq $url) { $problems.Add("feedUrl is invalid ($($r.Why))") }
    }
    # devFeedUrl: absent or "" = the dev channel uses feedUrl; anything else (a blank string too) must pass the very same validator.
    $devUrl = $null
    $devListed = -not [string]::IsNullOrEmpty($devText)
    if ($devListed) {
        $r = Resolve-FeedBaseUrl -Text $devText
        $devUrl = $r.Url
        if ($null -eq $devUrl) { $problems.Add("devFeedUrl is invalid ($($r.Why))") }
        if (-not $urlListed) { $problems.Add('devFeedUrl is set but feedUrl is empty') }
    }
    # Two different bases must be disjoint (a binary under the inner base would pass the outer channel's "under my base" check too).
    if ($null -ne $url -and $null -ne $devUrl -and -not $url.Equals($devUrl, [System.StringComparison]::Ordinal) -and
        ($devUrl.StartsWith($url, [System.StringComparison]::Ordinal) -or $url.StartsWith($devUrl, [System.StringComparison]::Ordinal))) {
        $problems.Add('feedUrl and devFeedUrl are nested (one is inside the other)')
    }

    $lists = @{}; $listed = @{}
    foreach ($prop in 'stableKeys', 'devKeys') {
        $valid = New-Object System.Collections.Generic.List[string]
        $count = 0
        $raw = Get-FeedJsonMember $cfg $prop
        if ($null -ne $raw -and $raw -isnot [System.Array]) { $problems.Add("$prop is not a list") }
        elseif ($null -ne $raw) {
            foreach ($item in $raw) {
                if ($item -isnot [string] -or [string]::IsNullOrWhiteSpace($item)) { $problems.Add("${prop}[$count] is invalid (empty or not a string)") }
                else {
                    try { $ec = ConvertFrom-SpkiBase64 $item.Trim(); $ec.Dispose(); $valid.Add($item.Trim()) }
                    catch { $problems.Add("${prop}[$count] is invalid ($($_.Exception.Message))") }
                }
                $count++
            }
        }
        $lists[$prop] = $valid; $listed[$prop] = $count
    }
    if ($urlListed -or $devListed -or $listed['stableKeys'] -gt 0 -or $listed['devKeys'] -gt 0) {
        # Something is configured: then everything must be, for both channels together.
        if (-not $urlListed -and ($listed['stableKeys'] -gt 0 -or $listed['devKeys'] -gt 0)) { $problems.Add('feedUrl is empty but keys are listed') }
        if ($listed['stableKeys'] -eq 0) { $problems.Add('stableKeys is empty (the feed needs keys for both channels)') }
        if ($listed['devKeys'] -eq 0) { $problems.Add('devKeys is empty (the feed needs keys for both channels)') }
        $stableDer = @($lists['stableKeys'] | ForEach-Object { [Convert]::ToBase64String([Convert]::FromBase64String($_)) })
        foreach ($k in $lists['devKeys']) {
            if ($stableDer -ccontains [Convert]::ToBase64String([Convert]::FromBase64String($k))) { $problems.Add('the same key is listed in stableKeys and devKeys'); break }
        }
    }
    if ($problems.Count -gt 0) { throw "feed.json is misconfigured: $($problems -join '; ')" }
    if (-not $urlListed) {
        return [pscustomobject]@{ Enabled = $false; Mode = 'off'; Reason = 'feed.json is empty (feed off)'; FeedUrl = ''; ConfiguredFeedUrl = ''; DevFeedUrl = ''; Keys = @() }
    }
    $own = if ($Channel -eq 'stable') { $lists['stableKeys'] } else { $lists['devKeys'] }
    $base = if ($Channel -eq 'dev' -and $devListed) { $devUrl } else { $url }
    [pscustomobject]@{ Enabled = $true; Mode = 'on'; Reason = ''; FeedUrl = $base; ConfiguredFeedUrl = $url; DevFeedUrl = $(if ($devListed) { $devUrl } else { '' }); Keys = @($own) }
}

# Base64 DER SubjectPublicKeyInfo (ECDSA P-256) -> ECDsa public key. Parsed by hand: ImportSubjectPublicKeyInfo does not
# exist on Windows PowerShell 5.1 (.NET Framework).
function ConvertFrom-SpkiBase64 {
    param([Parameter(Mandatory)] [string]$Base64)
    try { $der = [Convert]::FromBase64String($Base64) } catch { throw "Public key is not valid Base64." }
    $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
    if ($der.Length -ne 91) { throw 'Public key is not an ECDSA P-256 SubjectPublicKeyInfo (expected 91 bytes).' }
    for ($i = 0; $i -lt $header.Length; $i++) { if ($der[$i] -ne $header[$i]) { throw 'Public key is not an uncompressed ECDSA P-256 SubjectPublicKeyInfo.' } }
    $point = New-Object System.Security.Cryptography.ECPoint
    $point.X = [byte[]]$der[27..58]
    $point.Y = [byte[]]$der[59..90]
    $params = New-Object System.Security.Cryptography.ECParameters
    $params.Curve = [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')
    $params.Q = $point
    [System.Security.Cryptography.ECDsa]::Create($params)
}

# True when SigBase64 (one-line Base64, IEEE P1363 r||s) is a valid ECDSA P-256/SHA-256 signature of Bytes under ANY of Keys.
function Test-FeedSignature {
    param([Parameter(Mandatory)] [byte[]]$Bytes, [Parameter(Mandatory)] [string]$SigBase64, [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$Keys)
    try { $sig = [Convert]::FromBase64String($SigBase64.Trim()) } catch { return $false }
    if ($sig.Length -ne 64) { return $false }
    foreach ($k in $Keys) {
        $ec = ConvertFrom-SpkiBase64 $k
        try { if ($ec.VerifyData($Bytes, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) { return $true } }
        finally { $ec.Dispose() }
    }
    return $false
}

# Sort key for X.Y.Z and X.Y.Z-dev.N (ASCII digits only: the .NET digit shorthand also matches other Unicode digits): a final build sorts above any dev build of the same X.Y.Z.
function Get-VersionKey {
    param([Parameter(Mandatory)] [string]$Version)
    if ($Version -cnotmatch '\A([0-9]+\.[0-9]+\.[0-9]+)(?:-dev\.([0-9]+))?\z') { throw "Not a feed version: '$Version'" }
    $n = if ($Matches[2]) { [int64]$Matches[2] } else { [int64]::MaxValue }
    [pscustomobject]@{ Base = [version]$Matches[1]; N = $n }
}

# -1, 0, 1 like CompareTo.
function Compare-FeedVersion {
    param([Parameter(Mandatory)] [string]$A, [Parameter(Mandatory)] [string]$B)
    $x = Get-VersionKey $A; $y = Get-VersionKey $B
    $c = $x.Base.CompareTo($y.Base)
    if ($c -ne 0) { return $c }
    return $x.N.CompareTo($y.N)
}

# The environment of the embedded-feed self-test (Embedded_feed_trust_matches_ci_expectation) of check-published.ps1. Returns a hashtable of
# the three variables the test reads; a value of $null means "leave it unset for that run".
#   -ExpectFeed 'on'  : CLINK_EXPECT_FEED=on, and BOTH bases are required (the client-side test requires both when on): the normalised
#                       stable base (Get-FeedConfig -Channel stable .FeedUrl) and dev base (Get-FeedConfig -Channel dev .FeedUrl).
#   -ExpectFeed 'off' : CLINK_EXPECT_FEED=off, both bases must be empty or absent and stay unset.
#   no -ExpectFeed    : all three stay unset, and giving a base is an error (it would be silently ignored).
function Get-FeedSelfTestEnvironment {
    param([AllowNull()] [AllowEmptyString()] [string]$ExpectFeed, [AllowNull()] [AllowEmptyString()] [string]$ExpectFeedUrl, [AllowNull()] [AllowEmptyString()] [string]$ExpectDevFeedUrl)
    $stable = ([string]$ExpectFeedUrl).Trim()
    $dev = ([string]$ExpectDevFeedUrl).Trim()
    $result = @{ CLINK_EXPECT_FEED = $null; CLINK_EXPECT_FEED_URL = $null; CLINK_EXPECT_DEV_FEED_URL = $null }
    if ([string]::IsNullOrEmpty($ExpectFeed)) {
        if ($stable -or $dev) { throw '-ExpectFeedUrl / -ExpectDevFeedUrl need -ExpectFeed on.' }
        return $result
    }
    if ($ExpectFeed -cne 'on' -and $ExpectFeed -cne 'off') { throw "-ExpectFeed must be 'on' or 'off', not '$ExpectFeed'." }
    $result['CLINK_EXPECT_FEED'] = $ExpectFeed
    if ($ExpectFeed -ceq 'off') {
        if ($stable -or $dev) { throw "-ExpectFeed off cannot come with a feed URL (stable '$stable', dev '$dev'): the feed is off, so the exe embeds none." }
        return $result
    }
    foreach ($pair in @(@('-ExpectFeedUrl', $stable), @('-ExpectDevFeedUrl', $dev))) {
        if ($pair[1].Length -eq 0) { throw "$($pair[0]) is required with -ExpectFeed on (pass the base from Get-FeedConfig -Channel stable / dev)." }
        if (-not (Test-FeedUrlShape -Url $pair[1])) { throw "$($pair[0]) '$($pair[1])' is not a canonical https feed base (ends with '/')." }
    }
    $result['CLINK_EXPECT_FEED_URL'] = $stable
    $result['CLINK_EXPECT_DEV_FEED_URL'] = $dev
    $result
}
