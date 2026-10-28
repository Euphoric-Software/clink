# Script tests: (1) every tools\**\*.ps1 and build.ps1 parses without errors, (2) every tools\tests\*.tests.ps1 runs as a standalone
# script and exits 0. Plain PowerShell, no modules; runs on Windows PowerShell 5.1 and pwsh 7. Exit code 0 = all green, 1 = failures.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$hostExe = (Get-Process -Id $PID).Path
$failures = 0

# 1. Parse every script.
$scripts = @(Get-ChildItem -LiteralPath (Join-Path $repo 'tools') -Recurse -Filter '*.ps1' -File) + @(Get-Item -LiteralPath (Join-Path $repo 'build.ps1'))
foreach ($file in $scripts) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        $failures++
        Write-Host "PARSE FAIL  $($file.FullName)"
        foreach ($e in $errors) { Write-Host "    line $($e.Extent.StartLineNumber): $($e.Message)" }
    } else {
        Write-Host "parse ok    $($file.FullName.Substring($repo.Length + 1))"
    }
}

# 2. Run every *.tests.ps1 in its own process.
$testFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.tests.ps1' -File | Sort-Object Name)
if ($testFiles.Count -eq 0) { $failures++; Write-Host 'no *.tests.ps1 found' }
foreach ($test in $testFiles) {
    Write-Host "=== $($test.Name)"
    $run = Start-Process -FilePath $hostExe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $test.FullName + '"')) -Wait -PassThru -NoNewWindow
    if ($run.ExitCode -ne 0) { $failures++; Write-Host "TEST FILE FAIL  $($test.Name) (exit code $($run.ExitCode))" }
    else { Write-Host "test file ok    $($test.Name)" }
}

if ($failures -gt 0) { Write-Host "run-script-tests: $failures failure(s)"; exit 1 }
Write-Host 'run-script-tests: all ok'
exit 0
