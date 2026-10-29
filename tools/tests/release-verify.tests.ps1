# Tests for tools/lib/release-verify-lib.ps1 and its use in release.yml / release-dev.yml: the checks of a GitHub release by ID (adoption of
# another release, author, name, body, commit, immutability, assets), the tag checks (404, annotated tags, retries) and the delete-on-failure
# wrapper, all against a FAKE gh function; plus the real step scripts of both workflows run against the same fake. Standalone: exits 0 when
# everything passed, 1 otherwise. Windows PowerShell 5.1 and pwsh 7, no modules. Nothing here touches the network or GitHub.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$workflowDir = Join-Path $repo '.github\workflows'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-relverifytests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$libPath = Join-Path $toolsDir 'lib\release-verify-lib.ps1'
. $libPath
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

# ---- the package folder the release is compared with ---------------------------------------------------------------------------------
$sha = 'a' * 40
$script:random = New-Object System.Random
function New-PackageFolder {
    param([string]$Name, [string]$Channel = '', [switch]$WithManifest)
    $dir = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($n in 'Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'Clink-Setup.exe', 'Clink-Setup.exe.sha256', 'THIRD-PARTY-NOTICES.txt') {
        $bytes = New-Object byte[] 300
        $script:random.NextBytes($bytes)
        [System.IO.File]::WriteAllBytes((Join-Path $dir $n), $bytes)
    }
    if ($WithManifest) { foreach ($n in "$Channel.json", "$Channel.json.sig") { [System.IO.File]::WriteAllBytes((Join-Path $dir $n), $utf8.GetBytes("manifest $n")) } }
    $dir
}
function New-ReleaseObject {
    # A release as GitHub returns it for the package folder. Mutate edits the object.
    param([string]$Dir, [string]$Channel, [string]$Version, [string]$State = 'draft', [string]$Id = '123', [scriptblock]$Mutate)
    $assets = foreach ($f in (Get-ChildItem -LiteralPath $Dir -File | Sort-Object Name)) {
        if ($f.Name -in 'package.json') { continue }
        [pscustomobject]@{ name = $f.Name; state = 'uploaded'; size = $f.Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $rel = [pscustomobject]@{
        id = [int64]$Id; draft = ($State -eq 'draft'); immutable = ($State -eq 'published'); prerelease = ($Channel -eq 'dev'); tag_name = "v$Version"
        name = $(if ($Channel -eq 'dev') { "Clink v$Version (dev)" } else { "Clink v$Version" }); author = [pscustomobject]@{ login = 'github-actions[bot]' }
        target_commitish = $sha; body = $(if ($Channel -eq 'dev') { Get-ExpectedDevReleaseBody -Commit $sha } else { 'Generated notes' }); assets = @($assets)
    }
    if ($Mutate) { & $Mutate $rel }
    $rel
}
function Get-TestAsset {
    param($Release, [string]$Name)
    $found = @($Release.assets | Where-Object { $_.name -ceq $Name })
    if ($found.Count -ne 1) { throw "test setup: no single asset $Name" }
    $found[0]
}
function Invoke-Match {
    param($Release, [string]$Dir, [string]$Channel, [string]$Version, [string]$State = 'draft', [string[]]$Ids = @('123'), [string]$Commit = $sha)
    Assert-ReleaseMatches -Release $Release -ReleaseId '123' -State $State -Channel $Channel -Version $Version -PackageDir $Dir -IdsWithTag $Ids -Commit $Commit
}

# ---- the fake gh -------------------------------------------------------------------------------------------------------------------
$script:ghCalls = New-Object System.Collections.ArrayList
$script:ghRelease = $null            # parsed release returned by GET releases/<id>
$script:ghListing = @()              # tsv lines of the paginated listing
$script:ghTags = @{}                 # tag name -> @{ Code; Json } for git/ref/tags/<name>; a list of such answers is consumed in order
$script:ghTagObjects = @{}           # sha -> json for git/tags/<sha>
$script:ghDeleteCode = 0
function Reset-FakeGh {
    $script:ghCalls.Clear(); $script:ghRelease = $null; $script:ghListing = @(); $script:ghTags = @{}; $script:ghTagObjects = @{}; $script:ghDeleteCode = 0
}
function gh {
    $a = @($args | ForEach-Object { [string]$_ })
    [void]$script:ghCalls.Add($a -join ' ')
    $global:LASTEXITCODE = 0
    if ($a[0] -ne 'api') { $global:LASTEXITCODE = 99; return 'fake gh: unsupported' }
    if ($a[1] -eq '-X' -and $a[2] -eq 'DELETE') {
        if ($script:ghDeleteCode -ne 0) { $global:LASTEXITCODE = $script:ghDeleteCode; return 'gh: delete refused (HTTP 403)' }
        return
    }
    $path = $a[1]
    if ($path -ceq 'repos/o/r/releases?per_page=100') { return $script:ghListing }
    if ($path -cmatch '\Arepos/o/r/releases/[0-9]+\z') {
        if ($null -eq $script:ghRelease) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
        return ($script:ghRelease | ConvertTo-Json -Depth 6)
    }
    if ($path -cmatch '\Arepos/o/r/git/ref/tags/(?<tag>.+)\z') {
        $name = $Matches['tag']
        $answer = $script:ghTags[$name]
        if ($answer -is [System.Collections.Queue]) { $answer = if ($answer.Count -gt 1) { $answer.Dequeue() } else { $answer.Peek() } }
        if ($null -eq $answer) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
        if ($answer.Code -ne 0) { $global:LASTEXITCODE = $answer.Code; return $answer.Json }
        return $answer.Json
    }
    if ($path -cmatch '\Arepos/o/r/git/tags/(?<sha>[0-9a-f]{40})\z') {
        $json = $script:ghTagObjects[$Matches['sha']]
        if ($null -eq $json) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
        return $json
    }
    $global:LASTEXITCODE = 1
    return "fake gh: unknown path $path"
}
function New-TagRef { param([string]$Type, [string]$Sha) (@{ ref = 'refs/tags/x'; object = @{ type = $Type; sha = $Sha } } | ConvertTo-Json -Depth 4) }
function Get-StepRunBlock {
    # The run: script of the step called Name in the job of a workflow (the job starts at "  <job>:").
    param([string]$Workflow, [string]$Job, [string]$Name)
    $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $Workflow))
    $jobAt = $text.IndexOf("`n  ${Job}:")
    Assert-True ($jobAt -ge 0) "$Workflow has no job $Job"
    $stepAt = $text.IndexOf("      - name: $Name`n", $jobAt)
    Assert-True ($stepAt -ge 0) "$Workflow job $Job has no step '$Name'"
    $next = $text.IndexOf("`n      - name:", $stepAt + 10)
    $step = if ($next -gt 0) { $text.Substring($stepAt, $next - $stepAt) } else { $text.Substring($stepAt) }
    $runAt = $step.IndexOf("        run: |`n")
    Assert-True ($runAt -ge 0) "step '$Name' has no run block"
    $lines = @($step.Substring($runAt + "        run: |`n".Length) -split "`n" | Where-Object { $_ -match '^          ' -or $_.Trim() -eq '' })
    # drop trailing comment lines of the next step
    $body = New-Object System.Collections.Generic.List[string]
    foreach ($l in ($step.Substring($runAt + "        run: |`n".Length) -split "`n")) { if ($l -match '^          ') { $body.Add($l.Substring(10)) } elseif ($l.Trim() -eq '') { $body.Add('') } else { break } }
    ($body -join "`n")
}

