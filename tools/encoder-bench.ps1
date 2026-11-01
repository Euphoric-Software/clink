<#
.SYNOPSIS
  Quality metrics and the pass checks for a Clink encoder bench (Clink.exe --encoder-bench), as a Markdown report.

.DESCRIPTION
  Developer tool, Windows PowerShell 5.1. Reads the *.json / *.mp4 pairs the bench wrote into -OutFolder, measures VMAF (mean and 5th
  percentile), SSIM and PSNR of each MP4 against -Reference with ffmpeg (setpts=PTS-STARTPTS on both inputs, shortest=1,
  ts_sync_mode=nearest, two threads, BelowNormal), and writes <OutFolder>\encoder-bench-report.md with one row per run and the design's
  pass checks.

  Only run JSONs benched against -Reference are used (same full path, case-insensitive, and the same file size as the JSON recorded);
  others are skipped and listed as "skipped: <reason>". Curves and checks are built per GROUP of (codec, --seconds limit, keyframe
  seconds); the report shows each group on its own and warns loudly when a codec has more than one group or a group mixes Clink builds.

  The CBR ("off") runs of a group are a curve of VMAF (mean and 5th percentile) against ACHIEVED video Mbps (video bits / (frames out /
  fps), container excluded), interpolated in log size. Every vbr, lowlatency-off and vbr-lowlatency-off run is judged against that curve
  (never extrapolated: outside the curve's range the result is "outside the CBR range"; a size within 0.5% of an end point counts as
  that end point and is labelled "at edge"):

    VBR            PASS by branch A or B.
                   A, same quality at least 10% smaller: the run's mean VMAF >= the CBR curve's mean at (run Mbps / 0.9) (no allowance)
                   and its p5 >= the curve's p5 there - 1.0. For a monotonic curve: CBR needs >= 1/0.9 of the bytes for the run's quality.
                   B, better at equal size: mean >= 1.0 VMAF above the curve at the run's own Mbps.
                   A missing mean or p5 on either side means the branch cannot be evaluated; it is never a pass.
                   Every vbr and lowlatency-off row also shows an "equal-quality size": the CBR curve inverted at the run's mean VMAF
                   (log-size interpolation), shown as run size / CBR size, e.g. "87% of CBR". It is information; branches A / B decide.
    Low latency off  PASS when the mean VMAF is >= 1 above the curve at the run's own Mbps. (vbr-lowlatency-off is compared with the
                   vbr curve for this check, so the VBR gain is not counted twice.)
    Peak cap       a vbr mode's busiest second must be <= 1.65 x its mean second and <= 1.65 x the requested Mbps (the replay ring is
                   sized from that peak). Not run for INVALID runs.
    Invalid        a run is INVALID (listed with the reason, left out of the curve and of every check) when: low latency off did not
                   apply (applied lowLatency true or any fallback), a vbr mode did not apply as VBR or its output did not vary
                   ("VBR did not take effect"), outputs came out of order, the encoder dropped frames, the number of frames the reference
                   decodes differs from its ffprobe packet count (whole-clip runs), or the VMAF log's frame count differs from the frames
                   the encoder gave.

  The report also states the reference (codec, size, frame rate, bitrate, colour tags) and warns when it is too low a bitrate to bench
  against, is itself a Clink recording, or is not BT.709 limited range (the bench's decoder asks for BT.709 limited; a conversion lowers
  every score equally); the adapter; and, per run, the encoder evidence. The latency evidence is "held at end" (frames the undrained
  encoder still held). "Max in flight" saturates when feeding faster than real time, and the "feeder wait" is expected there
  (Thread.Sleep(1) is about 15.6 ms): neither says anything about a live recording. The design's live-drop criterion (0 drops while
  recording) needs the Clink.exe --selftest smoke evidence.

  ffmpeg and ffprobe are used in place (the Copium folder's copies); nothing is copied or installed. If ffmpeg has no libvmaf the report
  carries SSIM and PSNR only, with a note, and the VMAF-based checks say "n/a".

  Per-run metrics are cached in <name>.metrics.json beside the run together with the MP4's size and modified time (and the reference's),
  whether libvmaf was present, ffmpeg's version line and the filter-graph version; they are reused only while all of those match, and a
  cached run without a VMAF score is stale whenever libvmaf is present. A measurement without VMAF is only cached when libvmaf is absent.
  -Force measures again.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\encoder-bench.ps1 -OutFolder V:\EuphoricSoftware\Clink\dist\bench1 -Reference C:\clips\reference.mp4
#>
[CmdletBinding()]
param(
    [string]$OutFolder,
    [string]$Reference,
    [string]$FfmpegPath = 'V:\EuphoricSoftware\Copium\node_modules\ffmpeg-static\ffmpeg.exe',
    [string]$FfprobePath = 'V:\EuphoricSoftware\Copium\node_modules\ffprobe-static\bin\win32\x64\ffprobe.exe',
    [ValidateRange(1, 8)][int]$Threads = 2,
    [switch]$Force,
    # Defines the functions and returns (what tools\tests\encoder-bench.tests.ps1 loads).
    [switch]$LibraryOnly
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------ pure functions (tested)

$script:ModeOrder = @('off', 'vbr', 'lowlatency-off', 'vbr-lowlatency-off')
$script:VbrSizeRatio = 0.90          # branch A: VBR uses at most 90% of the bytes of the CBR it is compared with ...
$script:VbrP5Drop = 1.0              # ... for a mean VMAF no lower (no allowance) and a 5th percentile within 1.0
$script:VmafGain = 1.0               # branch B / low latency: "gained at equal size"
$script:EdgeTolerance = 0.005        # a size within 0.5% of a curve end point counts as that end point
$script:PeakCapFactor = 1.65         # a vbr mode's busiest second over its mean second and over the requested Mbps (design: the ring is sized from the peak)
$script:FilterGraphVersion = 'g2'    # bump when the ffmpeg filter graph below changes: it makes every cached metrics file stale
$script:RefHeadroom = 2.0            # the reference should be at least this many times the highest bench Mbps
$script:Eps = 1e-9

# The p-th percentile (0-100) with linear interpolation between the closest ranks. Null for an empty list.
function Get-Percentile {
    param([double[]]$Values, [double]$Percent)
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 1) { return [double]$sorted[0] }
    $rank = ($Percent / 100.0) * ($sorted.Count - 1)
    $lo = [math]::Floor($rank)
    $hi = [math]::Ceiling($rank)
    if ($lo -eq $hi) { return [double]$sorted[[int]$lo] }
    $frac = $rank - $lo
    return [double]($sorted[[int]$lo] + ($sorted[[int]$hi] - $sorted[[int]$lo]) * $frac)
}

# The mode whose runs form the curve a result is judged against: Kind 'Vbr' -> the CBR ("off") curve, Kind 'LowLatency' -> the curve of its
# low-latency-on counterpart. Null = no check.
function Get-BaselineMode {
    param([string]$Mode, [string]$Kind)
    if ($Kind -eq 'Vbr') {
        if ($Mode -eq 'vbr' -or $Mode -eq 'vbr-lowlatency-off') { return 'off' }
    }
    elseif ($Kind -eq 'LowLatency') {
        if ($Mode -eq 'lowlatency-off') { return 'off' }
        if ($Mode -eq 'vbr-lowlatency-off') { return 'vbr' }
    }
    return $null
}

function New-CheckResult {
    param([string]$Result, [string]$Detail)
    [pscustomobject]@{ Result = $Result; Detail = $Detail }
}

function Join-Reasons {
    param($Row)
    return ((@($Row.InvalidReasons) | Where-Object { $_ }) -join '; ')
}

# The group a run belongs to: runs that can share a curve (same codec, --seconds limit and keyframe interval).
function Get-GroupKey {
    param([string]$Codec, $SecondsLimit, $KeyframeSeconds)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $s = 'whole clip'; if ($null -ne $SecondsLimit) { $s = 'first ' + ([double]$SecondsLimit).ToString('0.###', $inv) + ' s' }
    $k = 'unknown'; if ($null -ne $KeyframeSeconds) { $k = ([double]$KeyframeSeconds).ToString('0.##', $inv) + ' s' }
    return ('{0}, {1}, GOP {2}' -f $Codec, $s, $k)
}

