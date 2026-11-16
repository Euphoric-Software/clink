<#
  Standalone test of tools\lib\changelog-lib.ps1 (the release-time CHANGELOG.md rule) and of its use in tools\release.ps1
  (offline; no Pester). Exits 0 when all pass, 1 otherwise.
  Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\release-changelog.tests.ps1
  Works on Windows PowerShell 5.1 and PowerShell 7. Writes nothing.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repoRoot 'tools\lib\changelog-lib.ps1')

$script:Passed = 0
$script:Failed = 0
function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail)
    if ($Condition) { $script:Passed++; Write-Host "PASS  $Name" }
    else { $script:Failed++; Write-Host "FAIL  $Name  $Detail" }
}
function Get-Error {
    param([scriptblock]$Action)
    try { & $Action | Out-Null; return $null } catch { return $_.Exception.Message }
}

$date = '2026-12-01'
$sample = "# Changelog`nPreface | with {things}`n`n## Unreleased`n- New thing`n- Another`n`n## 0.4.8`n- Signed`n"

# the Unreleased heading becomes the release, entries untouched
$r = Update-ChangelogForRelease -Text $sample -Version '0.4.9' -Date $date
Assert-That 'Unreleased is renamed with the date' ($r.Changed -and $r.Text -ceq $sample.Replace('## Unreleased', '## 0.4.9 - 2026-12-01')) $r.Text
Assert-That 'no Unreleased heading is left' ($r.Text -notmatch '(?m)^## Unreleased')

# CRLF text keeps its line ends
$crlf = $sample.Replace("`n", "`r`n")
$r = Update-ChangelogForRelease -Text $crlf -Version '0.4.9' -Date $date
Assert-That 'CRLF is kept' ($r.Text -ceq $crlf.Replace('## Unreleased', '## 0.4.9 - 2026-12-01')) ($r.Text -replace "`r", '\r')

# the release section is already there: unchanged
$done = "## 0.4.9 - 2026-11-30`n- A`n## 0.4.8`n- B`n"
$r = Update-ChangelogForRelease -Text $done -Version '0.4.9' -Date $date
Assert-That 'an existing dated section is left alone' (-not $r.Changed -and $r.Text -ceq $done)
$r = Update-ChangelogForRelease -Text "## 0.4.9`n- A`n" -Version '0.4.9' -Date $date
Assert-That 'an existing undated section is left alone' (-not $r.Changed)

# refusals
$e = Get-Error { Update-ChangelogForRelease -Text "## 0.4.8`n- B`n" -Version '0.4.9' -Date $date }
Assert-That 'neither Unreleased nor the version: refused' ($e -match 'no ''## Unreleased'' and no ''## 0.4.9''') "$e"
$e = Get-Error { Update-ChangelogForRelease -Text "## Unreleased`n- A`n## 0.4.9`n- B`n" -Version '0.4.9' -Date $date }
Assert-That 'both Unreleased and the version: refused' ($e -match 'both') "$e"
$e = Get-Error { Update-ChangelogForRelease -Text "## Unreleased`n`n## 0.4.8`n- B`n" -Version '0.4.9' -Date $date }
Assert-That 'an empty Unreleased: refused' ($e -match 'no entries') "$e"
$e = Get-Error { Update-ChangelogForRelease -Text "## Unreleased`n- A`n## 1.0.0`n- B`n" -Version '0.4.9' -Date $date }
Assert-That 'a release older than a section already there: refused' ($e -match 'not older') "$e"
$e = Get-Error { Update-ChangelogForRelease -Text $sample -Version '0.4.9' -Date 'tomorrow' }
Assert-That 'a bad date: refused' ($e -match 'not a date') "$e"

# a pre-release is not in the changelog
$r = Update-ChangelogForRelease -Text "## 0.4.8`n- B`n" -Version '0.5.0-beta.1' -Date $date
Assert-That 'a pre-release version leaves the text alone' (-not $r.Changed)

# a trailing newline does not slip past the anchors
$r = Update-ChangelogForRelease -Text $sample -Version "0.4.9`n" -Date $date
Assert-That 'a version with a trailing newline is not taken as a release' (-not $r.Changed -and $r.Text -ceq $sample) ($r.Text -replace "`n", '\n')
$e = Get-Error { Update-ChangelogForRelease -Text $sample -Version '0.4.9' -Date "$date`n" }
Assert-That 'a date with a trailing newline: refused' ($e -match 'not a date') "$e"

