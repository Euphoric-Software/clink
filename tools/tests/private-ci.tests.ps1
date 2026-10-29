# Tests for the private-CI slice (S7): publish jobs without GitHub environments (repo secrets, strong job guards, secrets only in the
# publish-job steps that need them), artifact retention, notes.txt (generation, upload, notesUrl), the check-published feed expectations
# (CLINK_EXPECT_FEED_URL / CLINK_EXPECT_DEV_FEED_URL) and tools/new-feed-key.ps1 (dev "next" key, the owner summary). Standalone: exits 0
# when everything passed, 1 otherwise. Windows PowerShell 5.1 and pwsh 7, no modules. Nothing here touches a network, a real bucket or a
# real key: every key is a throwaway made by new-feed-key.ps1 or inside a test, every bucket is an in-memory fake.
# Skips (printed as "(skipped: ...)"): only when the root of the repository's drive cannot be written (new-feed-key tests need a folder that
# is outside every git work tree, and TEMP may be inside the repo).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$workflowDir = Join-Path $repo '.github\workflows'
$hostExe = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-privateci-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

. (Join-Path $toolsDir 'feed-lib.ps1')
. (Join-Path $toolsDir 'lib\feed-publish-lib.ps1')
. (Join-Path $toolsDir 'lib\notes-lib.ps1')
. (Join-Path $toolsDir 'lib\release-verify-lib.ps1')
$script:passed = 0
$script:failed = 0
$script:skipped = New-Object System.Collections.ArrayList

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
function Get-ScratchFolder {
    param([string]$Name)
    $path = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $path
}
function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}
function Invoke-Child {
    # Runs a script in a child host and returns Code and Text (stdout + stderr). Extra environment variables are set for the child only.
    param([string]$Script, [string[]]$Arguments, [hashtable]$Environment = @{})
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments
    $quoted = $all | ForEach-Object { if ($_ -match '\s' -or $_ -eq '') { '"' + $_ + '"' } else { $_ } }
    $id = [guid]::NewGuid().ToString('N')
    $outFile = Join-Path $scratch "out-$id.txt"
    $errFile = Join-Path $scratch "err-$id.txt"
    $saved = @{}
    foreach ($k in $Environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$Environment[$k]) }
    try {
        $run = Start-Process -FilePath $hostExe -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    } finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    [pscustomobject]@{ Code = $run.ExitCode; Text = [System.IO.File]::ReadAllText($outFile) + [System.IO.File]::ReadAllText($errFile) }
}