# The curve of a mode within a group: one point per valid, measured run of the codec (Mbps = ACHIEVED video Mbps, Mean / P5 = VMAF),
# sorted by Mbps. Failed and INVALID runs and runs without VMAF are not on it. Rows with the same Mbps count once. -Group (when given)
# limits the curve to the rows of that group.
function Get-QualityCurve {
    param($Rows, [string]$Codec, [string]$Mode, [string]$Group = '')
    $byGroup = $PSBoundParameters.ContainsKey('Group')
    $pts = New-Object System.Collections.ArrayList
    $last = $null
    foreach ($r in @($Rows | Where-Object { $_.Codec -eq $Codec -and $_.Mode -eq $Mode -and -not $_.Failed -and -not $_.Invalid -and $null -ne $_.VmafMean -and $_.AchievedMbps -gt 0 -and (-not $byGroup -or [string]$_.Group -eq $Group) } | Sort-Object AchievedMbps)) {
        if ($null -ne $last -and [math]::Abs([math]::Log([double]$r.AchievedMbps) - [math]::Log($last)) -lt $script:Eps) { continue }
        $last = [double]$r.AchievedMbps
        $p5 = $null; if ($null -ne $r.VmafP5) { $p5 = [double]$r.VmafP5 }
        [void]$pts.Add([pscustomobject]@{ Mbps = [double]$r.AchievedMbps; Mean = [double]$r.VmafMean; P5 = $p5 })
    }
    return $pts.ToArray()
}

# The curve at $Mbps, interpolated linearly in log Mbps. Never extrapolated: InRange is false outside [first, last], except that a value
# within 0.5% of an end point counts as that end point (AtEdge = $true). A one-point curve is "in range" only at that point.
# Returns @{ InRange; AtEdge; Mean; P5; MinMbps; MaxMbps }.
function Get-CurveAt {
    param($Curve, [double]$Mbps)
    $pts = @($Curve)
    $res = @{ InRange = $false; AtEdge = $false; Mean = $null; P5 = $null; MinMbps = $null; MaxMbps = $null }
    if ($pts.Count -eq 0 -or $Mbps -le 0) { return $res }
    $res.MinMbps = $pts[0].Mbps
    $res.MaxMbps = $pts[$pts.Count - 1].Mbps
    $x = [math]::Log($Mbps)
    $tol = 1e-9
    if ($x -lt ([math]::Log($pts[0].Mbps) - $tol) -or $x -gt ([math]::Log($pts[$pts.Count - 1].Mbps) + $tol)) {
        $edge = $null
        if ([math]::Abs($Mbps / $pts[0].Mbps - 1.0) -le ($script:EdgeTolerance + 1e-12)) { $edge = $pts[0] }
        elseif ([math]::Abs($Mbps / $pts[$pts.Count - 1].Mbps - 1.0) -le ($script:EdgeTolerance + 1e-12)) { $edge = $pts[$pts.Count - 1] }
        if ($null -eq $edge) { return $res }
        $res.InRange = $true; $res.AtEdge = $true; $res.Mean = $edge.Mean; $res.P5 = $edge.P5
        return $res
    }
    if ($pts.Count -eq 1) { $res.InRange = $true; $res.Mean = $pts[0].Mean; $res.P5 = $pts[0].P5; return $res }
    for ($i = 0; $i -lt $pts.Count - 1; $i++) {
        $x0 = [math]::Log($pts[$i].Mbps); $x1 = [math]::Log($pts[$i + 1].Mbps)
        if ($x -le ($x1 + $tol)) {
            $t = ($x - $x0) / ($x1 - $x0)
            if ($t -lt 0) { $t = 0 } elseif ($t -gt 1) { $t = 1 }
            $res.InRange = $true
            $res.Mean = $pts[$i].Mean + ($pts[$i + 1].Mean - $pts[$i].Mean) * $t
            if ($null -ne $pts[$i].P5 -and $null -ne $pts[$i + 1].P5) { $res.P5 = $pts[$i].P5 + ($pts[$i + 1].P5 - $pts[$i].P5) * $t }
            return $res
        }
    }
    return $res
}

# $Label names the curve's mode in the text: 'CBR' for the off curve, else the mode ('vbr').
function Format-Range {
    param($At, [string]$Label = 'CBR')
    if ($null -eq $At.MinMbps) { return "no $Label runs" }
    return ('{0} runs span {1:0.00} to {2:0.00} Mbps' -f $Label, $At.MinMbps, $At.MaxMbps)
}

function Get-CurveLabel {
    param([string]$Mode)
    if ($Mode -eq 'off') { return 'CBR' }
    return $Mode
}

# The "equal-quality size": the curve inverted at the run's mean VMAF (log-Mbps interpolation), as run Mbps / curve Mbps in percent.
# Returns @{ Text; Percent } ("87% of CBR"; "< 84% of CBR" / "> 95% of CBR" when the VMAF is above / below the curve's end points;
# "n/a (curve not monotonic)" when the mean does not increase with Mbps across the needed segment). Information only.
function Get-EqualQualitySize {
    param($Curve, $Mean, [double]$Mbps, [string]$Label = 'CBR')
    $pts = @($Curve)
    $none = { param($t) @{ Text = $t; Percent = $null } }
    if ($null -eq $Mean -or $pts.Count -eq 0 -or $Mbps -le 0) { return (& $none 'n/a (no curve)') }
    $m = [double]$Mean
    $n = $pts.Count
    if ($n -eq 1) {
        $pct = $Mbps / $pts[0].Mbps * 100.0
        if ([math]::Abs($m - $pts[0].Mean) -le $script:Eps) { return @{ Text = ('{0:0}% of {1}' -f $pct, $Label); Percent = $pct } }
        if ($m -lt $pts[0].Mean) { return @{ Text = ('> {0:0}% of {1}' -f $pct, $Label); Percent = $pct } }
        return @{ Text = ('< {0:0}% of {1}' -f $pct, $Label); Percent = $pct }
    }
    $hits = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $n - 1; $i++) {
        $lo = [math]::Min($pts[$i].Mean, $pts[$i + 1].Mean); $hi = [math]::Max($pts[$i].Mean, $pts[$i + 1].Mean)
        if ($m -ge ($lo - $script:Eps) -and $m -le ($hi + $script:Eps)) { [void]$hits.Add($i) }
    }
    if ($hits.Count -gt 0) {
        foreach ($i in $hits) { if ($pts[$i + 1].Mean -le $pts[$i].Mean) { return (& $none 'n/a (curve not monotonic)') } }
        $i = $hits[0]
        $x0 = [math]::Log($pts[$i].Mbps); $x1 = [math]::Log($pts[$i + 1].Mbps)
        $t = ($m - $pts[$i].Mean) / ($pts[$i + 1].Mean - $pts[$i].Mean)
        if ($t -lt 0) { $t = 0 } elseif ($t -gt 1) { $t = 1 }
        $pct = $Mbps / [math]::Exp($x0 + ($x1 - $x0) * $t) * 100.0
        return @{ Text = ('{0:0}% of {1}' -f $pct, $Label); Percent = $pct }
    }
    if ($m -lt $pts[0].Mean) {
        if ($pts[1].Mean -le $pts[0].Mean) { return (& $none 'n/a (curve not monotonic)') }
        $pct = $Mbps / $pts[0].Mbps * 100.0
        return @{ Text = ('> {0:0}% of {1}' -f $pct, $Label); Percent = $pct }
    }
    if ($pts[$n - 1].Mean -le $pts[$n - 2].Mean) { return (& $none 'n/a (curve not monotonic)') }
    $pct = $Mbps / $pts[$n - 1].Mbps * 100.0
    return @{ Text = ('< {0:0}% of {1}' -f $pct, $Label); Percent = $pct }
}

