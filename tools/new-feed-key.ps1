# Generates an update-feed signing key pair (ECDSA P-256) for one channel. Works on Windows PowerShell 5.1 and PowerShell 7.
# The private key (PKCS#8 PEM) is written to -PrivateKeyPath, which must NOT be inside any git work tree.
# Prints the public key (Base64 SPKI) to add to Clink/Assets/feed.json.
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$PrivateKeyPath
)
$ErrorActionPreference = 'Stop'

$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PrivateKeyPath)
$dir = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $dir -PathType Container)) { throw "Folder does not exist: $dir" }
if (Test-Path -LiteralPath $target) { throw "$target already exists; refusing to overwrite." }

# Refuse any location inside a git work tree. Two checks: git itself, and a walk up the folder tree looking for a .git entry
# (covers git being missing or refusing to answer, e.g. "dubious ownership").
$dir = (Resolve-Path -LiteralPath $dir).Path
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns git's stderr into an error
    $answer = & git -C $dir rev-parse --is-inside-work-tree 2>$null
    $gitExit = $LASTEXITCODE; $ErrorActionPreference = $eap
    if ($gitExit -eq 0 -and "$answer".Trim() -eq 'true') { throw "Refusing to write the private key inside a git work tree ($dir). Choose a path elsewhere." }
} else {
    Write-Warning 'git was not found; relying on the folder walk to detect a work tree.'
}
for ($d = $dir; $d; $d = Split-Path -Parent $d) {
    if (Test-Path -LiteralPath (Join-Path $d '.git')) { throw "Refusing to write the private key inside a git work tree ($d). Choose a path elsewhere." }
}

if ($PSVersionTable.PSVersion.Major -ge 7) {
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    try {
        $pem = $ecdsa.ExportPkcs8PrivateKeyPem()
        $pub = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    } finally { $ecdsa.Dispose() }
} else {
    $cp = New-Object System.Security.Cryptography.CngKeyCreationParameters
    $cp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $cp)   # null name = ephemeral (a PowerShell $null would become "" and persist the key)
    try {
        $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
        $blob = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)   # magic(4) cbKey(4) X(32) Y(32)
    } finally { $cng.Dispose() }
    $b64 = [Convert]::ToBase64String($pkcs8, [System.Base64FormattingOptions]::InsertLineBreaks) -replace "`r`n", "`n"
    $pem = "-----BEGIN PRIVATE KEY-----`n$b64`n-----END PRIVATE KEY-----`n"
    $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
    $spki = [byte[]]($header + $blob[8..71])
    $pub = [Convert]::ToBase64String($spki)
}
[System.IO.File]::WriteAllText($target, $pem, [System.Text.UTF8Encoding]::new($false))

"Private key written to: $target (keep it secret, back it up offline, never commit it; *.pem is git-ignored as a backstop)"
''
"Public key for the $Channel channel (Base64 SPKI):"
$pub
''
$keysProp = if ($Channel -eq 'stable') { 'stableKeys' } else { 'devKeys' }
$envName = if ($Channel -eq 'stable') { 'feed-stable' } else { 'feed-dev' }
$secretName = if ($Channel -eq 'stable') { 'CLINK_FEED_STABLE_SIGNING_KEY' } else { 'CLINK_FEED_DEV_SIGNING_KEY' }
"Next:"
"  1. Add the public key to the '$keysProp' list in Clink/Assets/feed.json in a reviewed commit (pull request). Clink builds"
"     made from that commit trust it; the feed is off while feed.json is completely empty and a build error while it is only partly filled in."
"  2. GitHub repo > Settings > Environments > $envName > Environment secrets: add $secretName (environment secret,"
"     not a repo-level secret). Recommended value, one line without newlines (make-manifest.ps1 accepts it or the plain PEM):"
"       [Convert]::ToBase64String([IO.File]::ReadAllBytes('$target'))"
"     Both channels must be enabled together: feed.json needs feedUrl plus at least one key in BOTH stableKeys and devKeys,"
"     and no key may appear in both lists."
"  3. Keep an offline backup of the private key (encrypted, off this PC). Then delete the working copy."
"     Until then restrict the file to your own account, e.g.:"
"       icacls `"$target`" /inheritance:r /grant:r `"`${env:USERNAME}:(R,D)`""
if ($Channel -eq 'stable') {
    "  4. Also generate an offline 'next' stable key now (run this script again, store it offline, do NOT put it in an environment"
    "     secret) and add its public key to stableKeys so that a rotation never needs an update that old clients cannot verify."
}
"See docs/RELEASING.md (key rotation)."