# ---- workflow helpers ---------------------------------------------------------------------------------------------------------------
function Read-Workflow { param([string]$Name) [System.IO.File]::ReadAllText((Join-Path $workflowDir $Name)) }
function Remove-YamlComments {
    # The workflow text without full-line comments (a comment may name a secret or "environment:" without using it).
    param([string]$Text)
    (($Text -split "`r?`n") | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"
}
function Split-Jobs {
    # Head, Build, Publish and (MF-2) Cleanup: the cleanup job follows the publish job; Publish is the publish job only. Cleanup is '' when
    # the workflow has no cleanup job.
    param([string]$Text)
    $b = $Text.IndexOf("`n  build:"); $p = $Text.IndexOf("`n  publish:"); $c = $Text.IndexOf("`n  cleanup:")
    Assert-True ($b -ge 0 -and $p -gt $b) 'workflow has no build and publish job'
    Assert-True ($c -lt 0 -or $c -gt $p) 'the cleanup job is not after the publish job'
    $pubEnd = if ($c -gt $p) { $c } else { $Text.Length }
    [pscustomobject]@{ Head = $Text.Substring(0, $b); Build = $Text.Substring($b, $p - $b); Publish = $Text.Substring($p, $pubEnd - $p); Cleanup = $(if ($c -gt $p) { $Text.Substring($c) } else { '' }) }
}
function Get-Steps {
    param([string]$JobText)
    @([regex]::Split($JobText, '(?m)^(?=      - name: )') | Where-Object { $_.StartsWith('      - name: ') })
}
function Get-StepTitle { param([string]$Step) (($Step -split "`n")[0] -replace '^\s*- name:\s*', '').Trim() }

$stableSecrets = @('CLINK_FEED_STABLE_SIGNING_KEY', 'R2_STABLE_ACCESS_KEY_ID', 'R2_STABLE_SECRET_ACCESS_KEY', 'R2_STABLE_ENDPOINT', 'R2_STABLE_BUCKET', 'CLOUDFLARE_API_TOKEN', 'CLOUDFLARE_ZONE_ID')
$devSecrets = @('CLINK_FEED_DEV_SIGNING_KEY', 'R2_DEV_ACCESS_KEY_ID', 'R2_DEV_SECRET_ACCESS_KEY', 'R2_DEV_ENDPOINT', 'R2_DEV_BUCKET', 'CLOUDFLARE_API_TOKEN', 'CLOUDFLARE_ZONE_ID')
$secretSteps = @('Check bucket secrets', 'Make and sign the manifest', 'Publish to the update feed')

try {
    # ================================================================== 1. no environments, strong job guards
    Invoke-Test 'no environment: key in any workflow (the publish jobs use repo secrets, not GitHub environments)' {
        foreach ($wf in Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml') {
            $code = Remove-YamlComments ([System.IO.File]::ReadAllText($wf.FullName))
            Assert-True ($code -notmatch '(?m)^\s*(?:- )?environment\s*:') "$($wf.Name) still has an environment: key"
            Assert-True ($code -notmatch '(?m)^\s*name:\s*feed-(stable|dev)\s*$') "$($wf.Name) still names the feed-stable / feed-dev environment"
        }
    }

    Invoke-Test 'job guards without environments: stable = pushed tag vX.Y.Z whose commit is on origin/main; dev = push (or dispatch) on refs/heads/dev' {
        $stable = Split-Jobs (Read-Workflow 'release.yml')
        $dev = Split-Jobs (Read-Workflow 'release-dev.yml')
        # Job-level if: evaluated before any step runs.
        Assert-True ($stable.Publish -match "(?m)^    if: github\.event_name == 'push' && startsWith\(github\.ref, 'refs/tags/v'\)\s*$") 'release.yml publish has no job-level if: for a pushed v* tag'
        Assert-True ($dev.Publish -match "(?m)^    if: github\.ref == 'refs/heads/dev' && \(github\.event_name == 'push' \|\| github\.event_name == 'workflow_dispatch'\)\s*$") 'release-dev.yml publish has no job-level if: for the dev branch'
        # The first step after checkout/guard order: the guards precede every secret.
        $guardStable = $stable.Publish.IndexOf('- name: Guard - stable tag, commit on main, release not yet there')
        $guardDev = $dev.Publish.IndexOf('- name: Guard - dev branch only, release not yet there')
        Assert-True ($guardStable -gt 0 -and $guardDev -gt 0) 'a publish guard step is missing'
        Assert-True ($stable.Publish.IndexOf('secrets.') -gt $guardStable -and $dev.Publish.IndexOf('secrets.') -gt $guardDev) 'a secret is referenced before the guard step'
        # The stable guard text: push event, the anchored pattern, the hard-coded origin/main check.
        $stableGuard = @(Get-Steps $stable.Publish | Where-Object { (Get-StepTitle $_) -like 'Guard - stable tag*' })[0]
        Assert-True ($stableGuard.Contains("`$env:GITHUB_EVENT_NAME -cne 'push'")) 'the stable guard does not require a push event'
        Assert-True ($stableGuard.Contains("-cnotmatch '\Arefs/tags/v[0-9]+\.[0-9]+\.[0-9]+\z'")) 'the stable guard pattern is not \A..\z'
        Assert-True ($stableGuard.Contains('git merge-base --is-ancestor $env:GITHUB_SHA origin/main')) 'the stable guard does not require the commit to be on origin/main'
        Assert-True ($stable.Publish.Contains('fetch-depth: 0')) 'the stable publish job does not fetch full history for the main check'
        # The pattern itself, applied to samples (case-sensitive, anchored, ASCII digits).
        $pattern = '\Arefs/tags/v[0-9]+\.[0-9]+\.[0-9]+\z'
        foreach ($ok in 'refs/tags/v0.4.2', 'refs/tags/v10.20.30') { Assert-True ($ok -cmatch $pattern) "$ok was refused" }
        $arabic = ([string][char]0x0661) + '.' + ([string][char]0x0662) + '.' + ([string][char]0x0663)
        foreach ($bad in 'refs/tags/v0.4.2-dev.1', "refs/tags/v0.4.2`n", 'refs/heads/main', 'refs/tags/v0.4', 'refs/tags/V0.4.2', 'refs/tags/v0.4.2x', 'xrefs/tags/v0.4.2', "refs/tags/v$arabic", 'refs/tags/v0.4.2.1') {
            Assert-True ($bad -cnotmatch $pattern) "'$bad' passes the stable tag pattern"
        }
        # Dev: exact ref, push or dispatch only.
        $devGuard = @(Get-Steps $dev.Publish | Where-Object { (Get-StepTitle $_) -like 'Guard - dev branch only*' })[0]
        Assert-True ($devGuard.Contains("`$env:GITHUB_REF -ne 'refs/heads/dev'")) 'the dev guard does not compare the ref with refs/heads/dev'
        Assert-True ($devGuard.Contains("`$env:GITHUB_EVENT_NAME -cne 'push' -and `$env:GITHUB_EVENT_NAME -cne 'workflow_dispatch'")) 'the dev guard does not restrict the event'
        # release.yml has no manual or branch trigger; build.yml (pull requests) has no publish job at all.
        Assert-True ((Read-Workflow 'release.yml') -notmatch 'workflow_dispatch') 'release.yml has workflow_dispatch'
        Assert-True ((Read-Workflow 'build.yml') -notmatch '(?m)^\s+publish:') 'build.yml has a publish job'
    }

    Invoke-Test 'secrets only in publish-job steps: never in build.yml, never in a build job, never job-wide, only the three secret steps, only the expected names' {
        # build.yml runs for pull requests: it must not name any secret at all.
        $buildYml = Remove-YamlComments (Read-Workflow 'build.yml')
        Assert-True ($buildYml -notmatch '(?i)secrets') 'build.yml names a secret'
        Assert-True ($buildYml -match '(?m)^\s+pull_request:') 'build.yml lost its pull_request trigger (this test guards that trigger)'
        foreach ($pair in @(@('release.yml', $stableSecrets), @('release-dev.yml', $devSecrets))) {
            $text = Read-Workflow $pair[0]
            $jobs = Split-Jobs $text
            Assert-True ((Remove-YamlComments $jobs.Head) -notmatch '(?i)secrets') "$($pair[0]): a secret is named in the workflow head"
            Assert-True ((Remove-YamlComments $jobs.Build) -notmatch '(?i)secrets') "$($pair[0]): the build job names a secret"
            $header = Remove-YamlComments ($jobs.Publish.Substring(0, $jobs.Publish.IndexOf('      - name: ')))
            Assert-True ($header -notmatch '(?i)secrets') "$($pair[0]): the publish job header (job-wide env) names a secret"
            $code = Remove-YamlComments $text
            Assert-True ($code -notmatch 'secrets:\s*inherit|toJSON\(secrets\)|secrets\[') "$($pair[0]): secrets are passed on wholesale or by dynamic name"
            $seen = New-Object System.Collections.Generic.HashSet[string]
            foreach ($step in (Get-Steps $jobs.Publish)) {
                $body = Remove-YamlComments $step
                $refs = [regex]::Matches($body, 'secrets\.([A-Za-z0-9_]+)')
                if ($refs.Count -eq 0) { continue }
                $title = Get-StepTitle $step
                Assert-True ($secretSteps -contains $title) "$($pair[0]): step '$title' names a secret but is not one of: $($secretSteps -join ', ')"
                Assert-True ($body -match '(?m)^\s+env:') "$($pair[0]): '$title' names a secret outside env:"
                foreach ($r in $refs) {
                    Assert-True ($pair[1] -contains $r.Groups[1].Value) "$($pair[0]): unexpected secret $($r.Groups[1].Value) in '$title'"
                    [void]$seen.Add($r.Groups[1].Value)
                }
                # A secret is only ever the value of an env: entry of its step, never inside a run: script.
                foreach ($line in ($body -split "`n")) {
                    if ($line -match 'secrets\.') { Assert-True ($line -match '^\s+[A-Za-z0-9_]+:\s+\$\{\{\s*secrets\.[A-Za-z0-9_]+\s*\}\}\s*$') "$($pair[0]): '$title' uses a secret other than as an env value: $($line.Trim())" }
                }
            }
            foreach ($n in $pair[1]) { Assert-True ($seen.Contains($n)) "$($pair[0]) no longer reads the repo secret $n" }
        }
    }

    Invoke-Test 'retention-days: every upload-artifact step in every workflow keeps the artifact exactly 1 day' {
        $uploads = 0
        foreach ($wf in Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml') {
            $code = Remove-YamlComments ([System.IO.File]::ReadAllText($wf.FullName))
            foreach ($step in ([regex]::Split($code, '(?m)^(?=\s+- (?:name|uses):)') | Where-Object { $_ -match 'actions/upload-artifact@' })) {
                $uploads++
                $values = @([regex]::Matches($step, '(?m)^\s+retention-days:\s*(\S+)\s*$') | ForEach-Object { $_.Groups[1].Value })
                Assert-True ($values.Count -eq 1 -and $values[0] -ceq '1') "$($wf.Name): an upload-artifact step lacks retention-days: 1 (found: $($values -join ', '))"
            }
        }
        Assert-True ($uploads -ge 2) "only $uploads upload-artifact steps found (release.yml and release-dev.yml each upload the package)"
        foreach ($name in 'release.yml', 'release-dev.yml') { Assert-True ([regex]::Matches((Read-Workflow $name), 'retention-days: 1\s*$', 'Multiline').Count -ge 1) "$name has no retention-days: 1" }
        Assert-True ((Read-Workflow 'build.yml') -notmatch 'retention-days:\s*[2-9]') 'build.yml keeps an artifact longer than 1 day'
    }

    # ================================================================== 2. notes.txt
    Invoke-Test 'notes: ConvertTo-FeedNotesText keeps LF and TAB, removes every other control character, CR, BOM and lone surrogates, and ends with one LF' {
        $nul = [string][char]0; $esc = [string][char]27; $del = [string][char]127; $c1 = [string][char]0x85; $bom = [string][char]0xFEFF; $bell = [string][char]7
        $raw = "Line one`r`nLine`ttwo$nul$esc[31m$bell$del$c1$bom`nLine three  `n`n`n"
        $clean = ConvertTo-FeedNotesText -Text $raw
        Assert-True ($clean -ceq "Line one`nLine`ttwo[31m`nLine three`n") "cleaned text is: $($clean -replace "`n", '\n' -replace "`t", '\t')"
        $lone = [string][char]0xD800 + 'a' + [string][char]0xDC00 + 'b' + [char]::ConvertFromUtf32(0x1F600)
        $clean2 = ConvertTo-FeedNotesText -Text $lone
        Assert-True ($clean2 -ceq ('ab' + [char]::ConvertFromUtf32(0x1F600) + "`n")) 'lone surrogates were kept or a valid pair was lost'
        Assert-True ($clean2.Length -gt 0 -and ('' -eq (Test-FeedNotesBytes -Bytes $utf8.GetBytes($clean2)))) 'the cleaned text is not a valid notes file'
        $every = -join (0..255 | ForEach-Object { [char]$_ })
        $all = ConvertTo-FeedNotesText -Text ('x' + $every)
        Assert-True ($all -cnotmatch '[\u0000-\u0008\u000B-\u001F\u007F-\u009F]') 'a control character survived'
        Assert-True ($all.Contains("`t") -and $all.Contains('A') -and $all.Contains([string][char]0xE9)) 'a tab or a normal character was lost'
        Assert-Throws { ConvertTo-FeedNotesText -Text "  `r`n`t " } 'empty' 'only white space'
        Assert-Throws { ConvertTo-FeedNotesText -Text $null } 'empty' 'null'
    }

    Invoke-Test 'notes: the 64 KB cap is on UTF-8 bytes, cuts at a character boundary and leaves a valid file (ASCII, 3-byte and 4-byte characters)' {
        $limit = 65536
        foreach ($unit in 'a', ([string][char]0x20AC), [char]::ConvertFromUtf32(0x1F600)) {
            $big = $unit * 90000
            $clean = ConvertTo-FeedNotesText -Text $big
            $bytes = $utf8.GetBytes($clean)
            Assert-True ($bytes.Length -le $limit) "cap broken for '$unit': $($bytes.Length) bytes"
            Assert-True ($bytes.Length -gt $limit - 64) "cut far too early for '$unit': $($bytes.Length) bytes"
            Assert-True ('' -eq (Test-FeedNotesBytes -Bytes $bytes)) "the capped text is not valid for '$unit': $(Test-FeedNotesBytes -Bytes $bytes)"
            Assert-True ($clean.Contains('[Notes cut at 64 KB.]')) 'no truncation mark'
        }
        $exact = ConvertTo-FeedNotesText -Text ('a' * 65535)   # 65535 + final LF = exactly the limit: untouched
        Assert-True ($utf8.GetByteCount($exact) -eq 65536 -and -not $exact.Contains('cut')) 'a file of exactly 64 KB was cut'
        $over = ConvertTo-FeedNotesText -Text ('a' * 65536)
        Assert-True ($over.Contains('cut') -and $utf8.GetByteCount($over) -le 65536) 'a file one byte over was not cut'
    }

    Invoke-Test 'notes: zero-width and bidi control characters are removed, so a link split by one is still removed' {
        $zw = [string][char]0x200B; $rlo = [string][char]0x202E
        $raw = "Fixed a bug in https://git${zw}hub.com/Euphoric-Software/clink/pull/1`nSafe${rlo}text`nA$([char]0x2060)B$([char]0x200F)C$([char]0x2066)D$([char]0x2069)E"
        $clean = ConvertTo-FeedNotesText -Text $raw
        Assert-True ($clean -cnotmatch '[\u200B-\u200F\u202A-\u202E\u2060\u2066-\u2069]') 'an invisible character survived'
        Assert-True ($clean.Contains("`nSafetext`n") -and $clean.Contains('ABCDE')) "the text around the invisible characters changed: $clean"
        $out = Remove-FeedNotesRepoLinks -Text $clean -Repository 'Euphoric-Software/clink'
        Assert-True ($out -cnotmatch 'github\.com' -and $out -cnotmatch 'pull/1') "the link survived: $out"
        $bare = ConvertTo-FeedNotesText -Text "git${zw}hub.com/Euphoric-Software/clink/pull/1`nkeep"
        Assert-True ((Remove-FeedNotesRepoLinks -Text $bare -Repository 'Euphoric-Software/clink') -cnotmatch 'github\.com') 'the scheme-less split link survived'
    }

    Invoke-Test 'notes: soft hyphen, U+061C, other invisible characters and Unicode tag characters are removed; Test-FeedNotesBytes refuses them' {
        $shy = [string][char]0x00AD; $alm = [string][char]0x061C
        $tag = [char]::ConvertFromUtf32(0xE0041)   # TAG LATIN CAPITAL LETTER A (a surrogate pair)
        $raw = "see git${shy}hub.com/Euphoric-Software/clink/pull/2`nA${alm}B${tag}C$([char]0x034F)D$([char]0x180E)E$([char]0x2063)F$([char]0x206A)G"
        $clean = ConvertTo-FeedNotesText -Text $raw
        Assert-True ($clean.Contains('ABCDEFG') -and $clean.Contains('github.com/')) "invisible characters survived or text changed: $clean"
        Assert-True ($clean -cnotmatch $script:FeedNotesInvisibleClass -and $clean -cnotmatch '\uDB40') 'an invisible character survived'
        $out = Remove-FeedNotesRepoLinks -Text $clean -Repository 'Euphoric-Software/clink'
        Assert-True ($out -cnotmatch 'github\.com') "the soft-hyphen split link survived: $out"
        $bad = Test-FeedNotesBytes -Bytes $utf8.GetBytes("a$([char]0x200B)b`n")
        Assert-True ($bad -match 'invisible') "U+200B was not refused: '$bad'"
        Assert-True ((Test-FeedNotesBytes -Bytes $utf8.GetBytes("a${tag}b`n")) -match 'invisible') 'a tag character was not refused'
        Assert-True ((Test-FeedNotesBytes -Bytes $utf8.GetBytes("a${alm}b`n")) -match 'invisible') 'U+061C was not refused'
    }

    Invoke-Test 'notes: Test-FeedNotesBytes / Assert-FeedNotesFile refuse empty, oversized, BOM, CR, NUL, invalid UTF-8 and a missing final LF' {
        Assert-True ('' -eq (Test-FeedNotesBytes -Bytes $utf8.GetBytes("ok`n"))) 'a good file was refused'
        $cases = [ordered]@{
            'empty'      = [byte[]]@()
            'oversized'  = $utf8.GetBytes('a' * 65537)
            'BOM'        = [byte[]](@(0xEF, 0xBB, 0xBF) + $utf8.GetBytes("x`n"))
            'CR'         = $utf8.GetBytes("a`r`nb`n")
            'NUL'        = [byte[]](@(97, 0, 98, 10))
            'invalid'    = [byte[]](@(97, 0xFF, 0xFE, 10))
            'no final LF' = $utf8.GetBytes('abc')
        }
        foreach ($k in $cases.Keys) {
            Assert-True ('' -ne (Test-FeedNotesBytes -Bytes ([byte[]]$cases[$k]))) "'$k' was accepted"
            $file = Join-Path $scratch "bad-notes-$($k -replace ' ', '-').txt"
            [System.IO.File]::WriteAllBytes($file, [byte[]]$cases[$k])
            Assert-Throws { Assert-FeedNotesFile -Path $file } 'Invalid notes file' "file '$k'"
        }
        Assert-Throws { Assert-FeedNotesFile -Path (Join-Path $scratch 'no-such-notes.txt') } 'missing' 'missing file'
    }

    Invoke-Test 'notes: Write-FeedNotesFile writes BOM-free UTF-8 that passes the check; Remove-FeedNotesRepoLinks drops the private repository links only' {
        $path = Join-Path $scratch 'written-notes.txt'
        [void](Write-FeedNotesFile -Text "## What's Changed`r`n* Fix the caf$([char]0xE9) by @someone in https://github.com/Euphoric-Software/clink/pull/12`r`n" -Path $path)
        $bytes = [System.IO.File]::ReadAllBytes($path)
        Assert-True (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB)) 'the file starts with a BOM'
        Assert-True (-not ($bytes -contains 13)) 'the file holds a CR'
        Assert-True ($utf8.GetString($bytes).Contains("caf$([char]0xE9)")) 'UTF-8 text was damaged'
        $generated = "## What's Changed`n* Fix a thing by @someone in https://github.com/Euphoric-Software/clink/pull/12`n* Other repo link https://github.com/other/repo/pull/3`n`n**Full Changelog**: https://github.com/Euphoric-Software/clink/compare/v0.4.1...v0.4.2"
        $stripped = Remove-FeedNotesRepoLinks -Text $generated -Repository 'Euphoric-Software/clink'
        Assert-True ($stripped -notmatch 'Euphoric-Software' -and $stripped -notmatch 'Full Changelog') "private links survived: $stripped"
        Assert-True ($stripped.Contains('* Fix a thing') -and $stripped -notmatch '@someone' -and $stripped.Contains('https://github.com/other/repo/pull/3')) "text or an unrelated link was lost (or the mention stayed): $stripped"
        Assert-Throws { Remove-FeedNotesRepoLinks -Text 'x' -Repository 'bad repo' } 'not owner/name' 'repository name'
    }

    Invoke-Test 'notes: the dev notes are the fixed dev release body (Get-FeedDevNotesText = Get-ExpectedDevReleaseBody) and the workflows write notes.txt in the right place' {
        $sha = 'b' * 40
        Assert-True ((Get-FeedDevNotesText -Commit $sha) -ceq (Get-ExpectedDevReleaseBody -Commit $sha)) 'the two dev bodies differ'
        $dev = Read-Workflow 'release-dev.yml'
        Assert-True ($dev.Contains('body: Dev build of ${{ github.sha }} from the dev branch.') -and $dev.Contains((Get-FeedDevNotesText -Commit '${{ github.sha }}'))) 'the dev draft body is not the dev notes text'
        $stable = Read-Workflow 'release.yml'
        foreach ($pair in @(@('release.yml', $stable), @('release-dev.yml', $dev))) {
            $pub = (Split-Jobs $pair[1]).Publish
            $notes = $pub.IndexOf('- name: Write the release notes (notes.txt)')
            Assert-True ($notes -gt 0) "$($pair[0]) has no notes step"
            Assert-True ($notes -gt $pub.IndexOf('- name: Download package artifact') -and $notes -lt $pub.IndexOf('- name: Make and sign the manifest') -and $notes -lt $pub.IndexOf('- name: Create GitHub release (draft)')) "$($pair[0]): the notes step is misplaced"
            Assert-True ($pub.Contains('dist/package/notes.txt')) "$($pair[0]) does not write dist/package/notes.txt"
            Assert-True ((Remove-YamlComments $pair[1]) -notmatch 'NotesUrl') "$($pair[0]) still passes -NotesUrl (make-manifest.ps1 derives it)"
            Assert-True ((Remove-YamlComments $pair[1]) -notmatch 'github\.com/\$env:GITHUB_REPOSITORY|releases/tag/') "$($pair[0]) still points notes at GitHub"
            # notes.txt is not a GitHub release asset: the asset list is unchanged.
            $assets = @(Get-Steps $pub | Where-Object { (Get-StepTitle $_) -eq 'List release assets' })[0]
            Assert-True ($assets -notmatch 'notes\.txt') "$($pair[0]): notes.txt was added to the GitHub release assets"
        }
        # Stable: the release body is the same file (not GitHub's separately generated text).
        Assert-True ($stable.Contains('body_path: dist/package/notes.txt') -and $stable.Contains('generate_release_notes: false') -and $stable.Contains('releases/generate-notes')) 'the stable release body is not the notes file'
        # The stable step needs the token and the repository name only; it creates nothing (a POST to generate-notes only returns text).
        $step = @(Get-Steps (Split-Jobs $stable).Publish | Where-Object { (Get-StepTitle $_) -like 'Write the release notes*' })[0]
        Assert-True ($step.Contains('GH_TOKEN: ${{ github.token }}') -and $step -notmatch 'secrets\.') 'the stable notes step has the wrong environment'
        Assert-True ($step.Contains('Remove-FeedNotesRepoLinks') -and $step.Contains('Write-FeedNotesFile')) 'the stable notes step does not clean and write the text through the library'
    }

    # ---- make-manifest: notesUrl ---------------------------------------------------------------------------------------------------------
    $keyPems = @()
    foreach ($i in 0..1) {
        $cp = New-Object System.Security.Cryptography.CngKeyCreationParameters
        $cp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
        $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $cp)
        try {
            $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
            $blob = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        } finally { $cng.Dispose() }
        $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
        $keyPems += [pscustomobject]@{
            Private = "-----BEGIN PRIVATE KEY-----`n" + [Convert]::ToBase64String($pkcs8) + "`n-----END PRIVATE KEY-----`n"
            Public  = [Convert]::ToBase64String([byte[]]($header + $blob[8..71]))
        }
    }
    $stableBase = 'https://dl.example/clink/'
    $devBase = 'https://dl-dev.example/clink/'
    $feedJson = Join-Path $scratch 'feed.json'
    [System.IO.File]::WriteAllText($feedJson, ('{"schema":1,"feedUrl":"' + $stableBase + '","devFeedUrl":"' + $devBase + '","stableKeys":["' + $keyPems[0].Public + '"],"devKeys":["' + $keyPems[1].Public + '"]}'), $utf8)
    $script:random = New-Object System.Random
    function New-RandomBytes { param([int]$Length) $b = New-Object byte[] $Length; $script:random.NextBytes($b); , $b }
    function New-NotesPackage {
        # A package folder make-manifest accepts, with notes.txt (unless -NoNotes; -NotesBytes replaces its content).
        param([string]$Name, [string]$Version = '0.4.2', [switch]$NoNotes, [byte[]]$NotesBytes)
        $dir = Get-ScratchFolder "pkg-$Name"
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink.exe'), (New-RandomBytes 5000))
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink-framework.exe'), (New-RandomBytes 2000))
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'Clink-Setup.exe'), (New-RandomBytes 3000))
        if (-not $NoNotes) { [System.IO.File]::WriteAllBytes((Join-Path $dir 'notes.txt'), $(if ($NotesBytes) { $NotesBytes } else { $utf8.GetBytes("Notes of $Name`n") })) }
        $files = [ordered]@{}
        foreach ($pair in @(@('exe', 'Clink.exe'), @('exeFramework', 'Clink-framework.exe'), @('installer', 'Clink-Setup.exe'))) {
            $path = Join-Path $dir $pair[1]
            $files[$pair[0]] = [ordered]@{ file = $pair[1]; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $pkg = [pscustomobject]@{ version = $Version; minRuntime = '10.0.14'; files = [pscustomobject]$files }
        [System.IO.File]::WriteAllText((Join-Path $dir 'package.json'), ($pkg | ConvertTo-Json -Depth 6), $utf8)
        $dir
    }
    function Invoke-Manifest {
        param([string]$Dir, [string]$Channel, [string]$Version, [string]$BaseUrl, [int]$KeyIndex)
        Invoke-Child (Join-Path $toolsDir 'make-manifest.ps1') @('-Channel', $Channel, '-Version', $Version, '-Tag', "v$Version", '-BaseUrl', $BaseUrl, '-PackageDir', $Dir, '-FeedJson', $feedJson) @{ CLINK_FEED_SIGNING_KEY = $keyPems[$KeyIndex].Private }
    }

    Invoke-Test 'notesUrl: make-manifest sets it to <base>releases/<tag>/notes.txt (stable) and <dev base>dev/<tag>/notes.txt (dev), signed, and takes no -NotesUrl' {
        $s = New-NotesPackage 'notes-stable'
        $r = Invoke-Manifest $s 'stable' '0.4.2' ($stableBase + 'releases/v0.4.2/') 0
        Assert-True ($r.Code -eq 0) "stable refused: $($r.Text)"
        $m = [System.IO.File]::ReadAllText((Join-Path $s 'stable.json')) | ConvertFrom-Json
        Assert-True ($m.notesUrl -ceq ($stableBase + 'releases/v0.4.2/notes.txt')) "stable notesUrl is $($m.notesUrl)"
        Assert-True (Test-FeedAssetUrlUnder -Url $m.notesUrl -Base $stableBase) 'the stable notesUrl is not under the stable base'
        Assert-True (Test-FeedSignature -Bytes ([System.IO.File]::ReadAllBytes((Join-Path $s 'stable.json'))) -SigBase64 ([System.IO.File]::ReadAllText((Join-Path $s 'stable.json.sig')).Trim()) -Keys @($keyPems[0].Public)) 'the stable signature does not verify'
        $d = New-NotesPackage 'notes-dev' '0.4.2-dev.7'
        $r = Invoke-Manifest $d 'dev' '0.4.2-dev.7' ($devBase + 'dev/v0.4.2-dev.7/') 1
        Assert-True ($r.Code -eq 0) "dev refused: $($r.Text)"
        $m = [System.IO.File]::ReadAllText((Join-Path $d 'dev.json')) | ConvertFrom-Json
        Assert-True ($m.notesUrl -ceq ($devBase + 'dev/v0.4.2-dev.7/notes.txt')) "dev notesUrl is $($m.notesUrl)"
        Assert-True (Test-FeedAssetUrlUnder -Url $m.notesUrl -Base $devBase) 'the dev notesUrl is not under the dev base'
        Assert-True (-not (Test-FeedAssetUrlUnder -Url $m.notesUrl -Base $stableBase)) 'the dev notesUrl is under the stable base'
        # The parameter is gone: passing it is an error and writes nothing.
        $x = New-NotesPackage 'notes-param'
        $r = Invoke-Child (Join-Path $toolsDir 'make-manifest.ps1') @('-Channel', 'stable', '-Version', '0.4.2', '-Tag', 'v0.4.2', '-BaseUrl', ($stableBase + 'releases/v0.4.2/'), '-NotesUrl', 'https://example.org/notes', '-PackageDir', $x, '-FeedJson', $feedJson) @{ CLINK_FEED_SIGNING_KEY = $keyPems[0].Private }
        Assert-True ($r.Code -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $x 'stable.json'))) 'a caller-chosen -NotesUrl was accepted'
        $mm = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'make-manifest.ps1'))
        Assert-True ($mm.Contains("`$NotesUrl = `$BaseUrl + 'notes.txt'") -and $mm.Contains('Test-FeedAssetUrlUnder -Url $manifest.notesUrl -Base $cfg.FeedUrl')) 'make-manifest.ps1 does not derive and validate notesUrl'
    }

    Invoke-Test 'notesUrl: make-manifest refuses a package without notes.txt or with an invalid one, and a base outside the channel (notes.txt is checked like the binaries)' {
        foreach ($case in @(
                @{ Name = 'missing'; Pkg = (New-NotesPackage 'nn-missing' -NoNotes); Pattern = 'notes file is missing' },
                @{ Name = 'bom'; Pkg = (New-NotesPackage 'nn-bom' -NotesBytes ([byte[]](@(0xEF, 0xBB, 0xBF) + $utf8.GetBytes("x`n")))); Pattern = 'BOM' },
                @{ Name = 'cr'; Pkg = (New-NotesPackage 'nn-cr' -NotesBytes $utf8.GetBytes("a`r`n")); Pattern = 'control character' },
                @{ Name = 'big'; Pkg = (New-NotesPackage 'nn-big' -NotesBytes ($utf8.GetBytes('a' * 70000))); Pattern = 'byte limit' })) {
            $r = Invoke-Manifest $case.Pkg 'stable' '0.4.2' ($stableBase + 'releases/v0.4.2/') 0
            Assert-True ($r.Code -ne 0 -and $r.Text -match $case.Pattern) "$($case.Name): not refused as expected: $($r.Text)"
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $case.Pkg 'stable.json'))) "$($case.Name): a manifest was written"
        }
        $w = New-NotesPackage 'nn-wrongbase'
        $r = Invoke-Manifest $w 'stable' '0.4.2' ($devBase + 'releases/v0.4.2/') 0
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'is not under the stable feed base') "a base outside the channel was accepted: $($r.Text)"
    }

    # ---- notes.txt upload: write-once and the public byte-equality check, with the fake bucket of feed.tests.ps1 ----------------------------
    $script:store = @{}
    $script:calls = New-Object System.Collections.ArrayList
    $fakeAws = {
        param([string[]]$Arguments)
        [void]$script:calls.Add($Arguments -join ' ')
        $keyAt = [array]::IndexOf($Arguments, '--key')
        $key = if ($keyAt -ge 0) { $Arguments[$keyAt + 1] } else { '' }
        $op = "$($Arguments[0]) $($Arguments[1])"
        if ($op -eq 's3api get-object') {
            if ($script:store.ContainsKey($key)) { [System.IO.File]::WriteAllBytes($Arguments[$Arguments.Count - 1], [byte[]]$script:store[$key]); return @{ Code = 0; Text = '{}' } }
            return @{ Code = 254; Text = 'An error occurred (NoSuchKey) when calling the GetObject operation: The specified key does not exist.' }
        }
        if ($op -eq 's3api put-object') {
            if ($script:store.ContainsKey($key)) { return @{ Code = 254; Text = 'An error occurred (PreconditionFailed) (412)' } }
            $bodyAt = [array]::IndexOf($Arguments, '--body')
            $script:store[$key] = [System.IO.File]::ReadAllBytes($Arguments[$bodyAt + 1])
            return @{ Code = 0; Text = '' }
        }
        @{ Code = 255; Text = "fake aws: unsupported call $($Arguments -join ' ')" }
    }
    function New-NotesAsset {
        param([string]$Text, [string]$Tag = 'v0.4.2', [string]$Folder = 'releases')
        $dir = Get-ScratchFolder ('notes-asset-' + [guid]::NewGuid().ToString('N'))
        $path = Join-Path $dir 'notes.txt'
        [void](Write-FeedNotesFile -Text $Text -Path $path)
        $bytes = [System.IO.File]::ReadAllBytes($path)
        [pscustomobject]@{ Name = 'notes.txt'; Local = $path; Sha256 = (Get-Sha256Hex $bytes); Size = [int64]$bytes.Length; Key = "clink/$Folder/$Tag/notes.txt"
            Url = "https://dl.example/clink/$Folder/$Tag/notes.txt"; Type = 'text/plain; charset=utf-8'; Bytes = $bytes }
    }

    Invoke-Test 'notes upload: notes.txt is a write-once versioned asset (conditional create, same bytes skipped, other bytes refused) at <prefix>/releases|dev/<tag>/notes.txt' {
        foreach ($case in @(@('releases', 'v0.4.2'), @('dev', 'v0.4.2-dev.7'))) {
            $script:store = @{}; $script:calls.Clear()
            $asset = New-NotesAsset -Text "First notes`nline two" -Tag $case[1] -Folder $case[0]
            $dl = Get-ScratchFolder 'dl'
            $r1 = Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $asset -CacheControl 'public, max-age=31536000, immutable' -ScratchDir $dl
            Assert-True ($r1 -eq 'uploaded') "first publish: $r1"
            Assert-True ($script:store.ContainsKey("clink/$($case[0])/$($case[1])/notes.txt")) 'the object is not at <prefix>/<folder>/<tag>/notes.txt'
            $put = @($script:calls | Where-Object { $_ -like 's3api put-object*' })[0]
            Assert-True ($put.Contains('--if-none-match *') -and $put.Contains('--content-type text/plain; charset=utf-8') -and $put.Contains('immutable')) "put-object call is wrong: $put"
            $r2 = Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $asset -CacheControl 'public, immutable' -ScratchDir $dl
            Assert-True ($r2 -eq 'skipped') "same bytes: $r2"
            $changed = New-NotesAsset -Text 'Different notes' -Tag $case[1] -Folder $case[0]
            Assert-Throws { Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $changed -CacheControl 'x' -ScratchDir $dl } 'already exists with different content' 'changed notes'
            Assert-True (@($script:calls | Where-Object { $_ -like 's3api put-object*' }).Count -eq 1) 'a second put-object was attempted'
        }
    }

    Invoke-Test 'notes upload: the PUBLIC url must serve the same bytes (status 200, hash and size), like the binaries' {
        $asset = New-NotesAsset -Text 'Public notes'
        $dl = Get-ScratchFolder 'pub'
        $goodHeaders = @{ 'content-type' = 'text/plain; charset=utf-8'; 'x-content-type-options' = 'nosniff' }
        $serve = { param($Bytes, $Status, $Headers = $goodHeaders) { param([string]$Url, [string]$OutFile) [System.IO.File]::WriteAllBytes($OutFile, [byte[]]$Bytes); @{ Status = $Status; Headers = $Headers } }.GetNewClosure() }
        Assert-FeedPublicAsset -Http (& $serve $asset.Bytes 200) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl
        Assert-Throws { Assert-FeedPublicAsset -Http (& $serve $utf8.GetBytes("Public notes!`n") 200) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'served bytes differ' 'altered bytes'
        Assert-Throws { Assert-FeedPublicAsset -Http (& $serve $asset.Bytes 404) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl } 'status 404' 'a 404'
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        Assert-True ($text.Contains("[pscustomobject]@{ Name = 'notes.txt'; Type = 'text/plain; charset=utf-8' }")) 'upload-feed.ps1 does not list notes.txt as a versioned asset'
        Assert-True ($text.Contains("Assert-FeedNotesFile -Path (Join-Path `$PackageDir 'notes.txt')") -and $text.Contains('"$feed$folder/notes.txt"')) 'upload-feed.ps1 does not validate the notes file or compare the manifest notesUrl with the public URL'
        Assert-True ($text.IndexOf("Name = 'notes.txt'") -lt $text.IndexOf('Publish-FeedVersionedAsset') -and $text.Contains('foreach ($a in $assets) { Assert-FeedPublicAsset -Http $httpInvoker -Asset $a }')) 'notes.txt is not in the list that is uploaded and then checked at its public URL'
        Assert-True ($text -notmatch '(?m)^\s*\[string\]\$NotesUrl' -and -not $text.Contains('-NotesUrl')) 'upload-feed.ps1 still has a -NotesUrl'
    }

    # ================================================================== 3. check-published expectations
    Invoke-Test 'check-published: Get-FeedSelfTestEnvironment sets all three variables when on, only CLINK_EXPECT_FEED when off, none when omitted' {
        $none = Get-FeedSelfTestEnvironment
        Assert-True ($none.Keys.Count -eq 3 -and $null -eq $none.CLINK_EXPECT_FEED -and $null -eq $none.CLINK_EXPECT_FEED_URL -and $null -eq $none.CLINK_EXPECT_DEV_FEED_URL) 'omitted: something is set'
        $off = Get-FeedSelfTestEnvironment -ExpectFeed 'off'
        Assert-True ($off.CLINK_EXPECT_FEED -ceq 'off' -and $null -eq $off.CLINK_EXPECT_FEED_URL -and $null -eq $off.CLINK_EXPECT_DEV_FEED_URL) 'off: the URLs are not unset'
        $offEmpty = Get-FeedSelfTestEnvironment -ExpectFeed 'off' -ExpectFeedUrl '' -ExpectDevFeedUrl ''
        Assert-True ($offEmpty.CLINK_EXPECT_FEED -ceq 'off' -and $null -eq $offEmpty.CLINK_EXPECT_FEED_URL) 'off with empty URLs (what the workflows read from an off feed.json) was refused'
        $on = Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase -ExpectDevFeedUrl $devBase
        Assert-True ($on.CLINK_EXPECT_FEED -ceq 'on' -and $on.CLINK_EXPECT_FEED_URL -ceq $stableBase -and $on.CLINK_EXPECT_DEV_FEED_URL -ceq $devBase) 'on: wrong values'
        # A dev base that is the stable base (no devFeedUrl) is valid: the dev channel then uses feedUrl.
        $same = Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase -ExpectDevFeedUrl $stableBase
        Assert-True ($same.CLINK_EXPECT_DEV_FEED_URL -ceq $stableBase) 'the shared base was refused'
    }

    Invoke-Test 'check-published: a wrong combination of -ExpectFeed / -ExpectFeedUrl / -ExpectDevFeedUrl is refused (both bases required when on; none when off or omitted)' {
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' } 'ExpectFeedUrl is required' 'on without any URL'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase } 'ExpectDevFeedUrl is required' 'on without the dev URL'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectDevFeedUrl $devBase } 'ExpectFeedUrl is required' 'on without the stable URL'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase -ExpectDevFeedUrl '   ' } 'ExpectDevFeedUrl is required' 'on with a blank dev URL'
        foreach ($bad in 'http://dl.example/clink/', 'https://dl.example/clink', 'https://DL.example/clink/', 'not a url', 'https://dl.example/clink/?x=1') {
            Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $bad -ExpectDevFeedUrl $devBase } 'not a canonical https feed base' "stable URL '$bad'"
            Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase -ExpectDevFeedUrl $bad } 'not a canonical https feed base' "dev URL '$bad'"
        }
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'off' -ExpectFeedUrl $stableBase } 'cannot come with a feed URL' 'off with a stable URL'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'off' -ExpectDevFeedUrl $devBase } 'cannot come with a feed URL' 'off with a dev URL'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeedUrl $stableBase } 'need -ExpectFeed on' 'a URL without -ExpectFeed'
        Assert-Throws { Get-FeedSelfTestEnvironment -ExpectFeed 'maybe' } "must be 'on' or 'off'" 'a bad mode'
        # The real script refuses the same combinations before it runs anything (size guards are skipped with -SkipRun, but this check comes first).
        $dir = Get-ScratchFolder 'cp-sizes'
        $sc = Join-Path $dir 'sc.exe'; $fx = Join-Path $dir 'fx.exe'
        foreach ($pair in @(@($sc, 76MB), @($fx, 32MB))) { $f = [System.IO.File]::Create($pair[0]); try { $f.SetLength([int64]$pair[1]) } finally { $f.Dispose() } }
        $script = Join-Path $toolsDir 'check-published.ps1'
        $r = Invoke-Child $script @('-ScExe', $sc, '-FxExe', $fx, '-SkipRun', '-ExpectFeed', 'on', '-ExpectFeedUrl', $stableBase)
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'ExpectDevFeedUrl is required') "check-published accepted 'on' with one base: $($r.Text)"
        $r = Invoke-Child $script @('-ScExe', $sc, '-FxExe', $fx, '-SkipRun', '-ExpectFeed', 'off', '-ExpectFeedUrl', $stableBase, '-ExpectDevFeedUrl', $devBase)
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'cannot come with a feed URL') "check-published accepted 'off' with bases: $($r.Text)"
        $r = Invoke-Child $script @('-ScExe', $sc, '-FxExe', $fx, '-SkipRun', '-ExpectFeed', 'on', '-ExpectFeedUrl', $stableBase, '-ExpectDevFeedUrl', $devBase)
        Assert-True ($r.Code -eq 0 -and $r.Text -match 'size guards ok') "check-published refused a good 'on' call: $($r.Text)"
        $r = Invoke-Child $script @('-ScExe', $sc, '-FxExe', $fx, '-SkipRun', '-ExpectFeed', 'off')
        Assert-True ($r.Code -eq 0) "check-published refused a good 'off' call: $($r.Text)"
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Invoke-Test 'check-published: the child process of the feed self-test sees the variables when on and none when off (a stale parent value never leaks in)' {
        $checkScript = Join-Path $toolsDir 'check-published.ps1'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($checkScript, [ref]$tokens, [ref]$errors)
        $defs = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and ($n.Name -eq 'Invoke-PublishedExe' -or $n.Name -eq 'Stop-ProcessTree') }, $true)
        Assert-True ($defs.Count -eq 2) 'Invoke-PublishedExe / Stop-ProcessTree not found in check-published.ps1'
        . ([scriptblock]::Create((($defs | ForEach-Object { $_.Extent.Text }) -join "`n")))
        $probe = '"Write-Output (''F='' + $env:CLINK_EXPECT_FEED); Write-Output (''S='' + $env:CLINK_EXPECT_FEED_URL); Write-Output (''D='' + $env:CLINK_EXPECT_DEV_FEED_URL)"'
        $names = 'CLINK_EXPECT_FEED', 'CLINK_EXPECT_FEED_URL', 'CLINK_EXPECT_DEV_FEED_URL'
        $before = @{}
        foreach ($n in $names) { $before[$n] = [Environment]::GetEnvironmentVariable($n); [Environment]::SetEnvironmentVariable($n, 'stale-from-the-parent') }
        try {
            $extra = Get-FeedSelfTestEnvironment -ExpectFeed 'on' -ExpectFeedUrl $stableBase -ExpectDevFeedUrl $devBase
            $on = Invoke-PublishedExe $hostExe @('-NoProfile', '-Command', $probe) 60 $extra
            Assert-True ($on.Out -match "(?m)^F=on\s*$" -and $on.Out -match "(?m)^S=$([regex]::Escape($stableBase))\s*$" -and $on.Out -match "(?m)^D=$([regex]::Escape($devBase))\s*$") "on: child sees: $($on.Out)"
            $off = Invoke-PublishedExe $hostExe @('-NoProfile', '-Command', $probe) 60 (Get-FeedSelfTestEnvironment -ExpectFeed 'off')
            Assert-True ($off.Out -match "(?m)^F=off\s*$" -and $off.Out -match "(?m)^S=\s*$" -and $off.Out -match "(?m)^D=\s*$") "off: child sees: $($off.Out)"
            $omitted = Invoke-PublishedExe $hostExe @('-NoProfile', '-Command', $probe) 60 (Get-FeedSelfTestEnvironment)
            Assert-True ($omitted.Out -match "(?m)^F=\s*$" -and $omitted.Out -match "(?m)^S=\s*$" -and $omitted.Out -match "(?m)^D=\s*$") "omitted: child sees: $($omitted.Out)"
            Assert-True ([Environment]::GetEnvironmentVariable('CLINK_EXPECT_FEED_URL') -ceq 'stale-from-the-parent') 'the parent value was not restored'
        } finally { foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $before[$n]) } }
        $text = [System.IO.File]::ReadAllText($checkScript)
        Assert-True ($text.Contains('Get-FeedSelfTestEnvironment -ExpectFeed $ExpectFeed -ExpectFeedUrl $ExpectFeedUrl -ExpectDevFeedUrl $ExpectDevFeedUrl')) 'check-published.ps1 does not build the self-test environment from its parameters'
        Assert-True ($text.Contains('$feedEnv[$name] = $feedSelfTestEnv[$name]')) 'the feed self-test run does not take the expectation environment'
        Assert-True ($text.Contains("@{ CLINK_TESTS_NO_SKIP = '1'; CLINK_EXPECT_FEED = `$null; CLINK_EXPECT_FEED_URL = `$null; CLINK_EXPECT_DEV_FEED_URL = `$null }")) 'the notices run does not unset all three variables'
    }

    Invoke-Test 'check-published callers: build.yml, release.yml and release-dev.yml pass both bases when the feed is on and none when it is off; release-dev unit tests keep setting them' {
        foreach ($name in 'build.yml', 'release.yml', 'release-dev.yml') {
            $text = Read-Workflow $name
            $calls = [regex]::Matches($text, '(?m)^\s+\./tools/check-published\.ps1[^\n]*')
            Assert-True ($calls.Count -eq 2) "$name : expected two check-published calls (on and off), found $($calls.Count)"
            $on = @($calls | Where-Object { $_.Value -match '-ExpectFeedUrl' })
            $off = @($calls | Where-Object { $_.Value -notmatch '-ExpectFeedUrl' })
            Assert-True ($on.Count -eq 1 -and $off.Count -eq 1) "$name : one call must pass the bases and one must not"
            Assert-True ($on[0].Value -match '-ExpectFeed \$cfg\.Mode -ExpectFeedUrl \S+ -ExpectDevFeedUrl \S+ -TimeoutSeconds 90') "$name : the on call is malformed: $($on[0].Value)"
            Assert-True ($off[0].Value -notmatch 'ExpectDevFeedUrl') "$name : the off call passes a base"
            Assert-True ($text -match "if \(\`$cfg\.Mode -eq 'on'\)") "$name : the calls are not selected by the feed mode"
            Assert-True ($text.Contains('-Channel stable') -and $text.Contains('-Channel dev')) "$name : does not read both channels from feed.json"
        }
        $rel = Read-Workflow 'release.yml'
        Assert-True ($rel.Contains('-ExpectFeedUrl $cfg.FeedUrl -ExpectDevFeedUrl $devCfg.FeedUrl')) 'release.yml: stable cfg gives the stable base and the dev cfg the dev base'
        $dev = Read-Workflow 'release-dev.yml'
        Assert-True ($dev.Contains('-ExpectFeedUrl $stableCfg.FeedUrl -ExpectDevFeedUrl $cfg.FeedUrl')) 'release-dev.yml: stable cfg gives the stable base and the dev cfg the dev base'
        Assert-True ($dev.Contains('$env:CLINK_EXPECT_FEED_URL = $stableCfg.FeedUrl') -and $dev.Contains('$env:CLINK_EXPECT_DEV_FEED_URL = $devCfg.FeedUrl')) 'release-dev.yml: the unit-test step no longer sets both bases'
    }

    # ================================================================== 3b. repair round 1 (feed-off refusal, floor seeds, headers, notes, artifact, endpoint)
    function Get-StepRunText {
        # The run: script of a step (the lines indented 10 spaces under "        run: |").
        param([string]$Step)
        $at = $Step.IndexOf("        run: |`n")
        Assert-True ($at -ge 0) "step '$(Get-StepTitle $Step)' has no run: block"
        $body = New-Object System.Collections.Generic.List[string]
        foreach ($l in ($Step.Substring($at + "        run: |`n".Length) -split "`n")) {
            if ($l -match '^          ') { $body.Add($l.Substring(10)) } elseif ($l.Trim() -eq '') { $body.Add('') } else { break }
        }
        $body -join "`n"
    }
    function Get-PublishStep { param([string]$Workflow, [string]$Title) @(Get-Steps (Split-Jobs (Read-Workflow $Workflow)).Publish | Where-Object { (Get-StepTitle $_) -eq $Title })[0] }
    function Get-BuildStep { param([string]$Workflow, [string]$Title) @(Get-Steps (Split-Jobs (Read-Workflow $Workflow)).Build | Where-Object { (Get-StepTitle $_) -eq $Title })[0] }

    Invoke-Test 'H1: both publish jobs REFUSE to publish while the feed is off (before the draft and before any secret step); the build job has no such gate' {
        foreach ($pair in @(@('release.yml', 'stable'), @('release-dev.yml', 'dev'))) {
            $name = $pair[0]
            $jobs = Split-Jobs (Read-Workflow $name)
            $step = Get-PublishStep $name 'Read feed configuration'
            $run = Get-StepRunText $step
            Assert-True ($run -match 'if \(-not \$cfg\.Enabled\) \{ throw ') "$name : the step does not throw when the feed is not enabled"
            Assert-True ($run -notmatch '::warning::' -and $run -notmatch 'without manifest assets') "$name : the step still warns and publishes feed-less"
            Assert-True ($run.IndexOf('throw') -lt $run.IndexOf('Add-Content')) "$name : outputs are written before the refusal"
            Assert-True ($jobs.Publish.IndexOf('- name: Read feed configuration') -lt $jobs.Publish.IndexOf('secrets.') -and $jobs.Publish.IndexOf('- name: Read feed configuration') -lt $jobs.Publish.IndexOf('- name: Create GitHub release (draft)')) "$name : the refusal is not before the first secret and the draft"
            # The build job has no feed gate: it builds and tests whatever feed.json says.
            Assert-True ($jobs.Build -notmatch 'Read feed configuration' -and $jobs.Build -notmatch 'refusing to publish' -and $jobs.Build -notmatch '\.Enabled') "$name : the build job gates on the feed"
            # "A feed that was on stays on" stays as defence in depth.
            Assert-True ($jobs.Publish.Contains('- name: Guard - a feed that was on stays on') -and $jobs.Publish.Contains('Assert-FeedNeverWasOn')) "$name : the defence-in-depth guard is gone"
            # Behaviour against a scratch feed.json: off -> throws, writes nothing; on -> passes.
            $work = Get-ScratchFolder "h1-$($pair[1])"
            New-Item -ItemType Directory -Force -Path (Join-Path $work 'Clink\Assets') | Out-Null
            $feedFile = Join-Path $work 'Clink\Assets\feed.json'
            $out = Join-Path $work 'github-output.txt'
            $block = $run.Replace('. ./tools/lib/feed-publish-lib.ps1', ". '$(Join-Path $toolsDir 'lib\feed-publish-lib.ps1')'").Replace('. ./tools/lib/release-mode-lib.ps1', ". '$(Join-Path $toolsDir 'lib\release-mode-lib.ps1')'")
            $savedOut = $env:GITHUB_OUTPUT; $savedPrefix = $env:CLINK_FEED_KEY_PREFIX; $savedImm = $env:CLINK_IMMUTABLE_RELEASES
            Push-Location $work
            try {
                $env:GITHUB_OUTPUT = $out; $env:CLINK_FEED_KEY_PREFIX = ''; [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $null)
                [System.IO.File]::WriteAllText($feedFile, '{"schema":1,"feedUrl":"","stableKeys":[],"devKeys":[]}', $utf8)
                $err = $null
                try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $err = $_.Exception.Message }
                Assert-True ($null -ne $err -and $err -match 'refusing to publish' -and $err -match 'feed-on commit') "$name : feed off did not throw the refusal: $err"
                Assert-True (-not (Test-Path -LiteralPath $out)) "$name : outputs were written although the feed is off"
                [System.IO.File]::WriteAllText($feedFile, ('{"schema":1,"feedUrl":"https://dl.example/clink/","devFeedUrl":"https://dl-dev.example/clink/","stableKeys":["' + $keyPems[0].Public + '"],"devKeys":["' + $keyPems[1].Public + '"]}'), $utf8)
                $err = $null
                try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $err = $_.Exception.Message }
                Assert-True ($null -eq $err) "$name : a feed that is on was refused: $err"
                Assert-True ([System.IO.File]::ReadAllText($out) -match 'enabled=true') "$name : enabled=true was not written"
            } finally { Pop-Location; $env:GITHUB_OUTPUT = $savedOut; [Environment]::SetEnvironmentVariable('CLINK_FEED_KEY_PREFIX', $savedPrefix); [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $savedImm) }
        }
    }

    Invoke-Test 'L-2: "Read feed configuration" validates CLINK_IMMUTABLE_RELEASES (through env) before the draft exists; a bad value fails with no output written, the later checks keep validating it' {
        foreach ($pair in @(@('release.yml', 'stable'), @('release-dev.yml', 'dev'))) {
            $name = $pair[0]
            $jobs = Split-Jobs (Read-Workflow $name)
            $step = Get-PublishStep $name 'Read feed configuration'
            Assert-True ($step.Contains('CLINK_IMMUTABLE_RELEASES: ${{ vars.CLINK_IMMUTABLE_RELEASES }}')) "$name : the variable does not reach the step through env"
            $run = Get-StepRunText $step
            Assert-True ($run.Contains('. ./tools/lib/release-mode-lib.ps1') -and $run.Contains('[void](Get-ImmutableReleasesMode -Value $env:CLINK_IMMUTABLE_RELEASES)')) "$name : the step does not call Get-ImmutableReleasesMode"
            Assert-True (-not $run.Contains('release-verify-lib') -and $step -notmatch 'GH_TOKEN|github\.token') "$name : the step loads the gh library or gets the token (it needs neither)"
            Assert-True ($run.IndexOf('Get-ImmutableReleasesMode') -lt $run.IndexOf('Add-Content')) "$name : outputs are written before the variable is validated"
            Assert-True ($jobs.Publish.IndexOf('- name: Read feed configuration') -lt $jobs.Publish.IndexOf('- name: Create GitHub release (draft)')) "$name : the step is not before the draft"
            # The later uses stay: the three release checks pass the variable too.
            Assert-True ([regex]::Matches($jobs.Publish, '-ImmutableMode \$env:CLINK_IMMUTABLE_RELEASES').Count -eq 3) "$name : the three release checks no longer pass the variable"
            $work = Get-ScratchFolder "l2-$($pair[1])"
            New-Item -ItemType Directory -Force -Path (Join-Path $work 'Clink\Assets') | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $work 'Clink\Assets\feed.json'), ('{"schema":1,"feedUrl":"https://dl.example/clink/","devFeedUrl":"https://dl-dev.example/clink/","stableKeys":["' + $keyPems[0].Public + '"],"devKeys":["' + $keyPems[1].Public + '"]}'), $utf8)
            $out = Join-Path $work 'github-output.txt'
            $block = $run.Replace('. ./tools/lib/feed-publish-lib.ps1', ". '$(Join-Path $toolsDir 'lib\feed-publish-lib.ps1')'").Replace('. ./tools/lib/release-mode-lib.ps1', ". '$(Join-Path $toolsDir 'lib\release-mode-lib.ps1')'")
            $savedOut = $env:GITHUB_OUTPUT; $savedPrefix = $env:CLINK_FEED_KEY_PREFIX; $savedImm = $env:CLINK_IMMUTABLE_RELEASES
            Push-Location $work
            try {
                $env:GITHUB_OUTPUT = $out; $env:CLINK_FEED_KEY_PREFIX = ''
                foreach ($bad in 'Required', 'unavailable ', 'yes', 'UNAVAILABLE', 'off') {
                    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
                    [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $bad)
                    $err = $null
                    try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $err = $_.Exception.Message }
                    Assert-True ($null -ne $err -and $err -match 'CLINK_IMMUTABLE_RELEASES must be') "$name : '$bad' was accepted: $err"
                    Assert-True (-not (Test-Path -LiteralPath $out)) "$name : outputs were written for '$bad'"
                }
                foreach ($good in '', 'required', 'unavailable') {
                    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
                    [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $(if ($good) { $good } else { $null }))
                    $err = $null
                    try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $err = $_.Exception.Message }
                    Assert-True ($null -eq $err -and ([System.IO.File]::ReadAllText($out) -match 'enabled=true')) "$name : '$good' was refused: $err"
                }
            } finally { Pop-Location; $env:GITHUB_OUTPUT = $savedOut; [Environment]::SetEnvironmentVariable('CLINK_FEED_KEY_PREFIX', $savedPrefix); [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $savedImm) }
        }
    }

    Invoke-Test 'M1: floor seeds - Assert-FeedFloorSeed accepts only X.Y.Z / X.Y.Z-dev.N (no ;, newline, quote, space), Get-FeedFloorSeeds reads the newest published stable and dev' {
        foreach ($ok in '0.4.2', '10.20.30', '0.4.2-dev.7') { Assert-True ((Assert-FeedFloorSeed -Value $ok -Channel dev) -ceq $ok) "$ok was refused for dev" }
        Assert-True ((Assert-FeedFloorSeed -Value '' -Channel stable) -ceq '' -and (Assert-FeedFloorSeed -Value $null -Channel dev) -ceq '') 'an empty seed (no seed) was refused'
        $arabic = ([string][char]0x0661) + '.2.3'
        foreach ($bad in '0.4.2;Foo=1', '0.4.2;', "0.4.2`n", "0.4.2`r`n", "0.4.2`n-p:X=1", '0.4.2 ', ' 0.4.2', '0.4', '0.4.2.1', 'v0.4.2', '0.4.2-dev', '0.4.2-dev.', '0.4.2-DEV.1', '0.4.2-beta.1', '0.4.2"', "0.4.2'", '0.4.2$(x)', '0.4.2%3B', '0.4.2,1', '0.4.2-dev.1;', $arabic, '..') {
            Assert-Throws { [void](Assert-FeedFloorSeed -Value $bad -Channel dev) } 'not a version' "dev seed [$($bad -replace "`n", '\n' -replace "`r", '\r')]"
        }
        Assert-Throws { [void](Assert-FeedFloorSeed -Value '0.4.2-dev.7' -Channel stable) } 'dev version' 'a dev version as the stable seed'
        # Listing lines: tag, draft flag, asset names, author login (L-4 added the author column).
        $bot = "`tgithub-actions[bot]"
        $listing = @("v0.4.1`tfalse`tClink.exe,stable.json,stable.json.sig$bot", "v0.4.2-dev.3`tfalse`tClink.exe,dev.json,dev.json.sig$bot", "v0.4.2-dev.9`ttrue`tdev.json$bot", "v0.4.2-dev.5`tfalse`tdev.json$bot", "v0.4.0`tfalse`tClink.exe$bot")
        $seeds = Get-FeedFloorSeeds -Listing $listing
        Assert-True ($seeds.Stable -ceq '0.4.1' -and $seeds.Dev -ceq '0.4.2-dev.5') "seeds are stable '$($seeds.Stable)' dev '$($seeds.Dev)' (a draft must not count)"
        $none = Get-FeedFloorSeeds -Listing @("v0.4.0`tfalse`tClink.exe$bot")
        Assert-True ($none.Stable -ceq '' -and $none.Dev -ceq '') 'no manifest-carrying release must mean no seed'
        Assert-Throws { [void](Get-FeedFloorSeeds -Listing @("v0.4.2;x`tfalse`tstable.json$bot")) } 'not a feed version' 'a tag that is not a feed version'
    }

    Invoke-Test 'L-4: floor seeds and the history floor count only releases made by github-actions[bot]; the old 3-column listing is refused' {
        $bot = "`tgithub-actions[bot]"
        # A higher release that a PERSON created (or a look-alike login) carries the manifest: it must not raise either seed or the floor.
        $listing = @("v0.4.1`tfalse`tstable.json,stable.json.sig$bot", "v9.9.9`tfalse`tstable.json`toctocat", "v0.5.0`tfalse`tstable.json`tgithub-actions", "v0.6.0`tfalse`tstable.json`tGitHub-Actions[bot]",
            "v0.6.1`tfalse`tstable.json`tgithub-actions[bot] ", "v0.4.2-dev.3`tfalse`tdev.json$bot", "v0.4.2-dev.99`tfalse`tdev.json`tsomeone", "v0.4.2-dev.98`tfalse`tdev.json`tnull", "nightly`tfalse`tstable.json`tsomeone")
        $seeds = Get-FeedFloorSeeds -Listing $listing
        Assert-True ($seeds.Stable -ceq '0.4.1' -and $seeds.Dev -ceq '0.4.2-dev.3') "seeds are stable '$($seeds.Stable)' dev '$($seeds.Dev)' (a non-bot release counted)"
        Assert-True ((Get-FeedHistoryFloor -Channel stable -Listing $listing) -ceq '0.4.1' -and (Get-FeedHistoryFloor -Channel dev -Listing $listing) -ceq '0.4.2-dev.3') 'the history floor counted a non-bot release'
        Assert-Throws { [void](Get-FeedHistoryFloor -Channel stable -Listing @("v0.4.1`tfalse`tstable.json")) } 'Unexpected release listing line' 'a 3-column line (no author) fails closed'
        Assert-Throws { [void](Get-FeedHistoryFloor -Channel stable -Listing @("v0.4.1`tfalse`tstable.json$bot`textra")) } 'Unexpected release listing line' 'a 5-column line'
        # Every listing in both workflows (seeds and history floor) asks for the author column.
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $jq = [regex]::Matches((Read-Workflow $name), "--jq '\.\[\] \| \[\.tag_name, \(\.draft \| tostring\), \(\[\.assets\[\]\.name\][^']*'")
            Assert-True ($jq.Count -eq 2) "$name : expected 2 tag/draft listings (seeds, history floor), found $($jq.Count)"
            foreach ($m in $jq) { Assert-True ($m.Value.Contains('(.author.login | tostring)] | @tsv')) "$name : a listing lacks the author column: $($m.Value)" }
        }
    }

    Invoke-Test 'M1: the seed of the channel being built must be strictly lower than the version (Assert-FeedSeedBelowVersion, wired in both release workflows)' {
        Assert-FeedSeedBelowVersion -Channel stable -Seed '0.4.1' -Version '0.4.2'
        Assert-FeedSeedBelowVersion -Channel dev -Seed '0.4.2-dev.3' -Version '0.4.2-dev.4'
        Assert-FeedSeedBelowVersion -Channel dev -Seed '0.4.1' -Version '0.4.2-dev.1'
        Assert-FeedSeedBelowVersion -Channel stable -Seed '' -Version '0.4.2'
        Assert-FeedSeedBelowVersion -Channel dev -Seed $null -Version '0.4.2-dev.1'
        Assert-Throws { Assert-FeedSeedBelowVersion -Channel stable -Seed '0.4.2' -Version '0.4.2' } 'not lower than the version being built' 'an equal stable seed'
        Assert-Throws { Assert-FeedSeedBelowVersion -Channel stable -Seed '0.4.3' -Version '0.4.2' } 'not lower than the version being built' 'a higher stable seed'
        Assert-Throws { Assert-FeedSeedBelowVersion -Channel dev -Seed '0.4.2-dev.4' -Version '0.4.2-dev.4' } 'not lower than the version being built' 'an equal dev seed'
        Assert-Throws { Assert-FeedSeedBelowVersion -Channel dev -Seed '0.4.2-dev.9' -Version '0.4.2-dev.4' } 'not lower than the version being built' 'a higher dev seed'
        $stable = Get-StepRunText (Get-BuildStep 'release.yml' 'Find the embedded floor seeds')
        Assert-True ($stable.Contains('Assert-FeedSeedBelowVersion -Channel stable -Seed $seeds.Stable -Version $env:VERSION')) 'release.yml: the stable seed is not checked against the version'
        Assert-True ((Get-BuildStep 'release.yml' 'Find the embedded floor seeds').Contains('VERSION: ${{ steps.version.outputs.version }}')) 'release.yml: the seeds step has no VERSION'
        $dev = Get-BuildStep 'release-dev.yml' 'Check the dev floor seed is below the version'
        Assert-True ((Get-StepRunText $dev).Contains('Assert-FeedSeedBelowVersion -Channel dev -Seed $env:MIN_DEV -Version $env:VERSION') -and $dev.Contains('MIN_DEV: ${{ steps.seeds.outputs.min_dev }}') -and $dev.Contains('VERSION: ${{ steps.version.outputs.version }}')) 'release-dev.yml: the dev seed is not checked against the version'
    }

    Invoke-Test 'L-4: the publish job refuses when its history floor differs from the seed the build job embedded (seeds are build outputs; run against a fake gh)' {
        Assert-FeedFloorMatchesSeed -Channel stable -Floor '0.4.1' -Seed '0.4.1'
        Assert-FeedFloorMatchesSeed -Channel dev -Floor $null -Seed ''
        Assert-FeedFloorMatchesSeed -Channel dev -Floor '' -Seed $null
        Assert-Throws { Assert-FeedFloorMatchesSeed -Channel stable -Floor '0.4.2' -Seed '0.4.1' } 'history floor is now .0\.4\.2. but the build job embedded.*0\.4\.1' 'a newer floor'
        Assert-Throws { Assert-FeedFloorMatchesSeed -Channel stable -Floor '0.4.1' -Seed '' } 'stale seed' 'a release appeared'
        Assert-Throws { Assert-FeedFloorMatchesSeed -Channel dev -Floor '' -Seed '0.4.2-dev.3' } 'stale seed' 'a release was deleted'
        foreach ($pair in @(@('release.yml', 'stable', 'min_stable'), @('release-dev.yml', 'dev', 'min_dev'))) {
            $name = $pair[0]
            $jobs = Split-Jobs (Read-Workflow $name)
            Assert-True ($jobs.Build.Contains('min_stable: ${{ steps.seeds.outputs.min_stable }}') -and $jobs.Build.Contains('min_dev: ${{ steps.seeds.outputs.min_dev }}')) "$name : the seeds are not build-job outputs"
            $step = Get-PublishStep $name 'Find the GitHub history floor'
            Assert-True ($step.Contains("BUILD_SEED: `${{ needs.build.outputs.$($pair[2]) }}")) "$name : the history step does not get the $($pair[1]) seed of the build job"
            $run = Get-StepRunText $step
            Assert-True ($run.Contains("Assert-FeedFloorMatchesSeed -Channel $($pair[1]) -Floor `$floor -Seed `$env:BUILD_SEED")) "$name : the history step does not compare the floor with the seed"
            Assert-True ($run.IndexOf('Assert-FeedFloorMatchesSeed') -lt $run.IndexOf('Add-Content')) "$name : the floor is output before the comparison"
            # Behaviour: the step's script against a fake gh listing.
            $block = $run.Replace('. ./tools/lib/feed-publish-lib.ps1', ". '$(Join-Path $toolsDir 'lib\feed-publish-lib.ps1')'")
            $work = Get-ScratchFolder "l4-$($pair[1])"
            $out = Join-Path $work 'github-output.txt'
            $manifest = "$($pair[1]).json"
            $tag = if ($pair[1] -ceq 'stable') { 'v0.4.1' } else { 'v0.4.2-dev.3' }
            $script:ghListingL4 = @("$tag`tfalse`tClink.exe,$manifest`tgithub-actions[bot]")
            function gh { $global:LASTEXITCODE = 0; $script:ghListingL4 }
            $saved = @{}; foreach ($n in 'GITHUB_OUTPUT', 'BUILD_SEED', 'GITHUB_REPOSITORY') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
            try {
                $env:GITHUB_OUTPUT = $out; $env:GITHUB_REPOSITORY = 'o/r'
                $env:BUILD_SEED = $tag.Substring(1)
                $err = $null; try { . ([scriptblock]::Create($block)) 6>$null | Out-Null } catch { $err = $_.Exception.Message }
                Assert-True ($null -eq $err -and ([System.IO.File]::ReadAllText($out) -match "floor=$([regex]::Escape($tag.Substring(1)))")) "$name : matching seed refused or no floor output: $err"
                Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
                [Environment]::SetEnvironmentVariable('BUILD_SEED', $null)
                $err = $null; try { . ([scriptblock]::Create($block)) 6>$null | Out-Null } catch { $err = $_.Exception.Message }
                Assert-True ($null -ne $err -and $err -match 'stale seed') "$name : a release that appeared after the build was not refused: $err"
                Assert-True (-not (Test-Path -LiteralPath $out)) "$name : the floor was output although it differs from the seed"
            } finally { Remove-Item Function:\gh -ErrorAction SilentlyContinue; foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) } }
        }
    }

    Invoke-Test 'M1: wiring - the seeds step is in both build jobs (read token, before the build), the seeds go to publish-flavours.ps1 and to the dev unit-test build; build.yml and local builds pass none' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $jobs = Split-Jobs (Read-Workflow $name)
            $seeds = Get-BuildStep $name 'Find the embedded floor seeds'
            Assert-True ($null -ne $seeds) "$name : no 'Find the embedded floor seeds' step in the build job"
            Assert-True ($seeds.Contains('id: seeds') -and $seeds.Contains('GH_TOKEN: ${{ github.token }}') -and $seeds -notmatch 'secrets\.') "$name : the seeds step has the wrong id or environment"
            $run = Get-StepRunText $seeds
            Assert-True ($run.Contains('Get-FeedFloorSeeds -Listing $listing') -and $run.Contains('gh api "repos/$env:GITHUB_REPOSITORY/releases?per_page=100" --paginate')) "$name : the seeds step does not list the releases and reuse the floor logic"
            Assert-True ($run.Contains('min_stable=') -and $run.Contains('min_dev=') -and $run -notmatch 'gh release|-X (POST|PATCH|DELETE)') "$name : the seeds step outputs are wrong or it writes"
            $publish = Get-BuildStep $name 'Publish both flavours'
            Assert-True ($publish.Contains('MIN_STABLE: ${{ steps.seeds.outputs.min_stable }}') -and $publish.Contains('MIN_DEV: ${{ steps.seeds.outputs.min_dev }}')) "$name : Publish both flavours does not get the seeds through env"
            Assert-True ((Get-StepRunText $publish).Contains('-MinStable $env:MIN_STABLE -MinDev $env:MIN_DEV')) "$name : publish-flavours is not called with the seeds"
            Assert-True ($jobs.Build.IndexOf('- name: Find the embedded floor seeds') -lt $jobs.Build.IndexOf('- name: Publish both flavours')) "$name : the seeds are found after the publish"
            Assert-True ($jobs.Build.IndexOf('- name: Find the embedded floor seeds') -gt $jobs.Build.IndexOf('- name: Fresh NuGet folders')) "$name : the seeds step ran before the NuGet setup"
        }
        $devJobs = Split-Jobs (Read-Workflow 'release-dev.yml')
        Assert-True ($devJobs.Build.IndexOf('- name: Find the embedded floor seeds') -lt $devJobs.Build.IndexOf('- name: Build')) 'release-dev.yml: the seeds are found after the unit-tested build'
        $build = Get-StepRunText (Get-BuildStep 'release-dev.yml' 'Build')
        Assert-True ($build.Contains('-p:ClinkMinStable=$env:MIN_STABLE') -and $build.Contains('-p:ClinkMinDev=$env:MIN_DEV') -and $build.Contains('dotnet build Clink/Clink.csproj -c Release @props')) 'release-dev.yml: the unit-tested build does not get the seeds'
        Assert-True ((Get-BuildStep 'release-dev.yml' 'Build').Contains('MIN_STABLE: ${{ steps.seeds.outputs.min_stable }}')) 'release-dev.yml: the Build step does not get the seeds through env'
        $dry = Read-Workflow 'build.yml'
        Assert-True ($dry -notmatch 'ClinkMin|MinStable|MinDev') 'build.yml (the dry run) passes a floor seed'
        $script = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'publish-flavours.ps1'))
        Assert-True ($script.Contains('"-p:ClinkMinStable=$MinStable"') -and $script.Contains('"-p:ClinkMinDev=$MinDev"') -and $script.Contains('if ($MinStable)') -and $script.Contains('if ($MinDev)')) 'publish-flavours.ps1 does not pass the seeds as properties only when given'
    }

    Invoke-Test 'M1: publish-flavours.ps1 refuses a seed that is not a version (a ; would add an MSBuild property) before it touches dotnet or any folder' {
        $script = Join-Path $toolsDir 'publish-flavours.ps1'
        foreach ($case in @(@('-MinStable', '0.4.2;Foo=1'), @('-MinStable', '0.4.2-dev.1'), @('-MinDev', '0.4.2;Foo=1'), @('-MinDev', 'x'), @('-MinDev', '0.4.2 -p:Evil=1'))) {
            $r = Invoke-Child $script @($case[0], $case[1])
            Assert-True ($r.Code -ne 0 -and $r.Text -match 'is not a version|must be a stable version') "$($case[0]) '$($case[1])' was accepted: $($r.Text)"
            Assert-True ($r.Text -notmatch '== publish') "$($case[0]) '$($case[1])' reached dotnet publish"
        }
    }

    Invoke-Test 'L4: the public check returns and asserts headers for notes.txt - Content-Type text/plain; charset=utf-8 (must), X-Content-Type-Options nosniff (warning until the Cloudflare rule exists, failure with -RequireNoSniff)' {
        $asset = New-NotesAsset -Text 'Header notes'
        $dl = Get-ScratchFolder 'hdr'
        $serve = { param($Headers, $Bytes = $asset.Bytes) { param([string]$Url, [string]$OutFile) [System.IO.File]::WriteAllBytes($OutFile, [byte[]]$Bytes); @{ Status = 200; Headers = $Headers } }.GetNewClosure() }
        $good = @{ 'content-type' = 'text/plain; charset=utf-8'; 'x-content-type-options' = 'nosniff' }
        $warned = Assert-FeedPublicAsset -Http (& $serve $good) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl 6>&1
        Assert-True (-not ($warned | Where-Object { "$_" -match '::warning::' })) 'a fully correct answer warned'
        # Upper-case values and a mixed-case Content-Type are fine (headers are case-insensitive).
        Assert-FeedPublicAsset -Http (& $serve @{ 'content-type' = 'Text/Plain; Charset=UTF-8'; 'x-content-type-options' = 'NOSNIFF' }) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl 6>$null
        # No nosniff: a warning, not a failure.
        $out = Assert-FeedPublicAsset -Http (& $serve @{ 'content-type' = 'text/plain; charset=utf-8' }) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl 6>&1
        Assert-True (@($out | Where-Object { "$_" -match '::warning::.*nosniff' }).Count -eq 1) "a missing nosniff did not warn: $out"
        Assert-Throws { Assert-FeedPublicAsset -Http (& $serve @{ 'content-type' = 'text/plain; charset=utf-8' }) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl -RequireNoSniff 6>$null } 'nosniff' '-RequireNoSniff without the header'
        Assert-Throws { Assert-FeedPublicAsset -Http (& $serve @{ 'content-type' = 'text/plain; charset=utf-8'; 'x-content-type-options' = 'sniff' }) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl -RequireNoSniff 6>$null } 'nosniff' 'another value'
        # Content-Type is a failure whatever else is right: wrong type, wrong charset, missing, no headers at all.
        foreach ($bad in @(@{ 'content-type' = 'text/html; charset=utf-8'; 'x-content-type-options' = 'nosniff' }, @{ 'content-type' = 'text/plain'; 'x-content-type-options' = 'nosniff' },
                @{ 'content-type' = 'text/plain; charset=iso-8859-1'; 'x-content-type-options' = 'nosniff' }, @{ 'content-type' = 'application/octet-stream' }, @{ 'x-content-type-options' = 'nosniff' }, $null)) {
            Assert-Throws { Assert-FeedPublicAsset -Http (& $serve $bad) -Asset $asset -Attempts 1 -DelaySeconds 0 -ScratchDir $dl 6>$null } 'Content-Type' "headers $(if ($bad) { ($bad.Values -join '|') } else { '(none)' })"
        }
        # Other assets (the binaries) are not checked for headers.
        $exe = [pscustomobject]@{ Name = 'Clink.exe'; Url = $asset.Url; Sha256 = $asset.Sha256; Size = $asset.Size }
        Assert-FeedPublicAsset -Http (& $serve $null) -Asset $exe -Attempts 1 -DelaySeconds 0 -ScratchDir $dl 6>$null
        # The real invoker returns the headers (lower-case names, one string each) next to the status: fed from a fake Invoke-WebRequest.
        function Invoke-WebRequest { param($Method, $Uri, $OutFile, [switch]$PassThru, $MaximumRedirection, [switch]$UseBasicParsing, $TimeoutSec)
            [System.IO.File]::WriteAllBytes($OutFile, [byte[]](1, 2, 3))
            $h = New-Object System.Collections.Specialized.OrderedDictionary
            $h.Add('Content-Type', [string[]]@('text/plain; charset=utf-8')); $h.Add('X-Content-Type-Options', [string[]]@('nosniff'))
            [pscustomobject]@{ StatusCode = 200; Headers = $h } }
        try {
            $real = & (New-FeedHttpInvoker) -Url 'https://dl.example/x' -OutFile (Join-Path $dl 'real-invoker.bin')
            Assert-True ($real.Status -eq 200 -and $real.Headers['content-type'] -ceq 'text/plain; charset=utf-8' -and $real.Headers['x-content-type-options'] -ceq 'nosniff') 'the real invoker does not return lower-case header names with their values'
        } finally { Remove-Item Function:\Invoke-WebRequest -ErrorAction SilentlyContinue }
        # upload-feed.ps1 checks every asset, notes.txt included, through Assert-FeedPublicAsset (which now looks at the headers).
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1')).Contains('foreach ($a in $assets) { Assert-FeedPublicAsset -Http $httpInvoker -Asset $a }')) 'upload-feed.ps1 does not run the public check on every asset'
    }

    Invoke-Test 'M2: the UTF-8 guard - U+FFFD and the a-circumflex+euro mojibake fail, normal non-ASCII text passes; the stable notes step sets the console to UTF-8 BEFORE gh api generate-notes' {
        $fffd = [string][char]0xFFFD
        $moji = ([string][char]0x00E2) + ([string][char]0x20AC) + ([string][char]0x2122)
        Assert-Throws { Assert-FeedNotesNoMojibake -Text "Fix the caf$fffd" } 'U\+FFFD' 'a replacement character'
        Assert-Throws { Assert-FeedNotesNoMojibake -Text "Don${moji}t" } 'mojibake' 'UTF-8 read as Windows-1252'
        Assert-Throws { Assert-FeedNotesNoMojibake -Text ('x' + ([string][char]0x00E2) + ([string][char]0x20AC)) } 'mojibake' 'the bare sequence'
        Assert-FeedNotesNoMojibake -Text ("Fix the caf$([char]0xE9) $([char]0x2014) it is $([char]::ConvertFromUtf32(0x1F600)) done, $([char]0x20AC)5 and $([char]0xE2)")
        Assert-FeedNotesNoMojibake -Text ''
        Assert-FeedNotesNoMojibake -Text $null
        $run = Get-StepRunText (Get-PublishStep 'release.yml' 'Write the release notes (notes.txt)')
        $enc = $run.IndexOf('[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)')
        $gen = $run.IndexOf('gh api -X POST')
        Assert-True ($enc -ge 0 -and $gen -gt $enc) 'the console encoding is not set to UTF-8 before gh api generate-notes'
        # MF-1 order: generate, clean, drop the links, then the guard (on the final text; the raw text is checked too, before anything else).
        $clean = $run.IndexOf('ConvertTo-FeedNotesText -Text $joined')
        $links = $run.IndexOf('Remove-FeedNotesRepoLinks -Text (ConvertTo-FeedNotesText -Text $joined)')
        $guard = $run.LastIndexOf('Assert-FeedNotesNoMojibake -Text $text')
        $write = $run.IndexOf('Write-FeedNotesFile -Text $text')
        Assert-True ($gen -lt $clean -and $links -ge 0 -and $links -le $clean -and $guard -gt $links -and $write -gt $guard) 'the notes step does not run generate -> clean -> remove links -> mojibake guard -> write'
        Assert-True ($run.Contains('Assert-FeedNotesNoMojibake -Text $joined') -and $run.IndexOf('Assert-FeedNotesNoMojibake -Text $joined') -gt $gen) 'the raw generated text is not checked for mojibake'
    }

    Invoke-Test 'MF-1+M2+L3: the stable notes step run for real (fake gh): ALWAYS generates (never reads the public or bucket notes), cleans, drops the private links, refuses mojibake' {
        $step = Get-PublishStep 'release.yml' 'Write the release notes (notes.txt)'
        Assert-True ($step -notmatch 'secrets\.' -and $step -notmatch 'FEED_URL') 'the notes step reads a secret or the feed url (it must not fetch anything from the feed)'
        $run = Get-StepRunText $step
        $work = Get-ScratchFolder 'notes-step'
        New-Item -ItemType Directory -Force -Path (Join-Path $work 'dist\package') | Out-Null
        $notesFile = Join-Path $work 'dist\package\notes.txt'
        $block = $run.Replace('. ./tools/lib/notes-lib.ps1', ". '$(Join-Path $toolsDir 'lib\notes-lib.ps1')'")
        $generated = "## What's Changed`n* Fix the caf$([char]0xE9) by @octo in https://github.com/Euphoric-Software/clink/pull/12`n* See https://github.com/Euphoric-Software/clink/issues/4 for details`n`n## New Contributors`n* @newbie made their first contribution in https://github.com/Euphoric-Software/clink/pull/13`n`n**Full Changelog**: https://github.com/Euphoric-Software/clink/compare/v0.4.1...v0.4.2"
        $script:ghGenerated = $generated
        $script:ghCalls = New-Object System.Collections.ArrayList
        $script:netCalls = New-Object System.Collections.ArrayList
        function gh { [void]$script:ghCalls.Add(($args -join ' ')); $global:LASTEXITCODE = 0; $script:ghGenerated }
        # Anything that could read the public or bucket notes is a recorded fake: the step must call none of them.
        function Invoke-WebRequest { [void]$script:netCalls.Add('Invoke-WebRequest ' + ($args -join ' ')); throw 'the notes step fetched something' }
        function Invoke-RestMethod { [void]$script:netCalls.Add('Invoke-RestMethod ' + ($args -join ' ')); throw 'the notes step fetched something' }
        $saved = @{}; foreach ($n in 'FEED_URL', 'VERSION', 'GITHUB_REPOSITORY') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
        $savedEnc = [Console]::OutputEncoding
        $savedCwd = [Environment]::CurrentDirectory
        Push-Location $work
        try {
            [Environment]::CurrentDirectory = $work     # the step writes dist/package/notes.txt through .NET, which resolves against the process folder
            [Environment]::SetEnvironmentVariable('FEED_URL', 'https://dl.example/clink/'); [Environment]::SetEnvironmentVariable('VERSION', '0.4.2'); [Environment]::SetEnvironmentVariable('GITHUB_REPOSITORY', 'Euphoric-Software/clink')
            $runStep = { Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue; $script:ghCalls.Clear(); $e = $null; try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $e = $_.Exception.Message }; $e }
            # 1. Generated, cleaned (no repo URL, no mention, no contributors section), UTF-8 kept, a valid notes file; exactly one gh call (the POST).
            $e = & $runStep
            Assert-True ($null -eq $e) "the notes step failed: $e"
            Assert-True ($script:ghCalls.Count -eq 1 -and $script:ghCalls[0] -ceq 'api -X POST repos/Euphoric-Software/clink/releases/generate-notes -f tag_name=v0.4.2 --jq .body') "gh calls: $($script:ghCalls -join ' | ')"
            Assert-True ($script:netCalls.Count -eq 0) "the step fetched something: $($script:netCalls -join ' | ')"
            $text = $utf8.GetString([System.IO.File]::ReadAllBytes($notesFile))
            Assert-True ($text -notmatch 'github\.com/Euphoric' -and $text -notmatch '@octo|@newbie' -and $text -notmatch 'Full Changelog|New Contributors') "private data survived in: $text"
            Assert-True ($text.Contains("* Fix the caf$([char]0xE9)") -and $text.Contains("What's Changed")) "text was lost: $text"
            Assert-True ('' -eq (Test-FeedNotesBytes -Bytes ([System.IO.File]::ReadAllBytes($notesFile)))) 'the written notes are not a valid file'
            Assert-True ([Console]::OutputEncoding.WebName -ceq 'utf-8') 'the console output encoding was not set to UTF-8'
            # 2. A notes.txt already in dist/package (or anywhere) is never reused: the step regenerates and overwrites it.
            [System.IO.File]::WriteAllBytes($notesFile, $utf8.GetBytes("Planted earlier`n"))
            $e = $null; try { . ([scriptblock]::Create($block)) 6>$null 3>$null | Out-Null } catch { $e = $_.Exception.Message }
            Assert-True ($null -eq $e -and $script:ghCalls.Count -ge 1 -and -not $utf8.GetString([System.IO.File]::ReadAllBytes($notesFile)).Contains('Planted')) "an existing notes file was reused: $e"
            # 3. Clean BEFORE the link filter: a control character inside a repository link cannot hide it from the filter.
            $script:ghGenerated = "* Fix in https://github$([char]0)$([char]0x1B).com/Euphoric-Software/clink/pull/9 done"
            $e = & $runStep
            Assert-True ($null -eq $e -and $utf8.GetString([System.IO.File]::ReadAllBytes($notesFile)) -notmatch '(?i)Euphoric-Software') "a link hidden by a control character survived: $e"
            # 4. Empty generated text: the fallback "Clink <tag>".
            $script:ghGenerated = ''
            $e = & $runStep
            Assert-True ($null -eq $e -and $utf8.GetString([System.IO.File]::ReadAllBytes($notesFile)) -ceq "Clink v0.4.2`n") "the empty-text fallback is wrong: $e"
            # 5. Mojibake or U+FFFD in the generated text: the step fails and writes nothing.
            foreach ($badText in ("Fix caf$([char]0xFFFD)"), ("Don$([char]0xE2)$([char]0x20AC)$([char]0x2122)t")) {
                $script:ghGenerated = $badText
                $e = & $runStep
                Assert-True ($null -ne $e -and $e -match 'U\+FFFD|mojibake') "a damaged text was accepted: $e"
                Assert-True (-not (Test-Path -LiteralPath $notesFile)) 'a notes file was written from damaged text'
            }
            Assert-True ($script:netCalls.Count -eq 0) "the step fetched something: $($script:netCalls -join ' | ')"
        } finally {
            [Environment]::CurrentDirectory = $savedCwd
            Pop-Location
            foreach ($f in 'gh', 'Invoke-WebRequest', 'Invoke-RestMethod') { Remove-Item "Function:\$f" -ErrorAction SilentlyContinue }
            try { [Console]::OutputEncoding = $savedEnc } catch { }
            foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
        }
    }

    Invoke-Test 'MF-1: the stable notes step never reads published notes (no Get-FeedPublishedNotesBytes, no GET of the public notes, no feed url); the reuse helpers are gone' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $step = Get-PublishStep $name 'Write the release notes (notes.txt)'
            $run = Get-StepRunText $step
            foreach ($bad in 'Get-FeedPublishedNotesBytes', 'New-FeedNotesFetcher', 'Invoke-WebRequest', 'Invoke-RestMethod', 'curl', 'wget', 'FEED_URL', 'notes.txt"', 's3api', 'aws ', 'Reusing') {
                Assert-True (-not $run.Contains($bad)) "$name : the notes step contains '$bad'"
            }
            # Every gh invocation (at a line start, after & or a pipe; a message text that names gh does not count) must be the generate-notes POST.
            Assert-True ($run -notmatch '(?im)(?:^|&|\|)\s*gh\s+api\b(?!\s+-X POST "repos/\$env:GITHUB_REPOSITORY/releases/generate-notes")' -and $run -notmatch '(?i)-X\s+GET|--method\s+GET|(?:^|&|\|)\s*gh\s+release') "$name : the notes step makes a gh call other than generate-notes"
            Assert-True ($step -notmatch 'steps\.feed\.outputs\.feed_url') "$name : the notes step still receives the feed url"
        }
        $stableRun = Get-StepRunText (Get-PublishStep 'release.yml' 'Write the release notes (notes.txt)')
        Assert-True ($stableRun.Contains('gh api -X POST "repos/$env:GITHUB_REPOSITORY/releases/generate-notes" -f "tag_name=$tag" --jq .body') -and $stableRun -notmatch '(?m)^\s*\}\s*else\s*\{') 'the stable notes step does not ALWAYS generate'
        $lib = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'lib\notes-lib.ps1'))
        Assert-True (-not $lib.Contains('function Get-FeedPublishedNotesBytes') -and -not $lib.Contains('function New-FeedNotesFetcher')) 'the reuse helpers are still defined in notes-lib.ps1'
        foreach ($wf in Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml') {
            $code = [System.IO.File]::ReadAllText($wf.FullName)
            Assert-True ($code -notmatch 'Get-FeedPublishedNotesBytes|New-FeedNotesFetcher') "$($wf.Name) still names a notes reuse helper"
        }
    }

    Invoke-Test 'MF-1: an existing notes.txt with different bytes makes Publish-FeedVersionedAsset (and the preflight) throw "already exists with different content"; the same bytes are skipped (fake aws)' {
        foreach ($case in @(@('releases', 'v0.4.2'), @('dev', 'v0.4.2-dev.7'))) {
            $script:store = @{}; $script:calls.Clear()
            $ours = New-NotesAsset -Text "Generated by this run`nline two" -Tag $case[1] -Folder $case[0]
            # Somebody with bucket write but no key planted notes for the NEXT version ahead of time.
            $script:store[$ours.Key] = $utf8.GetBytes("Planted text, not what the release says`n")
            $dl = Get-ScratchFolder 'mf1-dl'
            Assert-Throws { Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $ours -CacheControl 'public, max-age=31536000, immutable' -ScratchDir $dl } 'already exists with different content.*publish a newer version' "$($case[0]): planted notes"
            Assert-Throws { Publish-FeedVersionedAssets -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Assets @($ours) -CacheControl 'x' -ScratchDir $dl } 'already exists with different content' "$($case[0]): planted notes (preflight)"
            Assert-True (@($script:calls | Where-Object { $_ -like 's3api put-object*' }).Count -eq 0) "$($case[0]): something was written over the planted notes"
            Assert-True ((Get-Sha256Hex ([byte[]]$script:store[$ours.Key])) -ne $ours.Sha256) "$($case[0]): the planted object changed"
            # The very same bytes (a genuine earlier upload of this run's notes): skipped, nothing written.
            $script:store[$ours.Key] = [byte[]]$ours.Bytes; $script:calls.Clear()
            $r = Publish-FeedVersionedAsset -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Asset $ours -CacheControl 'x' -ScratchDir $dl 6>$null
            Assert-True ($r -ceq 'skipped' -and @($script:calls | Where-Object { $_ -like 's3api put-object*' }).Count -eq 0) "$($case[0]): the same bytes were not skipped: $r"
        }
    }

    Invoke-Test 'L-5: upload preflight - every versioned key (binaries, .sha256, notes.txt) is checked before the first write; a conflict on the LAST asset writes nothing (fake aws)' {
        $dir = Get-ScratchFolder ('l5-' + [guid]::NewGuid().ToString('N'))
        $names = 'Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'Clink-Setup.exe', 'notes.txt'
        $assets = foreach ($n in $names) {
            $path = Join-Path $dir $n
            [System.IO.File]::WriteAllBytes($path, $utf8.GetBytes("content of $n`n"))
            $bytes = [System.IO.File]::ReadAllBytes($path)
            [pscustomobject]@{ Name = $n; Local = $path; Sha256 = (Get-Sha256Hex $bytes); Size = [int64]$bytes.Length; Key = "clink/releases/v0.4.2/$n"; Url = "https://dl.example/clink/releases/v0.4.2/$n"; Type = 'text/plain'; Bytes = $bytes }
        }
        $dl = Get-ScratchFolder 'l5-dl'
        # A conflict on the last asset (notes.txt): nothing at all is written, every key was read first.
        $script:store = @{}; $script:calls.Clear()
        $script:store['clink/releases/v0.4.2/notes.txt'] = $utf8.GetBytes("different`n")
        Assert-Throws { Publish-FeedVersionedAssets -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Assets $assets -CacheControl 'x' -ScratchDir $dl 6>$null } 'notes\.txt already exists with different content' 'a conflict on the last asset'
        Assert-True (@($script:calls | Where-Object { $_ -like 's3api put-object*' }).Count -eq 0) "something was written before the conflict was found: $(@($script:calls) -join ' | ')"
        Assert-True (@($script:calls | Where-Object { $_ -like 's3api get-object*' }).Count -eq $names.Count) 'not every key was read in the preflight'
        Assert-True ($script:store.Count -eq 1) 'the bucket gained an object'
        # A conflict on the first asset: the same.
        $script:store = @{}; $script:calls.Clear()
        $script:store['clink/releases/v0.4.2/Clink.exe'] = $utf8.GetBytes("other exe`n")
        Assert-Throws { Publish-FeedVersionedAssets -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Assets $assets -CacheControl 'x' -ScratchDir $dl 6>$null } 'Clink\.exe already exists with different content' 'a conflict on the first asset'
        Assert-True (@($script:calls | Where-Object { $_ -like 's3api put-object*' }).Count -eq 0) 'something was written although the first asset conflicts'
        # No conflict, two already there with the same bytes: those are skipped, the rest uploaded (conditional create), in order.
        $script:store = @{}; $script:calls.Clear()
        $script:store['clink/releases/v0.4.2/Clink.exe'] = [byte[]]$assets[0].Bytes
        $script:store['clink/releases/v0.4.2/notes.txt'] = [byte[]]$assets[5].Bytes
        $results = @(Publish-FeedVersionedAssets -Aws $fakeAws -Bucket 'b' -Endpoint 'https://r2.example' -Assets $assets -CacheControl 'x' -ScratchDir $dl 6>$null)
        Assert-True (($results -join ',') -ceq 'skipped,uploaded,uploaded,uploaded,uploaded,skipped') "results: $($results -join ',')"
        $firstPut = [array]::IndexOf(@($script:calls), @($script:calls | Where-Object { $_ -like 's3api put-object*' })[0])
        $lastGet = [array]::LastIndexOf(@($script:calls), @($script:calls | Where-Object { $_ -like 's3api get-object*' })[-1])
        Assert-True ($lastGet -lt $firstPut) 'a put happened before every key was checked'
        Assert-True (@($script:calls | Where-Object { $_ -like 's3api put-object*' -and $_ -like '*--if-none-match `**' }).Count -eq 4) 'the uploads are not conditional creates'
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        Assert-True ($text.Contains('Publish-FeedVersionedAssets -Aws $awsInvoker -Bucket $bucket -Endpoint $endpoint -Assets $assets -CacheControl $immutable') -and $text -notmatch 'Publish-FeedVersionedAsset -Aws') 'upload-feed.ps1 does not use the preflighted batch upload'
    }

    Invoke-Test 'L3: Remove-FeedNotesRepoLinks drops every remaining URL of this repository (any path, any case) and every @mention, keeps other links and e-mail addresses' {
        $repoName = 'Euphoric-Software/clink'
        $in = @(
            '* Add the thing by @octo-cat in https://github.com/Euphoric-Software/clink/pull/1',
            '* Mention in the middle @bob and more text',
            '* Compare https://github.com/euphoric-software/CLINK/compare/v1...v2 here',
            '* Tree https://github.com/Euphoric-Software/clink/tree/main/src and release https://github.com/Euphoric-Software/clink/releases/tag/v0.4.1',
            '* Bare https://github.com/Euphoric-Software/clink',
            '* Other https://github.com/Euphoric-Software/clink-extras/pull/2 stays? no: that is a different repo name prefix',
            '* Mail a@b.example and https://example.com/@user/page stay',
            '* Thanks to @alice and @bob-2.',
            '## New Contributors',
            '* @newbie made their first contribution in https://github.com/Euphoric-Software/clink/pull/9') -join "`n"
        $out = Remove-FeedNotesRepoLinks -Text $in -Repository $repoName
        Assert-True ($out -notmatch '(?i)github\.com/Euphoric-Software/clink(?![\w-])') "a repository URL survived: $out"
        Assert-True ($out -notmatch '(?<![\w@./+-])@[A-Za-z0-9]') "a mention survived: $out"
        Assert-True ($out -notmatch 'New Contributors' -and $out -notmatch 'made their first contribution') "the contributors section survived: $out"
        Assert-True ($out.Contains('* Add the thing') -and $out.Contains('Mention in the middle') -and $out.Contains('a@b.example') -and $out.Contains('https://example.com/@user/page')) "wanted text was lost: $out"
        Assert-True ($out.Contains('https://github.com/Euphoric-Software/clink-extras/pull/2')) 'another repository with the same prefix was altered'
        Assert-True ($out -notmatch '(?m)\s+(in|by)\s*$') "a dangling 'in' or 'by' was left: $out"
        # Nothing at all is left of a text that is only links (the step then falls back to "Clink <tag>").
        $only = Remove-FeedNotesRepoLinks -Text '**Full Changelog**: https://github.com/Euphoric-Software/clink/compare/a...b' -Repository $repoName
        Assert-True ([string]::IsNullOrWhiteSpace($only)) "a links-only text left: [$only]"
    }

    Invoke-Test 'L-3: the link filter also drops a URL before a sentence-final ".", ".git", http://, www.github.com and a bare github.com/<owner>/<repo>, and tidies the spaces it leaves' {
        $repoName = 'Euphoric-Software/clink'
        $cases = [ordered]@{
            'sentence-final dot'      = @('Docs live at https://github.com/Euphoric-Software/clink.', 'Docs live.')
            'dot after a path'        = @('Fixed in https://github.com/Euphoric-Software/clink/pull/7.', 'Fixed.')
            '.git'                    = @('Clone https://github.com/Euphoric-Software/clink.git now', 'Clone now')
            '.git, sentence end'      = @('Clone https://github.com/Euphoric-Software/CLINK.git.', 'Clone.')
            'http://'                 = @('Old http://github.com/Euphoric-Software/clink/pull/5 link', 'Old link')
            'www.github.com'          = @('Visit www.github.com/Euphoric-Software/clink/issues today', 'Visit today')
            'https://www.'            = @('Visit https://www.github.com/euphoric-software/clink today', 'Visit today')
            'bare github.com'         = @('Repo github.com/Euphoric-Software/clink, mirrored', 'Repo, mirrored')
            'bare with a query'       = @('Query github.com/Euphoric-Software/clink?tab=readme ok', 'Query ok')
            'Full Changelog over http' = @('**Full Changelog**: http://github.com/Euphoric-Software/clink/compare/v1...v2', $null)
            'double spaces collapsed' = @('* Tree https://github.com/Euphoric-Software/clink/tree/main and  more', '* Tree and more')
            'leading space removed'   = @('https://github.com/Euphoric-Software/clink is where it lives', 'is where it lives')
            'indent kept'             = @('  * nested http://www.github.com/Euphoric-Software/clink/pull/1 item', '  * nested item')
            'empty parentheses'       = @('* Link [PR](https://github.com/Euphoric-Software/clink/pull/1) done', '* Link [PR] done')
        }
        foreach ($k in $cases.Keys) {
            $got = Remove-FeedNotesRepoLinks -Text $cases[$k][0] -Repository $repoName
            if ($null -eq $cases[$k][1]) { Assert-True ([string]::IsNullOrWhiteSpace($got)) "${k}: the line survived: [$got]" }
            else { Assert-True ($got -ceq $cases[$k][1]) "${k}: got [$got], expected [$($cases[$k][1])]" }
            Assert-True ($got -notmatch '(?i)github\.com/Euphoric-Software/clink(?![\w-]|\.[\w-])') "${k}: a repository link survived: [$got]"
        }
        # Other repositories and look-alikes stay; lines that were not changed keep their spacing exactly.
        foreach ($keep in 'Pages https://github.com/Euphoric-Software/clink.github.io stays', 'Other https://github.com/Euphoric-Software/clink-extras/pull/2 stays',
            'Host notgithub.com/Euphoric-Software/clink stays', 'Aligned   columns   stay', '    indented code stays') {
            $got = Remove-FeedNotesRepoLinks -Text $keep -Repository $repoName
            Assert-True ($got -ceq $keep) "an unrelated line was changed: [$keep] -> [$got]"
        }
    }

    Invoke-Test 'MF-2: actions: write is held ONLY by a separate cleanup job (no checkout, no uses:, no secrets, no tools/ code, one non-fatal inline step, always() after build+publish); neither publish job has actions:' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = Read-Workflow $name
            $jobs = Split-Jobs $text
            Assert-True ((Remove-YamlComments $jobs.Head) -notmatch 'actions:') "$name : an actions: permission is workflow-wide"
            Assert-True ((Remove-YamlComments $jobs.Build) -notmatch '(?m)^\s+actions:') "$name : the build job has an actions: permission"
            Assert-True ((Remove-YamlComments $jobs.Publish) -notmatch '(?m)^\s+actions:') "$name : the publish job has an actions: permission"
            $pubHeader = Remove-YamlComments ($jobs.Publish.Substring(0, $jobs.Publish.IndexOf('      - name: ')))
            Assert-True ($pubHeader -match '(?m)^    permissions:\s*\n      contents: write\s*\n    [a-z]') "$name : the publish job permissions are not exactly contents: write"
            Assert-True (@(Get-Steps $jobs.Publish | Where-Object { (Get-StepTitle $_) -ceq 'Delete the build artifact' }).Count -eq 0) "$name : the publish job still deletes the artifact itself"
            Assert-True ($jobs.Build.Contains('artifact_id: ${{ steps.upload.outputs.artifact-id }}')) "$name : the build job does not export the artifact-id"
            $upload = Get-BuildStep $name 'Upload package artifact'
            Assert-True ($upload.Contains('id: upload') -and $upload.Contains('retention-days: 1') -and $upload.Contains('name: clink-package')) "$name : the upload step has no id, lost its retention or its name"
            # The cleanup job: exactly this header, exactly one step.
            Assert-True ($jobs.Cleanup.Length -gt 0) "$name : there is no cleanup job"
            $cleanup = Remove-YamlComments $jobs.Cleanup
            $header = $cleanup.Substring(0, $cleanup.IndexOf('      - name: '))
            Assert-True ($header -match "(?s)\A\s*cleanup:\n    needs: \[build, publish\]\n    if: always\(\) && needs\.build\.result == 'success'\n    runs-on: ubuntu-latest\n    permissions:\n      actions: write\n    steps:\n\z") "$name : the cleanup job header is not exactly needs/if/runs-on/permissions(actions: write)/steps: [$header]"
            Assert-True ($cleanup -notmatch '(?m)^\s*(?:- )?uses:' -and $cleanup -notmatch 'secrets\.' -and $cleanup -notmatch 'tools/' -and $cleanup -notmatch '(?i)checkout' -and $cleanup -notmatch '(?m)^    env:' -and $cleanup -notmatch 'contents:') "$name : the cleanup job has a uses:, a secret, tools/ code, a checkout, a job env or a contents permission"
            $steps = @(Get-Steps $jobs.Cleanup)
            Assert-True ($steps.Count -eq 1 -and (Get-StepTitle $steps[0]) -ceq 'Delete the build artifact') "$name : the cleanup job does not have exactly the one delete step"
            $step = $steps[0]
            Assert-True ($step -match '(?m)^        continue-on-error: true\s*$' -and $step -match '(?m)^        shell: pwsh\s*$') "$name : the delete step is not non-fatal pwsh"
            Assert-True ($step.Contains('ARTIFACT_ID: ${{ needs.build.outputs.artifact_id }}') -and $step.Contains('GH_TOKEN: ${{ github.token }}')) "$name : the delete step environment is wrong"
            $run = Get-StepRunText $step
            foreach ($needle in "-cnotmatch '\A[0-9]+\z'", 'gh api "repos/$env:GITHUB_REPOSITORY/actions/artifacts/$id"', "[string]`$artifact.name -cne 'clink-package'", '[string]$artifact.workflow_run.id -cne [string]$env:GITHUB_RUN_ID', 'gh api -X DELETE "repos/$env:GITHUB_REPOSITORY/actions/artifacts/$id"', '$global:LASTEXITCODE = 0') {
                Assert-True ($run.Contains($needle)) "$name : the delete step lacks: $needle"
            }
            Assert-True ($run.IndexOf('workflow_run.id') -lt $run.IndexOf('-X DELETE')) "$name : the ownership check is not before the DELETE"
            # Behaviour against a fake gh (GET answers with an artifact description; DELETE is recorded).
            $script:ghLines = New-Object System.Collections.ArrayList
            $script:ghArtifact = $null; $script:ghGetFail = $false; $script:ghDeleteFail = $false
            function gh {
                [void]$script:ghLines.Add(($args -join ' '))
                if ($args -contains 'DELETE') { if ($script:ghDeleteFail) { $global:LASTEXITCODE = 1; return 'gh: HTTP 403' }; $global:LASTEXITCODE = 0; return }
                if ($script:ghGetFail) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
                $global:LASTEXITCODE = 0; $script:ghArtifact
            }
            $saved = @{}; foreach ($n in 'ARTIFACT_ID', 'GITHUB_REPOSITORY', 'GITHUB_RUN_ID') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
            $deletes = { @($script:ghLines | Where-Object { $_ -like '*-X DELETE*' }) }
            try {
                $env:GITHUB_REPOSITORY = 'o/r'; $env:GITHUB_RUN_ID = '4242'
                # This run's clink-package: read, then deleted by id.
                $env:ARTIFACT_ID = '12345'
                $script:ghArtifact = '{"id":12345,"name":"clink-package","workflow_run":{"id":4242,"head_sha":"x"}}'
                $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                Assert-True (@($script:ghLines) -ccontains 'api repos/o/r/actions/artifacts/12345' -and @(& $deletes) -ccontains 'api -X DELETE repos/o/r/actions/artifacts/12345' -and $global:LASTEXITCODE -eq 0) "$name : this run's artifact was not deleted by id: $($script:ghLines -join ';')"
                Assert-True ($script:ghLines.IndexOf('api repos/o/r/actions/artifacts/12345') -lt $script:ghLines.IndexOf('api -X DELETE repos/o/r/actions/artifacts/12345')) "$name : DELETE came before the ownership GET"
                # Another run's artifact, another name, no workflow_run, junk JSON, a failed GET: a warning, no DELETE.
                foreach ($case in @(@('another run', '{"id":12345,"name":"clink-package","workflow_run":{"id":4241}}'), @('another name', '{"id":12345,"name":"clink-packagex","workflow_run":{"id":4242}}'),
                        @('name case', '{"id":12345,"name":"Clink-Package","workflow_run":{"id":4242}}'), @('no workflow_run', '{"id":12345,"name":"clink-package"}'), @('junk', 'not json'))) {
                    $script:ghLines.Clear(); $script:ghArtifact = $case[1]
                    $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                    Assert-True (@(& $deletes).Count -eq 0 -and $o -match '::warning::' -and $global:LASTEXITCODE -eq 0) "$name : $($case[0]): a DELETE was sent or no warning was given: $o"
                }
                $script:ghLines.Clear(); $script:ghGetFail = $true
                $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                Assert-True (@(& $deletes).Count -eq 0 -and $o -match '::warning::Could not read' -and $global:LASTEXITCODE -eq 0) "$name : a failed GET led to a DELETE or no warning: $o"
                $script:ghGetFail = $false
                # A missing run id must not make "no workflow_run" equal to it.
                $script:ghLines.Clear(); $env:GITHUB_RUN_ID = ''; $script:ghArtifact = '{"id":12345,"name":"clink-package"}'
                $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                Assert-True ($script:ghLines.Count -eq 0 -and $o -match '::warning::') "$name : an empty GITHUB_RUN_ID reached gh"
                $env:GITHUB_RUN_ID = '4242'
                # A malformed or empty id never reaches gh.
                foreach ($badId in '12345/../../releases/1', '', '12 34', '-1') {
                    $script:ghLines.Clear(); $env:ARTIFACT_ID = $badId
                    $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                    Assert-True ($script:ghLines.Count -eq 0 -and $o -match '::warning::') "$name : the id [$badId] reached gh or gave no warning"
                }
                # A failing DELETE only warns, exit code 0.
                $env:ARTIFACT_ID = '777'; $script:ghArtifact = '{"id":777,"name":"clink-package","workflow_run":{"id":4242}}'; $script:ghDeleteFail = $true
                $o = (. ([scriptblock]::Create($run)) 6>&1 | Out-String)
                Assert-True ($o -match '::warning::Could not delete' -and $global:LASTEXITCODE -eq 0) "$name : a failed delete was not a warning with exit code 0: $o"
            } finally { Remove-Item Function:\gh -ErrorAction SilentlyContinue; foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) } }
        }
        Assert-True ((Remove-YamlComments (Read-Workflow 'build.yml')) -notmatch 'actions:\s*write') 'build.yml has actions: write'
    }

    Invoke-Test 'L7: Assert-FeedR2Endpoint accepts only https://<32 hex>.r2.cloudflarestorage.com (or .eu.), never prints the value, and both "Check bucket secrets" steps call it' {
        $id = 'a1b2c3d4e5f60718293a4b5c6d7e8f90'
        Assert-FeedR2Endpoint -Value "https://$id.r2.cloudflarestorage.com"
        Assert-FeedR2Endpoint -Value "https://$id.eu.r2.cloudflarestorage.com"
        $secretish = 'SECRETVALUE'
        foreach ($bad in '', $null, "https://$id.r2.cloudflarestorage.com/", "https://$id.r2.cloudflarestorage.com/bucket", "https://$id.r2.cloudflarestorage.com:443", "http://$id.r2.cloudflarestorage.com", "https://$($id.ToUpperInvariant()).r2.cloudflarestorage.com",
            "https://$($id.Substring(1)).r2.cloudflarestorage.com", "https://${id}0.r2.cloudflarestorage.com", "https://evil.example/$id.r2.cloudflarestorage.com", "https://$id.r2.cloudflarestorage.com.evil.example", "https://$id.r2.cloudflarestorage.com@evil.example",
            "https://u@$id.r2.cloudflarestorage.com", "https://$id.r2.cloudflarestorage.com?x=1", "https://$id.r2.cloudflarestorage.com`n", " https://$id.r2.cloudflarestorage.com", "https://$id.us.r2.cloudflarestorage.com", "https://$id.r2.cloudflarestorage.com;x", "https://$secretish.r2.cloudflarestorage.com") {
            $msg = $null
            try { Assert-FeedR2Endpoint -Value $bad } catch { $msg = $_.Exception.Message }
            Assert-True ($null -ne $msg -and $msg -match 'is not https://') "[$bad] was accepted"
            Assert-True (-not $msg.Contains($secretish) -and -not $msg.Contains($id)) 'the message printed the value'
        }
        foreach ($pair in @(@('release.yml', 'R2_STABLE_ENDPOINT'), @('release-dev.yml', 'R2_DEV_ENDPOINT'))) {
            $run = Get-StepRunText (Get-PublishStep $pair[0] 'Check bucket secrets')
            Assert-True ($run.Contains("Assert-FeedR2Endpoint -Value `$env:R2_ENDPOINT -Name '$($pair[1])'") -and $run.Contains('. ./tools/lib/feed-publish-lib.ps1')) "$($pair[0]) : the endpoint is not validated"
            Assert-True ($run -notmatch 'Write-(Output|Host)[^\n]*\$env:R2_ENDPOINT' -and $run -notmatch 'throw[^\n]*\$env:R2_ENDPOINT') "$($pair[0]) : the endpoint may be printed"
        }
    }

    Invoke-Test 'I-3: client-source guard - recursive Clink\**\*.cs scan (Tests, bin*, obj* excluded), lenient only while a DEFINITION of VerifyGitHubReleaseAsync exists, the HttpClient x = new( form counted' {
        . (Join-Path $toolsDir 'lib\client-guard-lib.ps1')
        $guard = Join-Path $PSScriptRoot 'client-source-guard.tests.ps1'
        $legacyDef = "    private async Task<UpdateInfo?> VerifyGitHubReleaseAsync(UpdateInfo info, CancellationToken ct) { return null; }"
        function New-GuardTree {
            # A scratch repo: Clink\Services\Updater.cs (given text), Clink\MainWindow.xaml.cs, plus extra relative files.
            param([string]$Updater, [hashtable]$Extra = @{})
            $root = Get-ScratchFolder ('guard-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'Clink\Services') | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $root 'Clink\Services\Updater.cs'), $Updater, $utf8)
            [System.IO.File]::WriteAllText((Join-Path $root 'Clink\MainWindow.xaml.cs'), "class MainWindow { }`n", $utf8)
            foreach ($k in $Extra.Keys) {
                $p = Join-Path $root $k
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null
                [System.IO.File]::WriteAllText($p, [string]$Extra[$k], $utf8)
            }
            $root
        }
        $clean = "class Updater { static readonly HttpClient s_feedHttp = new(FeedHandler); static readonly SocketsHttpHandler FeedHandler = new() { }; }`n"
        $cases = @(
            @{ Name = 'clean tree'; Updater = $clean; Extra = @{}; Strict = ''; Code = 0; Pattern = '2 passed' },
            @{ Name = 'legacy definition + GitHub mention: skipped'; Updater = "$legacyDef`n// github`n"; Extra = @{}; Strict = ''; Code = 0; Pattern = 'skipped: .*mention' },
            @{ Name = 'legacy definition + CLINK_GUARD_STRICT=1: fails'; Updater = "$legacyDef`n"; Extra = @{}; Strict = '1'; Code = 1; Pattern = 'mentions GitHub' },
            @{ Name = 'only a MENTION of the name (cref, call): strict'; Updater = "/// <see cref=`"VerifyGitHubReleaseAsync`"/>`nvar x = await VerifyGitHubReleaseAsync(a, ct);`n"; Extra = @{}; Strict = ''; Code = 1; Pattern = 'mentions GitHub' },
            @{ Name = 'nested folder mention: caught'; Updater = $clean; Extra = @{ 'Clink\Engine\Deep\Thing.cs' = "// see GitHub`n" }; Strict = ''; Code = 1; Pattern = 'Engine\\Deep\\Thing\.cs:1' },
            @{ Name = 'MainWindow mention: caught'; Updater = $clean; Extra = @{ 'Clink\MainWindow.xaml.cs' = "// api.github.com`n" }; Strict = ''; Code = 1; Pattern = 'MainWindow\.xaml\.cs:1' },
            @{ Name = 'Tests, bin, obj_sc ignored'; Updater = $clean; Extra = @{ 'Clink\Tests\T.cs' = "// github`n"; 'Clink\bin\x\G.cs' = "// github`n"; 'Clink\obj_sc\Release\G.cs' = "// github`n" }; Strict = ''; Code = 0; Pattern = '2 passed' },
            @{ Name = 'two HttpClients, target-typed: fails'; Updater = "class U { static readonly HttpClient a = new(); static readonly HttpClient b = new(h) { }; }`n"; Extra = @{}; Strict = ''; Code = 1; Pattern = 'creates 2 HttpClient' },
            @{ Name = 'two HttpClients while legacy: skipped'; Updater = "$legacyDef`nclass U { static readonly HttpClient a = new(); HttpClient? b = new(h); }`n"; Extra = @{}; Strict = ''; Code = 0; Pattern = 'skipped: Updater\.cs creates 2 HttpClient' },
            @{ Name = 'two handlers: fails'; Updater = "class U { SocketsHttpHandler a = new(); var b = new SocketsHttpHandler(); }`n"; Extra = @{}; Strict = ''; Code = 1; Pattern = 'creates 2 SocketsHttpHandler' }
        )
        foreach ($case in $cases) {
            $root = New-GuardTree -Updater $case.Updater -Extra $case.Extra
            $r = Invoke-Child $guard @('-Root', $root) @{ CLINK_GUARD_STRICT = $case.Strict }
            Assert-True ($r.Code -eq $case.Code -and $r.Text -match $case.Pattern) "$($case.Name): exit $($r.Code), expected $($case.Code) and /$($case.Pattern)/: $($r.Text)"
            Assert-True ((Test-ClientLegacyUpdatePath -Root $root) -eq ($case.Updater.Contains('private async Task<UpdateInfo?> VerifyGitHubReleaseAsync('))) "$($case.Name): Test-ClientLegacyUpdatePath is wrong"
        }
        $treeRoot = New-GuardTree -Updater $clean -Extra @{ 'Clink\Tests\T.cs' = 'x'; 'Clink\Tests2\T.cs' = 'x'; 'Clink\obj\G.cs' = 'x'; 'Clink\binder\B.cs' = 'x'; 'Clink\bin-fx\G.cs' = 'x' }
        $clinkDir = (Resolve-Path -LiteralPath (Join-Path $treeRoot 'Clink')).Path
        $rels = @(Get-ClientGuardSourceFiles -Root $treeRoot | ForEach-Object { $_.Substring($clinkDir.Length + 1) })
        Assert-True (($rels -join ',') -ceq 'binder\B.cs,MainWindow.xaml.cs,Services\Updater.cs,Tests2\T.cs') "the scanned set is wrong: $($rels -join ',')"
    }

    Invoke-Test 'I-3: build.yml always sets CLINK_GUARD_STRICT=1 as a plain env entry in the script-test step' {
        $build = Read-Workflow 'build.yml'
        $at = $build.IndexOf('      - name: Script tests (PowerShell 7)')
        Assert-True ($at -gt 0) 'build.yml has no script-test step'
        $next = $build.IndexOf("`n      - name:", $at + 10)
        $step = if ($next -gt 0) { $build.Substring($at, $next - $at) } else { $build.Substring($at) }
        $run = Get-StepRunText $step
        # Only a plain env entry is accepted: no condition can make the guard lenient.
        Assert-True ($step -match "(?m)^          CLINK_GUARD_STRICT: '1'\s*$") 'the script-test step does not set the plain env entry CLINK_GUARD_STRICT: ''1'''
        Assert-True (-not $run.Contains('CLINK_GUARD_STRICT') -and -not $run.Contains('Test-ClientLegacyUpdatePath')) 'the script-test run text still makes the strict switch conditional'
        $guardText = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'client-source-guard.tests.ps1'))
        Assert-True ($guardText.Contains("lib\client-guard-lib.ps1") -and $guardText.Contains('Test-ClientLegacyDefinition') -and $guardText.Contains("`$env:CLINK_GUARD_STRICT -ceq '1'")) 'the guard does not use the shared rules or ignores CLINK_GUARD_STRICT'
        # The switch evaluated on this tree: strict exactly when no definition is left (the guard decides the same way).
        . (Join-Path $toolsDir 'lib\client-guard-lib.ps1')
        $legacyHere = Test-ClientLegacyUpdatePath -Root $repo
        $defined = @(Get-ClientGuardSourceFiles -Root $repo | Where-Object { [System.IO.File]::ReadAllText($_) -match 'Task<[^>]*>\s+VerifyGitHubReleaseAsync\s*\(' }).Count -gt 0
        Assert-True ($legacyHere -eq $defined) "Test-ClientLegacyUpdatePath says $legacyHere for this tree, a plain search says $defined"
        Write-Host "      (this tree: legacy GitHub update path $(if ($legacyHere) { 'still defined' } else { 'gone' }); CI always runs the guard strict)"
    }

    Invoke-Test 'docs: RELEASING.md carries the repair-round rules (feed-off red job, immutable variable, nosniff rule, lock list, keys without the clipboard, fork settings, stable-only first run, PR titles, actions budget)' {
        $doc = [System.IO.File]::ReadAllText((Join-Path $repo 'docs\RELEASING.md'))
        foreach ($needle in 'every\s+push to `dev` shows a red `publish` job', 'CLINK_IMMUTABLE_RELEASES', '`unavailable`', 'X-Content-Type-Options', 'Modify Response Header', 'nosniff',
            'npx wrangler r2 bucket lock list clink-stable', '_lock-probe', 'indefinite retention\) and for 30 days', "Get-Content -Raw 'E:\\keys\\clink-stable.pem' \| gh secret set CLINK_FEED_STABLE_SIGNING_KEY", 'exFAT',
            'Send secrets to workflows from fork pull requests', 'Send write tokens', 'Allow forking of private repositories', 'run for the FIRST time on `v0.4.2` itself', 'generate-notes', 'merge-base', 'annotated-tag|ANNOTATED', 'make_latest',
            'read the\s+titles of the pull requests', 'Actions budget', 'about 150 MB per run', '`Delete the build artifact`', 'do NOT hand-publish', 'release.s OWN `.sha256` assets', 'Find the embedded floor seeds', 'ClinkMinStable', 'ClinkMinDev',
            '\\Ahttps://\[0-9a-f\]\{32\}') {
            Assert-True ($doc -match $needle) "RELEASING.md lacks: $needle"
        }
        Assert-True ($doc -notmatch 'Set-Clipboard') 'RELEASING.md still recommends Set-Clipboard'
        Assert-True ($doc -notmatch '-NotesUrl https') 'the dry-run example still passes -NotesUrl'
        Assert-True ($doc -notmatch 'publishes? only the GitHub release|publishing the GitHub release without manifest assets|and only then publishes feed-less') 'a passage still describes feed-less publishing'
        Assert-True ($doc -notmatch '--if-none-match "\*"` on a scratch key') 'the old staging probe is still described'
    }

    Invoke-Test 'docs (round 2): no notes reuse, the cleanup job, re-run rules (L-1), seeds need S6 (I-1), variables checklist (I-2), Bot Fight Mode / WAF (I-4)' {
        $doc = [System.IO.File]::ReadAllText((Join-Path $repo 'docs\RELEASING.md'))
        foreach ($needle in 'Nothing already published is ever read back', 'plant `releases/<next tag>/notes.txt`',
            '"Re-run failed jobs" fails at `Download package artifact`', '"Re-run all jobs" rebuilds', 'publish a NEWER version',
            'holds `actions: write` and NOTHING else', '`workflow_run.id`', 'needs\.build\.result == .success.',
            'only once slice S6 lands', '`ClinkMinStable` and `ClinkMinDev`', '`AssemblyMetadata`', 'verifying its signature against `feed.json` before it counts',
            '`author.login` is exactly `github-actions\[bot\]`', 'Assert-FeedFloorMatchesSeed',
            'Settings > Secrets and\s+variables > Actions > Variables\*\* shows what you expect', 'Bot Fight Mode', 'WAF skip rule',
            'fails `Read feed configuration`, before the draft exists', 'PREFLIGHT over EVERY versioned object', 'Both workflows have three jobs') {
            Assert-True ($doc -match $needle) "RELEASING.md lacks: $needle"
        }
        foreach ($gone in 'A re-run reuses the public notes', 'reuse of public\s+notes', 'logs no reuse', 'deletes it as its last step', 'only for the last step', 'Then re-run, or publish', 'The \*\*Variables\*\* tab stays EMPTY') {
            Assert-True ($doc -notmatch $gone) "RELEASING.md still says: $gone"
        }
        $long = @($doc -split "`n" | Where-Object { $_.Length -gt 400 -and $_ -notmatch '^\|' })
        Assert-True ($long.Count -eq 0) "RELEASING.md has $($long.Count) prose line(s) over 400 characters: $(($long | ForEach-Object { $_.Substring(0, 60) }) -join ' | ')"
    }

    Invoke-Test 'item 16: the release.yml header says the guards hold unless a writer changes this workflow or tools/' {
        $head = (Read-Workflow 'release.yml') -split "`n" | Select-Object -First 8
        Assert-True (($head -join "`n") -match 'unless a writer changes this workflow or tools/') 'the header comment lacks the caveat'
    }

    # ================================================================== 4. new-feed-key.ps1
    $keyScript = Join-Path $toolsDir 'new-feed-key.ps1'
    $driveRoot = [System.IO.Path]::GetPathRoot($repo)
    $keyDir = Join-Path $driveRoot ('clink-nfk-' + [guid]::NewGuid().ToString('N'))
    $keyDirOk = $false
    try { New-Item -ItemType Directory -Force -Path $keyDir -ErrorAction Stop | Out-Null; $keyDirOk = $true } catch { $keyDirOk = $false }
    function Test-PemKeyPair {
        # The private key in the file signs, the printed public key verifies.
        param([string]$PemPath, [string]$Public)
        $pem = [System.IO.File]::ReadAllText($PemPath)
        Assert-True ($pem -match '\A-----BEGIN PRIVATE KEY-----\n[A-Za-z0-9+/=\n]+-----END PRIVATE KEY-----\n\z') 'the key file is not a PKCS#8 PEM with LF line ends'
        $b64 = ($pem -replace '-----[^-]+-----', '' -replace '\s', '')
        $cngKey = [System.Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($b64), [System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
        $ec = $null
        try {
            $ec = [System.Security.Cryptography.ECDsaCng]::new($cngKey)
            $data = $utf8.GetBytes('probe')
            $sig = [Convert]::ToBase64String($ec.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256))
        } finally { if ($ec) { $ec.Dispose() }; $cngKey.Dispose() }
        Test-FeedSignature -Bytes $data -SigBase64 $sig -Keys @($Public)
    }
    function Get-PrintedPublicKey {
        param([string]$Text)
        $line = @($Text -split "`r?`n" | Where-Object { $_ -match '\A[A-Za-z0-9+/]{100,}={0,2}\z' })
        Assert-True ($line.Count -eq 1) "expected exactly one public key line, found $($line.Count)"
        $line[0]
    }

    foreach ($case in @(@('stable', $false), @('dev', $false), @('stable', $true), @('dev', $true))) {
        $channel = $case[0]; $next = $case[1]
        $label = "new-feed-key: -Channel $channel$(if ($next) { ' -Next' }) writes a working key, prints the public key and the owner summary, and never prints private key material"
        Invoke-Test $label {
            if (-not $keyDirOk) { Write-Host "      (skipped: cannot create a folder under $driveRoot, outside every git work tree)"; [void]$script:skipped.Add($label); return }
            $pemPath = Join-Path $keyDir "clink-$channel$(if ($next) { '-next' }).pem"
            $args2 = @('-Channel', $channel, '-PrivateKeyPath', $pemPath) + $(if ($next) { @('-Next') } else { @() })
            $r = Invoke-Child $keyScript $args2
            Assert-True ($r.Code -eq 0) "exit $($r.Code): $($r.Text)"
            Assert-True (Test-Path -LiteralPath $pemPath -PathType Leaf) 'no key file'
            $pub = Get-PrintedPublicKey $r.Text
            Assert-True (Test-PemKeyPair -PemPath $pemPath -Public $pub) 'the printed public key does not match the private key file'
            $prop = if ($channel -eq 'stable') { 'stableKeys' } else { 'devKeys' }
            $secret = if ($channel -eq 'stable') { 'CLINK_FEED_STABLE_SIGNING_KEY' } else { 'CLINK_FEED_DEV_SIGNING_KEY' }
            $otherSecret = if ($channel -eq 'stable') { 'CLINK_FEED_DEV_SIGNING_KEY' } else { 'CLINK_FEED_STABLE_SIGNING_KEY' }
            $t = $r.Text
            Assert-True ($t.Contains("Clink/Assets/feed.json") -and $t.Contains("'$prop'") -and $t -match 'PUBLIC value') "the summary does not say the public key goes into $prop of feed.json"
            Assert-True ($t -notmatch 'Environments|environment secret|feed-stable|feed-dev') 'the summary still talks about GitHub environments'
            Assert-True ($t -match 'Back up the \.pem files to the USB drive') 'the summary lacks the USB backup line'
            Assert-True ($t.Contains($otherSecret) -eq $false) "the summary names the other channel's secret $otherSecret"
            if ($next) {
                Assert-True ($t.Contains('NEXT') -and $t -match 'NO repo secret' -and $t.Contains($secret) -eq $false) 'a next key must say that no repo secret gets it and must not name the secret'
                Assert-True ($t -match 'Rotation') 'a next key does not point at the rotation section'
            } else {
                Assert-True ($t.Contains("repo secret $secret") -and $t.Contains("Get-Content -Raw '$pemPath' | gh secret set $secret")) "the summary does not name the repo secret $secret and the gh secret set command"
                Assert-True ($t -notmatch 'Set-Clipboard') 'the summary still uses the clipboard'
                # icacls only where the drive has ACLs (L5): NTFS/ReFS get the command, any other file system gets the explanation.
                $fmt = ''; try { $fmt = [string](New-Object System.IO.DriveInfo ([System.IO.Path]::GetPathRoot($pemPath))).DriveFormat } catch { }
                if ($fmt -ieq 'NTFS' -or $fmt -ieq 'ReFS') { Assert-True ($t -match 'icacls') 'an NTFS drive got no icacls advice' }
                else { Assert-True ($t -notmatch 'icacls "' -and $t -match 'no\s+file permissions') "a $fmt drive got icacls advice or no explanation" }
                Assert-True ($t -match '-Next') 'a normal key does not suggest making a next key'
            }
            # No private key material anywhere in the output: neither a body line of the PEM nor the whole body, nor the file Base64.
            $pem = [System.IO.File]::ReadAllText($pemPath)
            $body = ($pem -replace '-----[^-]+-----', '' -replace '\s', '')
            Assert-True (-not $t.Contains($body)) 'the whole private key body was printed'
            foreach ($line in ($pem -split "`n")) { if ($line.Length -ge 20 -and $line -notmatch '^-----') { Assert-True (-not $t.Contains($line)) 'a private key line was printed' } }
            Assert-True (-not $t.Contains([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pemPath)))) 'the Base64 of the key file was printed'
            # Refuses to overwrite.
            $again = Invoke-Child $keyScript $args2
            Assert-True ($again.Code -ne 0 -and $again.Text -match 'refusing to overwrite') "a second run overwrote or did not refuse: $($again.Text)"
        }
    }

    Invoke-Test 'new-feed-key: refuses a path inside a git work tree and needs a channel (no key is written)' {
        $inRepo = Join-Path $repo 'tools\never-created-nfk.pem'
        $r = Invoke-Child $keyScript @('-Channel', 'dev', '-PrivateKeyPath', $inRepo, '-Next')
        Assert-True ($r.Code -ne 0 -and $r.Text -match 'git work tree') "a path inside the repository was accepted: $($r.Text)"
        Assert-True (-not (Test-Path -LiteralPath $inRepo)) 'a key file was written inside the repository'
        $text = [System.IO.File]::ReadAllText($keyScript)
        Assert-True ($text -match '\[switch\]\$Next') 'new-feed-key.ps1 has no -Next switch'
        Assert-True ($text -cnotmatch '(?<![A-Z_])CLINK_FEED_SIGNING_KEY') 'new-feed-key.ps1 names the bare CLINK_FEED_SIGNING_KEY'
        foreach ($needle in 'CLINK_FEED_STABLE_SIGNING_KEY', 'CLINK_FEED_DEV_SIGNING_KEY', 'USB drive', 'devKeys', 'stableKeys') { Assert-True ($text.Contains($needle)) "new-feed-key.ps1 lacks $needle" }
        # Windows PowerShell 5.1 must work (the owner does not use pwsh 7): no 7-only syntax.
        Assert-True ($text -notmatch '\?\?|\?\.|&&|\|\|\s*\{' -and $text -notmatch '#requires -Version 7') 'new-feed-key.ps1 uses syntax that Windows PowerShell 5.1 cannot parse'
        $bytes = [System.IO.File]::ReadAllBytes($keyScript)
        Assert-True (-not ($bytes | Where-Object { $_ -gt 127 })) 'new-feed-key.ps1 is not plain ASCII'
    }
} finally {
    if ($keyDirOk) { Remove-Item -LiteralPath $keyDir -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed, $($script:skipped.Count) skipped"
foreach ($s in $script:skipped) { Write-Host "SKIPPED: $s" }
if ($script:failed -gt 0) { exit 1 }
exit 0