# $Row: AchievedMbps, VmafMean, VmafP5 (null = not measured), Invalid, InvalidReasons. $Curve: Get-QualityCurve of the CBR runs.
# PASS by branch A (same mean quality or better and p5 within 1.0, at the CBR size 1/0.9 times the run's: at least 10% smaller) or
# branch B (mean >= 1.0 VMAF above the curve at the run's own Mbps). A missing mean / p5 makes a branch "not evaluable", never a pass.
function Test-BenchVbr {
    param($Row, $Curve)
    $pts = @($Curve)
    if ($Row.Invalid) { return (New-CheckResult 'INVALID' (Join-Reasons $Row)) }
    if ($null -eq $Row.VmafMean) { return (New-CheckResult 'n/a' 'no VMAF (libvmaf missing or the metric failed)') }
    if ($pts.Count -eq 0) { return (New-CheckResult 'n/a' 'no valid CBR (off) run with a VMAF score to build the curve from') }
    $run = [double]$Row.AchievedMbps
    $at90 = Get-CurveAt -Curve $pts -Mbps ($run / $script:VbrSizeRatio)
    $at1 = Get-CurveAt -Curve $pts -Mbps $run
    $edge90 = ''; if ($at90.AtEdge) { $edge90 = ' (at edge)' }
    $edge1 = ''; if ($at1.AtEdge) { $edge1 = ' (at edge)' }
    $parts = @()
    # branch A
    $a = 'Fail'
    if (-not $at90.InRange) {
        $a = 'Outside'
        $parts += ('A (>=10% smaller, same quality): CBR at {0:0.00} Mbps is outside the CBR range ({1})' -f ($run / $script:VbrSizeRatio), (Format-Range $at90))
    }
    elseif ($null -eq $Row.VmafP5 -or $null -eq $at90.Mean -or $null -eq $at90.P5) {
        $a = 'NotEvaluable'
        $parts += ('A (>=10% smaller, same quality): not evaluable, mean or p5 missing on the run or the CBR curve (CBR at {0:0.00} Mbps{1})' -f ($run / $script:VbrSizeRatio), $edge90)
    }
    else {
        $dMean = $Row.VmafMean - $at90.Mean; $dP5 = $Row.VmafP5 - $at90.P5
        if (($dMean -ge (-$script:Eps)) -and ($dP5 -ge (-$script:VbrP5Drop - $script:Eps))) { $a = 'Pass' }
        $parts += ('A (>=10% smaller, same quality): CBR at {0:0.00} Mbps{1}: mean {2:+0.00;-0.00;0.00}, p5 {3:+0.00;-0.00;0.00}' -f ($run / $script:VbrSizeRatio), $edge90, $dMean, $dP5)
    }
    # branch B
    $b = 'Fail'
    if (-not $at1.InRange) {
        $b = 'Outside'
        $parts += ('B (better at equal size): {0:0.00} Mbps is outside the CBR range ({1})' -f $run, (Format-Range $at1))
    }
    elseif ($null -eq $at1.Mean) {
        $b = 'NotEvaluable'
        $parts += 'B (better at equal size): not evaluable, no mean on the CBR curve'
    }
    else {
        $dMean1 = $Row.VmafMean - $at1.Mean
        if ($dMean1 -ge ($script:VmafGain - $script:Eps)) { $b = 'Pass' }
        $parts += ('B (better at equal size, {0:0.00} Mbps{1}): mean {2:+0.00;-0.00;0.00}' -f $run, $edge1, $dMean1)
    }
    $eq = Get-EqualQualitySize -Curve $pts -Mean $Row.VmafMean -Mbps $run
    $parts += ('equal-quality size: ' + $eq.Text)
    $detail = $parts -join '; '
    if ($a -eq 'Pass' -and $b -eq 'Pass') { return (New-CheckResult 'PASS' ($detail + ' (branches A and B)')) }
    if ($a -eq 'Pass') { return (New-CheckResult 'PASS' ($detail + ' (branch A: smaller at the same quality)')) }
    if ($b -eq 'Pass') { return (New-CheckResult 'PASS' ($detail + ' (branch B: better at equal size)')) }
    if ($a -eq 'Outside' -or $b -eq 'Outside') { return (New-CheckResult 'outside the CBR range' $detail) }
    if ($a -eq 'NotEvaluable' -or $b -eq 'NotEvaluable') { return (New-CheckResult 'n/a' $detail) }
    return (New-CheckResult 'FAIL' $detail)
}

# Output-side evidence that low latency really changed the encoder: frames it still held at the end and the most in flight, next to the
# run with low latency on at the same requested bitrate ($Sibling, may be null). Held at end is the latency evidence; max in flight
# saturates when the bench feeds faster than real time.
function Get-OutputEvidence {
    param($Row, $Sibling)
    $t = ('held at end {0}, max in flight {1}' -f $Row.Held, $Row.MaxInFlight)
    if ($null -ne $Sibling -and -not $Sibling.Failed) { $t += (' (low latency on: held {0}, max in flight {1})' -f $Sibling.Held, $Sibling.MaxInFlight) }
    return $t
}

# $CurveName: what the curve is called in the text ('CBR' or 'vbr'). -ShowEqualQuality adds the equal-quality size (against the CBR curve).
function Test-BenchLowLatency {
    param($Row, $Curve, $Sibling, [string]$CurveName = 'CBR', [switch]$ShowEqualQuality)
    $pts = @($Curve)
    if ($Row.Invalid) { return (New-CheckResult 'INVALID' (Join-Reasons $Row)) }
    if ($null -eq $Row.VmafMean) { return (New-CheckResult 'n/a' 'no VMAF (libvmaf missing or the metric failed)') }
    if ($pts.Count -eq 0) { return (New-CheckResult 'n/a' 'no valid low-latency-on run with a VMAF score to build the curve from') }
    $run = [double]$Row.AchievedMbps
    $at = Get-CurveAt -Curve $pts -Mbps $run
    $evidence = Get-OutputEvidence -Row $Row -Sibling $Sibling
    $eqText = ''
    if ($ShowEqualQuality) { $eqText = '; equal-quality size: ' + (Get-EqualQualitySize -Curve $pts -Mean $Row.VmafMean -Mbps $run -Label $CurveName).Text }
    if (-not $at.InRange) { return (New-CheckResult 'outside the CBR range' ('{0:0.00} Mbps is outside the {1} curve ({2}); {3}{4}' -f $run, $CurveName, (Format-Range $at $CurveName), $evidence, $eqText)) }
    $edge = ''; if ($at.AtEdge) { $edge = ' (at edge)' }
    $dMean = $Row.VmafMean - $at.Mean
    $detail = ('{0:0.00} Mbps{1}, mean {2:+0.00;-0.00;0.00} against the {3} curve at this rate; {4}{5}' -f $run, $edge, $dMean, $CurveName, $evidence, $eqText)
    if ($dMean -lt ($script:VmafGain - $script:Eps)) { return (New-CheckResult 'FAIL' $detail) }
    return (New-CheckResult 'PASS' $detail)
}

# The design's peak cap for a vbr mode: PeakMbps1s <= 1.65 x MeanMbps1s and <= 1.65 x the requested Mbps.
function Test-PeakCap {
    param($Row)
    if (-not ($Row.MeanMbps -gt 0)) { return (New-CheckResult 'n/a' 'no complete second of output') }
    $limit = $script:PeakCapFactor * $Row.MeanMbps
    $detail = ('peak 1 s {0:0.00} Mbps, mean {1:0.00} Mbps, limit {2:0.00} (x{3:0.000} of the mean)' -f $Row.PeakMbps, $Row.MeanMbps, $limit, ($Row.PeakMbps / $Row.MeanMbps))
    $ok = ($Row.PeakMbps -le ($limit + $script:Eps))
    if ($Row.RequestedMbps -gt 0) {
        $limitReq = $script:PeakCapFactor * $Row.RequestedMbps
        $detail += ('; requested {0:0.##} Mbps, limit {1:0.00} (x{2:0.000} of requested)' -f $Row.RequestedMbps, $limitReq, ($Row.PeakMbps / $Row.RequestedMbps))
        if (-not ($Row.PeakMbps -le ($limitReq + $script:Eps))) { $ok = $false }
    }
    if ($ok) { return (New-CheckResult 'PASS' $detail) }
    return (New-CheckResult 'FAIL' $detail)
}

# Why a run cannot be trusted (an array of reasons, empty = valid). $ReferencePackets: ffprobe's packet count of the reference (null = unknown).
function Get-RunInvalidReasons {
    param($Row, $ReferencePackets)
    $reasons = New-Object System.Collections.ArrayList
    $llOff = ($Row.Mode -eq 'lowlatency-off' -or $Row.Mode -eq 'vbr-lowlatency-off')
    $vbr = ($Row.Mode -eq 'vbr' -or $Row.Mode -eq 'vbr-lowlatency-off')
    if ($llOff) {
        if (-not $Row.HasApplied) { [void]$reasons.Add('no applied tuning was read back, so low latency off is unproven') }
        else {
            if ($Row.AppliedLowLatency) { [void]$reasons.Add('low latency did not go off (applied lowLatency = true)') }
            if ($Row.AppliedFallback) { [void]$reasons.Add('fallback: ' + $Row.AppliedFallback) }
        }
    }
    if ($vbr) {
        if (-not $Row.HasApplied) { [void]$reasons.Add('no applied tuning was read back, so VBR is unproven') }
        elseif ($Row.AppliedRate -ne 'PeakConstrainedVBR') { [void]$reasons.Add("applied rate mode is '$($Row.AppliedRate)', not VBR") }
        if (-not $Row.VbrTookEffect) {
            if ($Row.VbrVerdict) { [void]$reasons.Add([string]$Row.VbrVerdict) } else { [void]$reasons.Add('VBR did not take effect') }
        }
    }
    if ($Row.OutOfOrder -gt 0) { [void]$reasons.Add("$($Row.OutOfOrder) output frame(s) out of order") }
    if ($Row.Drops -gt 0) { [void]$reasons.Add("encoder dropped $($Row.Drops) frames") }
    if ($null -ne $ReferencePackets -and $null -eq $Row.SecondsLimit -and $Row.DecodedFrames -gt 0 -and [int64]$ReferencePackets -ne [int64]$Row.DecodedFrames) {
        [void]$reasons.Add("frame alignment: the reference has $ReferencePackets packets (ffprobe) but the bench decoded $($Row.DecodedFrames) frames")
    }
    if ($null -ne $Row.VmafFrames -and $Row.FramesOut -gt 0 -and [int64]$Row.VmafFrames -ne [int64]$Row.FramesOut) {
        [void]$reasons.Add("frame alignment: VMAF scored $($Row.VmafFrames) frames but the encoder gave $($Row.FramesOut)")
    }
    return $reasons.ToArray()
}

