# Release-time rule for CHANGELOG.md (the "What's new" window). Dot-sourced by tools\release.ps1 and tools\tests\release-changelog.tests.ps1.
#
# A stable release X.Y.Z needs a "## X.Y.Z" section: the "## Unreleased" heading is renamed to "## X.Y.Z - YYYY-MM-DD" (the entry
# lines, and so the catalog keys, stay as they are). Without an Unreleased section a "## X.Y.Z" section must already exist.
# Anything else throws: a release must not go out with a changelog the app would show nothing for. A pre-release version
# (1.2.3-beta.1) is not in the changelog and leaves the text alone.

function Update-ChangelogForRelease {
    param(
        [Parameter(Mandatory = $true)] [string]$Text,
        [Parameter(Mandatory = $true)] [string]$Version,
        [Parameter(Mandatory = $true)] [string]$Date
    )
    if ($Version -notmatch '^\d+\.\d+\.\d+\z') {
        return [pscustomobject]@{ Text = $Text; Changed = $false }
    }
    if ($Date -notmatch '^\d{4}-\d{2}-\d{2}\z') { throw "'$Date' is not a date like 2026-01-31." }

    $headings = @([regex]::Matches($Text, '(?m)^## (?<h>[^\r\n]*?)[ \t]*(?=\r?$)'))
    $unreleased = @($headings | Where-Object { $_.Groups['h'].Value -ceq 'Unreleased' })
    $own = @($headings | Where-Object { $_.Groups['h'].Value -cmatch ('^' + [regex]::Escape($Version) + '( - \d{4}-\d{2}-\d{2})?$') })
    $other = @($headings | Where-Object { $_.Groups['h'].Value -cmatch '^(\d+\.\d+\.\d+)( - \d{4}-\d{2}-\d{2})?$' })

    if ($own.Count -gt 0 -and $unreleased.Count -gt 0) {
        throw "CHANGELOG.md has both '## Unreleased' and '## $Version'. Fold the Unreleased entries into '## $Version' (or delete the section) first."
    }
    if ($own.Count -gt 0) { return [pscustomobject]@{ Text = $Text; Changed = $false } }
    if ($unreleased.Count -ne 1) {
        throw "CHANGELOG.md has no '## Unreleased' and no '## $Version' section: add the release's entries (with their 16 translations) first."
    }

    # the section needs at least one entry
    $start = $unreleased[0].Index + $unreleased[0].Length
    $next = $headings | Where-Object { $_.Index -gt $unreleased[0].Index } | Select-Object -First 1
    $end = if ($next) { $next.Index } else { $Text.Length }
    $body = $Text.Substring($start, $end - $start)
    if ($body -notmatch '(?m)^- \S') { throw "The '## Unreleased' section of CHANGELOG.md has no entries; there is nothing to release." }

    # the release must be newer than every released section
    foreach ($h in $other) {
        $v = [version]([regex]::Match($h.Groups['h'].Value, '^\d+\.\d+\.\d+').Value)
        if ($v -ge [version]$Version) { throw "CHANGELOG.md already has a section for $v, which is not older than $Version." }
    }

    $new = $Text.Substring(0, $unreleased[0].Index) + "## $Version - $Date" + $Text.Substring($start)
    return [pscustomobject]@{ Text = $new; Changed = $true }
}

# release.ps1 lets CHANGELOG.md differ from HEAD only by its own rename (a rerun after a failed commit or tag), so hand edits
# never ship unreviewed in the release commit. $HeadText is CHANGELOG.md at HEAD, or $null when it is not committed.
# Returns $null when the working text may be released, otherwise the reason it may not. Line ends are not compared.
function Get-ChangelogEditProblem {
    param(
        [AllowNull()] $HeadText,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string]$WorkingText,
        [Parameter(Mandatory = $true)] [string]$Version
    )
    if ($null -eq $HeadText) { return 'CHANGELOG.md is not committed. Commit it (and its translations) before releasing.' }
    $head = ([string]$HeadText).Replace("`r`n", "`n")
    $work = $WorkingText.Replace("`r`n", "`n")
    if ($work -ceq $head) { return $null }

    $refusal = "CHANGELOG.md has uncommitted changes other than release.ps1's '## Unreleased' -> '## $Version - date' rename. Commit or revert them before releasing."
    $m = [regex]::Match($work, '(?m)^## ' + [regex]::Escape($Version) + ' - (?<d>\d{4}-\d{2}-\d{2})[ \t]*$')
    if (-not $m.Success) { return $refusal }
    try { $expected = Update-ChangelogForRelease -Text $head -Version $Version -Date $m.Groups['d'].Value } catch { return $refusal }
    if ($expected.Changed -and $expected.Text -ceq $work) { return $null }
    return $refusal
}

# The UTF-8 text of $Path at HEAD in the repository at $RepoRoot (git's raw blob bytes, so no console decoding), or $null when
# HEAD has no such file or git fails.
function Get-GitFileAtHead {
    param(
        [Parameter(Mandatory = $true)] [string]$RepoRoot,
        [Parameter(Mandatory = $true)] [string]$Path
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'git'
    $psi.Arguments = 'cat-file blob "HEAD:' + $Path.Replace('"', '') + '"'
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $errTask = $p.StandardError.ReadToEndAsync()
        $buffer = New-Object System.IO.MemoryStream
        $p.StandardOutput.BaseStream.CopyTo($buffer)
        $p.WaitForExit()
        [void]$errTask.Result
        if ($p.ExitCode -ne 0) { return $null }
        return (New-Object System.Text.UTF8Encoding $false).GetString($buffer.ToArray()).TrimStart([char]0xFEFF)
    } finally {
        $p.Dispose()
    }
}
