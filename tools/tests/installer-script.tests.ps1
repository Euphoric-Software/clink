# Lint for installer\*.iss (no Inno Setup needed). Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\installer-script.tests.ps1
# Exit 0 = all pass, 1 = failures.
#  1. No line may start (after indentation) with '#' followed by a digit, '$' or a quote: ISPP treats any line whose first
#     non-blank character is '#' as a directive, so a Pascal continuation line such as "  #13#10 + ..." breaks ISCC.
#  2. Every #if/#ifdef/#ifndef has a matching #endif (and no #endif/#else/#elif without an opener).
$ErrorActionPreference = 'Stop'
$installerDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'installer'
$files = @(Get-ChildItem -LiteralPath $installerDir -Filter '*.iss' -File)
$failures = New-Object System.Collections.Generic.List[string]
if ($files.Count -eq 0) { $failures.Add("no .iss files found in $installerDir") }

foreach ($file in $files) {
    $depth = 0
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        $lineNo++
        if ($line -cmatch '\A\s*#[0-9$''"]') { $failures.Add("$($file.Name):${lineNo}: line starts with '#' but is not a directive: $line") }
        if ($line -cmatch '\A\s*#(if|ifdef|ifndef)\b') { $depth++ }
        elseif ($line -cmatch '\A\s*#endif\b') {
            $depth--
            if ($depth -lt 0) { $failures.Add("$($file.Name):${lineNo}: #endif without #if"); $depth = 0 }
        }
        elseif ($line -cmatch '\A\s*#(else|elif)\b' -and $depth -eq 0) { $failures.Add("$($file.Name):${lineNo}: #else/#elif without #if") }
    }
    if ($depth -ne 0) { $failures.Add("$($file.Name): $depth unclosed #if/#ifdef/#ifndef") }
}

# Self-check of the rule, so the test itself cannot silently rot.
if (-not ('  #13#10 + x' -cmatch '\A\s*#[0-9$''"]')) { $failures.Add('self-check: rule does not match a #13#10 continuation line') }
if ('  #ifdef X' -cmatch '\A\s*#[0-9$''"]') { $failures.Add('self-check: rule matches a real directive') }

# 3. [UninstallDelete] must not remove the shared updates folder (portable copies use it) or anything under %APPDATA%.
# The only entries allowed under {localappdata}\Clink: exactly what Updater.cs writes into updates\fx (Clink-<version>.exe, its
# .partial download and the .failed/.timeout/.installed markers), then the three folders, only when empty. Compared as "type|name"
# (lower case, forward slashes and doubled backslashes normalised, trailing backslash removed), so a parameter order or case change
# cannot slip through. Anything else under {localappdata}\Clink (any type, any wildcard) fails.
$allowedLocal = @(
    'files|{localappdata}\clink\updates\fx\clink-*.exe',
    'files|{localappdata}\clink\updates\fx\clink-*.exe.partial',
    'files|{localappdata}\clink\updates\fx\clink-*.exe.failed',
    'files|{localappdata}\clink\updates\fx\clink-*.exe.timeout',
    'files|{localappdata}\clink\updates\fx\clink-*.exe.installed',
    'dirifempty|{localappdata}\clink\updates\fx',
    'dirifempty|{localappdata}\clink\updates',
    'dirifempty|{localappdata}\clink'
)
# The whole [UninstallDelete] section is an ALLOWLIST: these three beside the exe plus the eight above. Anything else fails
# (other constants, '..', '{%env}', '{#preprocessor}', {userpf}, {userdocs}, wildcards at the Clink root, ...).
$allowedApp = @(
    'files|{app}\clink.exe.bak',
    'files|{app}\clink.exe.new',
    'files|{app}\clink.exe.failed'
)
$allowedAll = @($allowedApp) + @($allowedLocal)