function Set-RunValidity {
    param($Row, $ReferencePackets)
    if ($Row.Failed) { return }
    $reasons = @(Get-RunInvalidReasons -Row $Row -ReferencePackets $ReferencePackets)
    $Row.InvalidReasons = $reasons
    $Row.Invalid = ($reasons.Count -gt 0)
}

# All checks for a set of rows (same codec is a curve; the same codec and requested Mbps pair a run with its low-latency-on counterpart).
# Returns objects: Run, Check, Against, Result, Detail.
function Get-BenchChecks {
    param($Rows)
    $out = New-Object System.Collections.ArrayList
    foreach ($row in $Rows) {
        foreach ($kind in @('Vbr', 'LowLatency')) {
            $baseMode = Get-BaselineMode -Mode $row.Mode -Kind $kind
            if ($null -eq $baseMode) { continue }
            $against = "$baseMode curve"
            if ($row.Failed) {
                [void]$out.Add([pscustomobject]@{ Run = $row.Name; Check = $kind; Against = $against; Result = 'n/a'; Detail = 'the run failed' })
                continue
            }
            $curve = @(Get-QualityCurve -Rows $Rows -Codec $row.Codec -Mode $baseMode -Group ([string]$row.Group))
            if ($kind -eq 'Vbr') { $r = Test-BenchVbr -Row $row -Curve $curve }
            else {
                $sibling = @($Rows | Where-Object { $_.Codec -eq $row.Codec -and [string]$_.Group -eq [string]$row.Group -and $_.RequestedMbps -eq $row.RequestedMbps -and $_.Mode -eq $baseMode -and -not $_.Failed })[0]
                $r = Test-BenchLowLatency -Row $row -Curve $curve -Sibling $sibling -CurveName (Get-CurveLabel $baseMode) -ShowEqualQuality:($row.Mode -eq 'lowlatency-off')
            }
            [void]$out.Add([pscustomobject]@{ Run = $row.Name; Check = $kind; Against = $against; Result = $r.Result; Detail = $r.Detail })
        }
        # INVALID runs are left out of every check, the peak cap included
        if (($row.Mode -eq 'vbr' -or $row.Mode -eq 'vbr-lowlatency-off') -and -not $row.Failed -and -not $row.Invalid) {
            $pc = Test-PeakCap -Row $row
            [void]$out.Add([pscustomobject]@{ Run = $row.Name; Check = 'PeakCap'; Against = '1.65 x mean and requested'; Result = $pc.Result; Detail = $pc.Detail })
        }
    }
    return $out.ToArray()
}

# ------------------------------------------------------------------ metric parsing (tested)

# libvmaf's JSON log: frames[].metrics.vmaf. Returns @{ Mean; P5; Frames } or $null.
function Read-VmafLog {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $j = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $scores = @($j.frames | ForEach-Object { [double]$_.metrics.vmaf })
    if ($scores.Count -eq 0) { return $null }
    return @{ Mean = ($scores | Measure-Object -Average).Average; P5 = (Get-Percentile -Values $scores -Percent 5); Frames = $scores.Count }
}

# ffmpeg's summary lines: "SSIM Y:0.99 U:.. V:.. All:0.989 (19.5)" and "PSNR y:.. u:.. v:.. average:42.1 min:.. max:..". $null when absent.
# An identical-frames PSNR is "inf": it comes back as Psnr = $null with PsnrInfinite = $true (JSON has no Infinity).
function Read-SsimPsnr {
    param([string]$StderrText)
    $ssim = $null; $psnr = $null; $psnrInf = $false
    $m = [regex]::Matches($StderrText, 'SSIM\s+Y:[0-9.]+.*?All:([0-9.]+|inf)')
    if ($m.Count -gt 0) { $v = $m[$m.Count - 1].Groups[1].Value; if ($v -ne 'inf') { $ssim = [double]::Parse($v, [Globalization.CultureInfo]::InvariantCulture) } else { $ssim = 1.0 } }
    $m = [regex]::Matches($StderrText, 'PSNR\s+y:[0-9.inf]+.*?average:([0-9.]+|inf)')
    if ($m.Count -gt 0) { $v = $m[$m.Count - 1].Groups[1].Value; if ($v -ne 'inf') { $psnr = [double]::Parse($v, [Globalization.CultureInfo]::InvariantCulture) } else { $psnrInf = $true } }
    return @{ Ssim = $ssim; Psnr = $psnr; PsnrInfinite = $psnrInf }
}

