# Standalone tests for tools\lib\dotnet-runtime-lib.ps1 and tools\resolve-dotnet-runtime.ps1 (-NoDownload). No network.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\resolve-dotnet-runtime.tests.ps1   (exit 0 = all pass)
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $PSScriptRoot
. (Join-Path $toolsDir 'lib\dotnet-runtime-lib.ps1')
$fixture = Join-Path $PSScriptRoot 'fixtures\dotnet-releases\releases-valid.json'
$fixtureText = [System.IO.File]::ReadAllText($fixture)

$script:Pass = 0
$script:Fail = 0
function Assert-Test {
    param([string]$Name, [scriptblock]$Body)
    try { & $Body; $script:Pass++; Write-Host "PASS $Name" }
    catch { $script:Fail++; Write-Host "FAIL $Name : $($_.Exception.Message)" }
}
function Assert-Equal {
    param($Actual, $Expected)
    if ($Actual -ne $Expected) { throw "expected '$Expected' but got '$Actual'" }
}
function Assert-Rejected {
    # Mutates a fresh copy of the fixture and expects Select-DotNetDesktopRuntime to throw a message matching $Pattern.
    param([scriptblock]$Mutate, [string]$Pattern)
    $releases = $fixtureText | ConvertFrom-Json
    & $Mutate $releases
    $threw = $false
    try { [void](Select-DotNetDesktopRuntime -Releases $releases) }
    catch { $threw = $true; if ($_.Exception.Message -notmatch $Pattern) { throw "wrong error: $($_.Exception.Message)" } }
    if (-not $threw) { throw 'expected rejection but selection succeeded' }
}
function Get-X64Entry {
    param($Releases, [string]$Version = '10.0.14')
    $rel = @($Releases.releases | Where-Object { $_.runtime.version -eq $Version })[0]
    return @($rel.windowsdesktop.files | Where-Object { $_.name -eq 'windowsdesktop-runtime-win-x64.exe' })[0]
}

