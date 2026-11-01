# Lint for installer\*.iss and *.isi (no Inno Setup needed; the Default.isl checks use INNO_SETUP_DIR or the standard folders and print SKIP when it is missing). Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\installer-script.tests.ps1
# Exit 0 = all pass, 1 = failures.
#  1. No line may start (after indentation) with '#' followed by a digit, '$' or a quote: ISPP treats any line whose first
#     non-blank character is '#' as a directive, so a Pascal continuation line such as "  #13#10 + ..." breaks ISCC.
#  2. Every #if/#ifdef/#ifndef has a matching #endif (and no #endif/#else/#elif without an opener).
$ErrorActionPreference = 'Stop'
$installerDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'installer'
$files = @(Get-ChildItem -LiteralPath $installerDir -File | Where-Object { $_.Extension -in '.iss', '.isi' })
$failures = New-Object System.Collections.Generic.List[string]
if ($files.Count -eq 0) { $failures.Add("no .iss/.isi files found in $installerDir") }

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
    param([string]$Text, [string]$Label, [bool]$RequireEntries = $true)
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
    if ($RequireEntries) { foreach ($want in $allowedAll) {
        if (@($seen | Where-Object { $_ -eq $want }).Count -ne 1) { $out.Add("${Label}: [UninstallDelete] needs exactly one entry '$want'") }
    } }
    $out.ToArray()
}

