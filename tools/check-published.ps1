# Checks the two published flavours (tools/publish-flavours.ps1): size guards, then on each exe
#   --packaging-check <SelfContained|Framework>   (stdout has "packaging=<X>" and "result=ok")
#   --preflight
#   --unittest Embedded_feed_trust_matches_ci_expectation   (with CLINK_EXPECT_FEED when -ExpectFeed is given; with -ExpectFeed on also
#                                                             CLINK_EXPECT_FEED_URL and CLINK_EXPECT_DEV_FEED_URL from -ExpectFeedUrl / -ExpectDevFeedUrl)
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
    # CLINK_EXPECT_FEED_URL / CLINK_EXPECT_DEV_FEED_URL for the feed self-test: the normalised bases Get-FeedConfig gives for the stable and the
    # dev channel. Both are REQUIRED with -ExpectFeed on (the C# test requires both when on) and must be empty with -ExpectFeed off; with
    # no -ExpectFeed they are refused (Get-FeedSelfTestEnvironment in feed-lib.ps1). Unset in the exe's environment whenever the feed is off.
    [string]$ExpectFeedUrl,
    [string]$ExpectDevFeedUrl,
    [ValidateRange(1, 3600)] [int]$TimeoutSeconds = 300,   # per child process; the workflows pass 90
    [switch]$SkipRun   # only the size guards (used by the script tests)
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'feed-lib.ps1')
$MiB = 1MB
# Fail on a wrong combination before anything is run.
$feedSelfTestEnv = Get-FeedSelfTestEnvironment -ExpectFeed $ExpectFeed -ExpectFeedUrl $ExpectFeedUrl -ExpectDevFeedUrl $ExpectDevFeedUrl

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