function ConvertTo-NullableDouble {
    param($Value)
    $d = 0.0
    if ($null -ne $Value -and [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d }
    return $null
}

# ffprobe -of json (stream=codec_name,width,height,avg_frame_rate,bit_rate,nb_read_packets : format=bit_rate) of the reference.
# Returns Codec, Width, Height, Fps, VideoMbps, OverallMbps, Packets (null where ffprobe gave none), or $null when unreadable.
function Read-ProbeJson {
    param([string]$Text)
    try { $j = $Text | ConvertFrom-Json } catch { return $null }
    if ($null -eq $j) { return $null }
    $s = @($j.streams)[0]
    if ($null -eq $s) { return $null }
    $fps = $null
    if ($s.avg_frame_rate -match '^(\d+)/(\d+)$' -and [double]$Matches[2] -gt 0) { $fps = [double]$Matches[1] / [double]$Matches[2] }
    $vb = ConvertTo-NullableDouble $s.bit_rate
    $ob = $null; if ($j.format) { $ob = ConvertTo-NullableDouble $j.format.bit_rate }
    $pk = ConvertTo-NullableDouble $s.nb_read_packets
    $w = ConvertTo-NullableDouble $s.width; $h = ConvertTo-NullableDouble $s.height
    [pscustomobject]@{
        ColorRange = [string]$s.color_range; ColorSpace = [string]$s.color_space; ColorPrimaries = [string]$s.color_primaries; ColorTransfer = [string]$s.color_transfer
        Codec = [string]$s.codec_name; Width = $w; Height = $h; Fps = $fps
        VideoMbps = $(if ($null -ne $vb) { $vb / 1e6 } else { $null }); OverallMbps = $(if ($null -ne $ob) { $ob / 1e6 } else { $null })
        Packets = $(if ($null -ne $pk) { [int64]$pk } else { $null })
    }
}

# Is a cached <name>.metrics.json still the metrics of the files and tools on disk? The MP4 and the reference are compared by size and
# modified time; also whether libvmaf was present, ffmpeg's version line and the filter-graph version. A cached run without a VMAF score
# is stale whenever libvmaf is present now.
function Test-MetricsCurrent {
    param($Metrics, [int64]$Mp4Bytes, [string]$Mp4Ticks, [int64]$RefBytes, [string]$RefTicks, [bool]$HasVmaf, [string]$FfmpegVersion, [string]$GraphVersion)
    if ($null -eq $Metrics) { return $false }
    if ($null -eq $Metrics.Mp4Bytes -or $null -eq $Metrics.Mp4ModifiedTicks -or $null -eq $Metrics.RefBytes -or $null -eq $Metrics.RefModifiedTicks) { return $false }
    if ($null -eq $Metrics.HasVmaf -or $null -eq $Metrics.FfmpegVersion -or $null -eq $Metrics.GraphVersion) { return $false }
    if (([bool]$Metrics.HasVmaf -ne $HasVmaf) -or ([string]$Metrics.FfmpegVersion -ne $FfmpegVersion) -or ([string]$Metrics.GraphVersion -ne $GraphVersion)) { return $false }
    if ($HasVmaf -and $null -eq $Metrics.VmafMean) { return $false }
    return ([int64]$Metrics.Mp4Bytes -eq $Mp4Bytes) -and ([string]$Metrics.Mp4ModifiedTicks -eq $Mp4Ticks) -and ([int64]$Metrics.RefBytes -eq $RefBytes) -and ([string]$Metrics.RefModifiedTicks -eq $RefTicks)
}

# Should a measurement be written to the cache? Only a complete one: with libvmaf present the VMAF mean has to have been parsed.
function Test-MetricsCacheable {
    param($Metrics, [bool]$HasVmaf)
    if ($null -eq $Metrics) { return $false }
    return (-not $HasVmaf) -or ($null -ne $Metrics.VmafMean)
}

# Why a run JSON is not used with this reference, or $null when it is. $ReferenceFull: the reference's full path, $ReferenceBytes: its length.
# A run counts only when it was benched against this very file: the same normalized full path (case-insensitive) and, when the JSON
# carries the reference's description, the same file size.
function Test-RunMatchesReference {
    param($Json, [string]$ReferenceFull, [int64]$ReferenceBytes)
    if ([string]::IsNullOrWhiteSpace([string]$Json.reference)) { return 'the run JSON names no reference' }
    $jsonRef = $null
    try { $jsonRef = [System.IO.Path]::GetFullPath([string]$Json.reference) } catch { return "the run JSON's reference is not a usable path: $($Json.reference)" }
    if (-not [string]::Equals($jsonRef, [System.IO.Path]::GetFullPath($ReferenceFull), [StringComparison]::OrdinalIgnoreCase)) {
        return "benched against a different reference ($jsonRef), not $ReferenceFull"
    }
    if ($Json.referenceInfo -and $null -ne $Json.referenceInfo.fileBytes -and [int64]$Json.referenceInfo.fileBytes -ne $ReferenceBytes) {
        return "the reference file's size changed since the run ($($Json.referenceInfo.fileBytes) bytes then, $ReferenceBytes now)"
    }
    return $null
}

# The runs of one report, grouped: returns objects { Key; Codec; Rows } in codec / key order.
function Get-RunGroups {
    param($Rows)
    $groups = @()
    foreach ($key in @($Rows | ForEach-Object { [string]$_.Group } | Sort-Object -Unique)) {
        $mine = @($Rows | Where-Object { [string]$_.Group -eq $key })
        $groups += [pscustomobject]@{ Key = $key; Codec = [string]$mine[0].Codec; Rows = $mine }
    }
    return $groups
}

# Warnings about the groups: a codec benched under more than one setting (the groups are judged apart, and the sizes and curves are not
# comparable), and a group made by more than one Clink build (or by an older bench that did not record it).
function Get-GroupWarnings {
    param($Rows)
    $w = New-Object System.Collections.ArrayList
    $groups = @(Get-RunGroups -Rows $Rows)
    foreach ($codec in @($groups | ForEach-Object { $_.Codec } | Sort-Object -Unique)) {
        $mine = @($groups | Where-Object { $_.Codec -eq $codec })
        if ($mine.Count -gt 1) {
            [void]$w.Add(('**{0} was benched in {1} different groups ({2}).** Each group has its own curve and is judged on its own; runs from different groups are never compared. Delete the old runs or bench again with one setting if one report was meant.' -f $codec, $mine.Count, (($mine | ForEach-Object { $_.Key }) -join ' | ')))
        }
    }
    foreach ($g in $groups) {
        $versions = @($g.Rows | ForEach-Object { $v = [string]$_.ClinkVersion; if ($v) { $v } else { '(not recorded)' } } | Sort-Object -Unique)
        if ($versions.Count -gt 1) {
            [void]$w.Add(('**The group "{0}" mixes Clink builds: {1}.** The encoder setup may differ between its runs; bench them again with one build.' -f $g.Key, ($versions -join ', ')))
        }
    }
    return $w.ToArray()
}

# Warns when the reference's colour tags are not BT.709 limited (tv) range: the bench's decoder asks for BT.709 limited, so any other
# source is converted, which lowers every score equally. Absent, empty or "unknown" tags are not a mismatch.
function Get-ColorWarning {
    param($Ref)
    if ($null -eq $Ref) { return $null }
    $bad = @()
    $pairs = @(@('color_range', $Ref.ColorRange, 'tv'), @('color_space', $Ref.ColorSpace, 'bt709'), @('color_primaries', $Ref.ColorPrimaries, 'bt709'), @('color_transfer', $Ref.ColorTransfer, 'bt709'))
    foreach ($p in $pairs) {
        $v = [string]$p[1]
        if ($v -and $v -ne 'unknown' -and $v -ne $p[2]) { $bad += ('{0} = {1} (expected {2})' -f $p[0], $v, $p[2]) }
    }
    if ($bad.Count -eq 0) { return $null }
    return ('**The reference is not BT.709 limited (tv) range: {0}.** The bench decodes asking for BT.709 limited, so it is converted first; that lowers every VMAF score equally, so the comparisons hold but the absolute numbers do not.' -f ($bad -join ', '))
}

# Warnings about the reference. $Ref: Codec, VideoMbps, OverallMbps, ClinkBrand (any may be null). $RunCodecs: the codecs benched.
function Get-ReferenceWarnings {
    param($Ref, [double]$MaxBenchMbps, $RunCodecs)
    $w = New-Object System.Collections.ArrayList
    if ($null -eq $Ref) { [void]$w.Add('The reference could not be described (no bench JSON carried it and ffprobe gave nothing): its bitrate headroom is unchecked.'); return $w.ToArray() }
    $mbps = $null
    if ($null -ne $Ref.VideoMbps -and $Ref.VideoMbps -gt 0) { $mbps = [double]$Ref.VideoMbps }
    elseif ($null -ne $Ref.OverallMbps -and $Ref.OverallMbps -gt 0) { $mbps = [double]$Ref.OverallMbps }
    if ($null -eq $mbps) { [void]$w.Add('The reference bitrate is unknown, so the 2x headroom over the highest bench rate is unchecked.') }
    elseif ($mbps -lt $script:RefHeadroom * $MaxBenchMbps) {
        [void]$w.Add(('**The reference is only {0:0.0} Mbps, below {1:0}x the highest bench rate ({2:0.0} Mbps, so {3:0.0} Mbps).** Re-encoding material that is already this compressed hides what the higher rates would show and biases every VMAF number: use a source of at least {3:0.0} Mbps.' -f $mbps, $script:RefHeadroom, $MaxBenchMbps, ($script:RefHeadroom * $MaxBenchMbps)))
    }
    if ($Ref.ClinkBrand) {
        $same = @($RunCodecs | Where-Object { $_ -eq $Ref.Codec })
        if ($same.Count -gt 0) {
            [void]$w.Add(('**The reference looks like a Clink recording (clk1 brand) in {0}, and Clink records constant bitrate with the same encoder family.** Re-encoding it with "off" (CBR) partly repeats the original encode and favours CBR; use a lossless or much higher bitrate source for a fair comparison.' -f $Ref.Codec))
        }
    }
    $cw = Get-ColorWarning -Ref $Ref
    if ($cw) { [void]$w.Add($cw) }
    return $w.ToArray()
}

# One report row from a run's JSON (the bench) and its metrics (or $null).
function ConvertTo-BenchRow {
    param($Json, $Metrics)
    $failed = [bool]$Json.error
    $secs = $null; if ($null -ne $Json.secondsLimit) { $secs = [double]$Json.secondsLimit }
    $row = [pscustomobject]@{
        Name = [string]$Json.name; Mode = [string]$Json.mode; Codec = [string]$Json.codec; RequestedMbps = [double]$Json.requestedMbps
        Failed = $failed; Error = [string]$Json.error
        ClinkVersion = [string]$Json.clinkVersion
        Group = (Get-GroupKey -Codec ([string]$Json.codec) -SecondsLimit $secs -KeyframeSeconds $Json.keyframeSeconds)
        SizeBytes = [double]$Json.fileSizeBytes; AchievedMbps = [double]$Json.achievedMbps; FileMbps = [double]$Json.fileMbps
        PeakMbps = [double]$Json.peakMbps1s; MeanMbps = [double]$Json.meanMbps1s; PeakOverMean = [double]$Json.peakOverMean
        Drops = [int64]$Json.dropped; Held = [int64]$Json.framesHeldAtEnd; MaxInFlight = [int64]$Json.maxFramesInFlight
        FramesIn = [int64]$Json.framesIn; FramesOut = [int64]$Json.framesOut; DecodedFrames = [int64]$Json.decodedFrames
        OutOfOrder = [int64]$Json.outOfOrderOutputs; SecondsLimit = $secs
        EncodeSpeedFps = [double]$Json.encodeSpeedFps; SourceFps = [double]$Json.sourceFps; MaxWaitMs = [double]$Json.maxWaitForRoomMs
        VbrApplicable = [bool]$Json.vbrApplicable; VbrTookEffect = [bool]$Json.vbrTookEffect; VbrVerdict = [string]$Json.vbrVerdict
        HasApplied = $false; AppliedRate = ''; AppliedLowLatency = $null; AppliedFallback = ''
        Applied = ''
        Invalid = $false; InvalidReasons = @()
        VmafMean = $null; VmafP5 = $null; VmafFrames = $null; Ssim = $null; Psnr = $null; PsnrInfinite = $false; FfprobeMbps = $null
    }
    if ($Json.applied) {
        $row.HasApplied = $true
        $row.AppliedRate = [string]$Json.applied.rate
        $row.AppliedLowLatency = [bool]$Json.applied.lowLatency
        $row.AppliedFallback = [string]$Json.applied.fallback
        $ll = 'LL on'; if (-not $Json.applied.lowLatency) { $ll = 'LL off' }
        $row.Applied = '{0}, {1}' -f $Json.applied.rate, $ll
        if ($Json.applied.fallback) { $row.Applied += ' (fallback: ' + $Json.applied.fallback + ')' }
    }
    if ($Metrics) {
        if ($null -ne $Metrics.VmafMean) { $row.VmafMean = [double]$Metrics.VmafMean }
        if ($null -ne $Metrics.VmafP5) { $row.VmafP5 = [double]$Metrics.VmafP5 }
        if ($null -ne $Metrics.VmafFrames) { $row.VmafFrames = [int64]$Metrics.VmafFrames }
        if ($null -ne $Metrics.Ssim) { $row.Ssim = [double]$Metrics.Ssim }
        if ($null -ne $Metrics.Psnr) { $row.Psnr = [double]$Metrics.Psnr }
        if ($Metrics.PsnrInfinite) { $row.PsnrInfinite = $true }
        if ($null -ne $Metrics.FfprobeMbps) { $row.FfprobeMbps = [double]$Metrics.FfprobeMbps }
    }
    return $row
}

if ($LibraryOnly) { return }

# ------------------------------------------------------------------ the run

if ([string]::IsNullOrWhiteSpace($OutFolder) -or [string]::IsNullOrWhiteSpace($Reference)) {
    Write-Host 'usage: encoder-bench.ps1 -OutFolder <folder the bench wrote> -Reference <reference.mp4> [-Force] [-Threads 2]'
    exit 2
}
foreach ($p in @($FfmpegPath, $FfprobePath, $Reference)) {
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { Write-Host "not found: $p"; exit 2 }
}
if (-not (Test-Path -LiteralPath $OutFolder -PathType Container)) { Write-Host "not a folder: $OutFolder"; exit 2 }
$OutFolder = (Resolve-Path -LiteralPath $OutFolder).Path
$Reference = (Resolve-Path -LiteralPath $Reference).Path

# libvmaf?
$filters = & $FfmpegPath -hide_banner -filters
$hasVmaf = [bool]($filters -match '\blibvmaf\b')
$versionLine = (& $FfmpegPath -hide_banner -version | Select-Object -First 1)
if (-not $hasVmaf) { Write-Host 'NOTE: this ffmpeg has no libvmaf filter, so only SSIM and PSNR are measured; the VMAF-based pass checks will say n/a.' }

# Runs a native tool at BelowNormal in $OutFolder, stderr to a file; returns @{ ExitCode; Stderr }.
function Invoke-Tool {
    param([string]$Exe, [string]$ArgumentLine, [string]$ErrFile, [string]$OutFile)
    $p = Start-Process -FilePath $Exe -ArgumentList $ArgumentLine -WorkingDirectory $OutFolder -NoNewWindow -PassThru `
        -RedirectStandardError $ErrFile -RedirectStandardOutput $OutFile
    $null = $p.Handle    # keeps the handle so ExitCode is readable after the exit
    try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
    $p.WaitForExit()
    $err = ''
    if (Test-Path -LiteralPath $ErrFile) { $err = Get-Content -LiteralPath $ErrFile -Raw -Encoding UTF8 }
    return @{ ExitCode = $p.ExitCode; Stderr = $err }
}

# The reference through ffprobe: its stream properties (colour tags included) and its packet count (read once; counting packets reads the whole file).
$refProbe = $null
$refOut = Join-Path $OutFolder 'reference.ffprobe.out'
$refErr = Join-Path $OutFolder 'reference.ffprobe.err'
$res = Invoke-Tool -Exe $FfprobePath -ErrFile $refErr -OutFile $refOut `
    -ArgumentLine ('-v error -select_streams v:0 -count_packets -show_entries stream=codec_name,width,height,avg_frame_rate,bit_rate,nb_read_packets,color_range,color_space,color_primaries,color_transfer:format=bit_rate,duration -of json "{0}"' -f $Reference)
if ($res.ExitCode -eq 0 -and (Test-Path -LiteralPath $refOut)) { $refProbe = Read-ProbeJson -Text (Get-Content -LiteralPath $refOut -Raw -Encoding UTF8) }
Remove-Item -LiteralPath $refOut, $refErr -Force -ErrorAction SilentlyContinue
$refItem = Get-Item -LiteralPath $Reference
$refFull = [System.IO.Path]::GetFullPath($Reference)

$jsonFiles = @(Get-ChildItem -LiteralPath $OutFolder -Filter '*.json' -File | Where-Object { $_.Name -notlike '*.metrics.json' -and $_.Name -notlike '*.vmaf.json' } | Sort-Object Name)
$rows = New-Object System.Collections.ArrayList
$notes = New-Object System.Collections.ArrayList
$skipped = New-Object System.Collections.ArrayList
$benchRef = $null
$adapters = New-Object System.Collections.ArrayList
foreach ($f in $jsonFiles) {
    try { $j = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { [void]$notes.Add("$($f.Name): not JSON, skipped"); continue }
    if (-not $j.mode -or -not $j.name) { continue }    # not a bench run file
    $name = [string]$j.name
    if ($name -notmatch '^[A-Za-z0-9._-]+$') { [void]$notes.Add("$($f.Name): unusable run name, skipped"); continue }
    # only runs benched against THIS reference file are measured, curved and judged
    $why = Test-RunMatchesReference -Json $j -ReferenceFull $refFull -ReferenceBytes $refItem.Length
    if ($why) { [void]$skipped.Add("skipped: $($f.Name): $why"); continue }
    if ($null -eq $benchRef -and $j.referenceInfo) { $benchRef = $j.referenceInfo }    # from a run that matches the reference, not the first file
    if ($j.adapterName) { $a = ('{0} (LUID {1})' -f $j.adapterName, $j.adapterLuid); if ($adapters -notcontains $a) { [void]$adapters.Add($a) } }
    $mp4 = Join-Path $OutFolder ($name + '.mp4')
    $metricsFile = Join-Path $OutFolder ($name + '.metrics.json')
    $metrics = $null
    if (-not $j.error -and (Test-Path -LiteralPath $mp4)) {
        $mp4Item = Get-Item -LiteralPath $mp4
        $stamp = @{ Mp4Bytes = $mp4Item.Length; Mp4ModifiedTicks = [string]$mp4Item.LastWriteTimeUtc.Ticks; RefBytes = $refItem.Length; RefModifiedTicks = [string]$refItem.LastWriteTimeUtc.Ticks
            HasVmaf = $hasVmaf; FfmpegVersion = [string]$versionLine; GraphVersion = $script:FilterGraphVersion }
        $cached = $null
        if ((Test-Path -LiteralPath $metricsFile) -and -not $Force) {
            try { $cached = Get-Content -LiteralPath $metricsFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $cached = $null }
        }
        if ($cached -and (Test-MetricsCurrent -Metrics $cached -Mp4Bytes $stamp.Mp4Bytes -Mp4Ticks $stamp.Mp4ModifiedTicks -RefBytes $stamp.RefBytes -RefTicks $stamp.RefModifiedTicks `
                    -HasVmaf $hasVmaf -FfmpegVersion $stamp.FfmpegVersion -GraphVersion $stamp.GraphVersion)) {
            $metrics = $cached
            Write-Host "$name : metrics reused ($name.metrics.json)"
        }
        else {
            if ((Test-Path -LiteralPath $metricsFile) -and -not $Force) { Write-Host "$name : cached metrics do not match the MP4 / reference / ffmpeg / filter graph on disk"; [void]$notes.Add("${name}: cached metrics were out of date (the MP4, the reference, ffmpeg or the filter graph changed, or the cache had no VMAF score), measured again") }
            Write-Host "$name : measuring ..."
            $m = @{ VmafMean = $null; VmafP5 = $null; VmafFrames = $null; Ssim = $null; Psnr = $null; PsnrInfinite = $false; FfprobeMbps = $null
                Mp4Bytes = $stamp.Mp4Bytes; Mp4ModifiedTicks = $stamp.Mp4ModifiedTicks; RefBytes = $stamp.RefBytes; RefModifiedTicks = $stamp.RefModifiedTicks
                HasVmaf = $stamp.HasVmaf; FfmpegVersion = $stamp.FfmpegVersion; GraphVersion = $stamp.GraphVersion }
            # one pass: both inputs restamped from 0 and cut to the shorter one, frames paired by nearest timestamp; distorted first, reference second
            $vmafLog = $name + '.vmaf.json'
            $sync = 'shortest=1:ts_sync_mode=nearest'
            # (a change to either filter graph below needs a new $script:FilterGraphVersion, so that cached metrics go stale)
            if ($hasVmaf) {
                $graph = "[0:v]setpts=PTS-STARTPTS,split=3[d1][d2][d3];[1:v]setpts=PTS-STARTPTS,split=3[r1][r2][r3];" +
                         "[d1][r1]libvmaf=log_fmt=json:log_path=${vmafLog}:n_threads=${Threads}:${sync}[v];[d2][r2]ssim=${sync}[s];[d3][r3]psnr=${sync}[p]"
                $maps = '-map "[v]" -f null - -map "[s]" -f null - -map "[p]" -f null -'
            }
            else {
                $graph = "[0:v]setpts=PTS-STARTPTS,split=2[d2][d3];[1:v]setpts=PTS-STARTPTS,split=2[r2][r3];[d2][r2]ssim=${sync}[s];[d3][r3]psnr=${sync}[p]"
                $maps = '-map "[s]" -f null - -map "[p]" -f null -'
            }
            if (Test-Path -LiteralPath (Join-Path $OutFolder $vmafLog)) { Remove-Item -LiteralPath (Join-Path $OutFolder $vmafLog) -Force }
            $argLine = '-hide_banner -nostats -y -threads {0} -i "{1}" -threads {0} -i "{2}" -filter_complex_threads {0} -filter_complex "{3}" {4}' -f $Threads, $mp4, $Reference, $graph, $maps
            $res = Invoke-Tool -Exe $FfmpegPath -ArgumentLine $argLine -ErrFile (Join-Path $OutFolder ($name + '.ffmpeg.log')) -OutFile (Join-Path $OutFolder ($name + '.ffmpeg.out'))
            $measured = $false
            if ($res.ExitCode -ne 0) {
                [void]$notes.Add("${name}: ffmpeg exited $($res.ExitCode) (see $name.ffmpeg.log)")
            }
            else {
                $measured = $true
                $sp = Read-SsimPsnr -StderrText $res.Stderr
                $m.Ssim = $sp.Ssim; $m.Psnr = $sp.Psnr; $m.PsnrInfinite = $sp.PsnrInfinite
                if ($hasVmaf) {
                    $v = Read-VmafLog -Path (Join-Path $OutFolder $vmafLog)
                    if ($v) { $m.VmafMean = $v.Mean; $m.VmafP5 = $v.P5; $m.VmafFrames = $v.Frames } else { [void]$notes.Add("${name}: no VMAF scores in $vmafLog") }
                }
            }
            # ffprobe's own view of the file's bitrate (format=bit_rate)
            $pr = Invoke-Tool -Exe $FfprobePath -ArgumentLine ('-v error -show_entries format=bit_rate -of default=noprint_wrappers=1:nokey=1 "{0}"' -f $mp4) `
                -ErrFile (Join-Path $OutFolder ($name + '.ffprobe.err')) -OutFile (Join-Path $OutFolder ($name + '.ffprobe.out'))
            $probeText = ''
            if (Test-Path -LiteralPath (Join-Path $OutFolder ($name + '.ffprobe.out'))) { $probeText = (Get-Content -LiteralPath (Join-Path $OutFolder ($name + '.ffprobe.out')) -Raw).Trim() }
            $bps = 0.0
            if ($pr.ExitCode -eq 0 -and [double]::TryParse($probeText, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$bps)) { $m.FfprobeMbps = $bps / 1e6 }
            Remove-Item -LiteralPath (Join-Path $OutFolder ($name + '.ffprobe.out')), (Join-Path $OutFolder ($name + '.ffprobe.err')), (Join-Path $OutFolder ($name + '.ffmpeg.out')) -Force -ErrorAction SilentlyContinue
            # only a complete measurement is cached (with libvmaf present, one that has a VMAF score): the next run tries again otherwise
            if ($measured -and (Test-MetricsCacheable -Metrics $m -HasVmaf $hasVmaf)) { ($m | ConvertTo-Json) | Set-Content -LiteralPath $metricsFile -Encoding UTF8 }
            elseif (Test-Path -LiteralPath $metricsFile) { Remove-Item -LiteralPath $metricsFile -Force -ErrorAction SilentlyContinue }
            $metrics = $m
        }
    }
    $row = ConvertTo-BenchRow -Json $j -Metrics $metrics
    $refPackets = $null; if ($refProbe) { $refPackets = $refProbe.Packets }
    Set-RunValidity -Row $row -ReferencePackets $refPackets
    [void]$rows.Add($row)
}
foreach ($s in $skipped) { Write-Host $s }

if ($rows.Count -eq 0) { Write-Host "no bench runs (*.json with a mode) for this reference in $OutFolder"; exit 2 }

$sorted = @($rows | Sort-Object Group, RequestedMbps, @{ Expression = { [array]::IndexOf($script:ModeOrder, $_.Mode) } })
$checks = @(Get-BenchChecks -Rows $sorted)
$groups = @(Get-RunGroups -Rows $sorted)
$gops = @($sorted | ForEach-Object { $_.Group -replace '^.*GOP ', '' } | Sort-Object -Unique)

# the reference as the bench and ffprobe describe it
$ref = $null
if ($benchRef -or $refProbe) {
    $ref = [pscustomobject]@{ Codec = ''; Width = $null; Height = $null; Fps = $null; VideoMbps = $null; OverallMbps = $null; Packets = $null; ClinkBrand = $false
        ColorRange = ''; ColorSpace = ''; ColorPrimaries = ''; ColorTransfer = '' }
    if ($benchRef) {
        $ref.Codec = [string]$benchRef.codec; $ref.Width = $benchRef.width; $ref.Height = $benchRef.height; $ref.Fps = $benchRef.fps
        if ($benchRef.overallMbps -gt 0) { $ref.OverallMbps = [double]$benchRef.overallMbps }
        $ref.ClinkBrand = [bool]$benchRef.clinkBrand
    }
    if ($refProbe) {
        if (-not $ref.Codec) { $ref.Codec = $refProbe.Codec }
        if ($null -eq $ref.Width) { $ref.Width = $refProbe.Width; $ref.Height = $refProbe.Height }
        if ($null -eq $ref.Fps) { $ref.Fps = $refProbe.Fps }
        if ($null -eq $ref.OverallMbps) { $ref.OverallMbps = $refProbe.OverallMbps }
        $ref.VideoMbps = $refProbe.VideoMbps; $ref.Packets = $refProbe.Packets
        $ref.ColorRange = $refProbe.ColorRange; $ref.ColorSpace = $refProbe.ColorSpace; $ref.ColorPrimaries = $refProbe.ColorPrimaries; $ref.ColorTransfer = $refProbe.ColorTransfer
    }
}
$maxBenchMbps = [double](($rows | Measure-Object -Property RequestedMbps -Maximum).Maximum)
$runCodecs = @($rows | ForEach-Object { $_.Codec } | Sort-Object -Unique)
$warnings = @(Get-GroupWarnings -Rows $sorted) + @(Get-ReferenceWarnings -Ref $ref -MaxBenchMbps $maxBenchMbps -RunCodecs $runCodecs)

function Fmt { param($v, [string]$f) if ($null -eq $v) { return '-' } return ([double]$v).ToString($f, [Globalization.CultureInfo]::InvariantCulture) }

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# Encoder bench report')
[void]$md.AppendLine()
[void]$md.AppendLine("- Reference: ``$Reference``")
if ($ref) {
    $dim = '-'; if ($ref.Width) { $dim = '{0}x{1}' -f $ref.Width, $ref.Height }
    $clink = 'no'; if ($ref.ClinkBrand) { $clink = '**yes (clk1 brand)**' }
    [void]$md.AppendLine(('- Reference stream: codec {0}, {1}, {2} fps, video {3} Mbps (ffprobe stream), overall {4} Mbps (file size / duration, audio included), packets {5} (ffprobe), Clink recording: {6}' -f `
        $ref.Codec, $dim, (Fmt $ref.Fps '0.###'), (Fmt $ref.VideoMbps '0.0'), (Fmt $ref.OverallMbps '0.0'), $(if ($null -ne $ref.Packets) { $ref.Packets } else { '-' }), $clink))
    $tag = { param($v) if ($v) { $v } else { 'untagged' } }
    [void]$md.AppendLine(('- Reference colour (ffprobe): range {0}, space {1}, primaries {2}, transfer {3} (the bench decodes asking for BT.709 limited)' -f (& $tag $ref.ColorRange), (& $tag $ref.ColorSpace), (& $tag $ref.ColorPrimaries), (& $tag $ref.ColorTransfer)))
}
else { [void]$md.AppendLine('- Reference stream: unknown') }
$adapterText = '-'; if ($adapters.Count -gt 0) { $adapterText = $adapters -join '; ' }
[void]$md.AppendLine("- Adapter: $adapterText")
[void]$md.AppendLine("- Out folder: ``$OutFolder``")
[void]$md.AppendLine("- ffmpeg: $versionLine")
if ($hasVmaf) { [void]$md.AppendLine('- libvmaf: present (VMAF mean and 5th percentile of the per-frame scores, SSIM All, PSNR average)') }
else { [void]$md.AppendLine('- **libvmaf: missing in this ffmpeg. SSIM and PSNR only; VMAF columns are "-" and the VMAF-based checks are n/a.**') }
[void]$md.AppendLine('- Both inputs restamped with setpts=PTS-STARTPTS, shortest=1, ts_sync_mode=nearest (libvmaf, ssim, psnr).')
[void]$md.AppendLine("- ffmpeg limits: -threads $Threads on each input (decoders), -filter_complex_threads $Threads, libvmaf n_threads=$Threads, process at BelowNormal. ffmpeg's I/O threads and the OS thread pool are not limited.")
$gopText = 'unknown'; if ($gops.Count -gt 0) { $gopText = ($gops -join ', ') }
[void]$md.AppendLine("- Keyframe interval (GOP) of the bench runs: $gopText (--keyframe-seconds; a recording uses 1 s).")
[void]$md.AppendLine('- Groups (a curve and its checks never mix groups): ' + (($groups | ForEach-Object { '{0} ({1} run(s))' -f $_.Key, @($_.Rows).Count }) -join '; '))
[void]$md.AppendLine('- Peak 1 s and mean are the encoder output''s per-second bitrate (whole seconds only).')
[void]$md.AppendLine('- Sizes in the checks are the ACHIEVED video Mbps (video bits / (frames out / fps), container excluded), the same for the curve points and the judged runs.')
[void]$md.AppendLine('- The bench encodes as fast as the encoder takes frames and WAITS when it is full, so it drops no frame (a drop would make the run INVALID). The design''s live-drop criterion (0 drops while recording) is not testable here and needs the Clink.exe --selftest smoke evidence.')
if ($warnings.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Warnings')
    [void]$md.AppendLine()
    foreach ($w in $warnings) { [void]$md.AppendLine("- $w") }
}
if ($skipped.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Skipped run files')
    [void]$md.AppendLine()
    foreach ($s in $skipped) { [void]$md.AppendLine("- $s") }
}

foreach ($g in $groups) {
    $gRows = @($sorted | Where-Object { [string]$_.Group -eq $g.Key })
    $gNames = @($gRows | ForEach-Object { $_.Name })
    $gChecks = @($checks | Where-Object { $gNames -contains $_.Run })
    [void]$md.AppendLine()
    [void]$md.AppendLine("## Group: $($g.Key)")
    [void]$md.AppendLine()
    [void]$md.AppendLine('### Quality')
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Run | Codec | Req Mbps | Mode | Size MB | Video Mbps (curve axis) | File Mbps | ffprobe Mbps | VMAF frames | VMAF mean | VMAF p5 | SSIM | PSNR | Status |')
    [void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    foreach ($r in $gRows) {
        if ($r.Failed) {
            [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | FAILED: {4} |' -f $r.Name, $r.Codec, (Fmt $r.RequestedMbps '0.###'), $r.Mode, ($r.Error -replace '\|', '/')) + (' |' * 9))
            continue
        }
        $status = 'ok'; if ($r.Invalid) { $status = 'INVALID: ' + ((Join-Reasons $r) -replace '\|', '/') }
        $psnr = Fmt $r.Psnr '0.00'; if ($r.PsnrInfinite) { $psnr = 'inf' }
        $vf = '-'; if ($null -ne $r.VmafFrames) { $vf = [string]$r.VmafFrames }
        [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} |' -f `
                $r.Name, $r.Codec, (Fmt $r.RequestedMbps '0.###'), $r.Mode, (Fmt ($r.SizeBytes / 1048576.0) '0.00'), (Fmt $r.AchievedMbps '0.00'), (Fmt $r.FileMbps '0.00'), (Fmt $r.FfprobeMbps '0.00'),
                $vf, (Fmt $r.VmafMean '0.00'), (Fmt $r.VmafP5 '0.00'), (Fmt $r.Ssim '0.0000'), $psnr, $status))
    }
    [void]$md.AppendLine()
    [void]$md.AppendLine('### Encoder evidence')
    [void]$md.AppendLine()
    [void]$md.AppendLine('Bench drops are 0 by design (the feeder waits for room); a drop makes the run INVALID. Encode speed = frames submitted / feed seconds (decode, copy and encode together). **Held at end** (frames the undrained encoder still held) is the latency evidence. **Max in flight** saturates when the bench feeds faster than real time, and the **feeder wait** is expected then (the feeder sleeps with Thread.Sleep(1), about 15.6 ms): neither says anything about whether a live recording would drop a frame.')
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Run | Applied | Peak 1 s | Mean 1 s | Peak/mean | Peak cap (1.65x mean and requested) | In | Out | Held at end | Max in flight (saturates) | Bench drops | Encode speed fps (x source) | Feeder wait ms (expected) | Out of order | VBR valid |')
    [void]$md.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    foreach ($r in $gRows) {
        if ($r.Failed) { continue }
        $valid = 'n/a'
        if ($r.VbrApplicable) { if ($r.VbrTookEffect) { $valid = 'yes' } else { $valid = 'VBR did not take effect' } }
        $cap = '-'
        $capRow = @($gChecks | Where-Object { $_.Run -eq $r.Name -and $_.Check -eq 'PeakCap' })[0]
        if ($capRow) { $cap = $capRow.Result }
        $speed = Fmt $r.EncodeSpeedFps '0.0'
        if ($r.SourceFps -gt 0 -and $r.EncodeSpeedFps -gt 0) { $speed += ' (x' + (Fmt ($r.EncodeSpeedFps / $r.SourceFps) '0.00') + ')' }
        [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |' -f `
                $r.Name, ($r.Applied -replace '\|', '/'), (Fmt $r.PeakMbps '0.00'), (Fmt $r.MeanMbps '0.00'), (Fmt $r.PeakOverMean '0.000'), $cap, $r.FramesIn, $r.FramesOut, $r.Held, $r.MaxInFlight,
                $r.Drops, $speed, (Fmt $r.MaxWaitMs '0'), $r.OutOfOrder, $valid))
    }
    [void]$md.AppendLine()
    [void]$md.AppendLine('### Pass checks')
    [void]$md.AppendLine()
    [void]$md.AppendLine('The CBR ("off") runs of this group are a curve of VMAF (mean and p5) against achieved video Mbps, interpolated in log size, never extrapolated (a size within 0.5% of an end point counts as that end point, "at edge"). Each run is judged against it.')
    [void]$md.AppendLine()
    [void]$md.AppendLine('- VBR: PASS by branch A or B. A (same quality, at least 10% smaller): the mean VMAF is >= the CBR curve''s mean at (run Mbps / 0.9) and the p5 is >= the curve''s p5 there - 1.0. B (better at equal size): the mean is >= 1 VMAF above the curve at the run''s own Mbps. A missing mean or p5 means a branch is not evaluable (never a pass).')
    [void]$md.AppendLine('- Equal-quality size (information, not the verdict): the CBR curve inverted at the run''s mean VMAF; "87% of CBR" = the run needs 87% of the CBR bytes for the same mean VMAF. "< x%" / "> y%": the run''s VMAF is above / below the curve''s end points.')
    [void]$md.AppendLine('- Low latency off: PASS when the mean VMAF is >= 1 above the curve at the run''s own Mbps (vbr-lowlatency-off: the vbr curve). Output-side evidence (frames held at the end) is shown next to the low-latency-on run.')
    [void]$md.AppendLine('- PeakCap: a vbr mode''s busiest second is <= 1.65 x its mean second and <= 1.65 x the requested Mbps (not run for INVALID runs).')
    [void]$md.AppendLine('- "outside the CBR range": the run''s Mbps (or the Mbps / 0.9 comparison point) is beyond the CBR runs, so it is not judged; add CBR rates around it.')
    [void]$md.AppendLine('- INVALID: the run did not do what its mode says (see the list below) and is left out of the curve and of the checks.')
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Run | Check | Against | Result | Detail |')
    [void]$md.AppendLine('|---|---|---|---|---|')
    foreach ($c in $gChecks) { [void]$md.AppendLine(('| {0} | {1} | {2} | **{3}** | {4} |' -f $c.Run, $c.Check, $c.Against, $c.Result, ($c.Detail -replace '\|', '/'))) }
}
$invalid = @($sorted | Where-Object { $_.Invalid -and -not $_.Failed })
if ($invalid.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## INVALID runs')
    [void]$md.AppendLine()
    foreach ($r in $invalid) { [void]$md.AppendLine("- $($r.Name): $(Join-Reasons $r)") }
}
if ($notes.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Notes')
    [void]$md.AppendLine()
    foreach ($n in $notes) { [void]$md.AppendLine("- $n") }
}
$report = Join-Path $OutFolder 'encoder-bench-report.md'
[System.IO.File]::WriteAllText($report, $md.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "report: $report"
foreach ($w in $warnings) { Write-Host ("WARNING: " + ($w -replace '\*\*', '')) }
$fails = @($checks | Where-Object { $_.Result -eq 'FAIL' -or $_.Result -eq 'INVALID' }).Count
Write-Host ("{0} run(s) in {1} group(s), {2} check(s), {3} failed or invalid, {4} invalid run(s), {5} skipped file(s)" -f $sorted.Count, $groups.Count, $checks.Count, $fails, $invalid.Count, $skipped.Count)
exit 0
