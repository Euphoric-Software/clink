# Checks the two published flavours (tools/publish-flavours.ps1): size guards, then on each exe
#   --packaging-check <SelfContained|Framework>   (stdout has "packaging=<X>" and "result=ok")
#   --preflight
#   --unittest Embedded_feed_trust_matches_ci_expectation   (with CLINK_EXPECT_FEED when -ExpectFeed is given)
#   --unittest ThirdPartyNotices   (CLINK_TESTS_NO_SKIP=1 for that run only; "N passed, 0 failed, 0 skipped" with N > 0)
# then the N-1 regression on the framework exe (copied beside a garbage hostfxr.dll, DOTNET_ROOT at an empty folder: --packaging-check
# Framework and --preflight must still pass) and, when -Version is given, ProductVersion == Version. Clink.exe is a GUI-subsystem exe: Start-Process -PassThru
# -NoNewWindow with redirected stdout/stderr (then WaitForExit with -TimeoutSeconds) is what gives its output and exit code. Every run gets its own fresh TEMP/TMP.
# Do NOT run the whole --unittest suite on a published exe (5 tests fail there by design); unit tests stay on bin builds.
# The framework exe needs the WindowsDesktop 10 runtime under Program Files\dotnet (printed first).
param(
    [Parameter(Mandatory)] [string]$ScExe,
    [Parameter(Mandatory)] [string]$FxExe,
    [string]$Version,
    [ValidateSet('on', 'off')] [string]$ExpectFeed,   # CLINK_EXPECT_FEED for the feed self-test; omitted = the test only checks for problems
    [ValidateRange(1, 3600)] [int]$TimeoutSeconds = 300,   # per child process; the workflows pass 90
    [switch]$SkipRun   # only the size guards (used by the script tests)
)
$ErrorActionPreference = 'Stop'
$MiB = 1MB

foreach ($p in $ScExe, $FxExe) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Not found: $p" } }
$ScExe = (Resolve-Path -LiteralPath $ScExe).Path
$FxExe = (Resolve-Path -LiteralPath $FxExe).Path
if ($ScExe -eq $FxExe) { throw 'ScExe and FxExe are the same file.' }

# 1. Size guards.
$scSize = (Get-Item -LiteralPath $ScExe).Length
$fxSize = (Get-Item -LiteralPath $FxExe).Length
'self-contained {0:N1} MiB, framework-dependent {1:N1} MiB, difference {2:N1} MiB' -f ($scSize / $MiB), ($fxSize / $MiB), (($scSize - $fxSize) / $MiB)
if ($scSize -lt 64 * $MiB -or $scSize -gt 90 * $MiB) { throw ('The self-contained exe is {0:N1} MiB, outside 64-90 MiB (a runtime-less or doubled build?).' -f ($scSize / $MiB)) }
if ($fxSize -lt 28 * $MiB -or $fxSize -gt 40 * $MiB) { throw ('The framework-dependent exe is {0:N1} MiB, outside 28-40 MiB (is it really framework-dependent?).' -f ($fxSize / $MiB)) }
if ($scSize - $fxSize -lt 30 * $MiB) { throw ('The self-contained exe is only {0:N1} MiB larger than the framework-dependent one (expected at least 30 MiB): the flavours are mixed up or share an output folder.' -f (($scSize - $fxSize) / $MiB)) }
if ($SkipRun) { 'size guards ok (runs skipped)'; return }

# 2. Runtimes on this machine (the framework exe needs WindowsDesktop 10).
Write-Host '--- dotnet --list-runtimes'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { & $dotnet.Source --list-runtimes } else { Write-Host 'dotnet not on PATH' }

function Stop-ProcessTree {
    # Kills the process and its children. taskkill writes to stderr when the process is already gone (a race); on Windows
    # PowerShell 5.1 that stderr, redirected with 2>&1 under ErrorActionPreference Stop, becomes a terminating error, which must
    # not stop us from reaching Kill() and the "Timed out" error. -TaskKill is the tool to run (a test stubs it).
    param([System.Diagnostics.Process]$Process, [string]$TaskKill = 'taskkill.exe')
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $TaskKill /T /F /PID $Process.Id 2>&1 | Out-Null } catch { } finally { $ErrorActionPreference = $savedPreference }
    try { $Process.Kill() } catch { }
    $null = $Process.WaitForExit(10000)
}

