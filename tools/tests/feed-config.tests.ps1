# Tests for the feed.json rules of tools/feed-lib.ps1 (Get-FeedConfig, Resolve-FeedBaseUrl, Test-FeedAssetUrlUnder), which must match the
# client (FeedTrust.Create / Parse / ValidFeedUrl and UpdateFeed.IsUnder in Clink/Services/UpdateFeed.cs): the shared fixtures in
# tools/tests/feed-trust-cases.json run through Get-FeedConfig here and through FeedTrust.Parse in the C# unit tests. Standalone: exits 0 when
# everything passed, 1 otherwise. Windows PowerShell 5.1 and pwsh 7, no modules, no network. Every file lives in a scratch folder.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-feedcfgtests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
. (Join-Path $toolsDir 'feed-lib.ps1')
$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "assertion failed: $Message" }
}
function Assert-Throws {
    param([scriptblock]$Body, [string]$Pattern, [string]$What)
    $message = $null
    try { & $Body } catch { $message = $_.Exception.Message }
    Assert-True ($null -ne $message) "$What : did not throw"
    Assert-True ($message -match $Pattern) "$What : message '$message' does not match '$Pattern'"
}
function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    try { & $Body; $script:passed++; Write-Host "PASS  $Name" }
    catch { $script:failed++; Write-Host "FAIL  $Name : $($_.Exception.Message)" }
}
function New-RawFeedFile {
    param([string]$Text)
    $path = Join-Path $scratch ('feed-' + [guid]::NewGuid().ToString('N') + '.json')
    [System.IO.File]::WriteAllText($path, $Text, $utf8)
    $path
}

