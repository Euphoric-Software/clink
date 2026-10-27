#requires -Version 7
# Generates the update-feed signing key pair (ECDSA P-256).
# The private key (PKCS#8 PEM) is written to -PrivateKeyPath, which must be OUTSIDE this repo. Prints the public key (Base64 SPKI).
param([Parameter(Mandatory)] [string]$PrivateKeyPath)
$ErrorActionPreference = 'Stop'
$repo = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
if (-not $repo) { $repo = Split-Path -Parent $PSScriptRoot }
$repoFull = [System.IO.Path]::GetFullPath($repo).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
$target = [System.IO.Path]::GetFullPath($PrivateKeyPath)
if ($target.StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to write the private key inside the repo ($repoFull). Choose a path elsewhere." }
if (Test-Path -LiteralPath $target) { throw "$target already exists; refusing to overwrite." }
$dir = Split-Path -Parent $target
if (-not (Test-Path -LiteralPath $dir)) { throw "Folder does not exist: $dir" }

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
try {
    $pem = $ecdsa.ExportPkcs8PrivateKeyPem()
    $pub = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
} finally { $ecdsa.Dispose() }
[System.IO.File]::WriteAllText($target, $pem, [System.Text.UTF8Encoding]::new($false))

"Private key written to: $target (keep it secret, back it up, never commit it)"
''
"Public key (Base64 SPKI):"
$pub
''
"Next:"
"  1. GitHub repo > Settings > Secrets and variables > Actions > Secrets: add CLINK_FEED_SIGNING_KEY = the full contents of $target"
"  2. Same page > Variables: add CLINK_FEED_PUBLIC_KEY = the public key above"
"  3. Add variable CLINK_FEED_URL = the feed base URL ending in '/', e.g. https://cdn.example/clink/"
"  4. Delete or vault the private key file once stored. Losing it means shipping a new app build with the new public key."
