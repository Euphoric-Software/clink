<#
.SYNOPSIS
  Produces fresh documentation screenshots of the current Clink UI: no TEST marker, sample clips, nothing on screen, nothing touched.

.DESCRIPTION
  1. Generates sample clips (Clink.exe --dev-make-samples: drawn frames, no screen capture) into a scratch dev folder.
  2. Starts ONE hidden test instance of the exe with  --dev <folder> --dev-quiet --dev-no-marker --dev-demo-status <Game>:
     off-screen and cloaked, never activated, silent, not recording, its own settings/clips (see tools/screenshots/README.md).
  3. Drives it with UI Automation patterns only (select a tab, toggle/invoke a button, resize with TransformPattern) and saves each
     window with PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT) as a PNG with real transparency at the window corners.
  No input is sent, no window is activated, the screen is never captured, and only the process this script started is stopped.
  A watcher samples the foreground window every 100 ms; if the test instance ever holds it for 200 ms in a row, the watcher stops that
  process itself and the run ends.

  Pictures go to -OutputDir (default: a scratch folder under %TEMP%; a folder inside docs\ is refused). The script never writes into docs\: copy pictures there by hand.

.PARAMETER Exe          The Clink.exe to run (a build of this branch). Default: Release build if there is one, else Debug.
.PARAMETER OutputDir    Where the PNGs go (not inside docs\). Default: %TEMP%\clink-doc-shots\out
.PARAMETER WorkDir      Scratch folder for the dev instance (it makes a "dev" folder in it and deletes it again). Default: %TEMP%\clink-doc-shots\work
.PARAMETER Game         The game the header pretends to record. Must not start with "dev" or "-". Default: NeonDrift
.PARAMETER WideSize     Window size for the wide shots, in DIP (device-independent pixels): WIDTHxHEIGHT. Default: 1600 wide, with the aspect ratio of docs\screenshots\main-window.png.
.PARAMETER NarrowSize   Window size for the narrow shots, in DIP. Default: 470x800
.PARAMETER DumpTree     Also write the UI Automation tree of the main window to ui-tree.txt in the output folder.
.EXAMPLE
  .\tools\screenshots\capture.ps1 -OutputDir $env:TEMP\clink-shots
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$OutputDir = (Join-Path $env:TEMP 'clink-doc-shots\out'),
    [string]$WorkDir = (Join-Path $env:TEMP 'clink-doc-shots\work'),
    [string]$Game = 'NeonDrift',
    [string]$WideSize,
    [string]$NarrowSize = '470x800',
    [switch]$DumpTree
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# ---------------------------------------------------------------- inputs
if ($Game -match '^(dev|-)') { throw "-Game '$Game' must not start with 'dev' or '-' (the dev switches would take it for an option)." }
if (-not $Exe) {
    $candidates = @('Release', 'Debug' | ForEach-Object { Join-Path $repo "Clink\bin\$_\net10.0-windows10.0.26100.0\win-x64\Clink.exe" } | Where-Object { Test-Path -LiteralPath $_ })
    if ($candidates.Count -eq 0) { throw 'No Clink.exe found: build first (build.ps1 -Configuration Release) or pass -Exe.' }
    $Exe = $candidates | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } -Descending | Select-Object -First 1
}
$Exe = (Resolve-Path -LiteralPath $Exe).Path
function ConvertTo-ShotSize([string]$text) {
    if ($text -notmatch '^(\d+)x(\d+)$') { throw "bad size '$text' (WIDTHxHEIGHT)" }
    [pscustomobject]@{ W = [int]$Matches[1]; H = [int]$Matches[2] }
}
Add-Type -AssemblyName System.Drawing
$docs = Join-Path $repo 'docs\screenshots'
if ($WideSize) { $wide = ConvertTo-ShotSize $WideSize }
else {
    # the wide layout needs >= 900 DIP; 1600 wide with the proportions of the existing main-window picture
    $ref = Join-Path $docs 'main-window.png'
    $ratio = 1462.0 / 2560.0
    if (Test-Path -LiteralPath $ref) {
        $img = [System.Drawing.Image]::FromFile($ref)
        try { $ratio = [double]$img.Height / [double]$img.Width } finally { $img.Dispose() }
    }
    $wide = [pscustomobject]@{ W = 1600; H = [int][math]::Round(1600 * $ratio) }
}
$narrow = ConvertTo-ShotSize $NarrowSize
if ($wide.W -lt 900) { throw "the wide size must be at least 900 DIP wide (got $($wide.W))." }

