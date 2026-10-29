# Shared helpers for the update feed scripts (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7.
# Trust anchors live in Clink/Assets/feed.json (embedded in Clink.exe):
#   {"schema":1,"feedUrl":"https://.../clink/" or "","devFeedUrl":"https://.../clink/" (optional),"stableKeys":["<Base64 DER SPKI P-256>",...],"devKeys":[...]}
# feedUrl is the base of the stable channel (stable.json, its .sig and the stable binaries). devFeedUrl is the optional base of the dev
# channel (dev.json, its .sig and the dev binaries); absent or "" means the dev channel uses feedUrl.

# One validator for both URLs: https, a host, no user info (@), no query (?) or fragment (#), no backslash or whitespace, ends with '/'.
function Test-FeedUrlShape {
    param([AllowEmptyString()] [string]$Url)
    ($Url -match '^https://[^/?#@\\\s]+(/[^?#@\\\s]*)?$') -and $Url.EndsWith('/')
}

# Reads feed.json and validates it exactly like the client (contract v2):
#   - fully empty (feedUrl "" and devFeedUrl "" or absent and both key lists empty)  -> feed OFF (Enabled = $false, Mode = 'off')
#   - feedUrl valid AND stableKeys and devKeys both non-empty, every key a valid P-256 SPKI, lists disjoint -> ON;
#     a non-empty devFeedUrl must pass the same URL validation as feedUrl (and feedUrl must be non-empty)
#   - anything else that names a URL or a key (partial, malformed, overlapping)  -> THROWS (the client would be Misconfigured)
# Both channels are therefore enabled together. Returns the data for -Channel plus Mode:
#   FeedUrl = the BASE URL OF -Channel (stable: feedUrl; dev: devFeedUrl, or feedUrl when devFeedUrl is empty). Every script that
#   builds, checks or purges URLs uses this one.
#   ConfiguredFeedUrl = feedUrl as written; DevFeedUrl = devFeedUrl as written ('' when absent).
function Get-FeedConfig {
    param([Parameter(Mandatory)] [string]$Path, [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "feed.json not found: $Path" }
    $cfg = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ($cfg.schema -ne 1) { throw "feed.json: unsupported schema '$($cfg.schema)' (expected 1)." }
    $url = ([string]$cfg.feedUrl).Trim()
    $devUrl = ''
    if ($null -ne $cfg.devFeedUrl) {
        if ($cfg.devFeedUrl -isnot [string]) { throw 'feed.json: devFeedUrl must be a string.' }
        $devUrl = $cfg.devFeedUrl.Trim()
    }
    $lists = @{}
    foreach ($prop in 'stableKeys', 'devKeys') {
        $items = @()
        if ($null -ne $cfg.$prop) {
            foreach ($item in @($cfg.$prop)) {
                if ($item -isnot [string] -or [string]::IsNullOrWhiteSpace($item)) { throw "feed.json: $prop contains an empty or non-string entry." }
                $items += $item.Trim()
            }
        }
        $lists[$prop] = $items
    }
    if ($url -eq '' -and $devUrl -eq '' -and $lists['stableKeys'].Count -eq 0 -and $lists['devKeys'].Count -eq 0) {
        return [pscustomobject]@{ Enabled = $false; Mode = 'off'; Reason = 'feed.json is empty (feed off)'; FeedUrl = ''; ConfiguredFeedUrl = ''; DevFeedUrl = ''; Keys = @() }
    }
    # From here something is configured: all of it must be valid.
    if ($url -eq '') { throw 'feed.json: keys or devFeedUrl are set but feedUrl is empty. Set feedUrl or empty devFeedUrl and both key lists.' }
    if (-not (Test-FeedUrlShape $url)) {
        throw "feed.json: feedUrl must be https, have a host, no user info (@), no query (?) or fragment (#), and end with '/': $url"
    }
    if ($devUrl -ne '' -and -not (Test-FeedUrlShape $devUrl)) {
        throw "feed.json: devFeedUrl must be empty or follow the feedUrl rules (https, a host, no user info (@), no query (?) or fragment (#), ends with '/'): $devUrl"
    }
    foreach ($prop in 'stableKeys', 'devKeys') {
        if ($lists[$prop].Count -eq 0) { throw "feed.json: feedUrl is set but $prop is empty. Both channels are enabled together." }
        foreach ($k in $lists[$prop]) {
            try { $ec = ConvertFrom-SpkiBase64 $k; $ec.Dispose() } catch { throw "feed.json: an entry in $prop is not a valid P-256 public key ($($_.Exception.Message))" }
        }
    }
    foreach ($k in $lists['stableKeys']) {
        if ($lists['devKeys'] -contains $k) { throw 'feed.json: a key is listed in both stableKeys and devKeys; the channels must use different keys.' }
    }
    $own = if ($Channel -eq 'stable') { $lists['stableKeys'] } else { $lists['devKeys'] }
    $base = if ($Channel -eq 'dev' -and $devUrl -ne '') { $devUrl } else { $url }
    [pscustomobject]@{ Enabled = $true; Mode = 'on'; Reason = ''; FeedUrl = $base; ConfiguredFeedUrl = $url; DevFeedUrl = $devUrl; Keys = @($own) }
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