try {
    $fixture = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'feed-trust-cases.json'), $utf8) | ConvertFrom-Json
    $k1 = [string]$fixture.keys.K1
    $k2 = [string]$fixture.keys.K2
    function Expand-FixtureJson { param([string]$Text) $Text.Replace('@K1@', $k1).Replace('@K2@', $k2) }

    Invoke-Test 'fixtures: the shared file has every kind of case, and the two sides differ only where PowerShell is stricter' {
        $cases = @($fixture.cases)
        Assert-True ($cases.Count -ge 40) "only $($cases.Count) fixture cases"
        foreach ($state in 'off', 'on', 'misconfigured') { Assert-True (@($cases | Where-Object { $_.ps -ceq $state }).Count -ge 3) "fewer than 3 cases with ps = $state" }
        foreach ($c in $cases) {
            Assert-True (@('off', 'on', 'misconfigured') -ccontains [string]$c.ps -and @('off', 'on', 'misconfigured') -ccontains [string]$c.cs) "$($c.name): a state is not off/on/misconfigured"
            if ($c.ps -cne $c.cs) { Assert-True ($c.ps -ceq 'misconfigured' -and $c.cs -ceq 'on') "$($c.name): ps and cs differ, but not as 'PowerShell refuses what the client normalises'" }
            if ($c.cs -ceq 'on') { Assert-True ($c.stableBase -and $c.devBase) "$($c.name): an on case without resolved bases" }
        }
        Assert-True (($cases | ForEach-Object { $_.name } | Select-Object -Unique).Count -eq $cases.Count) 'two fixture cases share a name'
    }

    Invoke-Test 'fixtures: every case gives the state and the resolved bases the file says (Get-FeedConfig, both channels)' {
        foreach ($c in @($fixture.cases)) {
            $file = New-RawFeedFile (Expand-FixtureJson ([string]$c.json))
            switch ([string]$c.ps) {
                'misconfigured' {
                    $threw = $false
                    try { [void](Get-FeedConfig -Path $file -Channel stable) } catch { $threw = $true }
                    Assert-True $threw "$($c.name): stable did not throw"
                    $threw = $false
                    try { [void](Get-FeedConfig -Path $file -Channel dev) } catch { $threw = $true }
                    Assert-True $threw "$($c.name): dev did not throw"
                }
                'off' {
                    foreach ($ch in 'stable', 'dev') {
                        $r = Get-FeedConfig -Path $file -Channel $ch
                        Assert-True ((-not $r.Enabled) -and $r.Mode -ceq 'off' -and $r.FeedUrl -ceq '' -and $r.Keys.Count -eq 0) "$($c.name): $ch is not off"
                    }
                }
                'on' {
                    $s = Get-FeedConfig -Path $file -Channel stable
                    $d = Get-FeedConfig -Path $file -Channel dev
                    Assert-True ($s.Enabled -and $d.Enabled -and $s.Mode -ceq 'on') "$($c.name): not on"
                    Assert-True ($s.FeedUrl -ceq [string]$c.stableBase) "$($c.name): stable base '$($s.FeedUrl)', expected '$($c.stableBase)'"
                    Assert-True ($d.FeedUrl -ceq [string]$c.devBase) "$($c.name): dev base '$($d.FeedUrl)', expected '$($c.devBase)'"
                    Assert-True ($s.Keys.Count -eq 1 -and $s.Keys[0] -ceq $k1 -and $d.Keys.Count -eq 1 -and $d.Keys[0] -ceq $k2) "$($c.name): keys are not per channel (trimmed)"
                }
            }
        }
    }

    Invoke-Test 'misconfigured messages name every problem (the C# wording)' {
        $both = '"stableKeys":["' + $k1 + '"],"devKeys":["' + $k2 + '"]'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrl":"https://dl.example/clink/","devFeedUrl":"   ",' + $both + '}')) -Channel dev) } 'devFeedUrl is invalid \(empty\)' 'blank devFeedUrl'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrl":"https://dl.example/clink/","devFeedUrl":"https://dl.example/clink/dev/",' + $both + '}')) -Channel dev) } 'nested \(one is inside the other\)' 'nested bases'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrl":"https://dl.example/clink/","devFeedUrl":false,' + $both + '}')) -Channel dev) } 'devFeedUrl is not a string' 'devFeedUrl type'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrl":7,' + $both + '}')) -Channel dev) } 'feedUrl is not a string' 'feedUrl type'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":"1","feedUrl":"https://dl.example/clink/",' + $both + '}')) -Channel dev) } 'unsupported schema' 'schema type'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrl":"https://dl.example/clink/","stableKeys":"x","devKeys":["' + $k2 + '"]}')) -Channel dev) } 'stableKeys is not a list' 'a key list that is a string'
        # Several problems are all reported, in one message.
        $r = $null
        try { [void](Get-FeedConfig -Path (New-RawFeedFile '{"schema":2,"feedUrl":"http://dl.example/clink/","stableKeys":[],"devKeys":[]}') -Channel dev) } catch { $r = $_.Exception.Message }
        Assert-True ($r -match 'unsupported schema' -and $r -match 'feedUrl is invalid \(not https\)' -and $r -match 'stableKeys is empty' -and $r -match 'devKeys is empty') "the message lists not all problems: $r"
    }

    Invoke-Test 'property names that match a known key case-insensitively but not exactly are refused (PowerShell member access would read them, the client would not)' {
        $both = '"stableKeys":["' + $k1 + '"],"devKeys":["' + $k2 + '"]'
        foreach ($variant in 'FeedUrl', 'FEEDURL', 'feedurl', 'devfeedurl', 'DevFeedUrl') {
            $text = '{"schema":1,"feedUrl":"https://dl.example/clink/",' + $both + ',"' + $variant + '":"https://dl.example/clink/"}'
            # Windows PowerShell 5.1's ConvertFrom-Json already refuses two keys that differ only in case ("duplicated keys"); pwsh 7 keeps both.
            Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile $text) -Channel stable) } "(property '$variant' is spelled differently|duplicated keys)" "extra $variant beside the real property"
        }
        # The same variants on their own (the real property absent): the file would otherwise look empty or half-filled to the client.
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile '{"schema":1,"feedUrl":"","FeedUrl":"https://dl.example/clink/","stableKeys":[],"devKeys":[]}') -Channel stable) } "(property 'FeedUrl' is spelled differently|duplicated keys)" 'FeedUrl beside an empty feedUrl'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile ('{"Schema":1,"feedUrl":"https://dl.example/clink/",' + $both + '}')) -Channel stable) } "property 'Schema' is spelled differently" 'Schema'
        Assert-Throws { [void](Get-FeedConfig -Path (New-RawFeedFile '{"schema":1,"StableKeys":[],"devKeys":[]}') -Channel stable) } "property 'StableKeys' is spelled differently" 'StableKeys'
        # Properties that merely resemble a known one are unknown and ignored.
        $ok = Get-FeedConfig -Path (New-RawFeedFile ('{"schema":1,"feedUrls":"x","feedUrl":"https://dl.example/clink/",' + $both + '}')) -Channel stable
        Assert-True $ok.Enabled 'an unrelated property name was refused'
    }

    Invoke-Test 'Resolve-FeedBaseUrl: the rules of FeedTrust.ValidFeedUrl, canonical forms only, with the reason' {
        foreach ($good in 'https://h.example/', 'https://h.example/a/b/', 'https://h.example:8443/clink/', 'https://pub-1.r2.dev/clink-staging/', '  https://h.example/clink/ ') {
            $r = Resolve-FeedBaseUrl -Text $good
            Assert-True ($null -ne $r.Url -and $r.Url -ceq $good.Trim()) "refused or changed a good URL: '$good' -> '$($r.Url)' $($r.Why)"
        }
        $cases = @(
            @('', 'empty'), @('   ', 'empty'), @($null, 'empty'),
            @('h.example/clink/', 'not an absolute URL'), @('http://h.example/clink/', 'not https'), @('ftp://h.example/clink/', 'not https'),
            @('https://h.example/clink', "must end with '/'"), @('https://h.example', "must end with '/'"),
            @('https://h.example/clink/?x=1', 'query or fragment'), @('https://h.example/clink/#f', 'query or fragment'), @('https://h.example/clink/?', 'query or fragment'),
            @('https://u@h.example/clink/', 'user info'), @('https://u:p@h.example/clink/', 'user info'),
            @('https://H.example/clink/', 'canonical'), @('HTTPS://h.example/clink/', 'canonical'), @('https://h.example:443/clink/', 'canonical'),
            @('https://h.example/a/../clink/', 'canonical'), @('https://h.example/./clink/', 'canonical'), @('https://h.example/%63link/', 'canonical'),
            @('https://h.example/%41/', 'canonical'), @('https://h.example/%7e/', 'canonical'), @('https://h.example/cl ink/', 'canonical'),
            @('https://h.example\clink/', '(canonical|not an absolute URL)')   # .NET Framework refuses the backslash host outright, .NET Core normalises it
        )
        foreach ($c in $cases) {
            $r = Resolve-FeedBaseUrl -Text $c[0]
            Assert-True ($null -eq $r.Url) "accepted '$($c[0])' as '$($r.Url)'"
            $pattern = if ($c[1].StartsWith('(')) { $c[1] } else { [regex]::Escape($c[1]) }
            Assert-True ($r.Why -match $pattern) "'$($c[0])': the reason '$($r.Why)' lacks '$($c[1])'"
        }
        Assert-True (Test-FeedUrlShape 'https://h.example/clink/') 'Test-FeedUrlShape refuses a good URL'
        Assert-True (-not (Test-FeedUrlShape 'https://h.example/clink')) 'Test-FeedUrlShape accepts a bad URL'
    }

    Invoke-Test 'Test-FeedAssetUrlUnder: the rules of UpdateFeed.IsUnder (strictly inside the base, no //, no escaped plain character, no dot segment, no backslash, no query, same host and port)' {
        $base = 'https://dl.example/clink/'
        foreach ($good in 'https://dl.example/clink/releases/v0.4.2/Clink.exe', 'https://dl.example/clink/Clink.exe', 'https://dl.example/clink/dev/v0.4.2-dev.1/Clink-framework.exe', 'https://DL.example/clink/a/b.exe') {
            Assert-True (Test-FeedAssetUrlUnder -Url $good -Base $base) "refused $good"
        }
        foreach ($bad in 'https://dl.example/clink/', 'https://dl.example/clink', 'https://dl.example/other/Clink.exe', 'https://dl.example/clinkx/Clink.exe', 'http://dl.example/clink/Clink.exe',
            'https://dl.example:8443/clink/Clink.exe', 'https://evil.example/clink/Clink.exe', 'https://dl.example./clink/Clink.exe', 'https://u@dl.example/clink/Clink.exe',
            'https://dl.example/clink/Clink.exe?x=1', 'https://dl.example/clink/Clink.exe#f', 'https://dl.example/clink//Clink.exe', 'https://dl.example/clink/a//b/Clink.exe',
            'https://dl.example/clink/%63/Clink.exe', 'https://dl.example/clink/%41.exe', 'https://dl.example/clink/%2e%2e/Clink.exe', 'https://dl.example/clink/%2E/Clink.exe',
            'https://dl.example/clink/../Clink.exe', 'https://dl.example/clink/a/../../x/Clink.exe', 'https://dl.example/clink/a%5Cb.exe', 'https://dl.example/clink/a\b.exe', '', 'not a url',
            # Stricter than the client: a literal dot segment (or backslash) that Uri would normalise away (the client would fetch another URL than the one written).
            'https://dl.example/clink/a/../Clink.exe', 'https://dl.example/clink/./Clink.exe', 'https://dl.example/clink/a/./b/Clink.exe') {
            Assert-True (-not (Test-FeedAssetUrlUnder -Url $bad -Base $base)) "accepted $bad"
        }
        # An escaped slash stays inside the base as one segment name (the client does not refuse it either); a different base path works too.
        Assert-True (Test-FeedAssetUrlUnder -Url 'https://dl.example/clink/a%2Fb.exe' -Base $base) 'refused an escaped slash that stays inside the base'
        Assert-True (Test-FeedAssetUrlUnder -Url 'https://h.example/a/b/releases/v1/Clink.exe' -Base 'https://h.example/a/b/') 'refused a URL under a deeper base'
        Assert-True (-not (Test-FeedAssetUrlUnder -Url 'https://h.example/a/Clink.exe' -Base 'https://h.example/a/b/')) 'accepted a URL above a deeper base'
    }

    Invoke-Test 'the scripts stay BOM-free ASCII' {
        foreach ($rel in 'feed-lib.ps1', 'tests\feed-config.tests.ps1') {
            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $toolsDir $rel))
            Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "$rel has a BOM"
            Assert-True (-not ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1)) "$rel has non-ASCII bytes"
        }
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
