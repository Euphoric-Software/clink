<#
.SYNOPSIS
  Resolves the latest .NET 10 Desktop Runtime (win-x64) installer from Microsoft's release metadata and verifies it.
.DESCRIPTION
  Picks the release named in "latest-runtime", its windowsdesktop file windowsdesktop-runtime-win-x64.exe (https, allow-listed
  host), downloads it into a fresh empty scratch directory, verifies the SHA-512 from the JSON and the Authenticode signature
  (Valid, exact allow-listed signer subject, allow-listed chain root), computes the SHA-256 that Inno Setup needs, then deletes the download. The file is NEVER
  executed. Output: JSON {version,url,sha256,sha512,size} on stdout; with $env:GITHUB_OUTPUT also key=value lines.
  Fails closed (non-zero exit) on anything unexpected.
.PARAMETER ReleasesJsonPath
  Use a local releases.json instead of fetching it (offline tests).
.PARAMETER NoDownload
  Selection only: no download, so sha256/size are not available (emitted empty).
#>
[CmdletBinding()]
param(
    [string]$ReleasesJsonPath,
    [switch]$NoDownload,
    [string]$ReleasesUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\dotnet-runtime-lib.ps1')

$scratch = $null
try {
    if ($ReleasesJsonPath) {
        $jsonText = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleasesJsonPath).Path)
    }
    else {
        [void](Test-DotNetRuntimeUrl -Url $ReleasesUrl)
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        # Microsoft serves releases.json directly (HTTP 200, checked 2026-10-27), so no redirect is followed.
        $jsonText = (Invoke-WebRequest -UseBasicParsing -Uri $ReleasesUrl -MaximumRedirection 0 -TimeoutSec 60).Content
        if ($jsonText -is [byte[]]) { $jsonText = [System.Text.Encoding]::UTF8.GetString($jsonText) }
    }
    $releases = $jsonText | ConvertFrom-Json
    $pick = Select-DotNetDesktopRuntime -Releases $releases

    $sha256 = ''
    $size = ''
    if (-not $NoDownload) {
        $scratch = Join-Path $env:TEMP ('clink-s3a-' + [guid]::NewGuid().ToString('N'))
        $downloadDir = Join-Path $scratch 'download'
        [void](New-Item -ItemType Directory -Path $downloadDir)
        $target = Join-Path $downloadDir $pick.name
        $oldProgress = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { $response = Invoke-WebRequest -UseBasicParsing -Uri $pick.url -OutFile $target -PassThru -TimeoutSec 600 }
        finally { $ProgressPreference = $oldProgress }
        # Windows PowerShell: BaseResponse.ResponseUri. PowerShell 7: BaseResponse.RequestMessage.RequestUri (final URI).
        $finalUri = $response.BaseResponse.ResponseUri
        if ($null -eq $finalUri) { $finalUri = $response.BaseResponse.RequestMessage.RequestUri }
        if ($null -eq $finalUri) { throw 'Could not determine the final download URL.' }
        [void](Test-DotNetRuntimeUrl -Url $finalUri.AbsoluteUri)

        $actual512 = Get-FileHashHex -Path $target -Algorithm SHA512
        if ($actual512 -ne $pick.sha512) { throw "SHA-512 mismatch: expected $($pick.sha512), got $actual512." }
        $signer = Assert-MicrosoftAuthenticode -Path $target
        [Console]::Error.WriteLine("Authenticode OK, signer: $($signer.subject)")
        [Console]::Error.WriteLine("Authenticode chain root: $($signer.rootSubject) $($signer.rootThumbprint)")
        $sha256 = Get-FileHashHex -Path $target -Algorithm SHA256
        $size = (Get-Item -LiteralPath $target).Length
    }

    $result = [ordered]@{
        version = $pick.version
        url     = $pick.url
        sha256  = $sha256
        sha512  = $pick.sha512
        size    = $size
    }
    foreach ($key in $result.Keys) { Assert-SafeOutputValue -Name $key -Value ([string]$result[$key]) }
    $lines = Format-GitHubOutputLines -Result $result
    $result | ConvertTo-Json -Compress | Write-Output

    if ($env:GITHUB_OUTPUT) {
        [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    }
}
catch {
    [Console]::Error.WriteLine("resolve-dotnet-runtime failed: $($_.Exception.Message)")
    exit 1
}
finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch)) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