function Invoke-PublishedExe {
    # Runs the exe with the arguments, TEMP/TMP in a fresh folder, stdout/stderr captured. Returns Code, Out, Err.
    # DOTNET_DISABLE_GUI_ERRORS=1: a framework-dependent apphost without its runtime prints instead of showing a modal dialog.
    # After $TimeoutSeconds the process (and its children) is killed and this throws.
    param([string]$Exe, [string[]]$Arguments, [int]$TimeoutSeconds = 300, [hashtable]$ExtraEnvironment = @{})
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-check-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    $outFile = Join-Path $scratch 'stdout.txt'
    $errFile = Join-Path $scratch 'stderr.txt'
    $runTemp = Join-Path $scratch 'tmp'
    New-Item -ItemType Directory -Force -Path $runTemp | Out-Null
    $names = @('TEMP', 'TMP', 'DOTNET_DISABLE_GUI_ERRORS') + @($ExtraEnvironment.Keys)
    $saved = @{}
    foreach ($n in $names) { $saved[$n] = [System.Environment]::GetEnvironmentVariable($n) }
    $timedOut = $false
    try {
        [System.Environment]::SetEnvironmentVariable('TEMP', $runTemp)
        [System.Environment]::SetEnvironmentVariable('TMP', $runTemp)
        [System.Environment]::SetEnvironmentVariable('DOTNET_DISABLE_GUI_ERRORS', '1')
        foreach ($n in $ExtraEnvironment.Keys) { [System.Environment]::SetEnvironmentVariable($n, [string]$ExtraEnvironment[$n]) }
        $run = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $null = $run.Handle   # keeps the handle so ExitCode is available after the process ends (Windows PowerShell 5.1)
        if (-not $run.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            Stop-ProcessTree -Process $run
        } else { $run.WaitForExit() }
        $code = if ($timedOut) { -1 } else { $run.ExitCode }
    } finally { foreach ($n in $names) { [System.Environment]::SetEnvironmentVariable($n, $saved[$n]) } }
    if ($timedOut) {
        Start-Sleep -Milliseconds 300
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        throw "Timed out after $TimeoutSeconds s (process killed): $Exe $($Arguments -join ' ')"
    }
    $text = if (Test-Path -LiteralPath $outFile) { [System.IO.File]::ReadAllText($outFile) } else { '' }
    $err = if (Test-Path -LiteralPath $errFile) { [System.IO.File]::ReadAllText($errFile) } else { '' }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    [pscustomobject]@{ Code = $code; Out = $text; Err = $err }
}

$exitMeaning = @{ 1 = 'the build label and the runtime disagree'; 2 = 'unexpected packaging state'; 5 = 'the check itself failed'; 64 = 'bad argument' }
function Assert-Run {
    param([string]$Label, $Result, [string[]]$MustContain)
    $shown = ($Result.Out.Trim() -split "`r?`n" | Select-Object -Last 3) -join ' | '
    Write-Host "[$Label] exit $($Result.Code): $shown"
    if ($Result.Code -ne 0) {
        $why = if ($exitMeaning.ContainsKey([int]$Result.Code)) { " ($($exitMeaning[[int]$Result.Code]))" } else { '' }
        throw "$Label failed: exit code $($Result.Code)$why. stdout: $($Result.Out.Trim()) stderr: $($Result.Err.Trim())"
    }
    foreach ($needle in $MustContain) {
        # The needle must be a whole token: preceded by a line start or whitespace, followed by whitespace or the end.
        if ($Result.Out -notmatch ('(^|\s)' + [regex]::Escape($needle) + '(\s|$)')) { throw "${Label}: stdout does not contain '$needle'. stdout: $($Result.Out.Trim()) stderr: $($Result.Err.Trim())" }
    }
}

