# Pure selection/validation logic for resolve-dotnet-runtime.ps1 (dot-source this file). No network, no downloads.
# Compatible with Windows PowerShell 5.1 and pwsh 7. Every function fails closed by throwing.
# All regexes are anchored with \A...\z (a plain $ also matches before a trailing newline) and use explicit [0-9]/[0-9a-fA-F]
# classes (\d matches Unicode digits). -cmatch / -ceq wherever case matters.

# Hosts the .NET release metadata is allowed to point at. builds.dotnet.microsoft.com is the only host used by the
# current 10.0 releases.json (checked 2026-10-27: every windowsdesktop url is https://builds.dotnet.microsoft.com/...).
# Older metadata used dotnetcli.azureedge.net / dotnetcli.blob.core.windows.net; those are deliberately NOT allowed
# (retired CDN, not needed). Add a host here only after seeing it in the real JSON and reviewing it.
$script:AllowedDotNetHosts = @('builds.dotnet.microsoft.com')

# Committed floor: latest-runtime below this version is refused, so a stale or rolled-back releases.json cannot make the
# installer ship an old runtime. RAISE THIS whenever a newer .NET 10 security release ships (and re-run the tests).
$script:MinimumRuntimeVersion = '10.0.14'

# Exact signer subjects (X509 Subject, compared with -ceq) accepted for the runtime installer. Observed on the live
# 10.0.14 windowsdesktop-runtime-10.0.14-win-x64.exe download (2026-10-27). To update: run resolve-dotnet-runtime.ps1,
# read the "Authenticode OK, signer: ..." line on stderr, review it, and add the exact string here.
$script:AllowedSignerSubjects = @(
    'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
)

# Thumbprints (SHA-1, upper case) of the root certificate the signer chain must end in. Fail closed. To update: run the
# resolver, read the "chain root" line on stderr, confirm the subject/thumbprint against Microsoft's published root list.
$script:AllowedSignerRootThumbprints = @(
    '8F43288AD272F3103B6FB1428485EA3014C0BCFE'   # Microsoft Root Certificate Authority 2011
)

function Test-DotNetRuntimeUrl {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Url,
        [string[]]$AllowedHosts = $script:AllowedDotNetHosts
    )
    $parsed = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$parsed)) {
        throw "Runtime URL is not an absolute URL: '$Url'"
    }
    if ($parsed.Scheme -ne 'https') { throw "Runtime URL must be https: '$Url'" }
    if (-not [string]::IsNullOrEmpty($parsed.UserInfo)) { throw "Runtime URL must not contain credentials: '$Url'" }
    if (-not $parsed.IsDefaultPort) { throw "Runtime URL must use the default https port: '$Url'" }
    if ($AllowedHosts -notcontains $parsed.Host.ToLowerInvariant()) {
        throw "Runtime URL host '$($parsed.Host)' is not allow-listed ($($AllowedHosts -join ', '))."
    }
    return $parsed
}

function Assert-SafeOutputValue {
    # Values that end up in JSON, GITHUB_OUTPUT and the Inno command line: no control characters, quotes, spaces or '%'.
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Name, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    if ($Value -cmatch '[\x00-\x20\x7F-\uFFFF"''%]') { throw "Output value '$Name' contains a forbidden character (control, space, quote, '%' or non-ASCII)." }
}

function Get-ExpectedDotNetRuntimeUrl {
    param([Parameter(Mandatory = $true)][string]$Version)
    if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw "Not a plain x.y.z version: '$Version'" }
    return "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$Version/windowsdesktop-runtime-$Version-win-x64.exe"
}