# CHANGELOG.md may differ from HEAD only by release.ps1's own rename (a rerun after a failed commit or tag)
$renamed = (Update-ChangelogForRelease -Text $sample -Version '0.4.9' -Date '2026-11-30').Text
Assert-That 'edit check: unchanged from HEAD is fine' ($null -eq (Get-ChangelogEditProblem -HeadText $sample -WorkingText $sample -Version '0.4.9'))
Assert-That 'edit check: only the rename (from an earlier day) is fine' ($null -eq (Get-ChangelogEditProblem -HeadText $sample -WorkingText $renamed -Version '0.4.9'))
Assert-That 'edit check: CRLF working copy of the rename is fine' ($null -eq (Get-ChangelogEditProblem -HeadText $sample -WorkingText $renamed.Replace("`n", "`r`n") -Version '0.4.9'))
Assert-That 'edit check: a pre-release with an unchanged file is fine' ($null -eq (Get-ChangelogEditProblem -HeadText $sample -WorkingText $sample -Version '0.5.0-beta.1'))
$p = Get-ChangelogEditProblem -HeadText $null -WorkingText $sample -Version '0.4.9'
Assert-That 'edit check: a CHANGELOG.md not in HEAD (untracked) is refused' ($p -match 'not committed') "$p"
$p = Get-ChangelogEditProblem -HeadText $sample -WorkingText $sample.Replace('- Another', "- Another`n- Hand edit") -Version '0.4.9'
Assert-That 'edit check: a hand edit is refused' ($p -match 'uncommitted changes') "$p"
$p = Get-ChangelogEditProblem -HeadText $sample -WorkingText $renamed.Replace('- Another', '- Another, edited') -Version '0.4.9'
Assert-That 'edit check: the rename plus a hand edit is refused' ($p -match 'uncommitted changes') "$p"
$p = Get-ChangelogEditProblem -HeadText $sample -WorkingText $renamed -Version '0.4.10'
Assert-That 'edit check: a rename for another version is refused' ($p -match 'uncommitted changes') "$p"
$p = Get-ChangelogEditProblem -HeadText $sample -WorkingText $renamed -Version '0.5.0-beta.1'
Assert-That 'edit check: a changed file with a pre-release is refused' ($p -match 'uncommitted changes') "$p"
$p = Get-ChangelogEditProblem -HeadText "## 0.4.8`n- B`n" -WorkingText "## 0.4.9 - 2026-11-30`n- A`n## 0.4.8`n- B`n" -Version '0.4.9'
Assert-That 'edit check: a section added by hand is refused' ($p -match 'uncommitted changes') "$p"

# reading a file at HEAD (git's raw bytes)
$attributes = Get-GitFileAtHead -RepoRoot $repoRoot -Path '.gitattributes'
Assert-That 'Get-GitFileAtHead reads a committed file' ($attributes -match 'eol=lf') "$attributes"
Assert-That 'Get-GitFileAtHead returns null for a file not in HEAD' ($null -eq (Get-GitFileAtHead -RepoRoot $repoRoot -Path 'no-such-file-in-head.md'))

# release.ps1 really uses it and stages the file
$release = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'tools\release.ps1'))
Assert-That 'release.ps1 loads the changelog lib' ($release -match 'lib\\changelog-lib\.ps1')
Assert-That 'release.ps1 calls Update-ChangelogForRelease' ($release -match 'Update-ChangelogForRelease')
Assert-That 'release.ps1 stages CHANGELOG.md' ($release -match "'add', '--', \`$projRel, \`$changelogRel")
Assert-That 'release.ps1 refuses CHANGELOG.md edits other than its own' ($release -match '(?m)^\s*\$changelogProblem = Get-ChangelogEditProblem -HeadText \(Get-GitFileAtHead -RepoRoot \$root -Path \$changelogRel\)' -and $release -match '(?m)^\s*if \(\$changelogProblem\) \{ throw \$changelogProblem \}')
$changelogWrite = $release.IndexOf('WriteAllText($changelog,')
$projWrite = $release.IndexOf('WriteAllText($proj,')
Assert-That 'release.ps1 writes CHANGELOG.md before Clink.csproj' ($changelogWrite -ge 0 -and $projWrite -gt $changelogWrite) "$changelogWrite / $projWrite"
$versionRule = [regex]::Match($release, "\`$Version -notmatch '(?<p>[^']+)'").Groups['p'].Value
Assert-That 'release.ps1 accepts 1.2.3 and 1.2.3-beta.1' ($versionRule -and '1.2.3' -match $versionRule -and '1.2.3-beta.1' -match $versionRule) $versionRule
Assert-That 'release.ps1 refuses a version with a trailing newline' ($versionRule -and "1.2.3`n" -notmatch $versionRule) $versionRule

# the shipped file can be released: with "## Unreleased" as a new version; once released (no Unreleased), as its newest
# version, which is left alone. Checked on the shipped file and on a copy that has already been released.
function Test-ReleasableChangelog {
    param([string]$Text)
    try {
        if ($Text -cmatch '(?m)^## Unreleased[ \t]*\r?$') {
            $r = Update-ChangelogForRelease -Text $Text -Version '99.0.0' -Date $date
            return [bool]($r.Changed -and $r.Text -match '(?m)^## 99\.0\.0 - 2026-12-01\r?$')
        }
        $versions = @([regex]::Matches($Text, '(?m)^## (?<v>\d+\.\d+\.\d+)(?: - \d{4}-\d{2}-\d{2})?[ \t]*\r?$') | ForEach-Object { [version]$_.Groups['v'].Value } | Sort-Object -Descending)
        if ($versions.Count -eq 0) { return $false }
        $r = Update-ChangelogForRelease -Text $Text -Version $versions[0].ToString() -Date $date
        return [bool](-not $r.Changed -and $r.Text -ceq $Text)
    } catch {
        Write-Host "      $($_.Exception.Message)"
        return $false
    }
}
$shipped = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'CHANGELOG.md'))
Assert-That 'the shipped CHANGELOG.md can be released' (Test-ReleasableChangelog $shipped)
$shippedReleased = if ($shipped -cmatch '(?m)^## Unreleased[ \t]*\r?$') { (Update-ChangelogForRelease -Text $shipped -Version '99.0.0' -Date $date).Text } else { $shipped }
Assert-That 'the shipped CHANGELOG.md still passes once released' ($shippedReleased -notmatch '(?m)^## Unreleased' -and (Test-ReleasableChangelog $shippedReleased))
Assert-That 'a released changelog passes' (Test-ReleasableChangelog "## 0.4.9 - 2026-11-30`n- A`n`n## 0.4.8`n- B`n")
Assert-That 'a changelog with no sections fails' (-not (Test-ReleasableChangelog "# Changelog`n"))

Write-Host "release-changelog: $script:Passed passed, $script:Failed failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0
