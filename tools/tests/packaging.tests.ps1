# Tests for the two-flavour packaging scripts and the workflows. Standalone: exits 0 when everything passed, 1 otherwise.
# Windows PowerShell 5.1 and pwsh 7, no modules. Nothing here runs Clink.exe, touches the network, or uses a real key: the
# manifest test makes its own throwaway key pair inside a scratch folder (deleted at the end).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsDir = Join-Path $repo 'tools'
$workflowDir = Join-Path $repo '.github\workflows'
$hostExe = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('clink-pkgtests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

. (Join-Path $toolsDir 'lib\dotnet-runtime-lib.ps1')
# Runtime inputs for package.ps1 tests, derived from the committed floor of the lib so a floor bump does not break them.
$rtVersion = $script:MinimumRuntimeVersion
$rtUrl = Get-ExpectedDotNetRuntimeUrl -Version $rtVersion
$rtSha256 = 'ab' * 32
$script:passed = 0
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "assertion failed: $Message" }
}

function Invoke-Test {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:passed++
        Write-Host "PASS  $Name"
    } catch {
        $script:failed++
        Write-Host "FAIL  $Name : $($_.Exception.Message)"
    }
}

function New-FixtureFile {
    # A file of exactly $Length bytes with random content (or zeros when $Zero).
    param([string]$Path, [long]$Length, [switch]$Zero)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        if ($Zero) { $stream.SetLength($Length) }
        else {
            $bytes = New-Object byte[] $Length
            (New-Object System.Random).NextBytes($bytes)
            $stream.Write($bytes, 0, $bytes.Length)
        }
    } finally { $stream.Dispose() }
}

function Invoke-ScriptChild {
    # Runs a script in a child process of the same PowerShell host. Returns Code, Out, Err.
    param([string]$Script, [string[]]$Arguments, [hashtable]$Environment = @{})
    $id = [guid]::NewGuid().ToString('N')
    $outFile = Join-Path $scratch "out-$id.txt"
    $errFile = Join-Path $scratch "err-$id.txt"
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments
    $quoted = $all | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $saved = @{}
    foreach ($key in $Environment.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $Environment[$key]) }
    try {
        $run = Start-Process -FilePath $hostExe -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        $code = $run.ExitCode
    } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
    [pscustomobject]@{ Code = $code; Out = [System.IO.File]::ReadAllText($outFile); Err = [System.IO.File]::ReadAllText($errFile) }
}

function Import-ScriptFunction {
    # Returns the source text of the named top-level functions of a script (parsed, not run), to be defined with:  . ([scriptblock]::Create($text))
    param([string]$Path, [string[]]$Name)
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
    $found = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $Name -contains $node.Name }, $false)
    Assert-True ($found.Count -eq $Name.Count) "did not find all of $($Name -join ', ') in $Path"
    ($found | ForEach-Object { $_.Extent.Text }) -join "`n"
}

function Get-ScratchFolder {
    param([string]$Name)
    $path = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $path
}