function Select-DotNetDesktopRuntime {
    # $Releases = parsed releases.json (ConvertFrom-Json). Returns [pscustomobject] version, url, sha512, name.
    param(
        [Parameter(Mandatory = $true)]$Releases,
        [string]$FileName = 'windowsdesktop-runtime-win-x64.exe',
        [string]$Rid = 'win-x64',
        [string[]]$AllowedHosts = $script:AllowedDotNetHosts,
        [string]$MinimumVersion = $script:MinimumRuntimeVersion
    )
    $latest = [string]$Releases.'latest-runtime'
    if ($latest -cnotmatch '\A10\.[0-9]+\.[0-9]+\z') { throw "latest-runtime is missing or not a stable 10.x.y version: '$latest'" }
    if ([version]$latest -lt [version]$MinimumVersion) { throw "latest-runtime '$latest' is below the committed minimum $MinimumVersion." }

    $matchingReleases = @($Releases.releases | Where-Object { $null -ne $_.runtime -and [string]$_.runtime.version -ceq $latest })
    if ($matchingReleases.Count -eq 0) { throw "No release entry has runtime.version '$latest'." }
    if ($matchingReleases.Count -gt 1) { throw "More than one release entry has runtime.version '$latest'." }
    $release = $matchingReleases[0]

    $desktop = $release.windowsdesktop
    if ($null -eq $desktop) { throw "Release $latest has no windowsdesktop section." }
    if ([string]$desktop.version -cne $latest) { throw "windowsdesktop.version '$($desktop.version)' does not match runtime '$latest'." }

    $files = @($desktop.files | Where-Object { [string]$_.name -ceq $FileName -and [string]$_.rid -ceq $Rid })
    if ($files.Count -eq 0) { throw "windowsdesktop.files has no entry '$FileName' with rid '$Rid'." }
    if ($files.Count -gt 1) { throw "windowsdesktop.files has more than one entry '$FileName' with rid '$Rid'." }
    $file = $files[0]

    # The URL is rebuilt from the validated version; the JSON value must equal it exactly (raw and as System.Uri normalises
    # it, which also rejects dot segments, percent escapes, query, fragment, ports and case tricks). Only the constructed
    # value is returned.
    $url = [string]$file.url
    [void](Test-DotNetRuntimeUrl -Url $url -AllowedHosts $AllowedHosts)
    $expected = Get-ExpectedDotNetRuntimeUrl -Version $latest
    if ($url -cne $expected) { throw "Runtime URL does not equal the expected URL '$expected': '$url'" }
    if ([Uri]::new($url).AbsoluteUri -cne $expected) { throw "Runtime URL is not in normalised form: '$url'" }
    Assert-SafeOutputValue -Name 'url' -Value $expected

    $hash = [string]$file.hash
    if ($hash -cnotmatch '\A[0-9a-fA-F]{128}\z') { throw "Runtime hash is not a 128 hex character SHA-512: '$hash'" }

    return [pscustomobject]@{
        version = $latest
        url     = $expected
        sha512  = $hash.ToLowerInvariant()
        name    = $FileName
    }
}

function Get-FileHashHex {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('SHA256', 'SHA512')][string]$Algorithm
    )
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Test-MicrosoftSignerSubject {
    # Pure: the whole subject DN must be one of the allow-listed strings (case-sensitive exact match). No substring or
    # per-RDN matching, so quoted-RDN spoofs such as OU="a, O=Microsoft Corporation, b" cannot pass.
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Subject,
        [string[]]$AllowedSubjects = $script:AllowedSignerSubjects
    )
    foreach ($allowed in $AllowedSubjects) { if ($Subject -ceq $allowed) { return $true } }
    return $false
}

function Test-SignerRootThumbprint {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Thumbprint,
        [string[]]$AllowedThumbprints = $script:AllowedSignerRootThumbprints
    )
    foreach ($allowed in $AllowedThumbprints) { if ($Thumbprint -ceq $allowed) { return $true } }
    return $false
}

function Assert-MicrosoftAuthenticode {
    # Status Valid, Authenticode signature, exact allow-listed signer subject, chain ending in an allow-listed root.
    # Returns [pscustomobject] subject, rootSubject, rootThumbprint.
    param([Parameter(Mandatory = $true)][string]$Path)
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    if ($sig.Status -ne 'Valid') { throw "Authenticode status is '$($sig.Status)', expected Valid." }
    if ($sig.SignatureType -ne 'Authenticode') { throw "Signature type is '$($sig.SignatureType)', expected Authenticode." }
    if ($null -eq $sig.SignerCertificate) { throw 'No signer certificate.' }
    $subject = $sig.SignerCertificate.Subject
    if (-not (Test-MicrosoftSignerSubject -Subject $subject)) { throw "Signer subject is not on the allow-list: '$subject'. If Microsoft changed its signing certificate, review it and update the allow-list variables (`$script:AllowedSignerSubjects) in tools\lib\dotnet-runtime-lib.ps1." }

    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    # Revocation is covered by WinVerifyTrust (Get-AuthenticodeSignature Status Valid); the chain here only identifies the root.
    $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    try {
        [void]$chain.Build($sig.SignerCertificate)
        if ($chain.ChainElements.Count -lt 2) { throw 'Signer certificate chain has no root.' }
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        $rootThumb = $root.Thumbprint.ToUpperInvariant()
        if (-not (Test-SignerRootThumbprint -Thumbprint $rootThumb)) { throw "Signer chain root is not on the allow-list: '$($root.Subject)' $rootThumb. If Microsoft changed its root, review it and update the allow-list variables (`$script:AllowedSignerRootThumbprints) in tools\lib\dotnet-runtime-lib.ps1." }
        return [pscustomobject]@{ subject = $subject; rootSubject = $root.Subject; rootThumbprint = $rootThumb }
    }
    finally { $chain.Dispose() }
}

function Format-GitHubOutputLines {
    # Exactly five lines version/url/sha256/sha512/size; each is asserted before it is returned.
    param([Parameter(Mandatory = $true)]$Result)
    $lines = @()
    foreach ($key in @('version', 'url', 'sha256', 'sha512', 'size')) {
        $value = [string]$Result.$key
        Assert-SafeOutputValue -Name $key -Value $value
        $line = "$key=$value"
        if ($line -cnotmatch '\A(version|url|sha256|sha512|size)=[^\r\n]*\z') { throw "Output line for '$key' has an unexpected format." }
        $lines += $line
    }
    return , $lines
}