Assert-Test 'selects latest-runtime x64 exe from fixture' {
    $pick = Select-DotNetDesktopRuntime -Releases ($fixtureText | ConvertFrom-Json)
    Assert-Equal $pick.version '10.0.14'
    Assert-Equal $pick.url 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
    Assert-Equal $pick.sha512 '337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69'
}
Assert-Test 'follows latest-runtime, not first release' {
    $r = $fixtureText | ConvertFrom-Json
    $r.'latest-runtime' = '10.0.13'
    Assert-Equal (Select-DotNetDesktopRuntime -Releases $r -MinimumVersion '10.0.0').version '10.0.13'
}
Assert-Test 'rejects http url' {
    Assert-Rejected { param($r) (Get-X64Entry $r).url = 'http://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe' } 'https'
}
Assert-Test 'rejects foreign host' {
    Assert-Rejected { param($r) (Get-X64Entry $r).url = 'https://evil.example.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe' } 'allow-listed'
}
Assert-Test 'rejects look-alike host' {
    Assert-Rejected { param($r) (Get-X64Entry $r).url = 'https://builds.dotnet.microsoft.com.evil.example/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe' } 'allow-listed'
}
Assert-Test 'rejects credentials in url' {
    Assert-Rejected { param($r) (Get-X64Entry $r).url = 'https://user@builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe' } 'credentials'
}
Assert-Test 'rejects url with wrong file name' {
    Assert-Rejected { param($r) (Get-X64Entry $r).url = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/other.exe' } 'expected URL'
}
Assert-Test 'rejects missing x64 exe entry' {
    Assert-Rejected { param($r)
        $rel = @($r.releases | Where-Object { $_.runtime.version -eq '10.0.14' })[0]
        $rel.windowsdesktop.files = @($rel.windowsdesktop.files | Where-Object { $_.name -ne 'windowsdesktop-runtime-win-x64.exe' })
    } 'no entry'
}
Assert-Test 'rejects duplicate x64 exe entries' {
    Assert-Rejected { param($r)
        $rel = @($r.releases | Where-Object { $_.runtime.version -eq '10.0.14' })[0]
        $rel.windowsdesktop.files = @($rel.windowsdesktop.files) + @(Get-X64Entry $r)
    } 'more than one entry'
}
Assert-Test 'rejects hash of wrong length (SHA-256 length)' {
    Assert-Rejected { param($r) (Get-X64Entry $r).hash = ('ab' * 32) } '128 hex'
}
Assert-Test 'rejects non-hex hash' {
    Assert-Rejected { param($r) (Get-X64Entry $r).hash = ('zz' * 64) } '128 hex'
}
Assert-Test 'rejects latest-runtime pointing at a missing release' {
    Assert-Rejected { param($r) $r.'latest-runtime' = '10.0.99' } 'No release entry'
}
Assert-Test 'rejects duplicate releases for latest-runtime' {
    Assert-Rejected { param($r) $r.releases = @($r.releases) + @($r.releases[0]) } 'More than one release'
}
Assert-Test 'rejects missing windowsdesktop section' {
    Assert-Rejected { param($r) $r.releases[0].PSObject.Properties.Remove('windowsdesktop') } 'no windowsdesktop'
}
Assert-Test 'rejects non-10 latest-runtime' {
    Assert-Rejected { param($r) $r.'latest-runtime' = '9.0.1' } 'stable 10'
}
Assert-Test 'rejects preview latest-runtime' {
    Assert-Rejected { param($r) $r.'latest-runtime' = '10.0.0-preview.1' } 'stable 10'
}

# Exact-URL rule: the JSON url must equal the URL constructed from the validated version.
$goodUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
$badUrls = [ordered]@{
    'dot segments'    = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/x/../windowsdesktop-runtime-10.0.14-win-x64.exe'
    'single quote'    = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'"
    'double quote'    = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe"'
    'CR LF'           = "$goodUrl`r`n"
    'space'           = "$goodUrl "
    'percent'         = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64%2eexe'
    'query'           = "$goodUrl?a=b"
    'fragment'        = "$goodUrl#x"
    'non-default port' = 'https://builds.dotnet.microsoft.com:8443/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
    'other version path' = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe'
    'upper-case host' = 'https://BUILDS.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
}
foreach ($name in $badUrls.Keys) {
    $value = $badUrls[$name]
    Assert-Test "rejects url: $name" {
        Assert-Rejected { param($r) (Get-X64Entry $r).url = $value } '.'
    }
}
Assert-Test 'rejects windowsdesktop.version not matching' {
    Assert-Rejected { param($r) $r.releases[0].windowsdesktop.version = '10.0.13' } 'does not match'
}
Assert-Test 'rejects version with trailing newline' {
    Assert-Rejected { param($r) $r.'latest-runtime' = "10.0.14`n" } 'stable 10'
}
Assert-Test 'rejects Unicode digits in version' {
    Assert-Rejected { param($r) $r.'latest-runtime' = ('10.0.1' + [char]0x0664) } 'stable 10'
}
Assert-Test 'rejects hash with trailing newline' {
    Assert-Rejected { param($r) (Get-X64Entry $r).hash = ((Get-X64Entry $r).hash + "`n") } '128 hex'
}
Assert-Test 'rejects version below the committed floor' {
    Assert-Rejected { param($r) $r.'latest-runtime' = '10.0.13' } 'below the committed minimum'
}
Assert-Test 'floor constant equals fixture version or older' {
    if ([version]$script:MinimumRuntimeVersion -gt [version]'10.0.14') { throw 'floor is above the fixture; update the fixture' }
}
Assert-Test 'Assert-SafeOutputValue rejects bad characters' {
    foreach ($bad in @("a b", "a`nb", "a'b", 'a"b', 'a%b', "a`tb", ('a' + [char]0x00E9))) {
        $threw = $false
        try { Assert-SafeOutputValue -Name 'x' -Value $bad } catch { $threw = $true }
        if (-not $threw) { throw "accepted '$bad'" }
    }
    Assert-SafeOutputValue -Name 'x' -Value 'https://builds.dotnet.microsoft.com/a-b_c.exe'
}

# Signer subject decision (pure function).
$okSubject = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
Assert-Test 'subject: accepts the exact allow-listed DN' {
    if (-not (Test-MicrosoftSignerSubject -Subject $okSubject)) { throw 'rejected' }
}
$badSubjects = [ordered]@{
    'quoted-OU spoof'      = 'CN=Evil Ltd, OU="a, O=Microsoft Corporation, b", O=Evil Ltd, C=US'
    'bare O only'          = 'O=Microsoft Corporation'
    'prefix extra'         = 'CN=Evil, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
    'suffix extra'         = "$okSubject, OU=x"
    'different case'       = 'CN=.NET, O=MICROSOFT CORPORATION, L=Redmond, S=Washington, C=US'
    'trailing newline'     = "$okSubject`n"
    'look-alike'           = 'CN=.NET, O=Microsoft Corporation Ltd, L=Redmond, S=Washington, C=US'
    'empty'                = ''
}
foreach ($name in $badSubjects.Keys) {
    $value = $badSubjects[$name]
    Assert-Test "subject: rejects $name" {
        if (Test-MicrosoftSignerSubject -Subject $value) { throw 'accepted' }
    }
}
Assert-Test 'root thumbprint: pinned value accepted, others rejected' {
    if (-not (Test-SignerRootThumbprint -Thumbprint '8F43288AD272F3103B6FB1428485EA3014C0BCFE')) { throw 'pinned rejected' }
    if (Test-SignerRootThumbprint -Thumbprint '8f43288ad272f3103b6fb1428485ea3014c0bcfe') { throw 'lower case accepted' }
    if (Test-SignerRootThumbprint -Thumbprint ('0' * 40)) { throw 'unknown accepted' }
    if (Test-SignerRootThumbprint -Thumbprint '') { throw 'empty accepted' }
}
Assert-Test 'Authenticode: an unsigned file is rejected' {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-s3a-test-' + [guid]::NewGuid().ToString('N') + '.exe')
    try {
        [System.IO.File]::WriteAllBytes($tmp, [byte[]](1..64))
        $threw = $false
        try { [void](Assert-MicrosoftAuthenticode -Path $tmp) }
        catch { $threw = $true; if ($_.Exception.Message -notmatch "Authenticode status is '(NotSigned|UnknownError|NotSupportedFileFormat)'") { throw "wrong error: $($_.Exception.Message)" } }
        if (-not $threw) { throw 'accepted an unsigned file' }
    }
    finally { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
}
Assert-Test 'GITHUB_OUTPUT lines: exactly five, fixed order' {
    $lines = Format-GitHubOutputLines -Result ([pscustomobject]@{ version = '10.0.14'; url = $goodUrl; sha256 = ('ab' * 32); sha512 = ('cd' * 64); size = 123 })
    Assert-Equal @($lines).Count 5
    Assert-Equal (@($lines | ForEach-Object { $_.Split('=')[0] }) -join ',') 'version,url,sha256,sha512,size'
    foreach ($l in $lines) { if ($l -cnotmatch '\A(version|url|sha256|sha512|size)=[^\r\n]*\z') { throw "bad line $l" } }
}
Assert-Test 'GITHUB_OUTPUT lines: a value with a newline is refused' {
    $threw = $false
    try { [void](Format-GitHubOutputLines -Result ([pscustomobject]@{ version = "10.0.14`nx=y"; url = $goodUrl; sha256 = ''; sha512 = ''; size = '' })) } catch { $threw = $true }
    if (-not $threw) { throw 'accepted' }
}

# End to end through the script, offline (-NoDownload) in a child process; run under the current host's executable.
$hostExe = (Get-Process -Id $PID).Path
$resolverScript = Join-Path $toolsDir 'resolve-dotnet-runtime.ps1'
Assert-Test 'script -NoDownload prints JSON for the fixture' {
    $out = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $resolverScript -ReleasesJsonPath $fixture -NoDownload
    Assert-Equal $LASTEXITCODE 0
    $o = ($out -join "`n") | ConvertFrom-Json
    Assert-Equal $o.version '10.0.14'
    Assert-Equal $o.sha256 ''
}
Assert-Test 'script writes GITHUB_OUTPUT key=value lines' {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-s3a-test-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        $env:GITHUB_OUTPUT = $tmp
        [void](& $hostExe -NoProfile -ExecutionPolicy Bypass -File $resolverScript -ReleasesJsonPath $fixture -NoDownload)
        Assert-Equal $LASTEXITCODE 0
        $text = [System.IO.File]::ReadAllText($tmp)
        $lines = @($text -split "`n" | Where-Object { $_ -ne '' })
        Assert-Equal $lines.Count 5
        Assert-Equal (($lines | ForEach-Object { $_.Split('=')[0] }) -join ',') 'version,url,sha256,sha512,size'
        if ($text -notmatch '(?m)^version=10\.0\.14$') { throw "missing version line: $text" }
        if ($text -notmatch '(?m)^url=https://builds\.dotnet\.microsoft\.com/') { throw "missing url line: $text" }
    }
    finally { Remove-Item Env:\GITHUB_OUTPUT -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
}
Assert-Test 'script exits 1 on a rejected fixture' {
    $bad = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-s3a-test-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        [System.IO.File]::WriteAllText($bad, $fixtureText.Replace('https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe', 'http://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'))
        $ErrorActionPreference = 'Continue'
        [void](& $hostExe -NoProfile -ExecutionPolicy Bypass -File $resolverScript -ReleasesJsonPath $bad -NoDownload 2>$null)
        Assert-Equal $LASTEXITCODE 1
    }
    finally { Remove-Item -LiteralPath $bad -ErrorAction SilentlyContinue }
}

Write-Host "Passed: $script:Pass  Failed: $script:Fail"
if ($script:Fail -gt 0) { exit 1 }
exit 0