foreach ($file in $files) {
    foreach ($f in @(Get-InstallerUninstallFailures -Text ([System.IO.File]::ReadAllText($file.FullName)) -Label $file.Name -RequireEntries ($file.Extension -eq '.iss'))) { $failures.Add($f) }
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

# 4. Translations: the [Languages] section lists the app's 16 languages, vendored language files exist, and ClinkMessages.isi defines every
#    key for every language with the same %1..%9 placeholders as English, no %n and no leading/trailing blanks. ClinkMessages.isi may hold only
#    [CustomMessages] and [Messages]. Per-language rules: Arabic keeps LRM marks next to .NET / (x64) / a trailing placeholder, Simplified
#    Chinese uses full-width punctuation, Japanese uses full-width ? and !. [Messages] overrides (hindi, brazilianportuguese) are checked against
#    an explicit allowlist and against Default.isl of the installed Inno Setup (INNO_SETUP_DIR or the standard folders; skipped with a SKIP line otherwise).
$wantLangs = @('english','spanish','french','german','brazilianportuguese','italian','dutch','polish','turkish','russian','ukrainian','arabic','hindi','japanese','korean','chinesesimplified')
if (Test-Path -LiteralPath $realIss) {
    $issLangs = @([regex]::Matches($real, '(?m)^\s*Name:\s*"(?<n>[a-z]+)";\s*MessagesFile:\s*"(?<f>[^"]+)"') | ForEach-Object { $_.Groups['n'].Value })
    if ((($issLangs | Sort-Object) -join ',') -ne (($wantLangs | Sort-Object) -join ',')) { $failures.Add("[Languages] lists '$($issLangs -join ',')', expected the 16 app languages") }
    foreach ($m in [regex]::Matches($real, '(?m)^\s*Name:\s*"[a-z]+";\s*MessagesFile:\s*"(?<f>[^"]+)"')) {
        $f = $m.Groups['f'].Value
        if (-not $f.StartsWith('compiler:') -and -not (Test-Path -LiteralPath (Join-Path $installerDir $f))) { $failures.Add("[Languages] file not found next to Clink.iss: $f") }
    }
    # Setup follows the Windows UI language on every run (upgrades included) and asks only when none matches.
    $setupSection = [regex]::Match($real, '(?ms)^\[Setup\]\s*$(?<body>.*?)(?=^\[)').Groups['body'].Value
    foreach ($directive in 'LanguageDetectionMethod=uilanguage', 'ShowLanguageDialog=auto', 'UsePreviousLanguage=no') {
        if ($setupSection -notmatch ('(?m)^' + [regex]::Escape($directive) + '\s*$')) { $failures.Add("Clink.iss [Setup] lacks the line $directive") }
    }
}

# Inno Setup's own custom messages (Default.isl [CustomMessages]); a language may override them in ClinkMessages.isi as <lang>.<Name>.
$innoCustomEnglish = @{
    NameAndVersion = '%1 version %2'; AdditionalIcons = 'Additional shortcuts:'; CreateDesktopIcon = 'Create a &desktop shortcut'
    CreateQuickLaunchIcon = 'Create a &Quick Launch shortcut'; ProgramOnTheWeb = '%1 on the Web'; UninstallProgram = 'Uninstall %1'
    LaunchProgram = 'Launch %1'; AssocFileExtension = '&Associate %1 with the %2 file extension'
    AssocingFileExtension = 'Associating %1 with the %2 file extension...'; AutoStartProgramGroupDescription = 'Startup:'
    AutoStartProgram = 'Automatically start %1'; AddonHostProgramNotFound = '%1 could not be located in the folder you selected.%n%nDo you want to continue anyway?'
}
# INTENTIONAL OVERRIDES of [Messages] keys that the vendored/official language file already defines (all others must be keys the file LACKS).
# Hindi.islu (Inno 5.5.3) is poorly worded and misspelled on the screens Clink shows; BrazilianPortuguese.isl 6.7.3 writes "% 1" in OnlyOnTheseArchitectures.
$intentionalOverrides = @{
    hindi = @((@'
SetupAppTitle SetupWindowTitle UninstallAppTitle UninstallAppFullTitle InformationTitle ErrorTitle SetupLdrStartupMessage InvalidParameter
SetupAlreadyRunning WindowsVersionNotSupported SetupAppRunningError ExitSetupTitle ExitSetupMessage ButtonBack ButtonNext ButtonInstall ButtonCancel
ButtonNo ButtonFinish SelectLanguageTitle SelectLanguageLabel ClickNext BeveledLabel WelcomeLabel1 WelcomeLabel2 WizardSelectTasks SelectTasksDesc
SelectTasksLabel2 WizardSelectProgramGroup SelectStartMenuFolderDesc SelectStartMenuFolderLabel3 SelectStartMenuFolderBrowseLabel NoProgramGroupCheck2
WizardReady ReadyLabel1 ReadyLabel2a ReadyLabel2b ReadyMemoUserInfo ReadyMemoDir ReadyMemoType ReadyMemoComponents ReadyMemoGroup ReadyMemoTasks
WizardPreparing PreparingDesc PreviousInstallNotCompleted CannotContinue ApplicationsFound ApplicationsFound2 CloseApplications DontCloseApplications
ErrorCloseApplications WizardInstalling InstallingLabel FinishedHeadingLabel FinishedLabelNoIcons FinishedLabel ClickFinish FinishedRestartLabel
FinishedRestartMessage YesRadio NoRadio RunEntryExec RunEntryShellExec StatusClosingApplications StatusCreateDirs StatusExtractFiles StatusCreateIcons
StatusCreateRegistryEntries StatusSavingUninstall StatusRunProgram StatusRestartingApplications StatusRollback ConfirmUninstall UninstallStatusLabel
UninstalledAll UninstalledMost UninstalledAndNeedsRestart WizardUninstalling StatusUninstalling ShutdownBlockReasonInstallingApp
ShutdownBlockReasonUninstallingApp
'@) -split '\s+' | Where-Object { $_ })
    brazilianportuguese = @('OnlyOnTheseArchitectures')
}
$msgFile = Join-Path $installerDir 'ClinkMessages.isi'
$LRM = [char]0x200E
$RLM = [char]0x200F
if (-not (Test-Path -LiteralPath $msgFile)) { $failures.Add('installer\ClinkMessages.isi not found') }
else {
    $bytes = [System.IO.File]::ReadAllBytes($msgFile)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) { $failures.Add('ClinkMessages.isi must be UTF-8 with BOM (kept for safety; ISCC 6.3+ also reads BOM-less UTF-8)') }
    $msgs = @{}
    $overrides = @{}
    $innoOverrides = @{}
    $curSection = ''
    $msgLineNo = 0
    foreach ($line in ([System.IO.File]::ReadAllText($msgFile, [System.Text.Encoding]::UTF8) -split "\r?\n")) {
        $msgLineNo++
        $sm = [regex]::Match($line, '^\[(?<s>[^\]]+)\]\s*$')
        if ($sm.Success) {
            $curSection = $sm.Groups['s'].Value
            if ($curSection -ine 'CustomMessages' -and $curSection -ine 'Messages') { $failures.Add("ClinkMessages.isi:${msgLineNo}: section [$curSection] is not allowed (only [CustomMessages] and [Messages])") }
            continue
        }
        $mm = [regex]::Match($line, '^(?<l>[a-z]+)\.(?<k>[A-Za-z0-9]+)=(?<v>.*)$')
        if (-not $mm.Success) { continue }
        $l = $mm.Groups['l'].Value; $k = $mm.Groups['k'].Value; $v = $mm.Groups['v'].Value
        if ($curSection -eq '') { $failures.Add("ClinkMessages.isi:${msgLineNo}: $l.$k is outside any section"); continue }
        if ($curSection -ieq 'Messages') {
            if (-not $intentionalOverrides.ContainsKey($l)) { $failures.Add("ClinkMessages.isi: [Messages] override for unexpected language $l"); continue }
            if (-not $overrides.ContainsKey($l)) { $overrides[$l] = @{} }
            if ($overrides[$l].ContainsKey($k)) { $failures.Add("ClinkMessages.isi: duplicate [Messages] $l.$k") } else { $overrides[$l][$k] = $v }
            continue
        }
        if ($innoCustomEnglish.ContainsKey($k)) {
            if (-not $innoOverrides.ContainsKey($l)) { $innoOverrides[$l] = @{} }
            if ($innoOverrides[$l].ContainsKey($k)) { $failures.Add("ClinkMessages.isi: duplicate $l.$k") } else { $innoOverrides[$l][$k] = $v }
            continue
        }
        if (-not $msgs.ContainsKey($l)) { $msgs[$l] = @{} }
        if ($msgs[$l].ContainsKey($k)) { $failures.Add("ClinkMessages.isi: duplicate $l.$k") }
        $msgs[$l][$k] = $v
    }
    $enKeys = @()
    if ($msgs.ContainsKey('english')) { $enKeys = @($msgs['english'].Keys) } else { $failures.Add('ClinkMessages.isi: no english messages') }
    if ($enKeys.Count -eq 0) { $failures.Add('ClinkMessages.isi: english has no messages') }
    function Get-Placeholders([string]$s) { (@([regex]::Matches($s, '%\d') | ForEach-Object { $_.Value } | Sort-Object) -join ',') }
    foreach ($l in $wantLangs) {
        if (-not $msgs.ContainsKey($l)) { $failures.Add("ClinkMessages.isi: no messages for $l"); continue }
        foreach ($k in $enKeys) {
            if (-not $msgs[$l].ContainsKey($k)) { $failures.Add("ClinkMessages.isi: $l.$k is missing"); continue }
            $v = $msgs[$l][$k]
            if ($v -ne $v.Trim() -or $v -eq '') { $failures.Add("ClinkMessages.isi: $l.$k is empty or has leading/trailing blanks") }
            if ($v.Contains('%n') -or $v.Contains('%%')) { $failures.Add("ClinkMessages.isi: $l.$k contains %n or %%") }
            if ((Get-Placeholders $v) -ne (Get-Placeholders $msgs['english'][$k])) { $failures.Add("ClinkMessages.isi: $l.$k placeholders differ from english") }
            if ($l -ne 'english' -and $k -ne 'ClinkRuntimeTaskGroup') {
                foreach ($keep in '.NET') { if ($msgs['english'][$k].Contains($keep) -and -not $v.Contains($keep)) { $failures.Add("ClinkMessages.isi: $l.$k lost '$keep'") } }
                if ($msgs['english'][$k].Contains('Clink') -and -not $v.Contains('Clink')) { $failures.Add("ClinkMessages.isi: $l.$k lost 'Clink'") }
            }
            if ($l -ne 'arabic' -and ($v.Contains([string]$LRM) -or $v.Contains([string]$RLM))) { $failures.Add("ClinkMessages.isi: $l.$k contains a left-to-right / right-to-left mark (only Arabic uses them)") }
            if ($l -eq 'arabic') {
                # Bidi: in a right-to-left paragraph the '.' of '.NET' and the ')' of '(x64)' would stick to the Arabic side without a LRM;
                # a value that ends with a placeholder (a URL) needs a trailing LRM so a trailing '/' or '.' does not flip.
                foreach ($m in [regex]::Matches($v, '\.NET')) {
                    $ix = $m.Index
                    $ok = ($ix -ge 1 -and $v[$ix - 1] -eq $LRM) -or ($ix -ge 2 -and $v[$ix - 1] -eq ' ' -and ([string]$v[$ix - 2]) -cmatch '^[A-Za-z]$')
                    if (-not $ok) { $failures.Add("ClinkMessages.isi: arabic.$k has '.NET' at $ix without U+200E before it (and no ASCII word + space)") }
                }
                foreach ($m in [regex]::Matches($v, '\(x64\)')) {
                    $e = $m.Index + $m.Length
                    if ($e -ge $v.Length -or $v[$e] -ne $LRM) { $failures.Add("ClinkMessages.isi: arabic.$k has '(x64)' not followed by U+200E") }
                }
                if ($v -match '%\d\z') { $failures.Add("ClinkMessages.isi: arabic.$k ends with a placeholder; add a trailing U+200E") }
            }
            if ($l -eq 'chinesesimplified' -and (($v -replace '\(x64\)', '') -match '[,:;?!()]')) { $failures.Add("ClinkMessages.isi: chinesesimplified.$k uses half-width punctuation (use full-width, keep (x64) and .NET)") }
            if ($l -eq 'japanese' -and $v -match '[?!]') { $failures.Add("ClinkMessages.isi: japanese.$k uses a half-width ? or ! (use full-width)") }
        }
        foreach ($k in $msgs[$l].Keys) { if ($k -notin $enKeys) { $failures.Add("ClinkMessages.isi: $l.$k has no english counterpart") } }
    }
    # Overridden Inno custom messages keep their placeholders.
    foreach ($l in $innoOverrides.Keys) {
        foreach ($k in $innoOverrides[$l].Keys) {
            if ((Get-Placeholders $innoOverrides[$l][$k]) -ne (Get-Placeholders $innoCustomEnglish[$k])) { $failures.Add("ClinkMessages.isi: $l.$k placeholders differ from Default.isl") }
        }
    }

    # [Messages] overrides. Hindi.islu (vendored, written for Inno 5.5.3) and BrazilianPortuguese.isl may be overridden only for keys they LACK
    # (Hindi: required for every message Default.isl has) or for keys on $intentionalOverrides. Needs Hindi.islu (vendored); Default.isl comes
    # from INNO_SETUP_DIR or the standard Inno Setup 6 folders and the Default.isl checks are skipped, loudly, when it is not found.
    function Read-IslMessages([string]$path) {
        $h = @{}; $sec = ''
        foreach ($ln in ([System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) -split "\r?\n")) {
            $m1 = [regex]::Match($ln, '^\[(?<s>[A-Za-z]+)\]\s*$'); if ($m1.Success) { $sec = $m1.Groups['s'].Value; continue }
            $m2 = [regex]::Match($ln, '^(?<k>[A-Za-z0-9]+)=(?<v>.*)$')
            if ($sec -ieq 'Messages' -and $m2.Success) { $h[$m2.Groups['k'].Value] = $m2.Groups['v'].Value }
        }
        $h
    }
    $innoDir = $null
    if ($env:INNO_SETUP_DIR) {
        if (Test-Path -LiteralPath (Join-Path $env:INNO_SETUP_DIR 'Default.isl')) { $innoDir = $env:INNO_SETUP_DIR }
        else { $failures.Add("INNO_SETUP_DIR is set to '$env:INNO_SETUP_DIR' but Default.isl is not there") }
    } else {
        $innoDir = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6'), (Join-Path $env:ProgramFiles 'Inno Setup 6'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'Default.isl')) } | Select-Object -First 1
    }
    $defMsgs = $null
    if ($innoDir) { $defMsgs = Read-IslMessages (Join-Path $innoDir 'Default.isl') }
    elseif (-not $env:INNO_SETUP_DIR) { Write-Host 'SKIP: Default.isl not found (set INNO_SETUP_DIR)' }
    $ownFiles = @{ hindi = (Join-Path $installerDir 'Languages\Hindi.islu') }
    if ($innoDir) { $ownFiles['brazilianportuguese'] = (Join-Path $innoDir 'Languages\BrazilianPortuguese.isl') }
    $ownMsgs = @{}
    foreach ($l in $ownFiles.Keys) { if (Test-Path -LiteralPath $ownFiles[$l]) { $ownMsgs[$l] = Read-IslMessages $ownFiles[$l] } }
    foreach ($l in $intentionalOverrides.Keys) {
        $ov = $overrides[$l]; if (-not $ov) { $ov = @{} }
        foreach ($k in $ov.Keys) {
            $lacked = $ownMsgs.ContainsKey($l) -and -not $ownMsgs[$l].ContainsKey($k)
            if ($k -notin $intentionalOverrides[$l]) {
                if ($l -ne 'hindi') { $failures.Add("ClinkMessages.isi: $l.$k is not an intentional override (allowlist in installer-script.tests.ps1)") }
                elseif (-not $lacked) { $failures.Add("ClinkMessages.isi: hindi.$k overrides a message Hindi.islu defines and is not on the intentional allowlist") }
            } elseif ($lacked) { $failures.Add("ClinkMessages.isi: $l.$k is on the intentional allowlist but the language file does not define it (stale entry)") }
            if ($defMsgs) {
                if (-not $defMsgs.ContainsKey($k)) { $failures.Add("ClinkMessages.isi: $l.$k override is unknown to Default.isl"); continue }
                if ((Get-Placeholders $ov[$k]) -ne (Get-Placeholders $defMsgs[$k])) { $failures.Add("ClinkMessages.isi: $l.$k placeholders differ from Default.isl") }
                if (([regex]::Matches($ov[$k], '%n').Count) -ne ([regex]::Matches($defMsgs[$k], '%n').Count)) { $failures.Add("ClinkMessages.isi: $l.$k %n count differs from Default.isl") }
                if (([regex]::Matches($ov[$k], '\[[\w/]+\]').Count) -ne ([regex]::Matches($defMsgs[$k], '\[[\w/]+\]').Count)) { $failures.Add("ClinkMessages.isi: $l.$k [name]/[gb] tokens differ from Default.isl") }
            }
        }
        foreach ($k in $intentionalOverrides[$l]) { if (-not $ov.ContainsKey($k)) { $failures.Add("ClinkMessages.isi: intentional $l.$k is on the allowlist but not defined") } }
    }
    if ($defMsgs -and $ownMsgs.ContainsKey('hindi')) {
        $hindiOv = $overrides['hindi']; if (-not $hindiOv) { $hindiOv = @{} }
        foreach ($k in $defMsgs.Keys) {
            if ($ownMsgs['hindi'].ContainsKey($k) -or $defMsgs[$k] -eq '') { continue }  # empty defaults (HelpTextNote) need no translation
            if (-not $hindiOv.ContainsKey($k)) { $failures.Add("ClinkMessages.isi: no [Messages] hindi.$k override (Hindi.islu lacks it, Default.isl has it)") }
        }
    }
    # Hindi: no two accelerators (&x) of one dialog may share a letter (effective text = Hindi.islu plus the overrides).
    if ($ownMsgs.ContainsKey('hindi')) {
        $effective = @{}; foreach ($k in $ownMsgs['hindi'].Keys) { $effective[$k] = $ownMsgs['hindi'][$k] }
        if ($overrides.ContainsKey('hindi')) { foreach ($k in $overrides['hindi'].Keys) { $effective[$k] = $overrides['hindi'][$k] } }
        $groups = @(
            @('ButtonBack','ButtonNext','ButtonCancel'), @('ButtonBack','ButtonInstall','ButtonCancel'), @('ButtonBack','ButtonFinish'), @('ButtonYes','ButtonNo'),
            @('ButtonStopDownload','ButtonCancel'), @('ButtonStopExtraction','ButtonCancel'), @('CloseApplications','DontCloseApplications'), @('YesRadio','NoRadio'),
            @('PrivilegesRequiredOverrideAllUsers','PrivilegesRequiredOverrideCurrentUser'),
            @('FileExistsOverwriteExisting','FileExistsKeepExisting','FileExistsOverwriteOrKeepAll'),
            @('ExistingFileNewerOverwriteExisting','ExistingFileNewerKeepExisting','ExistingFileNewerOverwriteOrKeepAll'),
            @('ExistingFileReadOnlyRetry','ExistingFileReadOnlyKeepExisting'),
            @('AbortRetryIgnoreRetry','AbortRetryIgnoreIgnore','FileAbortRetryIgnoreSkipNotRecommended'),
            @('AbortRetryIgnoreRetry','FileAbortRetryIgnoreSkipNotRecommended','FileAbortRetryIgnoreIgnoreNotRecommended')
        )
        foreach ($g in $groups) {
            $seenAcc = @{}
            foreach ($k in $g) {
                if (-not $effective.ContainsKey($k)) { $failures.Add("ClinkMessages.isi: hindi accelerator check: $k is not defined"); continue }
                $am = [regex]::Match(($effective[$k] -replace '&&', ''), '&(?<c>.)')
                if (-not $am.Success) { continue }
                $c = $am.Groups['c'].Value.ToLowerInvariant()
                if ($seenAcc.ContainsKey($c)) { $failures.Add("ClinkMessages.isi: hindi accelerator '&$c' is used by both $($seenAcc[$c]) and $k in one dialog") } else { $seenAcc[$c] = $k }
            }
        }
    }
    # Every CustomMessage / {cm:} reference in Clink.iss must exist.
    if (Test-Path -LiteralPath $realIss) {
        $refs = @([regex]::Matches($real, "CustomMessage\('(?<k>Clink[A-Za-z0-9]+)'\)|\{cm:(?<k>Clink[A-Za-z0-9]+)") | ForEach-Object { $_.Groups['k'].Value } | Sort-Object -Unique)
        foreach ($r in $refs) { if ($r -notin $enKeys) { $failures.Add("Clink.iss uses $r but ClinkMessages.isi does not define it") } }
        foreach ($k in $enKeys) { if ($k -notin $refs) { $failures.Add("ClinkMessages.isi defines $k but Clink.iss never uses it") } }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    Write-Host "Failed: $($failures.Count)"
    exit 1
}
Write-Host "PASS installer lint ($($files.Count) file(s))"
exit 0
