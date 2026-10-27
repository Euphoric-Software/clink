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

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    Write-Host "Failed: $($failures.Count)"
    exit 1
}
Write-Host "PASS installer lint ($($files.Count) file(s))"
exit 0