$targets = @(
    [pscustomobject]@{ Label = 'self-contained'; Exe = $ScExe; Packaging = 'SelfContained' },
    [pscustomobject]@{ Label = 'framework'; Exe = $FxExe; Packaging = 'Framework' }
)
foreach ($t in $targets) {
    Write-Host "=== $($t.Label): $($t.Exe)"
    if ($Version) {
        $product = ((Get-Item -LiteralPath $t.Exe).VersionInfo.ProductVersion -split '\+')[0]
        if ($product -ne $Version) { throw "$($t.Label): ProductVersion is '$product', expected '$Version' (the updater would refuse it)." }
        Write-Host "[$($t.Label)] ProductVersion $product"
    }
    $must = @("packaging=$($t.Packaging)", "build=$($t.Packaging)", 'result=ok')
    Assert-Run "$($t.Label) --packaging-check" (Invoke-PublishedExe $t.Exe @('--packaging-check', $t.Packaging) $TimeoutSeconds) $must
    Assert-Run "$($t.Label) --preflight" (Invoke-PublishedExe $t.Exe @('--preflight') $TimeoutSeconds) @()
    # CLINK_EXPECT_FEED / CLINK_TESTS_NO_SKIP only for the feed self-test run.
    $feedEnv = @{ CLINK_TESTS_NO_SKIP = '1' }
    if ($ExpectFeed) { $feedEnv['CLINK_EXPECT_FEED'] = $ExpectFeed } else { $feedEnv['CLINK_EXPECT_FEED'] = $null }
    $feed = Invoke-PublishedExe $t.Exe @('--unittest', 'Embedded_feed_trust_matches_ci_expectation') $TimeoutSeconds $feedEnv
    Assert-Run "$($t.Label) feed self-test" $feed @()
    if ($feed.Out -notmatch '1 passed, 0 failed') { throw "$($t.Label) feed self-test did not report '1 passed, 0 failed'. stdout: $($feed.Out.Trim())" }
    # The third-party notices tests: the embedded THIRD-PARTY-NOTICES.txt resource and its parser, run on the published exe.
    # CLINK_TESTS_NO_SKIP only for this run (a skipped test must fail); the summary must say N passed (N > 0), 0 failed, 0 skipped.
    $notices = Invoke-PublishedExe $t.Exe @('--unittest', 'ThirdPartyNotices') $TimeoutSeconds @{ CLINK_TESTS_NO_SKIP = '1'; CLINK_EXPECT_FEED = $null }
    Assert-Run "$($t.Label) notices self-test" $notices @()
    $summary = [regex]::Match($notices.Out, '(?<![0-9])([0-9]+) passed, 0 failed, 0 skipped')
    if (-not $summary.Success -or [int]$summary.Groups[1].Value -lt 1) { throw "$($t.Label) notices self-test did not report 'N passed, 0 failed, 0 skipped' with N > 0. stdout: $($notices.Out.Trim())" }
}

# N-1 regression for the framework exe (Windows apphost): a hostfxr.dll planted next to the exe and DOTNET_ROOT pointing at an empty
# folder must change nothing, because the build uses AppHostDotNetSearch=Global (registry / Program Files, never the exe's folder
# and never DOTNET_ROOT). The exe is copied to a fresh folder so nothing real is next to it; both folders are deleted afterwards.
Write-Host '=== framework N-1 regression: planted hostfxr.dll + empty DOTNET_ROOT'
$n1Base = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-n1-' + [guid]::NewGuid().ToString('N'))
try {
    $n1Exe = Join-Path (New-Item -ItemType Directory -Force -Path (Join-Path $n1Base 'app')).FullName 'Clink.exe'
    $emptyRoot = (New-Item -ItemType Directory -Force -Path (Join-Path $n1Base 'empty-dotnet-root')).FullName
    Copy-Item -LiteralPath $FxExe -Destination $n1Exe
    $garbage = New-Object byte[] 4096
    (New-Object System.Random).NextBytes($garbage)
    [System.IO.File]::WriteAllBytes((Join-Path (Split-Path -Parent $n1Exe) 'hostfxr.dll'), $garbage)
    $n1Env = @{ DOTNET_ROOT = $emptyRoot; DOTNET_ROOT_X64 = $emptyRoot }
    Assert-Run 'N-1 framework --packaging-check' (Invoke-PublishedExe $n1Exe @('--packaging-check', 'Framework') $TimeoutSeconds $n1Env) @('packaging=Framework', 'build=Framework', 'result=ok')
    Assert-Run 'N-1 framework --preflight' (Invoke-PublishedExe $n1Exe @('--preflight') $TimeoutSeconds $n1Env) @()
} finally {
    Remove-Item -LiteralPath $n1Base -Recurse -Force -ErrorAction SilentlyContinue
}
'check-published: all ok'