try {
    # =============================================================== the pure check
    $stableDir = New-PackageFolder 'stable-nomanifest'
    $stableMfDir = New-PackageFolder 'stable-manifest' -Channel stable -WithManifest
    $devDir = New-PackageFolder 'dev-nomanifest'
    $devMfDir = New-PackageFolder 'dev-manifest' -Channel dev -WithManifest

    Invoke-Test 'Get-RequiredReleaseAssets: the seven names, plus the channel manifest and its signature only when this build made them' {
        Assert-True (@(Get-RequiredReleaseAssets -Channel stable -PackageDir $stableDir).Count -eq 7) 'not seven names without a manifest'
        $with = @(Get-RequiredReleaseAssets -Channel stable -PackageDir $stableMfDir)
        Assert-True ($with.Count -eq 9 -and $with -ccontains 'stable.json' -and $with -ccontains 'stable.json.sig') 'the stable manifest names are missing'
        Assert-True (@(Get-RequiredReleaseAssets -Channel dev -PackageDir $stableMfDir).Count -eq 7) 'the stable manifest counted for the dev channel'
        $devWith = @(Get-RequiredReleaseAssets -Channel dev -PackageDir $devMfDir)
        Assert-True ($devWith -ccontains 'dev.json' -and $devWith -ccontains 'dev.json.sig') 'the dev manifest names are missing'
    }

    Invoke-Test 'Assert-ReleaseMatches: a correct draft and a correct published release pass (stable and dev, with and without a manifest)' {
        foreach ($c in @(@('stable', '0.4.2', $stableDir), @('stable', '0.4.2', $stableMfDir), @('dev', '0.4.2-dev.7', $devDir), @('dev', '0.4.2-dev.7', $devMfDir))) {
            foreach ($state in 'draft', 'published') {
                Invoke-Match (New-ReleaseObject -Dir $c[2] -Channel $c[0] -Version $c[1] -State $state) $c[2] $c[0] $c[1] $state
            }
        }
    }

    Invoke-Test 'Assert-ReleaseMatches: another release adopted or created (ids of the tag, the id read back, the author, the name, the body)' {
        $rel = { param($d, $ch, $v, $m) New-ReleaseObject -Dir $d -Channel $ch -Version $v -Mutate $m }
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2') $stableDir 'stable' '0.4.2' 'draft' @() } 'carried by 0 release' 'no release with the tag listed'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2') $stableDir 'stable' '0.4.2' 'draft' @('123', '124') } 'carried by 2 release' 'a second release with the tag'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2') $stableDir 'stable' '0.4.2' 'draft' @('999') } 'expected exactly one' 'another release has the tag'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.id = [int64]124 }) $stableDir 'stable' '0.4.2' } 'has id' 'the release read back has another id'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.author.login = 'some-user' }) $stableDir 'stable' '0.4.2' } 'not by github-actions\[bot\]' 'another author'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.author.login = 'GitHub-Actions[bot]' }) $stableDir 'stable' '0.4.2' } 'not by github-actions\[bot\]' 'author in another case'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.author = $null }) $stableDir 'stable' '0.4.2' } 'not by github-actions\[bot\]' 'no author'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.name = 'Clink v0.4.2 (hacked)' }) $stableDir 'stable' '0.4.2' } "expected 'Clink v0\.4\.2'" 'another name'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' { param($r) $r.name = 'Clink v0.4.2-dev.7' }) $devDir 'dev' '0.4.2-dev.7' } 'expected .Clink v0\.4\.2-dev\.7 \(dev\).' 'a dev release named like a stable one'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' { param($r) $r.body = 'Dev build of someone else' }) $devDir 'dev' '0.4.2-dev.7' } 'unexpected body' 'another dev body'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' { param($r) $r.body = $null }) $devDir 'dev' '0.4.2-dev.7' } 'unexpected body' 'no dev body'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.tag_name = 'v0.4.3' }) $stableDir 'stable' '0.4.2' } "expected v0\.4\.2" 'another tag'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' { param($r) $r.tag_name = 'V0.4.2' }) $stableDir 'stable' '0.4.2' } "expected v0\.4\.2" 'tag in another case'
    }

    Invoke-Test 'Assert-ReleaseMatches: draft or published state, immutability, prerelease flag, the built commit' {
        $rel = { param($d, $ch, $v, $s, $m) New-ReleaseObject -Dir $d -Channel $ch -Version $v -State $s -Mutate $m }
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'draft' { param($r) $r.draft = $false }) $stableDir 'stable' '0.4.2' 'draft' } 'not a draft' 'a draft that is not'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.draft = $true }) $stableDir 'stable' '0.4.2' 'published' } 'still a draft' 'a published release that is a draft'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.immutable = $false }) $stableDir 'stable' '0.4.2' 'published' } 'not immutable' 'a mutable published release'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.PSObject.Properties.Remove('immutable') }) $stableDir 'stable' '0.4.2' 'published' } 'not immutable' 'no immutable field'
        Assert-Throws { Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'draft' { param($r) $r.prerelease = $true }) $stableDir 'stable' '0.4.2' } 'prerelease' 'a stable release flagged prerelease'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' 'draft' { param($r) $r.prerelease = $false }) $devDir 'dev' '0.4.2-dev.7' } 'not flagged as a prerelease' 'a dev release that is a normal release'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' 'draft' { param($r) $r.target_commitish = ('b' * 40) }) $devDir 'dev' '0.4.2-dev.7' } 'not at the built commit' 'a draft at another commit'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' 'draft' { param($r) $r.target_commitish = 'dev' }) $devDir 'dev' '0.4.2-dev.7' } 'not at the built commit' 'a target that is a branch name'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' 'draft' { param($r) $r.target_commitish = $sha.ToUpperInvariant() }) $devDir 'dev' '0.4.2-dev.7' } 'not at the built commit' 'the commit in another case'
        Assert-Throws { Invoke-Match (& $rel $devDir 'dev' '0.4.2-dev.7' 'draft' $null) $devDir 'dev' '0.4.2-dev.7' 'draft' @('123') '' } 'built commit is missing' 'no -Commit for dev'
        # Stable never compares target_commitish (the owner's tag decides the commit; Assert-GitTagAtCommit checks it).
        Invoke-Match (& $rel $stableDir 'stable' '0.4.2' 'draft' { param($r) $r.target_commitish = 'main' }) $stableDir 'stable' '0.4.2' 'draft' @('123') ''
    }

    Invoke-Test 'Assert-ReleaseMatches: assets exactly once, uploaded, with the size and digest of the local file, and nothing else' {
        $rel = { param($d, $ch, $v, $m) New-ReleaseObject -Dir $d -Channel $ch -Version $v -Mutate $m }
        foreach ($c in @(@('stable', '0.4.2', $stableMfDir), @('dev', '0.4.2-dev.7', $devMfDir))) {
            $ch = $c[0]; $v = $c[1]; $d = $c[2]
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) (Get-TestAsset $r 'Clink-framework.exe').digest = $null }) $d $ch $v } 'no digest for asset Clink-framework\.exe' "$ch : a missing digest"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) (Get-TestAsset $r 'Clink.exe').digest = 'sha256:' + ('0' * 64) }) $d $ch $v } 'has digest sha256:0+ on GitHub' "$ch : a wrong digest"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) (Get-TestAsset $r 'Clink.exe').digest = (Get-TestAsset $r 'Clink.exe').digest.ToUpperInvariant() }) $d $ch $v } 'has digest' "$ch : a digest in another case"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) (Get-TestAsset $r 'Clink.exe.sha256').size = 5 }) $d $ch $v } 'bytes on GitHub' "$ch : a wrong size"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) (Get-TestAsset $r 'Clink.exe.sha256').state = 'starter' }) $d $ch $v } "expected 'uploaded'" "$ch : an asset that is not uploaded"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) $r.assets = @($r.assets) + [pscustomobject]@{ name = 'evil.dll'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) } }) $d $ch $v } 'unexpected assets: evil\.dll' "$ch : an extra asset"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) $r.assets = @($r.assets) + [pscustomobject]@{ name = 'clink.exe'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) } }) $d $ch $v } 'unexpected assets: clink\.exe' "$ch : a case variant of a required name"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) $r.assets = @($r.assets | Where-Object { $_.name -cne 'THIRD-PARTY-NOTICES.txt' }) }) $d $ch $v } 'lacks exactly one asset named THIRD-PARTY-NOTICES\.txt' "$ch : a missing asset"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) $r.assets = @($r.assets | Where-Object { $_.name -cne "$ch.json.sig" }) }) $d $ch $v } "lacks exactly one asset named $ch\.json\.sig" "$ch : a missing manifest signature"
            Assert-Throws { Invoke-Match (& $rel $d $ch $v { param($r) $r.assets = @($r.assets) + (Get-TestAsset $r 'Clink.exe') }) $d $ch $v } 'found 2' "$ch : an asset twice"
        }
        # A build without a manifest does not require one, but a manifest asset on the release is then unexpected.
        Assert-Throws { Invoke-Match (New-ReleaseObject -Dir $stableDir -Channel stable -Version '0.4.2' -Mutate { param($r) $r.assets = @($r.assets) + [pscustomobject]@{ name = 'stable.json'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) } }) $stableDir 'stable' '0.4.2' } 'unexpected assets: stable\.json' 'a manifest this build did not make'
    }

    # =============================================================== gh plumbing
    Invoke-Test 'Invoke-GhApi: returns the code and the text, merges stderr, and resets $LASTEXITCODE' {
        Reset-FakeGh
        $r = Invoke-GhApi -Arguments @('repos/o/r/releases/123')
        Assert-True ($r.Code -eq 1 -and $r.Text -match 'HTTP 404') "unexpected answer: $($r.Code) $($r.Text)"
        Assert-True ($global:LASTEXITCODE -eq 0) 'the exit code was not reset'
        Assert-True ($script:ghCalls[0] -ceq 'api repos/o/r/releases/123') "the call was: $($script:ghCalls[0])"
        $script:ghRelease = [pscustomobject]@{ id = 1 }
        $r = Invoke-GhApi -Arguments @('repos/o/r/releases/123')
        Assert-True ($r.Code -eq 0 -and ($r.Text | ConvertFrom-Json).id -eq 1) 'a good answer was not returned as text'
    }

    Invoke-Test 'Get-ReleaseIdsForTag and Assert-ReleaseById: reads by ID, lists every release of the tag, refuses an adopted release' {
        Reset-FakeGh
        $good = New-ReleaseObject -Dir $stableDir -Channel stable -Version '0.4.2'
        $script:ghRelease = $good
        $script:ghListing = @("123`tv0.4.2", "122`tv0.4.1", "100`tv0.4.2-dev.1")
        $ids = @(Get-ReleaseIdsForTag -Repository 'o/r' -Tag 'v0.4.2')
        Assert-True ($ids.Count -eq 1 -and $ids[0] -ceq '123') "ids: $($ids -join ',')"
        Assert-ReleaseById -Repository 'o/r' -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir 6>$null
        # The listing call pages through everything.
        Assert-True (@($script:ghCalls | Where-Object { $_ -like 'api repos/o/r/releases?per_page=100 --paginate --jq*' }).Count -ge 1) 'the listing is not paginated'
        # Another release (a stale draft somebody made) carries the tag as well: refused.
        $script:ghListing = @("123`tv0.4.2", "77`tv0.4.2")
        Assert-Throws { Assert-ReleaseById -Repository 'o/r' -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir 6>$null } 'carried by 2 release' 'a second release of the tag'
        # The only release of the tag is a different one than the ID this run got: refused.
        $script:ghListing = @("77`tv0.4.2")
        Assert-Throws { Assert-ReleaseById -Repository 'o/r' -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir 6>$null } 'expected exactly one' 'the tag belongs to another release'
        # An ID that is not a number is never sent to gh.
        $script:ghCalls.Clear()
        Assert-Throws { Assert-ReleaseById -Repository 'o/r' -ReleaseId '123/../x' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir } 'not numeric' 'a malformed id'
        Assert-Throws { Assert-ReleaseById -Repository 'o/r' -ReleaseId '' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir } 'not numeric' 'an empty id'
        Assert-True ($script:ghCalls.Count -eq 0) 'gh was called with a malformed id'
        # A release that cannot be read fails.
        $script:ghRelease = $null
        Assert-Throws { Assert-ReleaseById -Repository 'o/r' -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir } 'Could not read release 123' 'a failed read'
    }

    # =============================================================== tags
    Invoke-Test 'Assert-GitTagMissing: only HTTP 404 means the tag is not there' {
        Reset-FakeGh
        Assert-GitTagMissing -Repository 'o/r' -Tag 'v0.4.2-dev.7' 6>$null
        $script:ghTags['v0.4.2-dev.7'] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
        Assert-Throws { Assert-GitTagMissing -Repository 'o/r' -Tag 'v0.4.2-dev.7' } 'already exists' 'an existing tag'
        $script:ghTags['v0.4.2-dev.7'] = @{ Code = 1; Json = 'gh: Forbidden (HTTP 403)' }
        Assert-Throws { Assert-GitTagMissing -Repository 'o/r' -Tag 'v0.4.2-dev.7' } 'Could not check whether the tag' 'a 403'
        $script:ghTags['v0.4.2-dev.7'] = @{ Code = 1; Json = 'gh: Server Error (HTTP 500)' }
        Assert-Throws { Assert-GitTagMissing -Repository 'o/r' -Tag 'v0.4.2-dev.7' } 'Could not check whether the tag' 'a 500'
    }

    Invoke-Test 'Get-GitTagTarget / Assert-GitTagAtCommit: lightweight and annotated tags (dereferenced), wrong commit, wrong object type, retries' {
        Reset-FakeGh
        $tagObj = 'c' * 40
        $script:ghTags['v1'] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
        Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v1' -Commit $sha -Attempts 1 6>$null
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v1' -Commit ('b' * 40) -Attempts 1 } 'not at the built commit' 'a lightweight tag at another commit'
        # Annotated: ref -> tag object -> commit.
        $script:ghTags['v2'] = @{ Code = 0; Json = (New-TagRef 'tag' $tagObj) }
        $script:ghTagObjects[$tagObj] = (@{ object = @{ type = 'commit'; sha = $sha } } | ConvertTo-Json -Depth 4)
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v2' -Commit $sha -Attempts 1 } 'annotated tag' 'an annotated tag where a plain one is required'
        Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v2' -Commit $sha -AllowAnnotated -Attempts 1 6>$null
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v2' -Commit ('b' * 40) -AllowAnnotated -Attempts 1 } 'not at the built commit' 'an annotated tag at another commit (dereferenced)'
        # An annotated tag whose object is not a commit.
        $script:ghTagObjects[$tagObj] = (@{ object = @{ type = 'tree'; sha = $sha } } | ConvertTo-Json -Depth 4)
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v2' -Commit $sha -AllowAnnotated -Attempts 1 } "'tree' object" 'a tag of a tree'
        # A tag object that cannot be read, and a malformed sha.
        $script:ghTagObjects.Clear()
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v2' -Commit $sha -AllowAnnotated -Attempts 1 } 'Could not read the annotated tag object' 'an unreadable tag object'
        $script:ghTags['v3'] = @{ Code = 0; Json = (New-TagRef 'tag' 'xyz') }
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v3' -Commit $sha -AllowAnnotated -Attempts 1 } 'malformed sha' 'a malformed object sha'
        # A chain of annotated tags deeper than 5 is refused.
        $script:ghTags['v4'] = @{ Code = 0; Json = (New-TagRef 'tag' $tagObj) }
        $script:ghTagObjects[$tagObj] = (@{ object = @{ type = 'tag'; sha = $tagObj } } | ConvertTo-Json -Depth 4)
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v4' -Commit $sha -AllowAnnotated -Attempts 1 } 'nests more than 5' 'a loop of annotated tags'
        # Retries: two failed reads, then the tag shows (3 attempts).
        Reset-FakeGh
        $q = New-Object System.Collections.Queue
        $q.Enqueue(@{ Code = 1; Json = 'gh: Not Found (HTTP 404)' }); $q.Enqueue(@{ Code = 1; Json = 'gh: Not Found (HTTP 404)' }); $q.Enqueue(@{ Code = 0; Json = (New-TagRef 'commit' $sha) })
        $script:ghTags['late'] = $q
        Assert-GitTagAtCommit -Repository 'o/r' -Tag 'late' -Commit $sha -Attempts 3 -DelaySeconds 0 6>$null
        Assert-True (@($script:ghCalls | Where-Object { $_ -like 'api repos/o/r/git/ref/tags/late' }).Count -eq 3) 'the tag was not read exactly 3 times'
        # Never there: three attempts, then a failure that names the attempts.
        $script:ghCalls.Clear()
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'never' -Commit $sha -Attempts 3 -DelaySeconds 0 } 'after 3 attempt' 'a tag that never shows'
        Assert-True (@($script:ghCalls | Where-Object { $_ -like 'api repos/o/r/git/ref/tags/never' }).Count -eq 3) 'not 3 attempts'
        # A wrong commit is final: no retry.
        $script:ghCalls.Clear(); $script:ghTags['v1'] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
        Assert-Throws { Assert-GitTagAtCommit -Repository 'o/r' -Tag 'v1' -Commit ('b' * 40) -Attempts 3 -DelaySeconds 0 } 'not at the built commit' 'a wrong commit'
        Assert-True ($script:ghCalls.Count -eq 1) 'a wrong commit was retried'
    }

    Invoke-Test 'Invoke-ReleaseCheckOrDelete: a passing check deletes nothing; a failing one deletes the release by ID and fails loudly (also when the delete fails)' {
        Reset-FakeGh
        Invoke-ReleaseCheckOrDelete -Repository 'o/r' -ReleaseId '123' -What 'test' -Check { 'fine' | Out-Null }
        Assert-True (@($script:ghCalls | Where-Object { $_ -like '*DELETE*' }).Count -eq 0) 'a passing check deleted the release'
        $out = $null
        try { Invoke-ReleaseCheckOrDelete -Repository 'o/r' -ReleaseId '123' -What 'after publishing' -Check { throw 'the digest differs' } 6>$null } catch { $out = $_.Exception.Message }
        Assert-True ($null -ne $out -and $out -match 'RELEASE INTEGRITY INCIDENT \(after publishing\): the digest differs' -and $out -match 'Release 123 was DELETED') "no incident message: $out"
        Assert-True (@($script:ghCalls | Where-Object { $_ -ceq 'api -X DELETE repos/o/r/releases/123' }).Count -eq 1) "the release was not deleted by ID once: $($script:ghCalls -join ' | ')"
        $script:ghDeleteCode = 1
        $out = $null
        try { Invoke-ReleaseCheckOrDelete -Repository 'o/r' -ReleaseId '123' -What 'x' -Check { throw 'bad' } 6>$null } catch { $out = $_.Exception.Message }
        Assert-True ($out -match 'could NOT be deleted: delete it by hand') "a failed delete is not reported: $out"
        Assert-True ($global:LASTEXITCODE -eq 0) 'the exit code of the failed delete leaked'
        Assert-True (-not (Remove-ReleaseById -Repository 'o/r' -ReleaseId '123/x')) 'a malformed id was deleted'
    }

    # =============================================================== the workflows' steps, run for real against the fake gh
    $savedEnv = @{}
    foreach ($n in 'GITHUB_REPOSITORY', 'RELEASE_ID', 'VERSION', 'GITHUB_SHA') { $savedEnv[$n] = [Environment]::GetEnvironmentVariable($n) }
    function Invoke-WorkflowStep {
        # Runs the run: script of a publish-job step with the lib path and the package folder swapped for test paths. Returns the error text or $null.
        param([string]$Workflow, [string]$Name, [string]$PackageDir)
        $block = Get-StepRunBlock $Workflow 'publish' $Name
        $block = $block.Replace('. ./tools/lib/release-verify-lib.ps1', ". '$libPath'").Replace('dist/package', $PackageDir.Replace('\', '/'))
        $script:ghCalls.Clear()
        try { . ([scriptblock]::Create($block)) 6>$null | Out-Null; return $null } catch { return $_.Exception.Message }
    }
    try {
        [Environment]::SetEnvironmentVariable('GITHUB_REPOSITORY', 'o/r'); [Environment]::SetEnvironmentVariable('RELEASE_ID', '123'); [Environment]::SetEnvironmentVariable('GITHUB_SHA', $sha)

        foreach ($case in @(@('release.yml', 'stable', '0.4.2', $stableMfDir), @('release-dev.yml', 'dev', '0.4.2-dev.7', $devMfDir))) {
            $wf = $case[0]; $ch = $case[1]; $v = $case[2]; $dir = $case[3]
            [Environment]::SetEnvironmentVariable('VERSION', $v)
            $tag = "v$v"
            $setGood = {
                Reset-FakeGh
                $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v
                $script:ghListing = @("123`t$tag", "9`tv0.0.1")
                if ($ch -eq 'stable') { $script:ghTags[$tag] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) } }
            }

            Invoke-Test "$wf : 'Verify the release assets' runs the by-ID checks (and for dev the tag-404 check); a missing digest, an extra asset, a draft at another commit or an adopted release fail it" {
                & $setGood
                if ($ch -eq 'dev') { $script:ghTags.Clear() }
                Assert-True ($null -eq (Invoke-WorkflowStep $wf 'Verify the release assets' $dir)) 'a correct draft was refused'
                & $setGood; if ($ch -eq 'dev') { $script:ghTags.Clear() }
                (Get-TestAsset $script:ghRelease 'Clink-framework.exe').digest = $null
                Assert-True ((Invoke-WorkflowStep $wf 'Verify the release assets' $dir) -match 'no digest for asset Clink-framework\.exe') 'a missing digest passed'
                & $setGood; if ($ch -eq 'dev') { $script:ghTags.Clear() }
                $script:ghRelease.assets = @($script:ghRelease.assets) + [pscustomobject]@{ name = 'evil.dll'; state = 'uploaded'; size = 1; digest = 'sha256:' + ('1' * 64) }
                Assert-True ((Invoke-WorkflowStep $wf 'Verify the release assets' $dir) -match 'unexpected assets: evil\.dll') 'an extra asset passed'
                & $setGood; if ($ch -eq 'dev') { $script:ghTags.Clear() }
                $script:ghListing = @("123`t$tag", "77`t$tag")
                Assert-True ((Invoke-WorkflowStep $wf 'Verify the release assets' $dir) -match 'carried by 2 release') 'an adopted/second release passed'
                if ($ch -eq 'dev') {
                    & $setGood; $script:ghTags.Clear()
                    $script:ghRelease.target_commitish = ('b' * 40)
                    Assert-True ((Invoke-WorkflowStep $wf 'Verify the release assets' $dir) -match 'not at the built commit') 'a draft at another commit passed'
                    & $setGood; $script:ghTags[$tag] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
                    Assert-True ((Invoke-WorkflowStep $wf 'Verify the release assets' $dir) -match 'already exists') 'an existing dev tag passed'
                }
            }

            Invoke-Test "$wf : 'Re-verify the release before publishing' repeats the checks right before the PATCH and DELETES the release when one fails" {
                & $setGood; if ($ch -eq 'dev') { $script:ghTags.Clear() }
                Assert-True ($null -eq (Invoke-WorkflowStep $wf 'Re-verify the release before publishing' $dir)) 'a correct draft was refused'
                Assert-True (@($script:ghCalls | Where-Object { $_ -like '*DELETE*' }).Count -eq 0) 'a correct draft was deleted'
                & $setGood; if ($ch -eq 'dev') { $script:ghTags.Clear() }
                (Get-TestAsset $script:ghRelease 'Clink.exe').digest = 'sha256:' + ('0' * 64)    # changed between the first check and the publish
                $err = Invoke-WorkflowStep $wf 'Re-verify the release before publishing' $dir
                Assert-True ($err -match 'RELEASE INTEGRITY INCIDENT \(before publishing\)' -and $err -match 'has digest sha256:0+') "no incident: $err"
                Assert-True (@($script:ghCalls | Where-Object { $_ -ceq 'api -X DELETE repos/o/r/releases/123' }).Count -eq 1) 'the release was not deleted'
                if ($ch -eq 'dev') {
                    & $setGood; $script:ghTags[$tag] = @{ Code = 0; Json = (New-TagRef 'commit' ('d' * 40)) }   # a tag appeared meanwhile
                    $err = Invoke-WorkflowStep $wf 'Re-verify the release before publishing' $dir
                    Assert-True ($err -match 'already exists' -and @($script:ghCalls | Where-Object { $_ -like '*DELETE*' }).Count -eq 1) 'a tag that appeared meanwhile did not delete the draft'
                } else {
                    & $setGood; $script:ghTags[$tag] = @{ Code = 0; Json = (New-TagRef 'commit' ('d' * 40)) }   # the owner's tag moved
                    $err = Invoke-WorkflowStep $wf 'Re-verify the release before publishing' $dir
                    Assert-True ($err -match 'not at the built commit' -and @($script:ghCalls | Where-Object { $_ -like '*DELETE*' }).Count -eq 1) 'a moved stable tag did not delete the draft'
                }
            }

            Invoke-Test "$wf : 'Verify the published release' needs a published, immutable release and DELETES it when anything differs" {
                & $setGood
                $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v -State published
                Assert-True ($null -eq (Invoke-WorkflowStep $wf 'Verify the published release' $dir)) 'a correct published release was refused'
                Assert-True (@($script:ghCalls | Where-Object { $_ -like '*DELETE*' }).Count -eq 0) 'a correct published release was deleted'
                $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v -State published -Mutate { param($r) $r.immutable = $false }
                $err = Invoke-WorkflowStep $wf 'Verify the published release' $dir
                Assert-True ($err -match 'RELEASE INTEGRITY INCIDENT \(after publishing\)' -and $err -match 'not immutable') "a mutable release passed: $err"
                Assert-True (@($script:ghCalls | Where-Object { $_ -ceq 'api -X DELETE repos/o/r/releases/123' }).Count -eq 1) 'the mutable release was not deleted'
                $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v -State published -Mutate { param($r) (Get-TestAsset $r 'Clink-Setup.exe').size = 1 }
                Assert-True ((Invoke-WorkflowStep $wf 'Verify the published release' $dir) -match 'bytes on GitHub') 'a changed size passed'
            }
        }

        # The tag checks after publishing: stable has its own tag read inside "Verify the published release", dev has a separate step.
        [Environment]::SetEnvironmentVariable('VERSION', '0.4.2-dev.7')
        Invoke-Test "release-dev.yml : 'Verify the published tag' needs a plain tag at the built commit (3 attempts) and DELETES the release otherwise" {
            Reset-FakeGh
            $script:ghTags['v0.4.2-dev.7'] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
            Assert-True ($null -eq (Invoke-WorkflowStep 'release-dev.yml' 'Verify the published tag' $devMfDir)) 'a correct tag was refused'
            $script:ghTags['v0.4.2-dev.7'] = @{ Code = 0; Json = (New-TagRef 'commit' ('b' * 40)) }
            $err = Invoke-WorkflowStep 'release-dev.yml' 'Verify the published tag' $devMfDir
            Assert-True ($err -match 'RELEASE INTEGRITY INCIDENT' -and $err -match 'not at the built commit') "a tag at another commit passed: $err"
            Assert-True (@($script:ghCalls | Where-Object { $_ -ceq 'api -X DELETE repos/o/r/releases/123' }).Count -eq 1) 'the release was not deleted'
            $script:ghTags['v0.4.2-dev.7'] = @{ Code = 0; Json = (New-TagRef 'tag' ('c' * 40)) }
            $script:ghTagObjects['c' * 40] = (@{ object = @{ type = 'commit'; sha = $sha } } | ConvertTo-Json -Depth 4)
            Assert-True ((Invoke-WorkflowStep 'release-dev.yml' 'Verify the published tag' $devMfDir) -match 'annotated tag') 'an annotated dev tag passed'
            Reset-FakeGh
            $err = Invoke-WorkflowStep 'release-dev.yml' 'Verify the published tag' $devMfDir
            Assert-True ($err -match 'after 3 attempt' -and @($script:ghCalls | Where-Object { $_ -like 'api repos/o/r/git/ref/tags/*' }).Count -eq 3) "a missing tag was not retried 3 times: $err"
        }

        Invoke-Test 'both workflows: the expected dev body of the lib is the body: of the draft step, and the dev name/target are what the lib checks' {
            $dev = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release-dev.yml'))
            $m = [regex]::Match($dev, '(?m)^          body: (?<body>.+)$')
            Assert-True $m.Success 'release-dev.yml has no body: line'
            $yamlBody = $m.Groups['body'].Value.Replace('${{ github.sha }}', $sha)
            Assert-True ($yamlBody -ceq (Get-ExpectedDevReleaseBody -Commit $sha)) "the body in release-dev.yml differs from the lib: [$yamlBody]"
            Assert-True ($dev.Contains('name: Clink v${{ needs.build.outputs.version }} (dev)') -and (Get-ExpectedReleaseName -Channel dev -Version '1.2.3-dev.4') -ceq 'Clink v1.2.3-dev.4 (dev)') 'the dev release name differs from the lib'
            $stable = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release.yml'))
            Assert-True ($stable.Contains('name: Clink v${{ needs.build.outputs.version }}' + "`n") -and (Get-ExpectedReleaseName -Channel stable -Version '1.2.3') -ceq 'Clink v1.2.3') 'the stable release name differs from the lib'
        }
    } finally {
        foreach ($n in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($n, $savedEnv[$n]) }
    }

    # =============================================================== H2: CLINK_IMMUTABLE_RELEASES
    Invoke-Test 'H2: Get-ImmutableReleasesMode - unset, empty and required mean required; unavailable is the only relaxation; anything else throws' {
        foreach ($v in $null, '', 'required') { Assert-True ((Get-ImmutableReleasesMode -Value $v) -ceq 'required') "[$v] is not 'required'" }
        Assert-True ((Get-ImmutableReleasesMode -Value 'unavailable') -ceq 'unavailable') 'unavailable was refused'
        foreach ($bad in 'Unavailable', 'UNAVAILABLE', 'Required', 'true', 'false', 'no', '0', ' unavailable', 'unavailable ', "unavailable`n", 'unavailable;required', 'unavailable,', 'skip', ' ') {
            Assert-Throws { [void](Get-ImmutableReleasesMode -Value $bad) } 'must be unset' "value [$($bad -replace "`n", '\n')]"
        }
    }

    Invoke-Test 'H2: Assert-ReleaseMatches - unavailable skips ONLY the immutable and the missing-digest assertions (each with a warning); a wrong digest, size, draft state or author still fail' {
        $rel = { param($d, $ch, $v, $s, $m) New-ReleaseObject -Dir $d -Channel $ch -Version $v -State $s -Mutate $m }
        $match = { param($r, $mode) Assert-ReleaseMatches -Release $r -ReleaseId '123' -State published -Channel stable -Version '0.4.2' -PackageDir $stableDir -IdsWithTag @('123') -ImmutableMode $mode }
        $mutable = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.immutable = $false }
        foreach ($mode in '', 'required') { Assert-Throws { & $match $mutable $mode } 'not immutable.*CLINK_IMMUTABLE_RELEASES' "mode [$mode] with a mutable release" }
        $out = & $match $mutable 'unavailable' 6>&1
        Assert-True (@($out | Where-Object { "$_" -match '::warning::.*not immutable.*CLINK_IMMUTABLE_RELEASES=unavailable' }).Count -eq 1) "no warning for the mutable release: $out"
        $noField = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.PSObject.Properties.Remove('immutable') }
        $out = & $match $noField 'unavailable' 6>&1
        Assert-True (@($out | Where-Object { "$_" -match '::warning::' }).Count -eq 1) 'an absent immutable field did not warn'
        # Missing digest: fails by default, passes with a warning per asset when unavailable.
        $noDigest = & $rel $stableDir 'stable' '0.4.2' 'draft' { param($r) (Get-TestAsset $r 'Clink-framework.exe').digest = $null }
        Assert-Throws { Assert-ReleaseMatches -Release $noDigest -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir -IdsWithTag @('123') } 'no digest for asset Clink-framework' 'a missing digest by default'
        $out = Assert-ReleaseMatches -Release $noDigest -ReleaseId '123' -State draft -Channel stable -Version '0.4.2' -PackageDir $stableDir -IdsWithTag @('123') -ImmutableMode 'unavailable' 6>&1
        Assert-True (@($out | Where-Object { "$_" -match '::warning::.*no digest for asset Clink-framework\.exe' }).Count -eq 1) "no warning for the missing digest: $out"
        # Everything else stays strict in the relaxed mode.
        $wrong = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) (Get-TestAsset $r 'Clink.exe').digest = 'sha256:' + ('0' * 64) }
        Assert-Throws { & $match $wrong 'unavailable' } 'has digest sha256:0+ on GitHub' 'a WRONG digest in the relaxed mode'
        $size = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) (Get-TestAsset $r 'Clink-Setup.exe').size = 1 }
        Assert-Throws { & $match $size 'unavailable' } 'bytes on GitHub' 'a wrong size in the relaxed mode'
        $draftState = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.draft = $true }
        Assert-Throws { & $match $draftState 'unavailable' } 'still a draft' 'a draft after publishing in the relaxed mode'
        $author = & $rel $stableDir 'stable' '0.4.2' 'published' { param($r) $r.author.login = 'mallory' }
        Assert-Throws { & $match $author 'unavailable' } 'not by github-actions' 'another author in the relaxed mode'
        Assert-Throws { & $match $mutable 'garbage' } 'must be unset' 'a garbage mode'
    }

    Invoke-Test 'H2: the three verify steps of both workflows read CLINK_IMMUTABLE_RELEASES from vars and pass it through env (-ImmutableMode), never inline' {
        foreach ($case in @(@('release.yml', 'stable'), @('release-dev.yml', 'dev'))) {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $case[0]))
            foreach ($title in 'Verify the release assets', 'Re-verify the release before publishing', 'Verify the published release') {
                $at = $text.IndexOf("      - name: $title`n"); Assert-True ($at -gt 0) "$($case[0]) lacks '$title'"
                $next = $text.IndexOf("`n      - name:", $at + 10)
                $step = if ($next -gt 0) { $text.Substring($at, $next - $at) } else { $text.Substring($at) }
                Assert-True ($step.Contains('CLINK_IMMUTABLE_RELEASES: ${{ vars.CLINK_IMMUTABLE_RELEASES }}')) "$($case[0]) '$title' lacks the env entry"
                Assert-True ($step.Contains('-ImmutableMode $env:CLINK_IMMUTABLE_RELEASES')) "$($case[0]) '$title' does not pass the mode to Assert-ReleaseById"
            }
            $code = (($text -split "`r?`n") | Where-Object { $_.TrimStart() -notmatch '^#' }) -join "`n"
            # Three verify steps plus (L-2) "Read feed configuration", which validates the value before the draft exists.
            $readAt = $text.IndexOf("      - name: Read feed configuration`n"); Assert-True ($readAt -gt 0) "$($case[0]) lacks 'Read feed configuration'"
            $readNext = $text.IndexOf("`n      - name:", $readAt + 10)
            Assert-True ($text.Substring($readAt, $readNext - $readAt).Contains('CLINK_IMMUTABLE_RELEASES: ${{ vars.CLINK_IMMUTABLE_RELEASES }}')) "$($case[0]) 'Read feed configuration' lacks the env entry"
            Assert-True ([regex]::Matches($code, '\$\{\{[^}]*CLINK_IMMUTABLE_RELEASES').Count -eq 4) "$($case[0]): the variable is not read exactly four times (once per verify step and in Read feed configuration), all through env:"
            Assert-True ($code -notmatch '(?m)^\s+run:.*\$\{\{') "$($case[0]): an expression inside a run: line"
        }
    }

    function Invoke-WorkflowStepOutput {
        # Like Invoke-WorkflowStep, but returns the error text AND what the step wrote to the host (warnings).
        param([string]$Workflow, [string]$Name, [string]$PackageDir)
        $block = (Get-StepRunBlock $Workflow 'publish' $Name).Replace('. ./tools/lib/release-verify-lib.ps1', ". '$libPath'").Replace('dist/package', $PackageDir.Replace('\', '/'))
        $script:ghCalls.Clear()
        $err = $null; $out = ''
        try { $out = (. ([scriptblock]::Create($block)) 6>&1 | Out-String) } catch { $err = $_.Exception.Message }
        [pscustomobject]@{ Error = $err; Out = $out }
    }
    Invoke-Test 'H2: the real verify steps against a mutable release - unset deletes, required deletes, unavailable passes and warns, garbage fails (before anything is published at the first check)' {
        $savedMode = [Environment]::GetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES')
        try {
            foreach ($case in @(@('release.yml', 'stable', '0.4.2', $stableMfDir), @('release-dev.yml', 'dev', '0.4.2-dev.7', $devMfDir))) {
                $wf = $case[0]; $ch = $case[1]; $v = $case[2]; $dir = $case[3]; $tag = "v$v"
                [Environment]::SetEnvironmentVariable('GITHUB_REPOSITORY', 'o/r'); [Environment]::SetEnvironmentVariable('RELEASE_ID', '123'); [Environment]::SetEnvironmentVariable('GITHUB_SHA', $sha); [Environment]::SetEnvironmentVariable('VERSION', $v)
                $setMutable = {
                    Reset-FakeGh
                    $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v -State published -Mutate { param($r) $r.immutable = $false }
                    $script:ghListing = @("123`t$tag")
                    $script:ghTags[$tag] = @{ Code = 0; Json = (New-TagRef 'commit' $sha) }
                }
                $deletes = { @($script:ghCalls | Where-Object { $_ -ceq 'api -X DELETE repos/o/r/releases/123' }).Count }
                foreach ($unset in $true, $false) {
                    & $setMutable
                    [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $(if ($unset) { $null } else { 'required' }))
                    $r = Invoke-WorkflowStepOutput $wf 'Verify the published release' $dir
                    Assert-True ($r.Error -match 'RELEASE INTEGRITY INCIDENT \(after publishing\)' -and $r.Error -match 'not immutable') "$wf unset=$unset : a mutable release passed: $($r.Error)"
                    Assert-True ((& $deletes) -eq 1) "$wf unset=$unset : the mutable release was not deleted"
                }
                & $setMutable
                [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', 'unavailable')
                $r = Invoke-WorkflowStepOutput $wf 'Verify the published release' $dir
                Assert-True ($null -eq $r.Error) "$wf : unavailable still failed: $($r.Error)"
                Assert-True ($r.Out -match '(?s)::warning::.*not immutable.*unavailable') "$wf : unavailable did not warn: $($r.Out)"
                Assert-True ((& $deletes) -eq 0) "$wf : unavailable deleted the release"
                # unavailable does not excuse anything else (a changed asset size still deletes).
                & $setMutable
                (Get-TestAsset $script:ghRelease 'Clink-Setup.exe').size = 1
                $r = Invoke-WorkflowStepOutput $wf 'Verify the published release' $dir
                Assert-True ($r.Error -match 'bytes on GitHub' -and (& $deletes) -eq 1) "$wf : a wrong size passed or was not deleted in the relaxed mode: $($r.Error)"
                # Garbage: the FIRST check (before anything is uploaded or deleted) refuses it; the later checks refuse it too.
                foreach ($bad in 'Unavailable', 'true', 'unavailable ', 'skip') {
                    & $setMutable
                    [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $bad)
                    $script:ghRelease = New-ReleaseObject -Dir $dir -Channel $ch -Version $v -State draft
                    if ($ch -eq 'dev') { $script:ghTags.Clear() }
                    $r = Invoke-WorkflowStepOutput $wf 'Verify the release assets' $dir
                    Assert-True ($r.Error -match 'CLINK_IMMUTABLE_RELEASES must be unset' -and (& $deletes) -eq 0) "$wf [$bad]: the first check did not refuse garbage without deleting: $($r.Error)"
                    & $setMutable
                    $r = Invoke-WorkflowStepOutput $wf 'Verify the published release' $dir
                    Assert-True ($r.Error -match 'CLINK_IMMUTABLE_RELEASES must be unset') "$wf [$bad]: the post-publish check accepted garbage: $($r.Error)"
                }
            }
        } finally { [Environment]::SetEnvironmentVariable('CLINK_IMMUTABLE_RELEASES', $savedMode) }
    }

    Invoke-Test 'the lib is BOM-free ASCII and every gh call goes through Invoke-GhApi' {
        foreach ($p in $libPath, $PSCommandPath) {
            $bytes = [System.IO.File]::ReadAllBytes($p)
            Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "$p has a BOM"
            Assert-True (-not ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1)) "$p has non-ASCII bytes"
        }
        $lib = [System.IO.File]::ReadAllText($libPath)
        Assert-True ([regex]::Matches($lib, '(?m)^\s*\$lines = @\(& gh api').Count -eq 1 -and ([regex]::Matches($lib, '&\s*gh\b').Count -eq 1)) 'the lib calls gh outside Invoke-GhApi'
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
