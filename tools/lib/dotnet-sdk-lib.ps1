# Checks of the .NET SDK a workflow job is about to build with (dot-source this file). Windows PowerShell 5.1 and pwsh 7, no modules.
# Run right after actions/setup-dotnet: the dotnet host (muxer) that the job resolves must be Authenticode-valid with an exact Microsoft
# signer subject whose chain ends in an allow-listed Microsoft root, and `dotnet --version` (run in the repository root, so global.json
# applies) must equal the version global.json pins.

# The root-thumbprint check reuses Test-SignerRootThumbprint of the runtime resolver's library.
. (Join-Path $PSScriptRoot 'dotnet-runtime-lib.ps1')

# Exact signer subjects (X509 Subject, compared with -ceq) accepted for dotnet.exe. OBSERVED ONLY: this is the subject the SDK's
# dotnet.exe carries today (observed 2026-10-27, SDK 10.0.402). To add one: let the step fail, read the subject it prints, check it
# against what Microsoft ships, and add that exact string here in a reviewed commit. Never add a subject that has not been observed.
$script:AllowedDotNetSdkSignerSubjects = @(
    'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
)

# Thumbprints (SHA-1, upper case) of the root the signer chain must end in. Observed: the chain of dotnet.exe (SDK 10.0.402) ends in
# Microsoft Root Certificate Authority 2011. Same rule: add a root only after observing it and checking it against Microsoft's list.
$script:AllowedDotNetSdkRootThumbprints = @(
    '8F43288AD272F3103B6FB1428485EA3014C0BCFE'   # Microsoft Root Certificate Authority 2011
)

# Returns $null when the facts are acceptable, otherwise the refusal text. Pure, so the script tests can feed it facts.
function Get-DotNetSdkRefusal {
    param(
        [AllowEmptyString()] [string]$SignatureStatus, [AllowEmptyString()] [string]$SignerSubject, [AllowEmptyString()] [string]$RootThumbprint,
        [AllowEmptyString()] [string]$ReportedVersion, [AllowEmptyString()] [string]$PinnedVersion,
        [string[]]$AllowedSubjects = $script:AllowedDotNetSdkSignerSubjects,
        [string[]]$AllowedRootThumbprints = $script:AllowedDotNetSdkRootThumbprints
    )
    if ($null -eq $AllowedSubjects -or $null -eq $AllowedRootThumbprints) { return 'The SDK signer allow-lists are not defined.' }
    if ($SignatureStatus -cne 'Valid') { return "dotnet.exe Authenticode status is '$SignatureStatus', expected 'Valid'." }
    $ok = $false
    foreach ($allowed in $AllowedSubjects) { if ($SignerSubject -ceq $allowed) { $ok = $true } }
    if (-not $ok) { return "dotnet.exe signer subject is not on the allow-list: '$SignerSubject'. If Microsoft changed it, review it and add it to `$script:AllowedDotNetSdkSignerSubjects in tools/lib/dotnet-sdk-lib.ps1." }
    if (-not (Test-SignerRootThumbprint -Thumbprint $RootThumbprint -AllowedThumbprints $AllowedRootThumbprints)) {
        return "dotnet.exe signer chain root is not on the allow-list: '$RootThumbprint'. If Microsoft changed its root, review it and add it to `$script:AllowedDotNetSdkRootThumbprints in tools/lib/dotnet-sdk-lib.ps1."
    }
    if ($PinnedVersion -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z') { return "global.json sdk.version '$PinnedVersion' is not an exact version." }
    if ($ReportedVersion -cne $PinnedVersion) { return "dotnet --version reports '$ReportedVersion' but global.json pins '$PinnedVersion'." }
    return $null
}

function Assert-DotNetSdk {
    param([string]$GlobalJsonPath = 'global.json')
    $pinned = [string]((Get-Content -Raw -LiteralPath $GlobalJsonPath | ConvertFrom-Json).sdk.version)
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $sig = Get-AuthenticodeSignature -LiteralPath $dotnet
    $subject = ''
    $rootThumb = ''
    $rootSubject = ''
    if ($sig.SignerCertificate) {
        $subject = $sig.SignerCertificate.Subject
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        # Revocation is covered by the Authenticode status; the chain here only identifies the root.
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        try {
            [void]$chain.Build($sig.SignerCertificate)
            if ($chain.ChainElements.Count -ge 2) {
                $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
                $rootThumb = $root.Thumbprint.ToUpperInvariant()
                $rootSubject = $root.Subject
            }
        } finally { $chain.Dispose() }
    }
    $dir = Split-Path -Parent (Resolve-Path -LiteralPath $GlobalJsonPath).Path
    $previousNoLogo = $env:DOTNET_NOLOGO
    $env:DOTNET_NOLOGO = '1'
    Push-Location -LiteralPath $dir
    try { $output = @(& $dotnet --version) } finally { Pop-Location; $env:DOTNET_NOLOGO = $previousNoLogo }
    $reported = ''
    foreach ($line in $output) { $text = ([string]$line).Trim(); if ($text) { $reported = $text } }   # the last non-empty line
    $refusal = Get-DotNetSdkRefusal -SignatureStatus ([string]$sig.Status) -SignerSubject $subject -RootThumbprint $rootThumb -ReportedVersion $reported -PinnedVersion $pinned
    if ($refusal) { throw "$refusal (dotnet: $dotnet)" }
    Write-Host "dotnet SDK $reported at ${dotnet}: Authenticode valid, signer $subject, chain root $rootSubject $rootThumb"
}