function Test-AdminOnlyAcl {
    # True when the access control list is admin-only: owned by SYSTEM, Administrators or TrustedInstaller, and no Allow entry gives
    # Users, Authenticated Users, Everyone, INTERACTIVE (or, stricter, any other account such as a single user) any write, delete,
    # change-permissions or take-ownership right.
    param($Acl)
    $owner = $Acl.GetOwner([System.Security.Principal.SecurityIdentifier])
    $trustedOwners = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if (-not $owner -or $trustedOwners -notcontains $owner.Value) { return $false }
    $writers = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', 'S-1-3-0')   # SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER
    $writeMask = [int][System.Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $writeMask = $writeMask -bor 0x40000000 -bor 0x10000000   # GenericWrite, GenericAll
    foreach ($rule in $Acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($writers -notcontains $rule.IdentityReference.Value -and (([int]$rule.FileSystemRights) -band $writeMask)) { return $false }
    }
    $true
}

function Get-TrustedDotNetRoots {
    # The machine-wide dotnet roots the Windows apphost may load a runtime from: <Program Files>\dotnet, plus the HKLM install location the
    # apphost reads (the 32-bit registry view, SOFTWARE\dotnet\Setup\InstalledVersions\x64, value InstallLocation), but only when that
    # location is <Program Files>\dotnet itself or its ACL is admin-only. Normalised and de-duplicated. -ReadInstallLocation is the registry
    # reader (a test replaces it).
    param([scriptblock]$ReadInstallLocation = {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry32')
            try {
                $key = $base.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
                if ($key) { try { [string]$key.GetValue('InstallLocation') } finally { $key.Dispose() } }
            } finally { $base.Dispose() }
        })
    $programFiles = if ([Environment]::Is64BitProcess) { [Environment]::GetFolderPath('ProgramFiles') } else { $env:ProgramW6432 }
    $default = [System.IO.Path]::GetFullPath((Join-Path $programFiles 'dotnet')).TrimEnd('\')
    $roots = New-Object System.Collections.Generic.List[string]
    $roots.Add($default)
    try {
        $registered = [string](& $ReadInstallLocation)
        if ($registered -and $registered.Trim()) {
            $location = [System.IO.Path]::GetFullPath($registered.Trim()).TrimEnd('\')
            if ($location -ieq $default) { $roots.Add($location) }
            elseif ((Test-Path -LiteralPath $location -PathType Container) -and (Test-AdminOnlyAcl (Get-Acl -LiteralPath $location))) { $roots.Add($location) }
        }
    } catch { }
    $unique = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) { if (-not ($unique | Where-Object { $_ -ieq $root })) { $unique.Add($root) } }
    @($unique)
}

function Get-FinalPath {
    # The path with every junction, symbolic link, subst drive and 8.3 name resolved (GetFinalPathNameByHandle), without the \\?\ prefix.
    param([string]$Path)
    if (-not ('ClinkFinalPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ClinkFinalPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder buffer, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    public static string Resolve(string path) {
        IntPtr handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);   // share all, OPEN_EXISTING, BACKUP_SEMANTICS (folders too)
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            StringBuilder buffer = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity) { buffer = new StringBuilder((int)length + 1); length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0); }
            return buffer.ToString();
        } finally { CloseHandle(handle); }
    }
}
'@
    }
    $final = [ClinkFinalPath]::Resolve($Path)
    if ($final.StartsWith('\\?\UNC\')) { $final = '\\' + $final.Substring(8) } elseif ($final.StartsWith('\\?\')) { $final = $final.Substring(4) }
    $final.TrimEnd('\')
}

function Assert-NoReparseComponent {
    # Throws when any component of the path, from the drive root down to the last one, is a junction or symbolic link (a reparse point).
    param([string]$Label, [string]$Path)
    $current = [System.IO.Path]::GetPathRoot($Path)
    $names = @($Path.Substring($current.Length) -split '\\' | Where-Object { $_ })
    $checked = @($current)
    foreach ($name in $names) { $checked += (Join-Path $checked[-1] $name) }
    foreach ($item in $checked) {
        $info = Get-Item -LiteralPath $item -Force -ErrorAction Stop
        if ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "$Label '$Path': '$item' is a junction or symbolic link." }
    }
}

function Assert-FrameworkRuntimeDir {
    # Positive check of the framework flavour's fx_dir: it must be <trusted root>\shared\Microsoft.NETCore.App\<fx_version>, with no junction
    # or symbolic link at any component from the drive root down (the trusted root included), and System.Private.CoreLib.dll a real file;
    # the final (link-resolved) paths of the folder and of the trusted root must agree too. Throws with the reason, returns the normalised path.
    param([string]$Label, [string]$Dir, [string]$Version, [string[]]$TrustedRoots)
    if (-not $Dir) { throw "$Label reported no fx_dir (a framework-dependent exe must load its runtime from disk)." }
    if (-not $Version) { throw "$Label reported no fx_version." }
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.+-]+)?$') { throw "$Label reported a malformed fx_version '$Version'." }
    if (@($Dir -split '[\\/]') -contains '..') { throw "$Label fx_dir '$Dir' contains '..'." }
    try { $full = [System.IO.Path]::GetFullPath($Dir).TrimEnd('\', '/') } catch { throw "$Label fx_dir '$Dir' is not a valid path." }
    $versionFolder = Split-Path -Leaf $full
    $appFolder = Split-Path -Parent $full
    $sharedFolder = if ($appFolder) { Split-Path -Parent $appFolder } else { $null }
    $rootFolder = if ($sharedFolder) { Split-Path -Parent $sharedFolder } else { $null }
    if (-not $rootFolder -or (Split-Path -Leaf $appFolder) -ne 'Microsoft.NETCore.App' -or (Split-Path -Leaf $sharedFolder) -ne 'shared') {
        throw "$Label fx_dir '$Dir' is not of the form <dotnet root>\shared\Microsoft.NETCore.App\<version>."
    }
    if ($versionFolder -ne $Version) { throw "$Label fx_dir '$Dir' ends in '$versionFolder' but fx_version is '$Version'." }
    $trusted = @($TrustedRoots | Where-Object { $_ } | ForEach-Object { try { [System.IO.Path]::GetFullPath($_).TrimEnd('\', '/') } catch { } })
    if (-not ($trusted | Where-Object { $_ -ieq $rootFolder })) { throw "$Label fx_dir '$Dir' is under '$rootFolder', not under a trusted dotnet root ($($trusted -join '; '))." }
    foreach ($folder in $full, $appFolder, $sharedFolder, $rootFolder) {
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "$Label fx_dir '$Dir': '$folder' is not an existing folder." }
    }
    Assert-NoReparseComponent "$Label fx_dir" $full
    $corelib = Join-Path $full 'System.Private.CoreLib.dll'
    if (-not (Test-Path -LiteralPath $corelib -PathType Leaf)) { throw "$Label fx_dir '$Dir' has no System.Private.CoreLib.dll (not a real runtime folder)." }
    if ((Get-Item -LiteralPath $corelib -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw "$Label fx_dir '$Dir': System.Private.CoreLib.dll is a symbolic link." }
    $finalDir = Get-FinalPath $full
    $finalRoot = Get-FinalPath $rootFolder
    if ($finalDir -ine (Join-Path $finalRoot "shared\Microsoft.NETCore.App\$Version")) { throw "$Label fx_dir '$Dir' resolves to '$finalDir', which is not below the trusted root's real path '$finalRoot'." }
    $finalCorelib = Get-FinalPath $corelib
    if ($finalCorelib -ine (Join-Path $finalDir 'System.Private.CoreLib.dll')) { throw "$Label fx_dir '$Dir': System.Private.CoreLib.dll resolves to '$finalCorelib', outside the runtime folder." }
    $full
}

function Assert-BundledRuntime {
    # The self-contained flavour must say its runtime is bundled: corelib=bundled, no framework deps file, empty fx_dir.
    param([string]$Label, [string]$Output)
    foreach ($needle in 'corelib=bundled', 'fx_deps=absent') {
        if ($Output -notmatch ('(^|\s)' + [regex]::Escape($needle) + '(\s|$)')) { throw "$Label did not report $needle. stdout: $($Output.Trim())" }
    }
    if ($Output -notmatch '(^|\s)fx_dir=""(\s|$)') { throw "$Label did not report an empty fx_dir (a bundled runtime has no runtime folder). stdout: $($Output.Trim())" }
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
    $packagingRun = Invoke-PublishedExe $t.Exe @('--packaging-check', $t.Packaging) $TimeoutSeconds
    Assert-Run "$($t.Label) --packaging-check" $packagingRun $must
    $versionMatch = [regex]::Match($packagingRun.Out, '(^|\s)fx_version=(\S+)')
    Write-Host "[$($t.Label)] runtime fx_version $(if ($versionMatch.Success) { $versionMatch.Groups[2].Value } else { '(not reported)' })"
    if ($t.Packaging -eq 'Framework') {
        $dirMatch = [regex]::Match($packagingRun.Out, '(^|\s)fx_dir="([^"]*)"')
        $fxVersion = if ($versionMatch.Success) { $versionMatch.Groups[2].Value } else { '' }
        $fxDir = if ($dirMatch.Success) { $dirMatch.Groups[2].Value } else { '' }
        $resolvedDir = Assert-FrameworkRuntimeDir "$($t.Label) --packaging-check" $fxDir $fxVersion (Get-TrustedDotNetRoots)
        Write-Host "[$($t.Label)] runtime fx_dir $resolvedDir (trusted dotnet root)"
    } else {
        Assert-BundledRuntime "$($t.Label) --packaging-check" $packagingRun.Out
        Write-Host "[$($t.Label)] runtime bundled"
    }
    Assert-Run "$($t.Label) --preflight" (Invoke-PublishedExe $t.Exe @('--preflight') $TimeoutSeconds) @()
    # CLINK_EXPECT_FEED / CLINK_TESTS_NO_SKIP only for the feed self-test run.
    $feedEnv = @{ CLINK_TESTS_NO_SKIP = '1' }
    foreach ($name in $feedSelfTestEnv.Keys) { $feedEnv[$name] = $feedSelfTestEnv[$name] }   # on: all three; off: only CLINK_EXPECT_FEED; omitted: none (unset)
    $feed = Invoke-PublishedExe $t.Exe @('--unittest', 'Embedded_feed_trust_matches_ci_expectation') $TimeoutSeconds $feedEnv
    Assert-Run "$($t.Label) feed self-test" $feed @()
    if ($feed.Out -notmatch '1 passed, 0 failed') { throw "$($t.Label) feed self-test did not report '1 passed, 0 failed'. stdout: $($feed.Out.Trim())" }
    # The third-party notices tests: the embedded THIRD-PARTY-NOTICES.txt resource and its parser, run on the published exe.
    # CLINK_TESTS_NO_SKIP only for this run (a skipped test must fail); the summary must say N passed (N > 0), 0 failed, 0 skipped.
    $notices = Invoke-PublishedExe $t.Exe @('--unittest', 'ThirdPartyNotices') $TimeoutSeconds @{ CLINK_TESTS_NO_SKIP = '1'; CLINK_EXPECT_FEED = $null; CLINK_EXPECT_FEED_URL = $null; CLINK_EXPECT_DEV_FEED_URL = $null }
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
    $n1Run = Invoke-PublishedExe $n1Exe @('--packaging-check', 'Framework') $TimeoutSeconds $n1Env
    Assert-Run 'N-1 framework --packaging-check' $n1Run @('packaging=Framework', 'build=Framework', 'result=ok')
    # The runtime must come from the machine's install, not from the planted folder: fx_dir is non-empty and not below the N-1 base.
    $n1Dir = [regex]::Match($n1Run.Out, '(^|\s)fx_dir="([^"]*)"')
    if (-not $n1Dir.Success -or -not $n1Dir.Groups[2].Value) { throw "N-1 framework --packaging-check reported no fx_dir. stdout: $($n1Run.Out.Trim())" }
    if ($n1Dir.Groups[2].Value.StartsWith($n1Base, [System.StringComparison]::OrdinalIgnoreCase)) { throw "N-1 framework runtime directory '$($n1Dir.Groups[2].Value)' is inside the planted folder '$n1Base'." }
    $n1Version = [regex]::Match($n1Run.Out, '(^|\s)fx_version=(\S+)').Groups[2].Value
    $n1Resolved = Assert-FrameworkRuntimeDir 'N-1 framework --packaging-check' $n1Dir.Groups[2].Value $n1Version (Get-TrustedDotNetRoots)
    Write-Host "[N-1] runtime fx_dir $n1Resolved"
    Assert-Run 'N-1 framework --preflight' (Invoke-PublishedExe $n1Exe @('--preflight') $TimeoutSeconds $n1Env) @()
} finally {
    Remove-Item -LiteralPath $n1Base -Recurse -Force -ErrorAction SilentlyContinue
}
'check-published: all ok'
