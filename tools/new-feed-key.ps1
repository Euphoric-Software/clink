# Generates an update-feed signing key pair (ECDSA P-256) for one channel. Works on Windows PowerShell 5.1 and PowerShell 7.
# The private key (PKCS#8 PEM) is written to -PrivateKeyPath, which must NOT be inside any git work tree. It is never printed: the
# only place it goes is that file. The script prints the public key (Base64 SPKI) and a short summary of what to do with each value.
#   -Channel stable|dev   which channel the key is for
#   -Next                 a "next" key for that channel (rotation, see docs/RELEASING.md): its public key goes into feed.json now, its
#                         private key goes into NO repo secret and stays on the USB drive until a rotation. Works for both channels.
param(
    [Parameter(Mandatory)] [ValidateSet('stable', 'dev')] [string]$Channel,
    [Parameter(Mandatory)] [string]$PrivateKeyPath,
    [switch]$Next
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
$pem = $null

$keysProp = if ($Channel -eq 'stable') { 'stableKeys' } else { 'devKeys' }
$secretName = if ($Channel -eq 'stable') { 'CLINK_FEED_STABLE_SIGNING_KEY' } else { 'CLINK_FEED_DEV_SIGNING_KEY' }
$what = if ($Next) { "NEXT $Channel key (for a later rotation)" } else { "$Channel key" }

"Private key written to: $target"
"  (this file is the only place the private key goes; it is never printed. *.pem is git-ignored as a backstop)"
''
"Public key for the $what (Base64 SPKI):"
$pub
''
"WHAT TO DO WITH IT"
"  1. PUBLIC value -> Clink/Assets/feed.json, in the '$keysProp' list (a reviewed commit). Both channels are enabled together: feed.json"
"     needs feedUrl plus at least one key in BOTH stableKeys and devKeys, and no key may appear in both lists."
if ($Next) {
    "  2. PRIVATE value -> NO repo secret. A next key is not used for signing yet: it stays on the USB drive until you rotate"
    "     (docs/RELEASING.md, Rotation). Clients that ship its public key then keep updating after the switch."
} else {
    "  2. PRIVATE value -> GitHub repo secret $secretName (repo > Settings > Secrets and variables > Actions > New repository secret)."
    "     Set it straight from the file, without the clipboard and without showing it (make-manifest.ps1 accepts the plain PEM or one line of Base64):"
    "       Get-Content -Raw '$target' | gh secret set $secretName"
    "     (add --repo <owner>/<repo> when you are not inside the repo's folder). Do not paste the key anywhere else: a clipboard and its history keep it."
}
# icacls only works on NTFS (and ReFS): a USB drive formatted exFAT or FAT32 has no ACLs at all.
$format = ''
try { $format = [string](New-Object System.IO.DriveInfo ([System.IO.Path]::GetPathRoot($target))).DriveFormat } catch { $format = '' }
if ($format -ieq 'NTFS' -or $format -ieq 'ReFS') {
    "  3. Back up the .pem files to the USB drive. Then delete the working copy. Until then restrict the file to your own account:"
    "       icacls `"$target`" /inheritance:r /grant:r `"`${env:USERNAME}:(R,D)`""
} else {
    "  3. Back up the .pem files to the USB drive. Then delete the working copy. This drive's file system is '$format' (not NTFS): it has no"
    "     file permissions, so icacls cannot protect the file here. Keep the drive with you, and delete the file as soon as it is backed up."
}
if (-not $Next) {
    "  4. Also make a NEXT key for this channel now (run this script again with -Next, a different file name) so that a later rotation never"
    "     needs an update that old clients cannot verify."
}
"See docs/RELEASING.md."