# ---------------------------------------------------------------- never write into docs\ (junctions and symlinks are resolved)
if (-not ('ShotNative' -as [type])) {
    Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ShotNative.cs') -Raw) -ReferencedAssemblies System.Drawing
}
# protected: docs\ of this checkout and docs\ of the canonical checkout (this one may be a git worktree of it)
$protectedRoots = New-Object System.Collections.Generic.List[string]
$protectedRoots.Add([ShotNative]::FinalPath((Join-Path $repo 'docs')))
$commonDir = $null
try { $commonDir = (& git -C $repo rev-parse --path-format=absolute --git-common-dir 2>$null | Select-Object -First 1) } catch { $commonDir = $null }
if ($commonDir -and (Test-Path -LiteralPath $commonDir)) {
    $canonicalDocs = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($commonDir))) 'docs'
    $protectedRoots.Add([ShotNative]::FinalPath($canonicalDocs))
} else {
    Write-Warning 'git did not report the canonical checkout: only docs\ of this checkout is protected.'
}
$repoFinal = [ShotNative]::FinalPath($repo)
function Test-InsideRoot([string]$final, [string]$root) {
    $a = $final.TrimEnd('\') + '\'
    $b = $root.TrimEnd('\') + '\'
    $a.StartsWith($b, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NotProtected([string]$path, [string]$what, [string[]]$roots) {
    $final = [ShotNative]::FinalPath($path)
    foreach ($root in $roots) {
        if (Test-InsideRoot $final $root) {
            throw "$what ($path, which leads to $final) is inside ${root}: this script never writes there. Pick another folder and copy the pictures by hand."
        }
    }
}
Assert-NotProtected $OutputDir '-OutputDir' $protectedRoots.ToArray()
Assert-NotProtected $WorkDir '-WorkDir' (@($protectedRoots) + $repoFinal)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
Assert-NotProtected $OutputDir '-OutputDir' $protectedRoots.ToArray()   # again, now that it exists
# no picture of an earlier run may survive a shot that fails this time
foreach ($n in 'main-window', 'clip-tab', 'app-tab', 'alerts-tab', 'toast', 'trim-editor', 'share-window', 'narrow', 'narrow-library') {
    $old = Join-Path $OutputDir "$n.png"
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force }
}

# ---------------------------------------------------------------- the scratch dev folder (only one this script made is ever deleted)
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$WorkDir = (Resolve-Path -LiteralPath $WorkDir).Path
Assert-NotProtected $WorkDir '-WorkDir' (@($protectedRoots) + $repoFinal)
$devDir = Join-Path $WorkDir 'dev'
$devMarker = Join-Path $devDir '.clink-doc-shots'
if (Test-Path -LiteralPath $devDir) {
    if (-not (Test-Path -LiteralPath $devMarker)) { throw "$devDir exists and was not made by this script: pick another -WorkDir." }
    Remove-Item -LiteralPath $devDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $devDir | Out-Null
[System.IO.File]::WriteAllText($devMarker, 'made by tools/screenshots/capture.ps1')
$createdDev = $true
$clipsDir = Join-Path $devDir 'clips'
$indexFile = Join-Path $devDir 'clips.json'

# ---------------------------------------------------------------- tools
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
[ShotNative]::UsePhysicalPixels()   # this thread: window sizes and UI Automation sizes are physical pixels
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

$script:proc = $null
$script:shots = @()

function Find-All($root, [string]$name, $type) {
    # always a name AND a control type: the App tab has a CheckBox called "Clips" next to the Clips tab
    $conds = [System.Windows.Automation.Condition[]]@(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
    $cond = [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conds)
    $found = $root.FindAll($TS::Descendants, $cond)
    $list = @(); foreach ($e in $found) { $list += $e }; , $list
}
function Find-One($root, [string]$name, $type) { $all = Find-All $root $name $type; if ($all.Count) { $all[0] } else { $null } }
function Find-ByType($root, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
    $found = $root.FindAll($TS::Descendants, $cond)
    $list = @(); foreach ($e in $found) { $list += $e }; , $list
}
function Stop-Instance {
    if ($script:proc) {
        try {
            if (-not $script:proc.HasExited) { $script:proc.Kill() }
            [void]$script:proc.WaitForExit(5000)
        } catch { }
    }
}
function Test-Foreground {
    if ([ShotNative+Watch]::Tripped) {
        $note = [ShotNative+Watch]::Note
        Stop-Instance
        throw "STOPPED: the test instance took the foreground ($note)."
    }
}
function Wait-For([scriptblock]$what, [int]$seconds, [string]$label) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $until) {
        Test-Foreground
        $r = & $what
        if ($r) { return $r }
        Start-Sleep -Milliseconds 250
    }
    throw "timed out after $seconds s waiting for $label"
}
function Select-Tab($main, [string]$name) {
    $tab = Wait-For { Find-One $main $name $CT::TabItem } 10 "the '$name' tab"
    $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Invoke-Button($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Switch-Toggle($element) { $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }

function Get-Windows { [ShotNative]::Windows([uint32]$script:proc.Id) }
function Get-MainWindow {
    Get-Windows | Where-Object { $_.ClassName -like 'HwndWrapper*' -and $_.Title -eq 'Clink' } | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
}
function New-Shot($hwnd, [string]$name, [int]$tries = 8) {
    # drawn again until two pictures in a row are identical (thumbnails and the video frame arrive a moment after the window)
    $path = Join-Path $OutputDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }   # a failed attempt leaves no stale picture
    $last = 0; $w = 0; $h = 0
    for ($i = 0; $i -lt $tries; $i++) {
        Test-Foreground
        $hash = [ShotNative]::Capture($hwnd, $path, [ref]$w, [ref]$h)
        if ($i -gt 0 -and $hash -eq $last) { break }
        $last = $hash
        Start-Sleep -Milliseconds 700
    }
    $blank = [ShotNative]::IsBlank($path)
    Write-Host ("  {0}  {1}x{2}" -f $name, $w, $h)
    if ($blank) { Write-Warning "$name is blank (all black or transparent): see the troubleshooting section of tools/screenshots/README.md" }
    $script:shots += [pscustomobject]@{ Name = $name; Path = $path; Width = $w; Height = $h; Blank = $blank }
}
function Set-WindowSize($hwnd, $size) {
    [ShotNative]::UsePhysicalPixels()
    $scale = [ShotNative]::Scale($hwnd)
    $pw = [int][math]::Round($size.W * $scale); $ph = [int][math]::Round($size.H * $scale)
    $done = $false
    try {
        $tp = $AE::FromHandle($hwnd).GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
        if ($tp.Current.CanResize) { $tp.Resize($pw, $ph); $done = $true }
    } catch { Write-Host "  (TransformPattern unavailable: $($_.Exception.Message))" }
    if (-not $done) {
        Write-Host "  (the window reports CanResize = false or no TransformPattern: falling back to SetWindowPos NOMOVE|NOZORDER|NOACTIVATE)"
        [void][ShotNative]::Resize($hwnd, $pw, $ph)
    }
    Start-Sleep -Milliseconds 1500
    $now = Get-Windows | Where-Object { $_.Handle -eq $hwnd }
    Write-Host ("  resized to {0}x{1} DIP (x{2} = {3}x{4} px); the window is now {5}x{6}" -f $size.W, $size.H, $scale, $pw, $ph, $now.Width, $now.Height)
}
function Wait-NewWindow($before, [int]$seconds, [string]$label, [scriptblock]$pick) {
    Wait-For {
        $new = @(Get-Windows | Where-Object { $before -notcontains $_.Handle })
        if ($DumpTree) { $new | ForEach-Object { Write-Host "    new: $_" } }
        & $pick $new
    } $seconds $label
}
function Close-Window($hwnd) {
    try {
        $AE::FromHandle($hwnd).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    } catch { Write-Host "  (could not close 0x$($hwnd.ToInt64().ToString('X')) politely: $($_.Exception.Message))" }
    Start-Sleep -Milliseconds 600
}
function Get-HandleList { @(Get-Windows | ForEach-Object { $_.Handle }) }

# ---------------------------------------------------------------- run
Write-Host "exe:    $Exe"
Write-Host "work:   $devDir"
Write-Host "output: $OutputDir"
Write-Host ("wide:   {0}x{1} DIP   narrow: {2}x{3} DIP" -f $wide.W, $wide.H, $narrow.W, $narrow.H)
[ShotNative+Watch]::Start()
try {
    # ---- sample clips (drawn frames; no capture)
    $samplesLog = Join-Path $WorkDir 'samples.txt'
    $gen = Start-Process -FilePath $Exe -ArgumentList @('--dev-make-samples', "`"$clipsDir`"") -NoNewWindow -PassThru -RedirectStandardOutput $samplesLog
    $null = $gen.Handle   # keep the process handle open so ExitCode is still readable after it exits
    if (-not $gen.WaitForExit(120000)) {
        try { $gen.Kill() } catch { }   # only this generator process
        throw "--dev-make-samples did not finish within 120 s and was stopped - see $samplesLog"
    }
    $gen.WaitForExit()   # flush the redirected output
    if ($gen.ExitCode -ne 0) { throw "--dev-make-samples exited $($gen.ExitCode) - see $samplesLog" }
    $mp4s = @(Get-ChildItem -LiteralPath $clipsDir -Filter *.mp4 -ErrorAction SilentlyContinue)
    if (-not (Test-Path -LiteralPath $indexFile)) { throw "--dev-make-samples did not write $indexFile" }
    if ($mp4s.Count -eq 0) { throw "--dev-make-samples wrote no .mp4 files into $clipsDir" }
    $index = @((Get-Content -LiteralPath $indexFile -Raw | ConvertFrom-Json) | ForEach-Object { $_ })   # PS 5.1 hands a JSON array over as one object
    $missing = @($index | Where-Object { -not (Test-Path -LiteralPath $_.Path) })
    if ($index.Count -eq 0 -or $missing.Count -gt 0) { throw "clips.json lists $($index.Count) clips, $($missing.Count) of them missing on disk." }
    Write-Host ("samples: {0} clips, index {1}" -f $mp4s.Count, $indexFile)

    # ---- the instance
    $instArgs = @('--dev', "`"$devDir`"", '--dev-quiet', '--dev-no-marker', '--dev-demo-status', "`"$Game`"")
    $script:proc = Start-Process -FilePath $Exe -ArgumentList $instArgs -PassThru
    [ShotNative+Watch]::Add($script:proc)
    Write-Host "instance: pid $($script:proc.Id)"

    $mainWin = Wait-For { Get-MainWindow } 40 'the main window'
    $hwnd = $mainWin.Handle
    $main = $AE::FromHandle($hwnd)
    Write-Host "main window: $mainWin"

    # ---- wide layout: settings tabs on the left, the library pane on the right
    Set-WindowSize $hwnd $wide
    Wait-For { (Find-All $main 'Trim' $CT::Button).Count -ge 3 } 40 'the clip rows' | Out-Null
    Start-Sleep -Seconds 3   # thumbnails
    if ($DumpTree) {
        $lines = New-Object System.Collections.Generic.List[string]
        function Add-TreeLine($e, [int]$depth) {
            if ($depth -gt 14) { return }
            $c = $e.Current
            $lines.Add(('{0}{1} "{2}" id={3}' -f ('  ' * $depth), $c.ControlType.ProgrammaticName.Replace('ControlType.', ''), $c.Name, $c.AutomationId))
            foreach ($k in $e.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) { Add-TreeLine $k ($depth + 1) }
        }
        Add-TreeLine $main 0
        [System.IO.File]::WriteAllLines((Join-Path $OutputDir 'ui-tree.txt'), $lines.ToArray())
    }

    # nested trims: open every closed chevron (a ToggleButton named "Show 1 trim of ...", "Hide ..." once open)
    Select-Tab $main 'Capture'
    for ($round = 0; $round -lt 8; $round++) {
        # Find-ByType returns ", $list" (one array object): assign it, then walk it. Piping it would make $_ the whole array.
        $buttons = Find-ByType $main $CT::Button
        $closed = $null
        # ClipGrouping.ToggleName: "Show 1 trim of X" / "Show 3 trims of X"; only on-screen chevrons that stand for 1 or more trims
        foreach ($b in $buttons) { if ($b.Current.Name -like 'Show [1-9]* of *' -and -not $b.Current.IsOffscreen) { $closed = $b; break } }
        if (-not $closed) { break }
        try { Switch-Toggle $closed } catch { Write-Host "  (could not open a trim list: $($_.Exception.Message))"; break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 800
    New-Shot $hwnd 'main-window.png'

    foreach ($pair in @(@('Clips', 'clip-tab.png'), @('App', 'app-tab.png'), @('Alerts', 'alerts-tab.png'))) {
        Select-Tab $main $pair[0]
        Start-Sleep -Milliseconds 700
        New-Shot $hwnd $pair[1]
    }

    # ---- a toast: the Alerts tab's first Preview button (Tag "clip"); it is shown off-screen and drawn from there
    $before = Get-HandleList
    $preview = Find-One $main 'Preview' $CT::Button
    if ($preview) {
        Invoke-Button $preview
        try {
            $toast = Wait-NewWindow $before 8 'the toast' {
                param($new)
                $mainNow = Get-MainWindow
                $new | Where-Object { $_.Title -eq 'Clink' -and $_.Width -lt $mainNow.Width -and $_.Width -gt 100 } | Select-Object -First 1
            }
            Start-Sleep -Milliseconds 800
            New-Shot $toast.Handle 'toast.png'
        } catch { Write-Warning "toast: $($_.Exception.Message) (skipped)" }
    } else { Write-Warning 'no Preview button found (toast skipped)' }

    # ---- the trim editor: the first sample row's Trim button, opened once
    Select-Tab $main 'Capture'
    Start-Sleep -Milliseconds 600
    $before = Get-HandleList
    $trimButton = Find-One $main 'Trim' $CT::Button
    if ($trimButton) {
        Invoke-Button $trimButton
        try {
            $trim = Wait-NewWindow $before 20 'the trim editor' {
                param($new)
                $new | Where-Object { $_.Title -ne 'Clink' -and $_.Title -ne '' -and $_.Width -gt 100 -and $_.Height -gt 60 } | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
            }
            Start-Sleep -Seconds 3   # the first frame
            New-Shot $trim.Handle 'trim-editor.png' 12
            Close-Window $trim.Handle
        } catch { Write-Warning "trim editor: $($_.Exception.Message) (skipped)" }
    } else { Write-Warning 'no Trim button found (trim editor skipped)' }

    # ---- the share window: the Button named exactly "Share" (a clip that is not shared yet); never "Copy link", never the folder button
    $before = Get-HandleList
    $share = Find-One $main 'Share' $CT::Button
    if ($share) {
        Invoke-Button $share
        try {
            $sw = Wait-NewWindow $before 10 'the share window' {
                param($new)
                $new | Where-Object { $_.Title -eq 'Share clip' } | Select-Object -First 1
            }
            Start-Sleep -Seconds 2
            New-Shot $sw.Handle 'share-window.png'
            Close-Window $sw.Handle
        } catch { Write-Warning "share window: $($_.Exception.Message) (skipped)" }
    } else { Write-Warning 'no Share button found (share window skipped)' }

    # ---- narrow layout: the settings tabs, then the Library tab
    Set-WindowSize $hwnd $narrow
    Select-Tab $main 'Capture'
    Start-Sleep -Milliseconds 800
    New-Shot $hwnd 'narrow.png'
    Select-Tab $main 'Library'
    Start-Sleep -Milliseconds 1200
    New-Shot $hwnd 'narrow-library.png'
}
finally {
    [ShotNative+Watch]::Stop()
    Stop-Instance
    if ($createdDev -and (Test-Path -LiteralPath $devMarker)) {
        try { Remove-Item -LiteralPath $devDir -Recurse -Force } catch { Write-Warning "could not delete $($devDir): $($_.Exception.Message)" }
    }
    Write-Host ''
    Write-Host "output folder: $OutputDir"
    foreach ($s in $script:shots) {
        $len = if (Test-Path -LiteralPath $s.Path) { (Get-Item -LiteralPath $s.Path).Length } else { 0 }
        Write-Host ("  {0}  {1}x{2}  {3} bytes{4}" -f $s.Path, $s.Width, $s.Height, $len, $(if ($s.Blank) { '  BLANK' } else { '' }))
    }
    $watch = [ShotNative+Watch]
    Write-Host ("foreground watch: tripped = {0}; samples = {1}, of the test instance = {2}; longest own-foreground run = {3} ms" -f $watch::Tripped, $watch::Samples, $watch::OwnSamples, $watch::LongestOwnMs)
    if ($watch::Tripped) { Write-Warning ([string]$watch::Note) }
}
Write-Host "done: $($script:shots.Count) pictures in $OutputDir"