try {
    # ------------------------------------------------------------------ package.ps1
    $fixtures = Get-ScratchFolder 'fixtures'
    $scExe = Join-Path $fixtures 'sc.exe'
    $fxExe = Join-Path $fixtures 'fx.exe'
    New-FixtureFile -Path $scExe -Length 5000
    New-FixtureFile -Path $fxExe -Length 2000
    $packageScript = Join-Path $toolsDir 'package.ps1'

    Invoke-Test 'package.ps1 writes the four files and package.json with the right fields' {
        $out = Get-ScratchFolder 'pkg-ok'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "exit code $($r.Code): $($r.Out) $($r.Err)"
        foreach ($name in 'Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'package.json') {
            Assert-True (Test-Path -LiteralPath (Join-Path $out $name)) "$name is missing"
        }
        Assert-True (@(Get-ChildItem -LiteralPath $out -Filter 'Clink-Setup.exe').Count -eq 0) 'an installer was produced with -SkipInstaller'
        $scHash = (Get-FileHash -LiteralPath $scExe -Algorithm SHA256).Hash.ToLowerInvariant()
        $fxHash = (Get-FileHash -LiteralPath $fxExe -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $out 'Clink.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -eq $scHash) 'Clink.exe is not the self-contained input'
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $out 'Clink-framework.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -eq $fxHash) 'Clink-framework.exe is not the framework input'
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $out 'Clink.exe.sha256')) -ceq "$scHash *Clink.exe`n") 'Clink.exe.sha256 line format'
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $out 'Clink-framework.exe.sha256')) -ceq "$fxHash *Clink-framework.exe`n") 'Clink-framework.exe.sha256 line format'
        $pkg = Get-Content -Raw -LiteralPath (Join-Path $out 'package.json') | ConvertFrom-Json
        Assert-True ($pkg.version -eq '0.4.2') 'version'
        Assert-True ($pkg.minRuntime -eq '10.0.0') 'minRuntime'
        Assert-True ($pkg.files.exe.file -eq 'Clink.exe' -and $pkg.files.exe.sha256 -eq $scHash -and $pkg.files.exe.size -eq 5000) 'files.exe'
        Assert-True ($pkg.files.exeFramework.file -eq 'Clink-framework.exe' -and $pkg.files.exeFramework.sha256 -eq $fxHash -and $pkg.files.exeFramework.size -eq 2000) 'files.exeFramework'
        Assert-True ($pkg.files.sha256file.file -eq 'Clink.exe.sha256') 'files.sha256file'
        Assert-True ($pkg.files.sha256fileFramework.file -eq 'Clink-framework.exe.sha256') 'files.sha256fileFramework'
    }

    Invoke-Test 'package.ps1 refuses the same file for both inputs' {
        $out = Get-ScratchFolder 'pkg-same'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $scExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) 'accepted identical inputs'
        Assert-True (($r.Out + $r.Err) -match 'same file') "no 'same file' message: $($r.Out) $($r.Err)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'package.json'))) 'wrote package.json anyway'
    }

    Invoke-Test 'package.ps1 refuses identical content in two files' {
        $copy = Join-Path $fixtures 'sc-copy.exe'
        Copy-Item -LiteralPath $scExe -Destination $copy -Force
        $out = Get-ScratchFolder 'pkg-copy'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $copy, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) 'accepted identical content'
        Assert-True (($r.Out + $r.Err) -match 'must be larger') "no 'must be larger' message (identical content, equal size is caught first): $($r.Out) $($r.Err)"
    }

    Invoke-Test 'package.ps1 refuses a self-contained exe that is not larger than the framework exe' {
        $out = Get-ScratchFolder 'pkg-swapped'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $fxExe, '-FrameworkExePath', $scExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) 'accepted swapped inputs'
        Assert-True (($r.Out + $r.Err) -match 'must be larger') "no 'must be larger' message (swapped): $($r.Out) $($r.Err)"
        $equal = Join-Path $fixtures 'equal-size.exe'
        New-FixtureFile -Path $equal -Length 5000
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $equal, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) 'accepted equal sizes'
        Assert-True (($r.Out + $r.Err) -match 'must be larger') "no 'must be larger' message (equal): $($r.Out) $($r.Err)"
    }

    Invoke-Test 'package.ps1 refuses a minRuntime that is not X.Y.Z' {
        $out = Get-ScratchFolder 'pkg-minruntime'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) 'accepted minRuntime 10.0'
        Assert-True (($r.Out + $r.Err) -match 'is not X\.Y\.Z') "no 'is not X.Y.Z' message: $($r.Out) $($r.Err)"
    }

    Invoke-Test 'package.ps1 refuses inputs whose ProductVersion differs from -Version (and says so)' {
        $out = Get-ScratchFolder 'pkg-version'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller')
        Assert-True ($r.Code -ne 0) 'accepted fixtures without a matching ProductVersion'
        Assert-True (($r.Out + $r.Err) -match 'ProductVersion') "no ProductVersion message: $($r.Out) $($r.Err)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'package.json'))) 'wrote package.json anyway'
    }

    $runtimeConfigText = '{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.1"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.7"}]}}'
    Invoke-Test 'package.ps1 reads minRuntime from -RuntimeConfigPath and from exactly one runtimeconfig under -FxBinDir' {
        $cfgDir = Get-ScratchFolder 'rtcfg-one'
        $cfg = Join-Path $cfgDir 'Clink.runtimeconfig.json'
        [System.IO.File]::WriteAllText($cfg, $runtimeConfigText, $utf8)
        $out = Get-ScratchFolder 'pkg-rt-explicit'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-RuntimeConfigPath', $cfg, '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "explicit config failed: $($r.Out) $($r.Err)"
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $out 'package.json') | ConvertFrom-Json).minRuntime -eq '10.0.7') 'minRuntime is not the WindowsDesktop version of the config'
        $out2 = Get-ScratchFolder 'pkg-rt-search'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out2, '-FxBinDir', $cfgDir, '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "single config in -FxBinDir failed: $($r.Out) $($r.Err)"
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $out2 'package.json') | ConvertFrom-Json).minRuntime -eq '10.0.7') 'minRuntime from the search'
    }

    Invoke-Test 'package.ps1 refuses zero or two runtimeconfigs under -FxBinDir' {
        $none = Get-ScratchFolder 'rtcfg-none'
        $two = Get-ScratchFolder 'rtcfg-two'
        foreach ($sub in 'a', 'b') {
            $d = Join-Path $two $sub
            New-Item -ItemType Directory -Force -Path $d | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $d 'Clink.runtimeconfig.json'), $runtimeConfigText, $utf8)
        }
        $expectedMessage = @{ $none = 'No Clink\.runtimeconfig\.json found'; $two = 'More than one Clink\.runtimeconfig\.json' }
        foreach ($dir in $none, $two) {
            $out = Get-ScratchFolder ('pkg-rt-bad-' + (Split-Path -Leaf $dir))
            $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-FxBinDir', $dir, '-SkipInstaller', '-SkipVersionCheck')
            Assert-True ($r.Code -ne 0) "accepted $dir"
            Assert-True (($r.Out + $r.Err) -match $expectedMessage[$dir]) "no '$($expectedMessage[$dir])' message for $dir : $($r.Out) $($r.Err)"
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'package.json'))) "wrote package.json for $dir"
        }
    }

    # Runtime info file as tools/resolve-dotnet-runtime.ps1 prints it.
    $runtimeInfo = Join-Path $fixtures 'dotnet-runtime.json'
    [System.IO.File]::WriteAllText($runtimeInfo, ('{"version":"' + $rtVersion + '","url":"' + $rtUrl + '","sha256":"' + $rtSha256 + '","sha512":"' + ('cd' * 64) + '","size":"123"}'), $utf8)
    $repoNotices = Join-Path $repo 'THIRD-PARTY-NOTICES.txt'
    $repoNoticesHash = (Get-FileHash -LiteralPath $repoNotices -Algorithm SHA256).Hash.ToLowerInvariant()

    function New-FakeIscc {
        # A fake ISCC.cmd in its own folder: records its arguments to <folder>\iscc-args.txt and, when -SetupPath is given, creates it.
        param([string]$Name, [string]$SetupPath, [int]$ExitCode = 0)
        $dir = Get-ScratchFolder $Name
        $cmd = Join-Path $dir 'ISCC.cmd'
        $text = "@echo off`r`necho %* > `"%~dp0iscc-args.txt`"`r`n"
        if ($SetupPath) { $text += "echo x> `"$SetupPath`"`r`n" }
        $text += "exit /b $ExitCode`r`n"
        [System.IO.File]::WriteAllText($cmd, $text, [System.Text.Encoding]::ASCII)
        [pscustomobject]@{ Cmd = $cmd; Args = (Join-Path $dir 'iscc-args.txt') }
    }

    Invoke-Test 'package.ps1 passes /DSourceExe=...Clink-framework.exe to ISCC and -RequireInstaller fails when no setup exe appears' {
        $fake = New-FakeIscc 'fake-iscc'
        $out = Get-ScratchFolder 'pkg-iscc-none'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-IsccPath', $fake.Cmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) "-RequireInstaller succeeded without a setup exe: $($r.Out)"
        Assert-True (($r.Out + $r.Err) -match 'did not produce') "no 'did not produce' message: $($r.Out) $($r.Err)"
        Assert-True (Test-Path -LiteralPath $fake.Args) 'the fake ISCC was not run'
        $recorded = [System.IO.File]::ReadAllText($fake.Args)
        Assert-True ($recorded -match '/DSourceExe=[^\r\n]*?Clink-framework\.exe("|\s|$)') "ISCC arguments lack /DSourceExe=...Clink-framework.exe: $recorded"
        Assert-True ($recorded -match '/DAppVersion=0\.4\.2') "ISCC arguments lack /DAppVersion: $recorded"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'package.json'))) 'wrote package.json although the installer is missing'

        # A fake that does produce the setup exe: package.json lists it.
        $out2 = Get-ScratchFolder 'pkg-iscc-ok'
        $fake2 = New-FakeIscc 'fake-iscc2' (Join-Path $out2 'Clink-Setup.exe')
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out2, '-MinRuntime', '10.0.0', '-IsccPath', $fake2.Cmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -eq 0) "a produced setup exe was refused: $($r.Out) $($r.Err)"
        $pkg2 = Get-Content -Raw -LiteralPath (Join-Path $out2 'package.json') | ConvertFrom-Json
        Assert-True ($pkg2.files.installer.file -eq 'Clink-Setup.exe') 'package.json lacks the installer'
        $setupHash = (Get-FileHash -LiteralPath (Join-Path $out2 'Clink-Setup.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $setupShaPath = Join-Path $out2 'Clink-Setup.exe.sha256'
        Assert-True (Test-Path -LiteralPath $setupShaPath) 'Clink-Setup.exe.sha256 is missing'
        Assert-True ([System.IO.File]::ReadAllText($setupShaPath) -ceq "$setupHash *Clink-Setup.exe`n") 'Clink-Setup.exe.sha256 line format / hash'
        Assert-True ($pkg2.files.sha256fileInstaller.file -eq 'Clink-Setup.exe.sha256') 'files.sha256fileInstaller'
        # The ISCC exit code is echoed.
        Assert-True (($r.Out) -match 'ISCC exit code: 0') "ISCC exit code not echoed: $($r.Out)"
        # No setup exe, no setup .sha256.
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'Clink-Setup.exe.sha256'))) 'a setup .sha256 exists although no setup exe was produced'
    }

    Invoke-Test 'package.ps1 fails closed on a non-zero ISCC exit (output echoed, stale setup sha256 deleted, no package.json) and echoes ISCC warnings' {
        # A fake ISCC that prints and exits 2. A stale Clink-Setup.exe.sha256 from an earlier run sits in the out dir.
        $failOut = Get-ScratchFolder 'pkg-iscc-exit2'
        [System.IO.File]::WriteAllText((Join-Path $failOut 'Clink-Setup.exe.sha256'), "stale *Clink-Setup.exe`n", $utf8)
        $failCmd = Join-Path (Get-ScratchFolder 'fake-iscc-exit2') 'ISCC.cmd'
        [System.IO.File]::WriteAllText($failCmd, "@echo off`r`necho Compiling something`r`necho Error: boom`r`nexit /b 2`r`n", [System.Text.Encoding]::ASCII)
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $failOut, '-MinRuntime', '10.0.0', '-IsccPath', $failCmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -ne 0) "a failing ISCC was accepted: $($r.Out)"
        Assert-True ($r.Out -match 'ISCC exit code: 2') "the exit code is not echoed: $($r.Out)"
        Assert-True ($r.Out -match 'ISCC: Error: boom' -and $r.Out -match 'ISCC: Compiling something') "the full ISCC output is not echoed on failure: $($r.Out)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $failOut 'package.json'))) 'wrote package.json after a failing ISCC'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $failOut 'Clink-Setup.exe.sha256'))) 'a stale Clink-Setup.exe.sha256 survived a failing ISCC run'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $failOut 'Clink-Setup.exe'))) 'a Clink-Setup.exe exists after a failing ISCC run'

        # Warnings are echoed on success (and only warnings), and /Qp is not passed.
        $warnOut = Get-ScratchFolder 'pkg-iscc-warn'
        $warnDir = Get-ScratchFolder 'fake-iscc-warn'
        $warnCmd = Join-Path $warnDir 'ISCC.cmd'
        $warnText = "@echo off`r`necho %* > `"%~dp0iscc-args.txt`"`r`necho Compiling something`r`necho Line 12: Warning: something is odd`r`necho Warning: second one`r`necho x> `"$(Join-Path $warnOut 'Clink-Setup.exe')`"`r`nexit /b 0`r`n"
        [System.IO.File]::WriteAllText($warnCmd, $warnText, [System.Text.Encoding]::ASCII)
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $warnOut, '-MinRuntime', '10.0.0', '-IsccPath', $warnCmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -eq 0) "a warning-only ISCC run failed: $($r.Out) $($r.Err)"
        Assert-True ($r.Out -match 'ISCC: Line 12: Warning: something is odd' -and $r.Out -match 'ISCC: Warning: second one') "the ISCC warnings are not echoed: $($r.Out)"
        Assert-True ($r.Out -notmatch 'Compiling something') "a non-warning ISCC line was echoed on success: $($r.Out)"
        Assert-True ($r.Out -match 'ISCC exit code: 0') "the exit code is not echoed: $($r.Out)"
        Assert-True (-not ([System.IO.File]::ReadAllText((Join-Path $warnDir 'iscc-args.txt')) -match '/Qp')) 'ISCC was called with /Qp, which suppresses warnings'
    }

    Invoke-Test 'package.ps1 -RequireInstaller passes all four /D values (runtime url, sha256, version, absolute NoticesFile) and lists the notices in package.json' {
        $out = Get-ScratchFolder 'pkg- spaced out'   # a space in the path: the recorded arguments must still match
        $fake = New-FakeIscc 'fake iscc spaced' (Join-Path $out 'Clink-Setup.exe')
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-IsccPath', $fake.Cmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -eq 0) "exit code $($r.Code): $($r.Out) $($r.Err)"
        $recorded = [System.IO.File]::ReadAllText($fake.Args)
        Assert-True ($recorded.Contains('/DDotNetRuntimeUrl=' + $rtUrl + ' ')) "no /DDotNetRuntimeUrl: $recorded"
        Assert-True ($recorded.Contains('/DDotNetRuntimeSha256=' + $rtSha256 + ' ')) "no /DDotNetRuntimeSha256: $recorded"
        Assert-True ($recorded.Contains('/DDotNetRuntimeVersion=' + $rtVersion + ' ')) "no /DDotNetRuntimeVersion: $recorded"
        $notices = [regex]::Match($recorded, '/DNoticesFile=([^\r\n]*?THIRD-PARTY-NOTICES\.txt)("|\s|$)')
        Assert-True $notices.Success "no /DNoticesFile=...THIRD-PARTY-NOTICES.txt: $recorded"
        $noticesPath = $notices.Groups[1].Value
        Assert-True ([System.IO.Path]::IsPathRooted($noticesPath)) "NoticesFile is not absolute: $noticesPath"
        Assert-True (Test-Path -LiteralPath $noticesPath -PathType Leaf) "NoticesFile does not exist: $noticesPath"
        Assert-True ((Get-FileHash -LiteralPath $noticesPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $repoNoticesHash) 'NoticesFile is not the repo notices'
        Assert-True ($recorded -match '/DSourceExe=' -and $recorded -match '/DAppVersion=0\.4\.2' -and $recorded -match '/DAppFileVersion=0\.4\.2\.0') "other defines missing: $recorded"
        $pkg = Get-Content -Raw -LiteralPath (Join-Path $out 'package.json') | ConvertFrom-Json
        Assert-True ($pkg.files.notices.file -eq 'THIRD-PARTY-NOTICES.txt' -and $pkg.files.notices.sha256 -eq $repoNoticesHash -and $pkg.files.notices.size -eq (Get-Item -LiteralPath $repoNotices).Length) 'files.notices'
        Assert-True (Test-Path -LiteralPath (Join-Path $out 'THIRD-PARTY-NOTICES.txt')) 'the notices were not copied to OutDir'

        # The explicit parameters give the same defines, and a custom -NoticesPath is used.
        $custom = Join-Path $fixtures 'custom-notices.txt'
        [System.IO.File]::WriteAllText($custom, "custom notices`r`n", $utf8)
        $out2 = Get-ScratchFolder 'pkg-explicit-runtime'
        $fake2 = New-FakeIscc 'fake-iscc-explicit' (Join-Path $out2 'Clink-Setup.exe')
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out2, '-MinRuntime', '10.0.0', '-IsccPath', $fake2.Cmd,
            '-DotNetRuntimeUrl', $rtUrl, '-DotNetRuntimeSha256', $rtSha256.ToUpperInvariant(), '-DotNetRuntimeVersion', $rtVersion, '-NoticesPath', $custom, '-RequireInstaller', '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -eq 0) "explicit parameters failed: $($r.Out) $($r.Err)"
        $recorded2 = [System.IO.File]::ReadAllText($fake2.Args)
        Assert-True ($recorded2.Contains('/DDotNetRuntimeSha256=' + $rtSha256 + ' ')) "the sha256 was not normalised to lower case: $recorded2"
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $out2 'THIRD-PARTY-NOTICES.txt')) -ceq "custom notices`r`n") 'the custom notices were not used'
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $out2 'package.json') | ConvertFrom-Json).files.notices.size -eq 16) 'files.notices.size of the custom file'
    }

    Invoke-Test 'package.ps1 -RequireInstaller is refused without runtime info, with bad runtime info, or with a missing notices file (nothing is run or written)' {
        $fake = New-FakeIscc 'fake-iscc-refuse'
        $good = @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-MinRuntime', '10.0.0', '-IsccPath', $fake.Cmd, '-RequireInstaller', '-SkipVersionCheck')
        $cases = [ordered]@{
            'no runtime info'          = @()
            'only a URL'               = @('-DotNetRuntimeUrl', $rtUrl)
            'info plus explicit'       = @('-RuntimeInfoPath', $runtimeInfo, '-DotNetRuntimeVersion', $rtVersion)
            'missing info file'        = @('-RuntimeInfoPath', (Join-Path $fixtures 'nope.json'))
            'version below the floor'  = @('-DotNetRuntimeUrl', (Get-ExpectedDotNetRuntimeUrl '10.0.1'), '-DotNetRuntimeSha256', $rtSha256, '-DotNetRuntimeVersion', '10.0.1')
            'version not x.y.z'        = @('-DotNetRuntimeUrl', $rtUrl, '-DotNetRuntimeSha256', $rtSha256, '-DotNetRuntimeVersion', ($rtVersion + '-rc.1'))
            'URL of another version'   = @('-DotNetRuntimeUrl', (Get-ExpectedDotNetRuntimeUrl '10.99.0'), '-DotNetRuntimeSha256', $rtSha256, '-DotNetRuntimeVersion', $rtVersion)
            'URL on another host'      = @('-DotNetRuntimeUrl', $rtUrl.Replace('builds.dotnet.microsoft.com', 'example.org'), '-DotNetRuntimeSha256', $rtSha256, '-DotNetRuntimeVersion', $rtVersion)
            'URL not https'            = @('-DotNetRuntimeUrl', $rtUrl.Replace('https://', 'http://'), '-DotNetRuntimeSha256', $rtSha256, '-DotNetRuntimeVersion', $rtVersion)
            'sha256 of 63 characters'  = @('-DotNetRuntimeUrl', $rtUrl, '-DotNetRuntimeSha256', $rtSha256.Substring(1), '-DotNetRuntimeVersion', $rtVersion)
            'sha256 not hex'           = @('-DotNetRuntimeUrl', $rtUrl, '-DotNetRuntimeSha256', ('zz' * 32), '-DotNetRuntimeVersion', $rtVersion)
            'an apostrophe in the sha256'    = @('-DotNetRuntimeUrl', $rtUrl, '-DotNetRuntimeSha256', ($rtSha256.Substring(1) + "'"), '-DotNetRuntimeVersion', $rtVersion)
            'missing notices file'     = @('-RuntimeInfoPath', $runtimeInfo, '-NoticesPath', (Join-Path $fixtures 'no-notices.txt'))
        }
        $expected = @{
            'no runtime info'          = 'needs the \.NET runtime inputs'
            'only a URL'               = 'together'
            'info plus explicit'       = 'exclude each other'
            'missing info file'        = 'Runtime info file not found'
            'version below the floor'  = 'below the committed minimum'
            'version not x.y.z'        = 'is not x\.y\.z'
            'URL of another version'   = 'is not the expected'
            'URL on another host'      = 'not allow-listed'
            'URL not https'            = 'must be https'
            'sha256 of 63 characters'  = 'is not 64 hex'
            'sha256 not hex'           = 'is not 64 hex'
            'an apostrophe in the sha256' = 'forbidden character'
            'missing notices file'     = 'notices file not found'
        }
        foreach ($name in $cases.Keys) {            $out = Get-ScratchFolder ('pkg-refuse-' + ($name -replace '\W', '-'))
            $r = Invoke-ScriptChild $packageScript ($good + @('-OutDir', $out) + $cases[$name])
            Assert-True ($r.Code -ne 0) "accepted: $name"
            Assert-True (($r.Out + $r.Err) -match $expected[$name]) "no '$($expected[$name])' message for: $name : $($r.Out) $($r.Err)"
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'package.json'))) "wrote package.json for: $name"
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $out 'Clink.exe'))) "copied files before refusing: $name"
            Assert-True (-not (Test-Path -LiteralPath $fake.Args)) "the fake ISCC ran for: $name"
        }
        # A refusal for a runtime info file with a missing field names the field.
        $partial = Join-Path $fixtures 'partial.json'
        [System.IO.File]::WriteAllText($partial, ('{"version":"' + $rtVersion + '","url":"' + $rtUrl + '"}'), $utf8)
        $out = Get-ScratchFolder 'pkg-refuse-partial'
        $r = Invoke-ScriptChild $packageScript ($good + @('-OutDir', $out, '-RuntimeInfoPath', $partial))
        Assert-True ($r.Code -ne 0 -and (($r.Out + $r.Err) -match "no 'sha256'")) "a runtime info file without sha256 was not refused clearly: $($r.Out) $($r.Err)"
    }

    Invoke-Test 'package.ps1 without -RequireInstaller and without runtime info warns and builds the installer without the runtime defines' {
        $out = Get-ScratchFolder 'pkg-no-runtime'
        $fake = New-FakeIscc 'fake-iscc-noruntime' (Join-Path $out 'Clink-Setup.exe')
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-IsccPath', $fake.Cmd, '-SkipVersionCheck', '-SkipSetupSizeCheck')
        Assert-True ($r.Code -eq 0) "exit code $($r.Code): $($r.Out) $($r.Err)"
        Assert-True (($r.Out + $r.Err) -match 'WITHOUT the runtime task') "no warning: $($r.Out) $($r.Err)"
        $recorded = [System.IO.File]::ReadAllText($fake.Args)
        Assert-True ($recorded -notmatch 'DDotNetRuntime') "runtime defines were passed anyway: $recorded"
        Assert-True ($recorded -match '/DNoticesFile=') "NoticesFile is always passed: $recorded"
    }

    Invoke-Test 'package.ps1 -SkipInstaller needs no runtime info and still ships the notices' {
        $out = Get-ScratchFolder 'pkg-skip-notices'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $out, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "exit code $($r.Code): $($r.Out) $($r.Err)"
        Assert-True ((Get-Content -Raw -LiteralPath (Join-Path $out 'package.json') | ConvertFrom-Json).files.notices.sha256 -eq $repoNoticesHash) 'files.notices'
    }
    Invoke-Test 'package.ps1 has LF line endings only' {
        Assert-True (-not ([System.IO.File]::ReadAllText($packageScript).Contains("`r"))) 'package.ps1 contains a CR'
    }
    # ------------------------------------------------------------------ make-manifest.ps1 (throwaway keys)
    Invoke-Test 'make-manifest.ps1 writes exeFramework and minRuntime from package.json, signed' {
        $keys = @()
        foreach ($i in 1..2) {
            $cp = New-Object System.Security.Cryptography.CngKeyCreationParameters
            $cp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
            $cng = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $cp)
            try {
                $pkcs8 = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
                $blob = $cng.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
            } finally { $cng.Dispose() }
            $header = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
            $keys += [pscustomobject]@{
                Private = "-----BEGIN PRIVATE KEY-----`n" + [Convert]::ToBase64String($pkcs8) + "`n-----END PRIVATE KEY-----`n"
                Public  = [Convert]::ToBase64String([byte[]]($header + $blob[8..71]))
            }
        }
        $feedJson = Join-Path $scratch 'feed.json'
        $feedText = '{"schema":1,"feedUrl":"https://downloads.example/clink/","stableKeys":["' + $keys[0].Public + '"],"devKeys":["' + $keys[1].Public + '"]}'
        [System.IO.File]::WriteAllText($feedJson, $feedText, $utf8)

        $pkgDir = Get-ScratchFolder 'manifest'
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $pkgDir, '-MinRuntime', '10.0.0', '-SkipInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "package.ps1 failed: $($r.Out) $($r.Err)"
        # make-manifest needs an installer entry: add a fixture setup exe.
        $setupName = 'Clink-Setup.exe'
        New-FixtureFile -Path (Join-Path $pkgDir $setupName) -Length 3000
        $pkgFile = Join-Path $pkgDir 'package.json'
        $pkg = Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json
        $pkg.files | Add-Member -NotePropertyName installer -NotePropertyValue ([pscustomobject]@{
            file = $setupName; size = 3000; sha256 = (Get-FileHash -LiteralPath (Join-Path $pkgDir $setupName) -Algorithm SHA256).Hash.ToLowerInvariant() })
        [System.IO.File]::WriteAllText($pkgFile, ($pkg | ConvertTo-Json -Depth 6), $utf8)

        $manifestScript = Join-Path $toolsDir 'make-manifest.ps1'
        $base = 'https://downloads.example/clink/releases/v0.4.2/'
        $manifestArgs = @('-Channel', 'stable', '-Version', '0.4.2', '-Tag', 'v0.4.2', '-BaseUrl', $base, '-NotesUrl', 'https://example.org/notes', '-PackageDir', $pkgDir, '-FeedJson', $feedJson)
        $r = Invoke-ScriptChild $manifestScript $manifestArgs @{ CLINK_FEED_SIGNING_KEY = $keys[0].Private }
        Assert-True ($r.Code -eq 0) "make-manifest.ps1 failed: $($r.Out) $($r.Err)"
        Assert-True (Test-Path -LiteralPath (Join-Path $pkgDir 'stable.json.sig')) 'stable.json.sig is missing'
        $manifest = Get-Content -Raw -LiteralPath (Join-Path $pkgDir 'stable.json') | ConvertFrom-Json
        Assert-True ($manifest.minRuntime -eq '10.0.0') "minRuntime is '$($manifest.minRuntime)'"
        Assert-True ($manifest.assets.exeFramework.url -eq ($base + 'Clink-framework.exe')) 'exeFramework url'
        Assert-True ($manifest.assets.exeFramework.size -eq 2000) 'exeFramework size'
        Assert-True ($manifest.assets.exeFramework.sha256 -eq $pkg.files.exeFramework.sha256) 'exeFramework sha256'
        Assert-True ($manifest.assets.exe.url -eq ($base + 'Clink.exe') -and $manifest.assets.exe.size -eq 5000) 'exe entry'
        Assert-True ($manifest.assets.installer.url -eq ($base + $setupName)) 'installer entry'

        # A package.json whose minRuntime is not X.Y.Z must not produce a manifest.
        $bad = Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json
        $bad.minRuntime = '10.0'
        [System.IO.File]::WriteAllText($pkgFile, ($bad | ConvertTo-Json -Depth 6), $utf8)
        Remove-Item -LiteralPath (Join-Path $pkgDir 'stable.json'), (Join-Path $pkgDir 'stable.json.sig') -Force
        $r = Invoke-ScriptChild $manifestScript $manifestArgs @{ CLINK_FEED_SIGNING_KEY = $keys[0].Private }
        Assert-True ($r.Code -ne 0) 'accepted minRuntime 10.0'
        Assert-True (($r.Out + $r.Err) -match 'is not X\.Y\.Z') "no 'is not X.Y.Z' message: $($r.Out) $($r.Err)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $pkgDir 'stable.json'))) 'wrote a manifest with a bad minRuntime'

        # A package.json without exeFramework (an old package) must not produce a manifest either.
        $old = Get-Content -Raw -LiteralPath $pkgFile | ConvertFrom-Json
        $old.minRuntime = '10.0.0'
        $old.files.PSObject.Properties.Remove('exeFramework')
        [System.IO.File]::WriteAllText($pkgFile, ($old | ConvertTo-Json -Depth 6), $utf8)
        $r = Invoke-ScriptChild $manifestScript $manifestArgs @{ CLINK_FEED_SIGNING_KEY = $keys[0].Private }
        Assert-True ($r.Code -ne 0) 'accepted a package without exeFramework'
    }

    # ------------------------------------------------------------------ upload-feed.ps1 (text only; it needs pwsh 7 and a bucket)
    Invoke-Test 'upload-feed.ps1 lists the framework assets and checks the exeFramework manifest entry' {
        $text = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'upload-feed.ps1'))
        foreach ($name in "'Clink-framework.exe'", "'Clink-framework.exe.sha256'", "'exeFramework'") {
            Assert-True ($text.Contains($name)) "upload-feed.ps1 does not mention $name"
        }
    }

    # ------------------------------------------------------------------ check-published.ps1 size guards (-SkipRun)
    $checkScript = Join-Path $toolsDir 'check-published.ps1'
    $mib = 1MB
    function Invoke-SizeCheck {
        param([long]$ScMiB, [long]$FxMiB)
        $dir = Get-ScratchFolder ("sizes-$ScMiB-$FxMiB")
        $sc = Join-Path $dir 'sc.exe'; $fx = Join-Path $dir 'fx.exe'
        New-FixtureFile -Path $sc -Length ($ScMiB * $mib) -Zero
        New-FixtureFile -Path $fx -Length ($FxMiB * $mib) -Zero
        $r = Invoke-ScriptChild $checkScript @('-ScExe', $sc, '-FxExe', $fx, '-SkipRun')
        Remove-Item -LiteralPath $dir -Recurse -Force
        $r
    }
    Invoke-Test 'check-published.ps1 size guards accept the real-world sizes' {
        $r = Invoke-SizeCheck 76 32
        Assert-True ($r.Code -eq 0) "rejected 76/32 MiB: $($r.Out) $($r.Err)"
    }
    Invoke-Test 'check-published.ps1 size guards reject out-of-range and mixed-up sizes' {
        foreach ($case in @(@(60, 32), @(95, 32), @(76, 25), @(76, 45), @(66, 38), @(32, 76))) {
            $r = Invoke-SizeCheck $case[0] $case[1]
            Assert-True ($r.Code -ne 0) "accepted SC $($case[0]) MiB / FX $($case[1]) MiB"
        }
    }
    Invoke-Test 'check-published.ps1 refuses the same file twice' {
        $dir = Get-ScratchFolder 'sizes-same'
        $one = Join-Path $dir 'one.exe'
        New-FixtureFile -Path $one -Length (76 * $mib) -Zero
        $r = Invoke-ScriptChild $checkScript @('-ScExe', $one, '-FxExe', $one, '-SkipRun')
        Assert-True ($r.Code -ne 0) 'accepted the same file for both flavours'
        Remove-Item -LiteralPath $dir -Recurse -Force
    }

    Invoke-Test 'check-published.ps1 runs a child with DOTNET_DISABLE_GUI_ERRORS=1, restores the variable and kills a child after the timeout' {
        . ([scriptblock]::Create((Import-ScriptFunction $checkScript @('Invoke-PublishedExe', 'Stop-ProcessTree'))))
        $before = $env:DOTNET_DISABLE_GUI_ERRORS
        $ok = Invoke-PublishedExe $hostExe @('-NoProfile', '-Command', '"Write-Output hi; Write-Output $env:DOTNET_DISABLE_GUI_ERRORS; Write-Output $env:SOME_TEST_VALUE"') 60 @{ SOME_TEST_VALUE = 'xyz' }
        Assert-True ($ok.Code -eq 0) "exit code $($ok.Code): $($ok.Err)"
        Assert-True ($ok.Out -match 'hi' -and $ok.Out -match '(?m)^1\s*$' -and $ok.Out -match 'xyz') "child output: $($ok.Out)"
        Assert-True ($env:DOTNET_DISABLE_GUI_ERRORS -eq $before) 'DOTNET_DISABLE_GUI_ERRORS was not restored'
        Assert-True (-not $env:SOME_TEST_VALUE) 'the extra environment variable leaked'
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $message = $null
        try { Invoke-PublishedExe $hostExe @('-NoProfile', '-Command', '"Start-Sleep -Seconds 60"') 2 | Out-Null } catch { $message = $_.Exception.Message }
        Assert-True ($message -and $message -match 'Timed out') "no timeout error: $message"
        Assert-True ($watch.Elapsed.TotalSeconds -lt 40) "the timeout took $($watch.Elapsed.TotalSeconds) s"
    }

    Invoke-Test 'check-published.ps1 Stop-ProcessTree survives a taskkill that writes to stderr or fails (the PS 5.1 race) and still kills the process' {
        . ([scriptblock]::Create((Import-ScriptFunction $checkScript @('Stop-ProcessTree'))))
        $stubDir = Get-ScratchFolder 'taskkill stub'
        $stubs = @{
            'stderr-exit1' = "@echo off`r`necho ERROR: The process was not found. 1>&2`r`nexit /b 128`r`n"
            'silent-exit0' = "@echo off`r`nexit /b 0`r`n"
        }
        foreach ($name in $stubs.Keys) {
            $stub = Join-Path $stubDir "$name.cmd"
            [System.IO.File]::WriteAllText($stub, $stubs[$name], [System.Text.Encoding]::ASCII)
            $child = Start-Process -FilePath $hostExe -ArgumentList @('-NoProfile', '-Command', '"Start-Sleep -Seconds 120"') -PassThru -WindowStyle Hidden
            try {
                $null = $child.Handle
                $savedPreference = $ErrorActionPreference
                $ErrorActionPreference = 'Stop'   # what the real script runs under
                try { Stop-ProcessTree -Process $child -TaskKill $stub } finally { $ErrorActionPreference = $savedPreference }
                Assert-True $child.HasExited "the process survived ($name stub): Kill() was not reached"
            } finally { if (-not $child.HasExited) { $child.Kill() } }
        }
    }

    Invoke-Test 'check-published.ps1 has the TimeoutSeconds range, the whole-token MustContain, the notices self-test and the N-1 regression' {
        $text = [System.IO.File]::ReadAllText($checkScript)
        foreach ($needle in '[ValidateRange(1, 3600)] [int]$TimeoutSeconds', "'--unittest', 'ThirdPartyNotices'", 'DOTNET_ROOT', 'hostfxr.dll', "'--packaging-check', 'Framework'", "'(^|\s)' + [regex]::Escape(`$needle) + '(\s|`$)'", 'CLINK_TESTS_NO_SKIP') {
            Assert-True ($text.Contains($needle)) "check-published.ps1 does not contain $needle"
        }
        $r = Invoke-ScriptChild $checkScript @('-ScExe', $scExe, '-FxExe', $fxExe, '-TimeoutSeconds', '0', '-SkipRun')
        Assert-True ($r.Code -ne 0) 'accepted -TimeoutSeconds 0'
        $r = Invoke-ScriptChild $checkScript @('-ScExe', $scExe, '-FxExe', $fxExe, '-TimeoutSeconds', '3601', '-SkipRun')
        Assert-True ($r.Code -ne 0) 'accepted -TimeoutSeconds 3601'
    }

    # ------------------------------------------------------------------ publish-flavours.ps1 (validation only; nothing is published)
    $publishScript = Join-Path $toolsDir 'publish-flavours.ps1'
    Invoke-Test 'publish-flavours.ps1 refuses an -OutRoot outside the repo and a bad -Configuration' {
        $outside = Join-Path $scratch 'outside-root'
        $r = Invoke-ScriptChild $publishScript @('-OutRoot', $outside)
        Assert-True ($r.Code -ne 0) 'accepted an -OutRoot outside the repo'
        Assert-True (($r.Out + $r.Err) -match 'inside the repo') "no 'inside the repo' message: $($r.Out) $($r.Err)"
        Assert-True (-not (Test-Path -LiteralPath $outside)) 'created the outside folder'
        $r = Invoke-ScriptChild $publishScript @('-Configuration', 'Foo')
        Assert-True ($r.Code -ne 0) 'accepted -Configuration Foo'
    }
    Invoke-Test 'publish-flavours.ps1 Resolve-OutRoot accepts only paths strictly inside the repo' {
        . ([scriptblock]::Create((Import-ScriptFunction $publishScript @('Resolve-OutRoot'))))
        Assert-True ((Resolve-OutRoot -Path (Join-Path $repo 'dist') -RepoRoot $repo) -eq (Join-Path $repo 'dist')) 'rejected <repo>\dist'
        foreach ($bad in $repo, (Join-Path $repo '..\elsewhere'), (Join-Path $repo 'dist\..\..\x'), $scratch, ($repo + '-sibling')) {
            $threw = $false
            try { Resolve-OutRoot -Path $bad -RepoRoot $repo | Out-Null } catch { $threw = $true }
            Assert-True $threw "accepted '$bad'"
        }
    }
    Invoke-Test 'publish-flavours.ps1 Remove-FlavourDirectory refuses reparse points and leaves the junction target untouched' {
        . ([scriptblock]::Create((Import-ScriptFunction $publishScript @('Remove-FlavourDirectory'))))
        $target = Get-ScratchFolder 'junction-target'
        $keep = Join-Path $target 'keep.txt'
        [System.IO.File]::WriteAllText($keep, 'keep', $utf8)
        $victim = Get-ScratchFolder 'junction-victim'
        [System.IO.File]::WriteAllText((Join-Path $victim 'a.txt'), 'a', $utf8)
        $link = Join-Path $victim 'inner'
        $direct = Join-Path $scratch 'junction-direct'
        & cmd.exe /c mklink /J "$link" "$target" | Out-Null
        & cmd.exe /c mklink /J "$direct" "$target" | Out-Null
        try {
            Assert-True ((Test-Path -LiteralPath $link) -and (Test-Path -LiteralPath $direct)) 'could not create the junctions'
            $threw = $false
            try { Remove-FlavourDirectory -Path $victim } catch { $threw = $true }
            Assert-True $threw 'deleted a tree that holds a junction'
            Assert-True (Test-Path -LiteralPath $keep) 'the junction target was touched (nested junction)'
            Assert-True (Test-Path -LiteralPath (Join-Path $victim 'a.txt')) 'the victim tree was partly deleted'
            $threw = $false
            try { Remove-FlavourDirectory -Path $direct } catch { $threw = $true }
            Assert-True $threw 'deleted a junction directly'
            Assert-True (Test-Path -LiteralPath $keep) 'the junction target was touched (direct junction)'
        } finally {
            foreach ($j in $link, $direct) { if (Test-Path -LiteralPath $j) { [System.IO.Directory]::Delete($j) } }
        }
        Assert-True (Test-Path -LiteralPath $keep) 'the junction target is gone after removing the links'
        $plain = Get-ScratchFolder 'plain-delete'
        New-Item -ItemType Directory -Force -Path (Join-Path $plain 'sub') | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $plain 'sub\f.txt'), 'f', $utf8)
        Remove-FlavourDirectory -Path $plain
        Assert-True (-not (Test-Path -LiteralPath $plain)) 'a plain tree was not deleted'
    }
    Invoke-Test 'publish-flavours.ps1 and build.ps1 Test-DotNetSdk runs "dotnet --version" in the repo root and refuses a failing or silent dotnet' {
        $stubDir = Get-ScratchFolder 'dotnet stub'
        $cwdFile = Join-Path $stubDir 'cwd.txt'
        $ok = Join-Path $stubDir 'dotnet-ok.cmd'
        $failing = Join-Path $stubDir 'dotnet-fail.cmd'
        $silent = Join-Path $stubDir 'dotnet-silent.cmd'
        [System.IO.File]::WriteAllText($ok, "@echo off`r`ncd > `"$cwdFile`"`r`necho 10.0.100`r`nexit /b 0`r`n", [System.Text.Encoding]::ASCII)
        [System.IO.File]::WriteAllText($failing, "@echo off`r`necho A compatible .NET SDK was not found. 1>&2`r`nexit /b 145`r`n", [System.Text.Encoding]::ASCII)
        [System.IO.File]::WriteAllText($silent, "@echo off`r`nexit /b 0`r`n", [System.Text.Encoding]::ASCII)
        . ([scriptblock]::Create((Import-ScriptFunction $publishScript @('Test-DotNetSdk'))))
        $before = (Get-Location).ProviderPath
        Assert-True ((Test-DotNetSdk -Exe $ok -WorkingDirectory $repo) -eq $true) 'publish-flavours: rejected a dotnet that prints a version'
        Assert-True (([System.IO.File]::ReadAllText($cwdFile).Trim()) -eq $repo.TrimEnd('\')) "publish-flavours: dotnet ran in '$([System.IO.File]::ReadAllText($cwdFile).Trim())', not the repo root"
        Assert-True ((Get-Location).ProviderPath -eq $before) 'publish-flavours: the location was not restored'
        Assert-True ((Test-DotNetSdk -Exe $failing -WorkingDirectory $repo) -eq $false) 'publish-flavours: accepted a failing dotnet'
        Assert-True ((Test-DotNetSdk -Exe $silent -WorkingDirectory $repo) -eq $false) 'publish-flavours: accepted a dotnet that prints nothing'
        Assert-True ((Test-DotNetSdk -Exe (Join-Path $stubDir 'no-such-dotnet.cmd') -WorkingDirectory $repo) -eq $false) 'publish-flavours: accepted a missing dotnet'
        Assert-True ((Get-Location).ProviderPath -eq $before) 'publish-flavours: the location was not restored after failures'
        # build.ps1 has its own copy (it reads $root).
        Remove-Item -LiteralPath $cwdFile -Force
        $root = $repo
        . ([scriptblock]::Create((Import-ScriptFunction (Join-Path $repo 'build.ps1') @('Test-DotNetSdk'))))
        Assert-True ((Test-DotNetSdk $ok) -eq $true) 'build.ps1: rejected a dotnet that prints a version'
        Assert-True (([System.IO.File]::ReadAllText($cwdFile).Trim()) -eq $repo.TrimEnd('\')) 'build.ps1: dotnet did not run in the repo root'
        Assert-True ((Test-DotNetSdk $failing) -eq $false) 'build.ps1: accepted a failing dotnet'
        Assert-True ((Test-DotNetSdk $silent) -eq $false) 'build.ps1: accepted a dotnet that prints nothing'
        Assert-True ((Get-Location).ProviderPath -eq $before) 'build.ps1: the location was not restored'
    }

    Invoke-Test 'publish-flavours.ps1 and build.ps1 run every dotnet call from the repo root (Push-Location / Pop-Location in try / finally)' {
        $publishText = [System.IO.File]::ReadAllText($publishScript)
        Assert-True ($publishText -match '(?s)Push-Location -LiteralPath \$root\s+try \{.*& \$DotNet @arguments.*\} finally \{ Pop-Location \}') 'publish-flavours.ps1: the publish loop is not inside Push-Location $root / try / finally Pop-Location'
        $buildText = [System.IO.File]::ReadAllText((Join-Path $repo 'build.ps1'))
        Assert-True ($buildText -match '(?s)Push-Location -LiteralPath \$root\s+try \{ & \$dotnet build.*\} finally \{ Pop-Location \}') 'build.ps1: dotnet build is not inside Push-Location $root / try / finally Pop-Location'
    }

    Invoke-Test 'publish-flavours.ps1 Assert-NoReparseParent refuses a junction anywhere between the output folder and the repo root' {
        . ([scriptblock]::Create((Import-ScriptFunction $publishScript @('Assert-NoReparseParent'))))
        $fakeRepo = Get-ScratchFolder 'fake-repo'
        $elsewhere = Get-ScratchFolder 'junction-elsewhere'
        $plainRoot = Join-Path $fakeRepo 'dist'
        Assert-NoReparseParent -Path $plainRoot -RepoRoot $fakeRepo   # nothing exists yet: fine
        New-Item -ItemType Directory -Force -Path (Join-Path $fakeRepo 'plain\sub') | Out-Null
        Assert-NoReparseParent -Path (Join-Path $fakeRepo 'plain\sub\dist') -RepoRoot $fakeRepo
        $link = Join-Path $fakeRepo 'linked'
        & cmd.exe /c mklink /J "$link" "$elsewhere" | Out-Null
        try {
            Assert-True (Test-Path -LiteralPath $link) 'could not create the junction'
            foreach ($candidate in $link, (Join-Path $link 'dist'), (Join-Path $link 'a\b\dist')) {
                $threw = $false
                try { Assert-NoReparseParent -Path $candidate -RepoRoot $fakeRepo } catch { $threw = $true }
                Assert-True $threw "accepted a path below the junction: $candidate"
            }
            # a link deeper in the chain (a\link\dist) is found as well
            New-Item -ItemType Directory -Force -Path (Join-Path $fakeRepo 'deep') | Out-Null
            $deepLink = Join-Path $fakeRepo 'deep\link'
            & cmd.exe /c mklink /J "$deepLink" "$elsewhere" | Out-Null
            try {
                $threw = $false
                try { Assert-NoReparseParent -Path (Join-Path $deepLink 'dist') -RepoRoot $fakeRepo } catch { $threw = $true }
                Assert-True $threw 'accepted a path below a junction in a middle folder'
            } finally { if (Test-Path -LiteralPath $deepLink) { [System.IO.Directory]::Delete($deepLink) } }
        } finally { if (Test-Path -LiteralPath $link) { [System.IO.Directory]::Delete($link) } }
        Assert-True (Test-Path -LiteralPath $elsewhere) 'the junction target is gone'
    }

    Invoke-Test 'package.ps1 no longer has a one-line if followed by an assignment' {
        $text = [System.IO.File]::ReadAllText($packageScript)
        Assert-True ($text -notmatch '(?m)^.*\}[ \t]{2,}\$MinRuntime = ') 'the if / assignment line in package.ps1 is joined again'
    }

    # ------------------------------------------------------------------ build.ps1 / script hygiene
    Invoke-Test 'build.ps1 and the packaging scripts have no BOM and only ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI)' {
        $files = @(Join-Path $repo 'build.ps1') + @(Get-ChildItem -LiteralPath $toolsDir -Recurse -Filter '*.ps1' -File | ForEach-Object { $_.FullName })
        Assert-True ($files.Count -ge 15) "only $($files.Count) scripts found"
        foreach ($file in $files) {
            $bytes = [System.IO.File]::ReadAllBytes($file)
            Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) "$file has a BOM"
            Assert-True (-not ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1)) "$file has non-ASCII bytes"
        }
    }

    Invoke-Test 'build.ps1 packages through publish-flavours and both flavour folders' {
        $text = [System.IO.File]::ReadAllText((Join-Path $repo 'build.ps1'))
        foreach ($needle in 'publish-flavours.ps1', 'dist\sc\Clink.exe', 'dist\fx\Clink.exe', '-FrameworkExePath') {
            Assert-True ($text.Contains($needle)) "build.ps1 does not contain $needle"
        }
    }

    # ------------------------------------------------------------------ workflows
    function Get-RunBlock {
        # The text of every run: script of a workflow (block scalar or single line).
        param([string]$Text)
        $lines = $Text -split "`r?`n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^(\s*)(?:- )?run:\s*(.*)$') {
                $indent = $Matches[1].Length
                $rest = $Matches[2]
                if ($rest -match '^[|>][-+]?\s*$') {
                    $body = New-Object System.Collections.Generic.List[string]
                    for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                        $line = $lines[$j]
                        if ($line.Trim().Length -gt 0 -and ($line.Length - $line.TrimStart().Length) -le $indent) { break }
                        $body.Add($line)
                    }
                    ($body -join "`n")
                } else { $rest }
            }
        }
    }

    $workflowFiles = Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml'
    Invoke-Test 'every uses: in every workflow is pinned to a 40-hex commit SHA' {
        $count = 0
        foreach ($wf in $workflowFiles) {
            foreach ($line in ([System.IO.File]::ReadAllText($wf.FullName) -split "`r?`n")) {
                if ($line -match '^\s*(?:- )?uses:\s*(\S+)') {
                    $count++
                    Assert-True ($Matches[1] -cmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$') "$($wf.Name): not pinned to a SHA: $($line.Trim())"
                }
            }
        }
        Assert-True ($count -ge 10) "only $count uses: lines found"
    }

    Invoke-Test 'no workflow run: script contains a ${{ }} expression' {
        $blocks = 0
        foreach ($wf in $workflowFiles) {
            foreach ($block in @(Get-RunBlock ([System.IO.File]::ReadAllText($wf.FullName)))) {
                $blocks++
                Assert-True (-not $block.Contains('${{')) "$($wf.Name): a run: script contains a template expression: $(($block -split "`n" | Where-Object { $_.Contains('${{') } | Select-Object -First 1))"
            }
        }
        Assert-True ($blocks -ge 20) "only $blocks run: scripts found (parser broken?)"
    }

    Invoke-Test 'every workflow installs the SDK from global.json and global.json pins it' {
        foreach ($wf in $workflowFiles) {
            $text = [System.IO.File]::ReadAllText($wf.FullName)
            Assert-True ($text.Contains('global-json-file: global.json')) "$($wf.Name) does not use global-json-file"
            Assert-True ($text -notmatch 'dotnet-version\s*:') "$($wf.Name) still has dotnet-version"
        }
        $global = Get-Content -Raw -LiteralPath (Join-Path $repo 'global.json') | ConvertFrom-Json
        Assert-True ($global.sdk.version -match '^\d+\.\d+\.\d+$') 'global.json sdk.version is not exact'
        Assert-True ($global.sdk.rollForward -eq 'disable') 'global.json rollForward is not disable'
    }

    Invoke-Test 'both release workflows publish the four asset names, the framework hash and the framework exe to package.ps1' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            foreach ($asset in 'dist/package/Clink.exe', 'dist/package/Clink.exe.sha256', 'dist/package/Clink-framework.exe', 'dist/package/Clink-framework.exe.sha256') {
                Assert-True ($text -match ("'" + [regex]::Escape($asset) + "'")) "$name does not list '$asset' as a release asset"
            }
            foreach ($needle in 'fx_sha256', 'FX_SHA256', 'Clink-framework.exe.sha256 does not describe', '-FrameworkExePath dist/fx/Clink.exe', '-ExePath dist/sc/Clink.exe',
                'publish-flavours.ps1', 'check-published.ps1', 'fail_on_unmatched_files: true') {
                Assert-True ($text.Contains($needle)) "$name does not contain '$needle'"
            }
        }
    }

    Invoke-Test 'both release workflows upload Clink-Setup.exe.sha256, verify it, and check the release assets by exact name before publishing' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            Assert-True ($text.Contains("'dist/package/Clink-Setup.exe', 'dist/package/Clink-Setup.exe.sha256'")) "${name}: Clink-Setup.exe.sha256 is not in the upload list"
            Assert-True ($text.Contains('Clink-Setup.exe.sha256 does not describe Clink-Setup.exe')) "${name}: the pre-upload verify does not check Clink-Setup.exe.sha256"
            $start = $text.IndexOf('- name: Verify the release assets')
            Assert-True ($start -gt 0) "${name}: no 'Verify the release assets' step"
            $create = $text.IndexOf('- name: Create GitHub release (draft)')
            $publishFeed = $text.IndexOf('- name: Publish to the update feed')
            $publishRelease = $text.IndexOf('- name: Publish the GitHub release')
            Assert-True ($create -gt 0 -and $start -gt $create -and $start -lt $publishFeed -and $start -lt $publishRelease) "${name}: the asset check is not between the draft upload and the publishing steps"
            $step = $text.Substring($start, $publishFeed - $start)
            Assert-True ($step.Contains("'Clink.exe', 'Clink.exe.sha256', 'Clink-framework.exe', 'Clink-framework.exe.sha256', 'Clink-Setup.exe', 'Clink-Setup.exe.sha256', 'THIRD-PARTY-NOTICES.txt'")) "${name}: the asset check lacks one of the seven names"
            Assert-True ($step.Contains('-ceq "v$env:VERSION"') -and $step.Contains('-ceq $name') -and $step.Contains("-ceq 'uploaded'")) "${name}: the asset check is not case-sensitive"
            Assert-True ($step.Contains('throw "Release v$env:VERSION lacks')) "${name}: the asset check does not fail on a missing asset"
            # By release ID, not by tag: draft flag, tag, state, size and digest are all compared.
            foreach ($needle in 'releases/$env:RELEASE_ID', 'ConvertFrom-Json', '$release.draft -ne $true', '$asset.state', '$asset.size', '$local.Length', '$asset.digest', "'sha256:'", '-ceq $expected', '::warning::') {
                Assert-True ($step.Contains($needle)) "${name}: the asset check lacks '$needle'"
            }
            Assert-True (-not $step.Contains('--paginate') -and -not $step.Contains('.Split(')) "${name}: the asset check still joins/splits asset names"
            # The expression ${{ ... }} appears only in env:, never inside the script.
            $runAt = $step.IndexOf('run: |')
            Assert-True ($runAt -gt 0) "${name}: no run block in the asset check"
            Assert-True (-not $step.Substring($runAt).Contains('${{')) "${name}: the asset check interpolates an expression into the script"
            Assert-True ($step.Contains('RELEASE_ID: ${{ steps.draft.outputs.id }}') -and $step.Contains("-cnotmatch '\A[0-9]+\z'")) "${name}: RELEASE_ID is not passed via env and validated as digits"
        }
    }

    Invoke-Test 'both release workflows give the draft step an id, pass RELEASE_ID via env only, and publish by release ID' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            $create = $text.IndexOf('- name: Create GitHub release (draft)')
            Assert-True ($create -gt 0 -and $text.Substring($create, 120).Contains('id: draft')) "${name}: the draft step has no 'id: draft'"
            # steps.draft.outputs.id is only ever the value of an env entry.
            $uses = [regex]::Matches($text, '[^\r\n]*steps\.draft\.outputs\.id[^\r\n]*')
            Assert-True ($uses.Count -eq 2) "${name}: expected 2 uses of steps.draft.outputs.id (verify and publish), found $($uses.Count)"
            foreach ($u in $uses) { Assert-True ($u.Value.Trim() -ceq 'RELEASE_ID: ${{ steps.draft.outputs.id }}') "${name}: steps.draft.outputs.id used outside an env entry: $($u.Value.Trim())" }
            $publishAt = $text.IndexOf('- name: Publish the GitHub release')
            $nextAt = $text.IndexOf("`n      - name:", $publishAt + 10)
            $publish = if ($nextAt -gt 0) { $text.Substring($publishAt, $nextAt - $publishAt) } else { $text.Substring($publishAt) }
            Assert-True ($publish.Contains('RELEASE_ID: ${{ steps.draft.outputs.id }}')) "${name}: the publish step has no RELEASE_ID env"
            Assert-True ($publish.Contains('gh api -X PATCH "repos/$env:GITHUB_REPOSITORY/releases/$env:RELEASE_ID" -F draft=false -f make_latest=')) "${name}: the release is not published by ID"
            Assert-True ($publish -notmatch 'gh release edit') "${name}: the publish step still edits the release by tag"
            Assert-True ($publish.Contains("-cnotmatch '\A[0-9]+\z'")) "${name}: the publish step does not validate RELEASE_ID"
            $publishRun = $publish.Substring($publish.IndexOf('run: |'))
            Assert-True (-not $publishRun.Contains('${{')) "${name}: the publish script interpolates an expression"
        }
        $stable = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release.yml'))
        $dev = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release-dev.yml'))
        $stablePublish = $stable.Substring($stable.IndexOf('- name: Publish the GitHub release'))
        $decideAt = $stable.IndexOf('- name: Decide whether this release becomes latest')
        $feedAt = $stable.IndexOf('- name: Publish to the update feed')
        $verifyAt = $stable.IndexOf('- name: Verify the release assets')
        Assert-True ($verifyAt -gt 0 -and $decideAt -gt $verifyAt -and $decideAt -lt $feedAt) 'release.yml: the make_latest step is not between the asset check and the feed upload'
        $decide = $stable.Substring($decideAt, $feedAt - $decideAt)
        Assert-True ($decide.Contains('id: latest') -and $decide.Contains('Get-MakeLatestValue -Version $env:VERSION -LatestTag $latestTag') -and $decide.Contains('releases/latest" --jq .tag_name')) 'release.yml: make_latest is not computed from the current latest release'
        Assert-True ($decide.Contains('. ./tools/lib/release-latest-lib.ps1') -and $decide.Contains('GITHUB_OUTPUT') -and $decide.Contains('make_latest=$makeLatest')) 'release.yml: the make_latest step does not load the helper or write its output'
        Assert-True ($decide.Contains("elseif (`$latestErr -cmatch '\(HTTP 404\)')") -and $decide -notmatch 'Not Found' -and $decide -notmatch '2>&1' -and $decide -notmatch '\$latestOut\)? -c?match') 'release.yml: only the anchored (HTTP 404) in stderr may mean no latest release, and stderr must stay separate'
        Assert-True ($decide.Contains('Remove-Item -LiteralPath $errFile -ErrorAction SilentlyContinue')) 'release.yml: the stderr temp file is not deleted after reading'
        Assert-True ($decide.Contains('if ($latestCode -eq 0) {') -and $decide.Contains('IsNullOrWhiteSpace($latestOut)') -and $decide.Contains('$latestTag = $latestOut') -and $decide.Contains('else { throw "Could not read the latest release')) 'release.yml: the latest lookup is not exit-code driven'
        Assert-True ($stablePublish.Contains('MAKE_LATEST: ${{ steps.latest.outputs.make_latest }}') -and $stablePublish.Contains("`$env:MAKE_LATEST -cnotmatch '\A(true|false)\z'") -and $stablePublish.Contains('$makeLatest = $env:MAKE_LATEST')) 'release.yml: the publish step does not take make_latest from a validated env'
        Assert-True ($stablePublish -notmatch 'Get-MakeLatestValue' -and $stablePublish -notmatch 'releases/latest"') 'release.yml: the publish step still computes make_latest'
        Assert-True ([regex]::Matches($stable, 'steps\.latest\.outputs\.make_latest').Count -eq 1) 'release.yml: steps.latest.outputs.make_latest is used more than once'
        Assert-True ($stable.Contains('$release.prerelease -ne $false') -and $dev.Contains('$release.prerelease -ne $true')) 'the asset checks do not assert the prerelease flag per channel'
        Assert-True ($stable.Contains("'stable.json', 'stable.json.sig'") -and $stable.Contains('Test-Path -LiteralPath dist/package/stable.json')) 'release.yml: the asset check does not require the channel manifest'
        Assert-True ($dev.Contains("'dev.json', 'dev.json.sig'") -and $dev.Contains('Test-Path -LiteralPath dist/package/dev.json')) 'release-dev.yml: the asset check does not require the channel manifest'
        Assert-True ($stable -notmatch 'target_commitish\s*[-=]' -and $dev -notmatch 'target_commitish\s*[-=]') 'a target_commitish check was added'
        $devPublish = $dev.Substring($dev.IndexOf('- name: Publish the GitHub release'))
        $devPublish = $devPublish.Substring(0, $devPublish.IndexOf('- name: Prune'))
        Assert-True ($devPublish.Contains('-f make_latest=false') -and $devPublish -notmatch 'make_latest=\$') 'release-dev.yml: the dev publish must pass make_latest=false'
    }

    Invoke-Test 'pwsh steps that handle a non-zero native exit reset $LASTEXITCODE (the runner appends an exit-code check)' {
        $targets = @(
            @('release.yml', '- name: Determine version', '$exists = $LASTEXITCODE -eq 0'),
            @('release.yml', '- name: Guard - stable tag, commit on main, release not yet there', '$exists = $LASTEXITCODE -eq 0'),
            @('release.yml', '- name: Decide whether this release becomes latest', '$latestCode = $LASTEXITCODE'),
            @('release-dev.yml', '- name: Guard - dev branch only, release not yet there', '$exists = $LASTEXITCODE -eq 0')
        )
        foreach ($t in $targets) {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $t[0]))
            $at = $text.IndexOf($t[1])
            Assert-True ($at -ge 0) "$($t[0]): no step '$($t[1])'"
            $next = $text.IndexOf("`n      - name:", $at + 10)
            $uses = $text.IndexOf("`n      # ", $at + 10)
            $end = $text.Length
            foreach ($c in $next, $uses) { if ($c -gt 0 -and $c -lt $end) { $end = $c } }
            $step = $text.Substring($at, $end - $at)
            $capture = $step.IndexOf($t[2])
            Assert-True ($capture -ge 0) "$($t[0]) / $($t[1]): the exit code capture '$($t[2])' is missing"
            $reset = $step.IndexOf('$global:LASTEXITCODE = 0', $capture)
            Assert-True ($reset -gt $capture) "$($t[0]) / $($t[1]): no '`$global:LASTEXITCODE = 0' after the exit code capture"
            $afterReset = $step.Substring($reset + '$global:LASTEXITCODE = 0'.Length)
            Assert-True ($afterReset -notmatch '(?m)^\s*(\$\w+\s*=\s*)?\(?\s*&?\s*(gh|git|aws|dotnet)\s') "$($t[0]) / $($t[1]): a native gh/git/aws/dotnet call follows the reset"
        }
    }

    Invoke-Test 'the runner wrapper (appended LASTEXITCODE exit) fails a step unless it resets $LASTEXITCODE' {
        $wrapperTail = 'if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }'
        $variants = [ordered]@{
            'no reset'   = @("`$ErrorActionPreference='stop'", '$PSNativeCommandUseErrorActionPreference = $false', 'cmd /c exit 1', '$null = ''x''', $wrapperTail)
            'with reset' = @("`$ErrorActionPreference='stop'", '$PSNativeCommandUseErrorActionPreference = $false', 'cmd /c exit 1', '$global:LASTEXITCODE = 0', '$null = ''x''', $wrapperTail)
        }
        $expected = @{ 'no reset' = 1; 'with reset' = 0 }
        foreach ($key in $variants.Keys) {
            $file = Join-Path $scratch ('runner-wrapper-' + [guid]::NewGuid().ToString('N') + '.ps1')
            [System.IO.File]::WriteAllText($file, ($variants[$key] -join "`r`n") + "`r`n", $utf8)
            try {
                $run = Start-Process -FilePath $hostExe -ArgumentList @('-NoProfile', '-Command', "`". '$file'`"") -Wait -PassThru -NoNewWindow
                Assert-True ($run.ExitCode -eq $expected[$key]) "wrapper variant '$key' exited $($run.ExitCode), expected $($expected[$key])"
            } finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
        }
    }

    Invoke-Test 'Get-MakeLatestValue: higher gives true, equal or lower false, no latest true, garbage throws' {
        . (Join-Path $toolsDir 'lib\release-latest-lib.ps1')
        Assert-True ((Get-MakeLatestValue -Version '0.4.2' -LatestTag 'v0.4.1') -ceq 'true') 'higher patch'
        Assert-True ((Get-MakeLatestValue -Version '0.10.0' -LatestTag 'v0.9.9') -ceq 'true') 'numeric (not text) comparison'
        Assert-True ((Get-MakeLatestValue -Version '1.0.0' -LatestTag 'v0.99.99') -ceq 'true') 'higher major'
        Assert-True ((Get-MakeLatestValue -Version '0.4.1' -LatestTag 'v0.4.1') -ceq 'false') 'equal'
        Assert-True ((Get-MakeLatestValue -Version '0.4.0' -LatestTag 'v0.4.1') -ceq 'false') 'lower'
        Assert-True ((Get-MakeLatestValue -Version '0.9.9' -LatestTag 'v0.10.0') -ceq 'false') 'lower (numeric)'
        Assert-True ((Get-MakeLatestValue -Version '0.4.2' -LatestTag '') -ceq 'true') 'missing latest (empty)'
        Assert-True ((Get-MakeLatestValue -Version '0.4.2') -ceq 'true') 'missing latest (omitted)'
        Assert-True ((Get-MakeLatestValue -Version '0.4.2' -LatestTag "  v0.4.1`r`n") -ceq 'true') 'latest tag with surrounding whitespace'
        Assert-True ((Get-MakeLatestValue -Version '0.4.1' -LatestTag " v0.4.1 ") -ceq 'false') 'equal latest tag with surrounding whitespace'
        foreach ($bad in 'v99999999999.0.0', 'v0.99999999999.0', 'latest', '0.4.1', 'v0.4', 'v0.4.1-dev.3', 'V0.4.1', 'v0.4.1 extra', 'HTTP 500') {
            $threw = $false
            try { [void](Get-MakeLatestValue -Version '0.4.2' -LatestTag $bad) } catch { $threw = $true }
            Assert-True $threw "garbage latest tag '$bad' did not throw"
        }
        foreach ($bad in '', '0.4', 'v0.4.2', '0.4.2-dev.1', '0.4.2; rm') {
            $threw = $false
            try { [void](Get-MakeLatestValue -Version $bad -LatestTag 'v0.4.1') } catch { $threw = $true }
            Assert-True $threw "garbage version '$bad' did not throw"
        }
    }

    Invoke-Test 'both release workflows wire the runtime resolver, the notices check, the freshness gate, the pinned Inno Setup and the notices asset' {
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            $build = $text.Substring($text.IndexOf("`n  build:"), $text.IndexOf("`n  publish:") - $text.IndexOf("`n  build:"))
            $publish = $text.Substring($text.IndexOf("`n  publish:"))
            # Order inside the build job: publish -> notices check -> resolver -> freshness -> check-published -> Inno -> package.
            $order = @('publish-flavours.ps1', 'make-notices.ps1 -Check -AssetsFile Clink/obj_sc/project.assets.json', 'id: runtime', 'resolve-dotnet-runtime.ps1',
                'check-runtime-freshness.ps1 -AssetsFile Clink/obj_sc/project.assets.json -LatestVersion $env:LATEST_RUNTIME', 'check-published.ps1', 'Install Inno Setup (pinned, hash-verified)', 'tools/package.ps1')
            $previous = -1
            foreach ($needle in $order) {
                $at = $build.IndexOf($needle)
                Assert-True ($at -gt $previous) "$name build job: '$needle' is missing or out of order"
                $previous = $at
            }
            Assert-True ($build -match 'LATEST_RUNTIME:\s*\$\{\{ steps\.runtime\.outputs\.version \}\}') "${name}: the freshness step does not take the resolver's version through env"
            Assert-True ($build -match 'dotnet-runtime\.json') "${name}: the resolver JSON is not saved to RUNNER_TEMP"
            Assert-True ($build -match '-RuntimeInfoPath \(Join-Path \$env:RUNNER_TEMP ''dotnet-runtime\.json''\) -RequireInstaller') "${name}: package.ps1 is not called with -RuntimeInfoPath and -RequireInstaller"
            Assert-True ($build.Contains('-TimeoutSeconds 90')) "${name}: check-published is not called with -TimeoutSeconds 90"
            # Inno Setup: hash-pinned download into the runner temp folder, hash compared before the installer runs.
            Assert-True ($build.Contains('INNO_SHA256: 9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732')) "${name}: Inno Setup SHA-256 pin missing"
            $hashAt = $build.IndexOf('Get-FileHash -LiteralPath $setup -Algorithm SHA256')
            $runAt = $build.IndexOf('Start-Process -FilePath $setup')
            Assert-True ($hashAt -gt 0 -and $runAt -gt $hashAt -and $build.Contains('-ne $env:INNO_SHA256')) "${name}: the Inno Setup hash is not checked before the installer runs"
            # Notices asset and its hash flow.
            Assert-True ($text.Contains("'dist/package/THIRD-PARTY-NOTICES.txt'")) "${name}: THIRD-PARTY-NOTICES.txt is not a release asset"
            Assert-True ($text.Contains('notices_sha256: ${{ steps.package.outputs.notices_sha256 }}') -and $build.Contains('notices_sha256=$($pkg.files.notices.sha256)')) "${name}: notices_sha256 is not a build output"
            Assert-True ($publish.Contains('NOTICES_SHA256: ${{ needs.build.outputs.notices_sha256 }}') -and $publish.Contains('dist/package/THIRD-PARTY-NOTICES.txt -Algorithm SHA256')) "${name}: the publish job does not verify the notices hash"
            Assert-True ($publish.Contains('fail_on_unmatched_files: true')) "${name}: fail_on_unmatched_files is gone"
            Assert-True ($build -notmatch 'secrets\.') "${name}: the build job references secrets"
        }
        $stable = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release.yml'))
        $dev = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'release-dev.yml'))
        Assert-True ($stable -match 'LatestVersion \$env:LATEST_RUNTIME -FailOnStale') 'release.yml: the freshness check does not fail on a stale runtime'
        Assert-True ($dev -notmatch 'FailOnStale') 'release-dev.yml: the freshness check must only warn'
    }

    Invoke-Test 'build.yml package dry run checks the notices and does not run the resolver (the installer uses the committed floor)' {
        $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'build.yml'))
        $job = $text.Substring($text.IndexOf('package-dry-run:'))
        Assert-True ($job.Contains('make-notices.ps1 -Check -AssetsFile Clink/obj_sc/project.assets.json')) 'the dry run does not check the notices'
        Assert-True ($job -notmatch '(?m)^\s*(?:- )?run:.*resolve-dotnet-runtime|(?m)^\s+\./tools/resolve-dotnet-runtime') 'the dry run runs the resolver'
        Assert-True ($job.Contains('-TimeoutSeconds 90')) 'the dry run does not pass -TimeoutSeconds 90'
    }

    Invoke-Test 'build.yml has a package dry run without environment or secrets' {
        $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir 'build.yml'))
        $start = $text.IndexOf('package-dry-run:')
        Assert-True ($start -ge 0) 'no package-dry-run job'
        $job = $text.Substring($start)
        foreach ($needle in 'publish-flavours.ps1', 'check-published.ps1', 'package.ps1', '-RequireInstaller', 'Install Inno Setup (pinned, hash-verified)', 'MinimumRuntimeVersion', 'Get-ExpectedDotNetRuntimeUrl') { Assert-True ($job.Contains($needle)) "dry run lacks $needle" }
        Assert-True ($job -notmatch '-SkipInstaller') 'the dry run still skips the installer'
        Assert-True ($job.Contains('INNO_SHA256: 9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732')) 'dry run: Inno Setup SHA-256 pin missing'
        $hashAt = $job.IndexOf('Get-FileHash -LiteralPath $setup -Algorithm SHA256')
        $runAt = $job.IndexOf('Start-Process -FilePath $setup')
        Assert-True ($hashAt -gt 0 -and $runAt -gt $hashAt -and $job.Contains('-ne $env:INNO_SHA256')) 'dry run: the Inno Setup hash is not checked before the installer runs'
        Assert-True ($job.Contains("'0' * 64")) 'dry run: the dummy runtime SHA-256 is gone'
        Assert-True ($job -notmatch 'upload-artifact') 'the dry run uploads something'
        $inc = [regex]::Matches($text, 'timeout-minutes:\s*20').Count
        Assert-True ($inc -ge 1) 'Check the published exes has no timeout-minutes: 20'
        Assert-True ($job -notmatch 'secrets\.') 'the dry run references secrets'
        Assert-True ($job -notmatch '(?m)^\s*environment:') 'the dry run uses an environment'
        Assert-True ($job -notmatch 'contents:\s*write') 'the dry run can write contents'
        Assert-True ($job.Contains('run-script-tests.ps1')) 'the dry run does not run the script tests under pwsh'
    }

    Invoke-Test 'package.ps1 size guard: a real-ISCC setup exe of 0 bytes is refused, one over 1 MiB is accepted' {
        $zeroOut = Get-ScratchFolder 'pkg-size-zero'
        $zeroSetup = Join-Path $zeroOut 'Clink-Setup.exe'
        $dir = Get-ScratchFolder 'fake-iscc-zero'
        $zeroCmd = Join-Path $dir 'ISCC.cmd'
        [System.IO.File]::WriteAllText($zeroCmd, "@echo off`r`ntype nul > `"$zeroSetup`"`r`nexit /b 0`r`n", [System.Text.Encoding]::ASCII)
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $zeroOut, '-MinRuntime', '10.0.0', '-IsccPath', $zeroCmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -ne 0) "a 0-byte setup exe was accepted: $($r.Out)"
        Assert-True (($r.Out + $r.Err) -match 'only 0 bytes') "no size message: $($r.Out) $($r.Err)"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $zeroOut 'package.json'))) 'wrote package.json for a 0-byte setup exe'

        $bigOut = Get-ScratchFolder 'pkg-size-big'
        $bigSource = Join-Path $fixtures 'big-setup.bin'
        New-FixtureFile -Path $bigSource -Length 1200000 -Zero
        $bigCmd = Join-Path (Get-ScratchFolder 'fake-iscc-big') 'ISCC.cmd'
        [System.IO.File]::WriteAllText($bigCmd, "@echo off`r`ncopy /y `"$bigSource`" `"$(Join-Path $bigOut 'Clink-Setup.exe')`" > nul`r`nexit /b 0`r`n", [System.Text.Encoding]::ASCII)
        $r = Invoke-ScriptChild $packageScript @('-ExePath', $scExe, '-FrameworkExePath', $fxExe, '-Version', '0.4.2', '-OutDir', $bigOut, '-MinRuntime', '10.0.0', '-IsccPath', $bigCmd, '-RuntimeInfoPath', $runtimeInfo, '-RequireInstaller', '-SkipVersionCheck')
        Assert-True ($r.Code -eq 0) "a 1.2 MB setup exe was refused: $($r.Out) $($r.Err)"
    }

    Invoke-Test 'the committed runtime floor is at or above the runtimeVersion of tools/notices/sources.json' {
        $sources = Get-Content -Raw -LiteralPath (Join-Path $toolsDir 'notices\sources.json') | ConvertFrom-Json
        Assert-True ([version]$script:MinimumRuntimeVersion -ge [version]$sources.runtimeVersion) "MinimumRuntimeVersion $($script:MinimumRuntimeVersion) is below sources.json runtimeVersion $($sources.runtimeVersion)"
    }

    Invoke-Test 'both release workflows pick the resolver JSON by pattern, and every Check the published exes step has a 20 minute timeout' {
        foreach ($name in 'release.yml', 'release-dev.yml', 'build.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            $step = $text.IndexOf('- name: Check the published exes')
            Assert-True ($step -ge 0) "${name}: no Check the published exes step"
            Assert-True ($text.Substring($step, 200) -match 'timeout-minutes: 20\b') "${name}: Check the published exes has no timeout-minutes: 20"
        }
        foreach ($name in 'release.yml', 'release-dev.yml') {
            $text = [System.IO.File]::ReadAllText((Join-Path $workflowDir $name))
            Assert-True ($text.Contains("-match '^\{.*\}$'")) "${name}: the resolver JSON line is not picked by pattern"
            Assert-True ($text -notmatch '\$lines -join "`n"\)\.Trim\(\)') "${name}: the resolver output lines are still joined"
        }
        $resolver = [System.IO.File]::ReadAllText((Join-Path $toolsDir 'resolve-dotnet-runtime.ps1'))
        Assert-True ([regex]::Matches($resolver, "ProgressPreference = 'SilentlyContinue'").Count -ge 2) 'resolver does not silence progress around both downloads'
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "$($script:passed) passed, $($script:failed) failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