function Get-InstallerUninstallFailures {
    param([string]$Text, [string]$Label)
    $out = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    $sectionName = ''
    $seen = New-Object System.Collections.Generic.List[string]
    $lineNo = 0
    foreach ($line in ($Text -split "\r?\n")) {
        $lineNo++
        if ($line -cmatch '\A\s*\[(?<name>[^\]]+)\]') {
            $sectionName = $Matches['name']
            $inSection = ($sectionName -ieq 'UninstallDelete')
            if ($sectionName -ieq 'InstallDelete' -or $sectionName -ieq 'UninstallRun') { $out.Add("${Label}:${lineNo}: a [$sectionName] section is not allowed") }
            continue
        }
        if ($sectionName -ieq 'Code' -and $line -imatch '\bDelTree\s*\(' -and $line -notmatch '\A\s*//') { $out.Add("${Label}:${lineNo}: [Code] calls DelTree(: $line") }
        if ($line -match '\A\s*[^;\s].*\bRedirectionGuard\s*=\s*no\b' -or $line -match '\A\s*RedirectionGuard\s*=\s*no\b') {
            $out.Add("${Label}:${lineNo}: RedirectionGuard=no is not allowed")
        }
        if (-not $inSection -or $line -cmatch '\A\s*(;|\z)') { continue }
        # Type and Name are parsed independently, so the order of the parameters does not matter.
        $typeMatch = [regex]::Match($line, '(?:\A|;)\s*Type:\s*(?<v>\w+)', 'IgnoreCase')
        $nameMatch = [regex]::Match($line, '(?:\A|;)\s*Name:\s*"(?<v>[^"]*)"', 'IgnoreCase')
        if (-not $typeMatch.Success -or -not $nameMatch.Success) { $out.Add("${Label}:${lineNo}: [UninstallDelete] entry has no parsable Type and Name: $line"); continue }
        $type = $typeMatch.Groups['v'].Value.ToLowerInvariant()
        $name = ($nameMatch.Groups['v'].Value.ToLowerInvariant() -replace '/', '\' -replace '\\{2,}', '\').TrimEnd('\')
        $seen.Add("$type|$name")
        if ("$type|$name" -notin $allowedAll) {
            $out.Add("${Label}:${lineNo}: [UninstallDelete] '$type $name' is not on the allowlist (the updater's own files or an empty-folder cleanup)")
        }
    }
    foreach ($want in $allowedAll) {
        if (@($seen | Where-Object { $_ -eq $want }).Count -ne 1) { $out.Add("${Label}: [UninstallDelete] needs exactly one entry '$want'") }
    }
    $out.ToArray()
}

foreach ($file in $files) {
    foreach ($f in @(Get-InstallerUninstallFailures -Text ([System.IO.File]::ReadAllText($file.FullName)) -Label $file.Name)) { $failures.Add($f) }
}

# Negative tests: the real script, edited into each broken form, must fail; the real script itself must pass (above).
$realIss = Join-Path $installerDir 'Clink.iss'
if (Test-Path -LiteralPath $realIss) {
    $real = [System.IO.File]::ReadAllText($realIss)
    $pass = @(Get-InstallerUninstallFailures -Text $real -Label 'real')
    if ($pass.Count -ne 0) { $failures.Add("negative-test baseline: the real script reports $($pass.Count) problem(s)") }
    $anchor = 'Type: dirifempty; Name: "{localappdata}\Clink\updates"'
    if (-not $real.Contains($anchor)) { $failures.Add('negative-test setup: anchor line not found in Clink.iss') }
    else {
        $variants = [ordered]@{
            'old filesandordirs updates'      = $real.Replace($anchor, 'Type: filesandordirs; Name: "{localappdata}\Clink\updates"')
            'filesandordirs updates wildcard' = $real.Replace($anchor, "$anchor`r`nType: filesandordirs; Name: `"{localappdata}\Clink\updates\*`"")
            'reversed order filesandordirs'   = $real.Replace($anchor, "$anchor`r`nName: `"{localappdata}\Clink\updates`"; Type: filesandordirs")
            'reversed order files wildcard'   = $real.Replace($anchor, "$anchor`r`nName: `"{localappdata}\Clink\*`"; Type: files")
            'userappdata entry'               = $real.Replace($anchor, "$anchor`r`nName: `"{userappdata}\Clink\settings.json`"; Type: files")
            'userpf dotdot'                   = $real.Replace($anchor, "$anchor`r`nType: filesandordirs; Name: `"{userpf}\..\Clink\updates`"")
            'env constant'                    = $real.Replace($anchor, "$anchor`r`nType: filesandordirs; Name: `"{%LOCALAPPDATA}\Clink\updates`"")
            'preprocessor constant'           = $real.Replace($anchor, "$anchor`r`nType: filesandordirs; Name: `"{#Local}\Clink\updates`"")
            'localappdata wildcard'           = $real.Replace($anchor, "$anchor`r`nType: files; Name: `"{localappdata}\*`"")
            'userdocs dotdot'                 = $real.Replace($anchor, "$anchor`r`nType: filesandordirs; Name: `"{userdocs}\..\AppData\Roaming\Clink`"")
            'UninstallRun section'            = $real + "`r`n[UninstallRun]`r`nFilename: `"cmd.exe`"; Parameters: `"/c rd /s /q x`"`r`n"
            'InstallDelete section'           = $real + "`r`n[InstallDelete]`r`nType: files; Name: `"{app}\x`"`r`n"
            'DelTree in Code'                 = $real + "`r`n[Code]`r`nprocedure X;`r`nbegin`r`n  DelTree(ExpandConstant('{app}'), True, True, True);`r`nend;`r`n"
            'RedirectionGuard=no'             = $real.Replace('[Setup]', "[Setup]`r`nRedirectionGuard=no")
        }
        foreach ($k in $variants.Keys) {
            $res = @(Get-InstallerUninstallFailures -Text $variants[$k] -Label $k)
            if ($res.Count -eq 0) { $failures.Add("negative test '$k' was NOT rejected by the lint") }
        }
    }
}

# The "Launch Clink" checkbox on the last page is CHECKED by default (no "unchecked" flag on the [Run] entry that starts Clink.exe);
# it stays a postinstall entry that is skipped for a silent install and offered only when the runtime is present.
if (Test-Path -LiteralPath $realIss) {
    $runLines = @($real -split "`r?`n" | Where-Object { $_ -match '^\s*Filename:\s*"\{app\}\\Clink\.exe"' -and $_ -match 'postinstall' })
    if ($runLines.Count -ne 1) { $failures.Add("expected exactly one postinstall [Run] entry for Clink.exe, found $($runLines.Count)") }
    else {
        if ($runLines[0] -match '(?i)\bunchecked\b') { $failures.Add('the "Launch Clink" [Run] entry has the unchecked flag: it must be checked by default') }
        foreach ($flag in 'nowait', 'skipifsilent') { if ($runLines[0] -notmatch "\b$flag\b") { $failures.Add("the Launch Clink [Run] entry lost the $flag flag") } }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    Write-Host "Failed: $($failures.Count)"
    exit 1
}
Write-Host "PASS installer lint ($($files.Count) file(s))"
exit 0
