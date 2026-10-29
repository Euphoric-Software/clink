# Release notes for the update feed (dot-source this file). Works on Windows PowerShell 5.1 and PowerShell 7, no modules.
# The notes are ONE plain UTF-8 text file, notes.txt, published write-once next to the binaries:
#   stable: <prefix>/releases/<tag>/notes.txt        dev: <prefix>/dev/<tag>/notes.txt
# The manifest's notesUrl points at it (tools/make-manifest.ps1) and the app opens that URL in the browser only when it is https and under
# the manifest's base. Plain text: no BOM, at most 64 KB, and no control character except LF and TAB (CR is removed, so line ends are LF).

$script:FeedNotesMaxBytes = 65536
$script:FeedNotesTruncationMark = "`n[Notes cut at 64 KB.]`n"
# Invisible characters: soft hyphen, combining grapheme joiner, Arabic letter mark, Mongolian vowel separator, zero-width and bidi controls,
# invisible math operators, deprecated format controls.
$script:FeedNotesInvisibleClass = '[@00AD@034F@061C@180E@200B-@200F@202A-@202E@2060-@2064@2066-@2069@206A-@206F]'.Replace('@', '\u')   # "@" stands for the escape "\u" (this file stays ASCII)

function ConvertTo-FeedNotesText {
    # Cleans any text into the notes form: control characters other than LF and TAB are removed (CR, NUL, ESC, DEL and the C1 range, BOM
    # and lone surrogates too, plus zero-width U+200B-U+200F, U+2060 and bidi controls U+202A-U+202E, U+2066-U+2069), a CRLF becomes LF, trailing white space is trimmed, and exactly one final LF is added. Over 64 KB of UTF-8
    # the text is cut at a character boundary and a one-line mark is appended; the result is never over the limit. Throws when nothing is left.
    param([AllowNull()] [AllowEmptyString()] [string]$Text)
    $t = [string]$Text
    $t = $t -replace "`r`n", "`n"
    $t = [regex]::Replace($t, '[\u0000-\u0008\u000B-\u001F\u007F-\u009F\uFEFF]', '')
    $t = [regex]::Replace($t, $script:FeedNotesInvisibleClass, '')   # zero-width, bidi and other invisible characters (they could hide a link)
    $t = [regex]::Replace($t, '\uDB40[\uDC00-\uDC7F]', '')   # Unicode tag characters (U+E0000-U+E007F, a surrogate pair)
    $t = [regex]::Replace($t, '[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]', '')
    $t = $t.TrimEnd(" `t`n").TrimStart("`n")
    if ($t.Length -eq 0) { throw 'The release notes are empty after cleaning.' }
    $t += "`n"
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    if ($utf8.GetByteCount($t) -gt $script:FeedNotesMaxBytes) {
        $budget = $script:FeedNotesMaxBytes - $utf8.GetByteCount($script:FeedNotesTruncationMark)
        $bytes = $utf8.GetBytes($t)
        $cut = $budget
        while ($cut -gt 0 -and ($bytes[$cut] -band 0xC0) -eq 0x80) { $cut-- }   # never inside a multi-byte character
        $t = $utf8.GetString($bytes, 0, $cut).TrimEnd(" `t`n") + $script:FeedNotesTruncationMark
    }
    $t
}

function Remove-FeedNotesRepoLinks {
    # The repository is private, so its links mean nothing to a user: drop the generated " in https://github.com/<repo>/pull/N" tail of a
    # line and the whole "Full Changelog" line. Everything else is kept as it is.
    # L3, final pass: ANY other URL of this repository (https://github.com/<owner>/<repo>/..., any case) and every @handle mention is dropped
    # too, wherever it stands in the text: PR titles and GitHub's "by @user" / "New Contributors" lines would otherwise become public on R2.
    # (A mention is "@" plus a GitHub login that is not part of a word or an e-mail address.)
    # L-3: every spelling of a link to this repository counts, case-insensitively: https://, http:// or no scheme at all, an optional
    # "www.", github.com/<owner>/<repo> with an optional ".git", then an optional path, query or fragment up to white space (the punctuation
    # that ends a sentence or a bracket is not part of it). The name must end there: not followed by a word character or "-", nor by "."
    # plus one (so "clink-extras" and "clink.github.io" are other repositories, while the "." that ends a sentence is not part of the URL).
    # A line that a removal changed is tidied: empty () and <>, runs of spaces, a space before closing punctuation, and leading spaces the
    # line did not have are removed. Lines that did not change are left exactly as they were.
    param([AllowNull()] [AllowEmptyString()] [string]$Text, [Parameter(Mandatory)] [string]$Repository)
    if ($Repository -cnotmatch '\A[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\z') { throw "Repository '$Repository' is not owner/name." }
    $ic = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    $repoUrl = '(?<![\w.-])(?:https?://)?(?:www\.)?github\.com/' + [regex]::Escape($Repository) + '(?:\.git)?(?![\w-]|\.[\w-])(?:[/?#]\S*?(?=[.,;:!?)\]>]*(?:\s|$)))?'
    $mention = '(?<![\w@./+-])@[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?![\w-])'
    $lines = foreach ($line in (([string]$Text) -split "`r?`n")) {
        if ([regex]::IsMatch($line, '^\W*Full Changelog\W*:?\s*' + $repoUrl, $ic)) { continue }
        if ($line -match '^\s*[*-]\s*@[A-Za-z0-9-]+\s+made their first contribution' -or $line -match '^#+\s*New Contributors\s*$') { continue }
        $l = $line
        if ([regex]::IsMatch($l, $repoUrl, $ic)) {
            $l = [regex]::Replace($l, '\s*\b(?:in|at|see|from)\s+' + $repoUrl, '', $ic)   # "in <url>" / "see <url>" go with their URL
            $l = [regex]::Replace($l, $repoUrl, '', $ic)
        }
        if ($l -match $mention) {
            $l = [regex]::Replace($l, '\s*\b(?:by|and|from|thanks to|cc)\s+' + $mention, '', 'IgnoreCase')
            $l = [regex]::Replace($l, $mention, '')
        }
        if ($l -cne $line) {
            $lead = [regex]::Match($line, '\A[ \t]*').Value
            $l = [regex]::Replace($l, '\(\s*\)|<\s*>', '')
            $l = [regex]::Replace($l, '(?<=\S)[ \t]{2,}', ' ')
            $l = [regex]::Replace($l, '[ \t]+(?=[.,;:!?](?:\s|$))', '')
            $l = $lead + $l.TrimStart(" `t")
        }
        $l.TrimEnd(" `t")
    }
    $lines -join "`n"
}

function Assert-FeedNotesNoMojibake {
    # M2. The notes come from a native command's stdout: when the console code page was not UTF-8 the text can arrive as U+FFFD (a lost
    # character) or as the classic UTF-8-read-as-Windows-1252 sequence (U+00E2 U+20AC: a-circumflex, euro sign). Either means the notes are damaged,
    # and they are published write-once, so the step must fail instead.
    param([AllowNull()] [AllowEmptyString()] [string]$Text)
    $t = [string]$Text
    if ($t.Contains([string][char]0xFFFD)) { throw 'The generated release notes contain U+FFFD (a replacement character): the text was decoded with the wrong encoding.' }
    if ($t.Contains(([string][char]0x00E2) + ([string][char]0x20AC))) { throw 'The generated release notes contain the sequence a-circumflex + euro sign (UTF-8 text decoded as Windows-1252, mojibake).' }
}

function Get-FeedDevNotesText {
    # The fixed dev text: the same string as the dev release body (Get-ExpectedDevReleaseBody in release-verify-lib.ps1, which the verify
    # steps compare the body with; a script test keeps the two equal). It lives here too so that the dev notes step needs no gh library.
    param([Parameter(Mandatory)] [string]$Commit)
    "Dev build of $Commit from the dev branch. Clink installs it only with Dev Build turned on (click the version in the footer)."
}

function Test-FeedNotesBytes {
    # Returns '' when the bytes are a valid notes.txt, otherwise the reason: not empty, at most 64 KB, strict UTF-8 without a BOM, no control
    # character other than LF and TAB (so no CR), no lone surrogate, and a final LF.
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]]$Bytes)
    if ($Bytes.Length -eq 0) { return 'the notes file is empty' }
    if ($Bytes.Length -gt $script:FeedNotesMaxBytes) { return "the notes file is $($Bytes.Length) bytes, over the $($script:FeedNotesMaxBytes) byte limit" }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { return 'the notes file starts with a BOM' }
    $strict = New-Object System.Text.UTF8Encoding($false, $true)
    try { $text = $strict.GetString($Bytes) } catch { return 'the notes file is not valid UTF-8' }
    if ($text -cmatch '[\u0000-\u0008\u000B-\u001F\u007F-\u009F\uFEFF]') { return 'the notes file holds a control character other than LF and TAB (CR included)' }
    if ($text -cmatch $script:FeedNotesInvisibleClass -or $text -cmatch '\uDB40[\uDC00-\uDC7F]') { return 'the notes file holds a zero-width, bidi or other invisible character' }
    if ($text[$text.Length - 1] -ne "`n") { return 'the notes file does not end with a line feed' }
    ''
}

function Assert-FeedNotesFile {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The notes file is missing: $Path (run the notes step first)." }
    $problem = Test-FeedNotesBytes -Bytes ([System.IO.File]::ReadAllBytes($Path))
    if ($problem) { throw "Invalid notes file ${Path}: $problem." }
}

function Write-FeedNotesFile {
    # Cleans the text and writes it as UTF-8 without a BOM; returns the path. The written file passes Assert-FeedNotesFile.
    param([AllowNull()] [AllowEmptyString()] [string]$Text, [Parameter(Mandatory)] [string]$Path)
    $clean = ConvertTo-FeedNotesText -Text $Text
    [System.IO.File]::WriteAllBytes($Path, (New-Object System.Text.UTF8Encoding($false)).GetBytes($clean))
    Assert-FeedNotesFile -Path $Path
    $Path
}
