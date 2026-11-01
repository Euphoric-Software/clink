# Standalone tests for the report maths in tools\encoder-bench.ps1 (-LibraryOnly): percentile, the CBR curve and the pass checks on
# synthetic numbers, the validity rules, the run grouping, the metrics cache and the ffmpeg / ffprobe / libvmaf output parsing. No ffmpeg,
# no GPU, no files outside the temp folder.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools\tests\encoder-bench.tests.ps1   (exit 0 = all pass)
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $PSScriptRoot
. (Join-Path $toolsDir 'encoder-bench.ps1') -LibraryOnly

$script:Pass = 0
$script:Fail = 0
function Assert-Test {
    param([string]$Name, [scriptblock]$Body)
    try { & $Body; $script:Pass++; Write-Host "PASS $Name" }
    catch { $script:Fail++; Write-Host "FAIL $Name : $($_.Exception.Message)" }
}
function Assert-Equal {
    param($Actual, $Expected)
    if ($Actual -ne $Expected) { throw "expected '$Expected' but got '$Actual'" }
}
function Assert-Near {
    param([double]$Actual, [double]$Expected, [double]$Tolerance = 1e-9)
    if ([math]::Abs($Actual - $Expected) -gt $Tolerance) { throw "expected $Expected but got $Actual" }
}

# A report row with only what the checks read. $Rate is the run's ACHIEVED video Mbps, the axis of the curve (the old tests' "MB" numbers).
function New-Row {
    param([string]$Mode, [double]$Rate, $Mean, $P5, [string]$Codec = 'h264', [double]$Mbps = 16, [bool]$Invalid = $false, [string[]]$Reasons = @(), [string]$Group = '')
    [pscustomobject]@{ Name = "${Codec}_${Mbps}M_$Mode"; Mode = $Mode; Codec = $Codec; RequestedMbps = $Mbps; Failed = $false; AchievedMbps = $Rate; Group = $Group
        VmafMean = $Mean; VmafP5 = $P5; Invalid = $Invalid; InvalidReasons = $Reasons; Held = 0; MaxInFlight = 3; PeakMbps = 16; MeanMbps = 16 }
}
# A run JSON as the bench writes it (the fields the rows read), with overrides.
function New-Json {
    param([string]$Mode = 'off', [hashtable]$Over = @{})
    $o = [ordered]@{ name = "h264_16M_$Mode"; mode = $Mode; codec = 'h264'; requestedMbps = 16; error = $null; fileSizeBytes = 1048576; achievedMbps = 15.5; fileMbps = 15.9
        clinkVersion = '1.2.3'; keyframeSeconds = 1.0
        peakMbps1s = 16.0; meanMbps1s = 16.0; peakOverMean = 1.0; dropped = 0; framesHeldAtEnd = 0; maxFramesInFlight = 3; framesIn = 600; framesOut = 600
        decodedFrames = 600; outOfOrderOutputs = 0; secondsLimit = $null; vbrApplicable = $false; vbrTookEffect = $true; vbrVerdict = 'n/a (CBR mode)'
        applied = [ordered]@{ rate = 'CBR'; lowLatency = $true; fallback = $null } }
    foreach ($k in $Over.Keys) { $o[$k] = $Over[$k] }
    return (([pscustomobject]$o | ConvertTo-Json -Depth 5) | ConvertFrom-Json)
}
function New-ValidRow { param([string]$Mode = 'off', [hashtable]$Over = @{}) $r = ConvertTo-BenchRow -Json (New-Json -Mode $Mode -Over $Over) -Metrics $null; return $r }

# The CBR curve used by most tests: 10 Mbps at VMAF 90, 20 Mbps at 96 (p5 80 and 88).
function New-Curve { @(Get-QualityCurve -Rows @((New-Row 'off' 10 90.0 80.0), (New-Row 'off' 20 96.0 88.0)) -Codec 'h264' -Mode 'off') }

Assert-Test 'percentile: interpolates, handles one value and empty' {
    Assert-Near (Get-Percentile -Values @(1, 2, 3, 4, 5) -Percent 50) 3
    Assert-Near (Get-Percentile -Values @(1, 2, 3, 4, 5) -Percent 0) 1
    Assert-Near (Get-Percentile -Values @(1, 2, 3, 4, 5) -Percent 100) 5
    Assert-Near (Get-Percentile -Values @(1, 2, 3, 4, 5) -Percent 25) 2
    Assert-Near (Get-Percentile -Values @(10, 0) -Percent 5) 0.5
    Assert-Near (Get-Percentile -Values @(7) -Percent 5) 7
    Assert-Equal ($null -eq (Get-Percentile -Values @() -Percent 5)) $true
}
Assert-Test 'percentile: 5th of 0..100 is 5' {
    Assert-Near (Get-Percentile -Values (0..100 | ForEach-Object { [double]$_ }) -Percent 5) 5
}
Assert-Test 'baselines: both vbr modes use the CBR curve; low latency uses off, or vbr for vbr-lowlatency-off' {
    Assert-Equal (Get-BaselineMode -Mode 'vbr' -Kind 'Vbr') 'off'
    Assert-Equal (Get-BaselineMode -Mode 'vbr-lowlatency-off' -Kind 'Vbr') 'off'
    Assert-Equal (Get-BaselineMode -Mode 'lowlatency-off' -Kind 'LowLatency') 'off'
    Assert-Equal (Get-BaselineMode -Mode 'vbr-lowlatency-off' -Kind 'LowLatency') 'vbr'
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'off' -Kind 'Vbr')) $true
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'lowlatency-off' -Kind 'Vbr')) $true
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'vbr' -Kind 'LowLatency')) $true
}

# ---- the curve
Assert-Test 'curve: only valid, measured runs of the codec and mode, sorted by Mbps, equal rates once' {
    $rows = @(
        (New-Row 'off' 20 96.0 88.0), (New-Row 'off' 10 90.0 80.0), (New-Row 'off' 15 93.0 84.0),
        (New-Row 'off' 15 99.0 99.0),                                  # same rate as another: once
        (New-Row 'off' 12 80.0 70.0 'h264' 16 $true @('bad')),         # invalid: not on the curve
        (New-Row 'off' 14 $null $null),                                # no VMAF
        (New-Row 'off' 11 70.0 60.0 'hevc'),                           # other codec
        (New-Row 'vbr' 13 91.0 82.0)                                   # other mode
    )
    $c = @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off')
    Assert-Equal $c.Count 3
    Assert-Near $c[0].Mbps 10
    Assert-Near $c[2].Mbps 20
    Assert-Near $c[1].Mean 93
}
Assert-Test 'curve: the x axis is the achieved Mbps, not the file size' {
    $r = New-Row 'off' 12 90.0 80.0
    $r | Add-Member -NotePropertyName SizeBytes -NotePropertyValue 999999999.0
    $c = @(Get-QualityCurve -Rows @($r) -Codec 'h264' -Mode 'off')
    Assert-Near $c[0].Mbps 12
}
Assert-Test 'curve: only the rows of a group when -Group is given' {
    $rows = @((New-Row 'off' 10 90.0 80.0 'h264' 12 $false @() 'g1'), (New-Row 'off' 20 96.0 88.0 'h264' 20 $false @() 'g1'), (New-Row 'off' 15 70.0 60.0 'h264' 16 $false @() 'g2'))
    Assert-Equal @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off' -Group 'g1').Count 2
    Assert-Equal @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off' -Group 'g2').Count 1
    Assert-Equal @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off').Count 3
}
Assert-Test 'curve: interpolated in log Mbps, exact at the points, not extrapolated' {
    $c = New-Curve
    $mid = Get-CurveAt -Curve $c -Mbps ([math]::Sqrt(10 * 20))    # the geometric middle: half way in log
    Assert-Equal $mid.InRange $true
    Assert-Equal $mid.AtEdge $false
    Assert-Near $mid.Mean 93.0 1e-9
    Assert-Near $mid.P5 84.0 1e-9
    $lin = Get-CurveAt -Curve $c -Mbps 15                          # the arithmetic middle is above half way in log
    Assert-Equal ($lin.Mean -gt 93.0) $true
    Assert-Near (Get-CurveAt -Curve $c -Mbps 10).Mean 90 1e-9
    Assert-Near (Get-CurveAt -Curve $c -Mbps 20).Mean 96 1e-9
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 9.9).InRange $false
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 20.2).InRange $false
    Assert-Equal (Get-CurveAt -Curve @() -Mbps 100).InRange $false
}
Assert-Test 'curve: within 0.5% of an end point counts as that end point, labelled at edge; further out is outside' {
    $c = New-Curve
    $lo = Get-CurveAt -Curve $c -Mbps 9.97
    Assert-Equal $lo.InRange $true
    Assert-Equal $lo.AtEdge $true
    Assert-Near $lo.Mean 90 1e-9
    Assert-Near $lo.P5 80 1e-9
    $hi = Get-CurveAt -Curve $c -Mbps 20.08
    Assert-Equal $hi.InRange $true
    Assert-Equal $hi.AtEdge $true
    Assert-Near $hi.Mean 96 1e-9
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 9.94).InRange $false     # 0.6% below
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 20.12).InRange $false    # 0.6% above
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 15).AtEdge $false
}
Assert-Test 'curve: one point is in range only at its own rate (and within the edge tolerance)' {
    $c = @(Get-QualityCurve -Rows @((New-Row 'off' 10 90.0 80.0)) -Codec 'h264' -Mode 'off')
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 10).InRange $true
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 10.04).AtEdge $true
    Assert-Equal (Get-CurveAt -Curve $c -Mbps 11).InRange $false
}
Assert-Test 'range text names the curve''s mode' {
    $at = Get-CurveAt -Curve (New-Curve) -Mbps 30
    Assert-Equal (Format-Range $at) 'CBR runs span 10.00 to 20.00 Mbps'
    Assert-Equal (Format-Range $at 'vbr') 'vbr runs span 10.00 to 20.00 Mbps'
    Assert-Equal (Format-Range (Get-CurveAt -Curve @() -Mbps 5) 'vbr') 'no vbr runs'
    Assert-Equal (Get-CurveLabel 'off') 'CBR'
    Assert-Equal (Get-CurveLabel 'vbr') 'vbr'
}

# ---- equal-quality size
Assert-Test 'equal-quality size: the curve inverted at the run''s mean VMAF, as a percentage of the CBR size' {
    $c = New-Curve
    # VMAF 96 is reached by CBR at 20 Mbps: a run of 17.4 Mbps at 96 needs 87% of the CBR bytes
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 96.0 -Mbps 17.4).Text '87% of CBR'
    Assert-Near (Get-EqualQualitySize -Curve $c -Mean 96.0 -Mbps 17.4).Percent 87 1e-6
    # VMAF 93 is the geometric middle (14.142 Mbps): a run of 15 Mbps at 93 needs 106% of it
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 93.0 -Mbps 15).Text '106% of CBR'
    # above the top of the curve: the CBR size is more than 20 Mbps, so the ratio is below run / 20
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 97.0 -Mbps 17.4).Text '< 87% of CBR'
    # below the bottom: the CBR size is under 10 Mbps, so the ratio is above run / 10
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 85.0 -Mbps 9.0).Text '> 90% of CBR'
    Assert-Equal (Get-EqualQualitySize -Curve @() -Mean 90.0 -Mbps 9.0).Text 'n/a (no curve)'
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean $null -Mbps 9.0).Text 'n/a (no curve)'
}
Assert-Test 'equal-quality size: n/a when the curve does not increase across the needed segment' {
    $rows = @((New-Row 'off' 10 90.0 80.0), (New-Row 'off' 15 88.0 78.0), (New-Row 'off' 20 96.0 88.0))
    $c = @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off')
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 89.0 -Mbps 12).Text 'n/a (curve not monotonic)'    # in the falling first segment (and the rising second)
    Assert-Equal (Get-EqualQualitySize -Curve $c -Mean 92.0 -Mbps 12).Text ('{0:0}% of CBR' -f (12 / [math]::Exp([math]::Log(15) + ([math]::Log(20) - [math]::Log(15)) * (92.0 - 88.0) / 8.0) * 100))
    # outside the VMAF range of a curve whose end segment falls
    $falling = @(Get-QualityCurve -Rows @((New-Row 'off' 10 90.0 80.0), (New-Row 'off' 20 88.0 78.0)) -Codec 'h264' -Mode 'off')
    Assert-Equal (Get-EqualQualitySize -Curve $falling -Mean 95.0 -Mbps 12).Text 'n/a (curve not monotonic)'
    Assert-Equal (Get-EqualQualitySize -Curve $falling -Mean 80.0 -Mbps 12).Text 'n/a (curve not monotonic)'
}

# ---- VBR
Assert-Test 'VBR branch A: no mean allowance, p5 within 1.0, at the CBR rate 1/0.9 times the run''s' {
    $c = New-Curve
    # a VBR of 10 Mbps is compared with the curve at 11.11 Mbps (mean 90 + 6*log2(1.111) = 90.91, p5 80 + 8*0.152 = 81.22)
    $at = Get-CurveAt -Curve $c -Mbps (10 / 0.9)
    Assert-Near $at.Mean 90.912 0.001
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 $at.Mean ($at.P5 - 0.9)) $c).Result 'PASS'            # exactly the CBR mean passes
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 ($at.Mean + 0.3) ($at.P5 - 1.0)) $c).Result 'PASS'
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 ($at.Mean - 0.05) ($at.P5)) $c).Result 'FAIL'         # no 0.5 tolerance any more
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 ($at.Mean + 0.05) ($at.P5 - 1.05)) $c).Result 'FAIL'   # p5 more than 1.0 down
    # 18 Mbps / 0.9 = 20: the top of the curve, mean 96 / p5 88 -> p5 down to 87.0 allowed
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 18 96.0 87.0) $c).Result 'PASS'
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 18 95.9 87.5) $c).Result 'FAIL'
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 18 96.0 86.9) $c).Result 'FAIL'
    $r = Test-BenchVbr (New-Row 'vbr' 18 96.0 87.0) $c
    Assert-Equal ($r.Detail -like '*branch A*') $true
}
Assert-Test 'VBR branch B: passes on 1 VMAF over the curve at equal rate, not on less' {
    $c = New-Curve
    $eq = Get-CurveAt -Curve $c -Mbps 15
    $r = Test-BenchVbr (New-Row 'vbr' 15 ($eq.Mean + 1.0) ($eq.P5 - 5)) $c
    Assert-Equal $r.Result 'PASS'
    Assert-Equal ($r.Detail -like '*branch B*') $true
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 15 ($eq.Mean + 0.9) ($eq.P5 - 5)) $c).Result 'FAIL'
}
Assert-Test 'VBR is not judged outside the CBR range (no extrapolation) and says so' {
    $c = New-Curve
    # 9 Mbps is below the lowest CBR rate; the 10 Mbps comparison is on the curve but equal rate is not: fails branch A, B unjudged
    $r = Test-BenchVbr (New-Row 'vbr' 9 80.0 70.0) $c
    Assert-Equal $r.Result 'outside the CBR range'
    # a branch A pass is still a pass when the equal-rate point is outside
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 9 90.0 80.0) $c).Result 'PASS'
    # 25 Mbps: nothing of it is on the curve
    $r2 = Test-BenchVbr (New-Row 'vbr' 25 99.0 99.0) $c
    Assert-Equal $r2.Result 'outside the CBR range'
    Assert-Equal ($r2.Detail -like '*outside the CBR range*CBR runs span 10.00 to 20.00 Mbps*') $true
}
Assert-Test 'VBR at an edge: 0.3% below the lowest CBR rate counts as the end point and says so' {
    $c = New-Curve
    $r = Test-BenchVbr (New-Row 'vbr' 9.97 80.0 70.0) $c      # equal rate at edge (mean 90): 80 is far below -> FAIL, not "outside"
    Assert-Equal $r.Result 'FAIL'
    Assert-Equal ($r.Detail -like '*(at edge)*') $true
}
Assert-Test 'VBR: a missing p5 or mean on either side never passes branch A' {
    $c = New-Curve
    $at = Get-CurveAt -Curve $c -Mbps (10 / 0.9)
    # run without p5: branch A not evaluable; branch B (needs +1.0 at 10 Mbps = 91.0) decides
    $r = Test-BenchVbr (New-Row 'vbr' 10 ($at.Mean + 5) $null) $c
    Assert-Equal $r.Result 'PASS'
    Assert-Equal ($r.Detail -like '*A (*not evaluable*') $true
    Assert-Equal ($r.Detail -like '*branch B*') $true
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 90.5 $null) $c).Result 'n/a'
    # curve without p5 (the p5 of the CBR runs was not measured): same
    $noP5 = @(Get-QualityCurve -Rows @((New-Row 'off' 10 90.0 $null), (New-Row 'off' 20 96.0 $null)) -Codec 'h264' -Mode 'off')
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 90.5 80.0) $noP5).Result 'n/a'
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 99.0 80.0) $noP5).Result 'PASS'   # branch B
    # no mean at all
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 10 $null 80.0) $c).Result 'n/a'
}
Assert-Test 'VBR detail carries the equal-quality size' {
    $c = New-Curve
    $r = Test-BenchVbr (New-Row 'vbr' 17.4 96.0 88.0) $c
    Assert-Equal ($r.Detail -like '*equal-quality size: 87% of CBR*') $true
}
Assert-Test 'VBR that is INVALID is reported INVALID with its reasons whatever the numbers say' {
    $c = New-Curve
    $r = Test-BenchVbr (New-Row 'vbr' 15 99.0 99.0 'h264' 16 $true @('VBR did not take effect: fell back')) $c
    Assert-Equal $r.Result 'INVALID'
    Assert-Equal ($r.Detail -like '*VBR did not take effect*') $true
}
Assert-Test 'no VMAF gives n/a for both checks' {
    $c = New-Curve
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 15 $null $null) $c).Result 'n/a'
    Assert-Equal (Test-BenchLowLatency (New-Row 'lowlatency-off' 15 $null $null) $c $null).Result 'n/a'
    Assert-Equal (Test-BenchVbr (New-Row 'vbr' 15 90.0 80.0) @()).Result 'n/a'
}

# ---- low latency off
Assert-Test 'low latency off: +1 VMAF over the curve at its own rate passes, less fails, outside the range is not judged' {
    $c = New-Curve
    $eq = Get-CurveAt -Curve $c -Mbps 15
    Assert-Equal (Test-BenchLowLatency (New-Row 'lowlatency-off' 15 ($eq.Mean + 1.0) 80.0) $c $null).Result 'PASS'
    Assert-Equal (Test-BenchLowLatency (New-Row 'lowlatency-off' 15 ($eq.Mean + 0.99) 80.0) $c $null).Result 'FAIL'
    Assert-Equal (Test-BenchLowLatency (New-Row 'lowlatency-off' 25 99.0 80.0) $c $null).Result 'outside the CBR range'
    # a higher rate at the same requested bitrate does not count as a gain: it is judged at its own rate
    Assert-Equal (Test-BenchLowLatency (New-Row 'lowlatency-off' 18 ($eq.Mean + 1.0) 80.0) $c $null).Result 'FAIL'
}
Assert-Test 'low latency off: the range text names the vbr curve when that is the baseline' {
    $c = New-Curve
    $r = Test-BenchLowLatency (New-Row 'vbr-lowlatency-off' 25 99.0 80.0) $c $null -CurveName 'vbr'
    Assert-Equal $r.Result 'outside the vbr range'
    Assert-Equal ($r.Detail -like '*vbr runs span*') $true
    Assert-Equal ($r.Detail -like '*CBR runs span*') $false
}
Assert-Test 'low latency off: INVALID when it did not go off, and the evidence is shown next to the low-latency-on run' {
    $c = New-Curve
    $bad = Test-BenchLowLatency (New-Row 'lowlatency-off' 15 99.0 90.0 'h264' 16 $true @('low latency did not go off')) $c $null
    Assert-Equal $bad.Result 'INVALID'
    $on = New-Row 'off' 15 93.0 84.0; $on.Held = 0; $on.MaxInFlight = 2
    $ll = New-Row 'lowlatency-off' 15 99.0 90.0; $ll.Held = 4; $ll.MaxInFlight = 7
    $r = Test-BenchLowLatency $ll $c $on
    Assert-Equal $r.Result 'PASS'
    Assert-Equal ($r.Detail -like '*held at end 4*max in flight 7*low latency on: held 0, max in flight 2*') $true
    $r2 = Test-BenchLowLatency $ll $c $on -ShowEqualQuality
    Assert-Equal ($r2.Detail -like '*equal-quality size: *% of CBR*') $true
}

# ---- peak cap
Assert-Test 'peak cap: peak 1 s <= 1.65 x the mean second and <= 1.65 x the requested Mbps' {
    $row = New-Row 'vbr' 15 90.0 80.0; $row.MeanMbps = 10; $row.PeakMbps = 16.5; $row.RequestedMbps = 10
    Assert-Equal (Test-PeakCap $row).Result 'PASS'
    $row.PeakMbps = 16.6
    Assert-Equal (Test-PeakCap $row).Result 'FAIL'
    # within 1.65 x the mean second but over 1.65 x the requested Mbps (a run whose mean second sits well above the target)
    $row.MeanMbps = 12; $row.PeakMbps = 18.0; $row.RequestedMbps = 10
    $r = Test-PeakCap $row
    Assert-Equal $r.Result 'FAIL'
    Assert-Equal ($r.Detail -like '*of requested*') $true
    $row.RequestedMbps = 11
    Assert-Equal (Test-PeakCap $row).Result 'PASS'
    $row.MeanMbps = 0
    Assert-Equal (Test-PeakCap $row).Result 'n/a'
}

# ---- Get-BenchChecks
Assert-Test 'Get-BenchChecks judges every vbr / low-latency-off run against the CBR curve and adds the peak cap' {
    $rows = @(
        (New-Row 'off' 10 90.0 80.0 'h264' 12), (New-Row 'off' 20 96.0 88.0 'h264' 20),
        (New-Row 'vbr' 15 94.0 85.0 'h264' 16),
        (New-Row 'lowlatency-off' 15 95.0 85.0 'h264' 16),
        (New-Row 'vbr-lowlatency-off' 15 94.0 85.0 'h264' 16),
        (New-Row 'vbr' 12 90.0 80.0 'hevc' 14)            # no CBR hevc run at all
    )
    $checks = @(Get-BenchChecks -Rows $rows)
    # vbr(16): Vbr + PeakCap; lowlatency-off(16): LowLatency; vbr-lowlatency-off(16): Vbr + LowLatency + PeakCap; hevc vbr: Vbr + PeakCap
    Assert-Equal $checks.Count 8
    $v = @($checks | Where-Object { $_.Run -eq 'h264_16M_vbr' -and $_.Check -eq 'Vbr' })[0]
    Assert-Equal $v.Against 'off curve'
    $ll0 = @($checks | Where-Object { $_.Run -eq 'h264_16M_lowlatency-off' })[0]
    Assert-Equal $ll0.Result 'PASS'
    Assert-Equal ($ll0.Detail -like '*equal-quality size: *% of CBR*') $true
    Assert-Equal ($v.Detail -like '*equal-quality size: *% of CBR*') $true
    Assert-Equal @($checks | Where-Object { $_.Run -eq 'h264_16M_vbr-lowlatency-off' -and $_.Check -eq 'Vbr' })[0].Against 'off curve'
    # its low-latency check uses the vbr curve (a single point, 15 Mbps: its own rate, so it is in range)
    $ll = @($checks | Where-Object { $_.Run -eq 'h264_16M_vbr-lowlatency-off' -and $_.Check -eq 'LowLatency' })[0]
    Assert-Equal $ll.Against 'vbr curve'
    Assert-Equal @($checks | Where-Object { $_.Run -eq 'h264_16M_vbr' -and $_.Check -eq 'PeakCap' }).Count 1
    $missing = @($checks | Where-Object { $_.Run -eq 'hevc_14M_vbr' -and $_.Check -eq 'Vbr' })[0]
    Assert-Equal $missing.Result 'n/a'
}
Assert-Test 'Get-BenchChecks: an INVALID vbr run gets no peak cap' {
    $rows = @((New-Row 'off' 10 90.0 80.0 'h264' 12), (New-Row 'off' 20 96.0 88.0 'h264' 20), (New-Row 'vbr' 15 95.0 85.0 'h264' 16 $true @('VBR did not take effect')))
    $checks = @(Get-BenchChecks -Rows $rows)
    Assert-Equal @($checks | Where-Object { $_.Check -eq 'PeakCap' }).Count 0
    Assert-Equal @($checks | Where-Object { $_.Check -eq 'Vbr' })[0].Result 'INVALID'
}
Assert-Test 'Get-BenchChecks: the 0.9x rates make the branch A comparison land on a measured CBR point' {
    # CBR at 10.8, 12 and 20 Mbps; a VBR of 10.8 Mbps is compared with the curve at 12 Mbps, which is the measured 12 Mbps point.
    $rows = @(
        (New-Row 'off' 10.8 89.0 79.0 'h264' 10.8), (New-Row 'off' 12 90.0 80.0 'h264' 12), (New-Row 'off' 20 96.0 88.0 'h264' 20),
        (New-Row 'vbr' 10.8 90.5 79.1 'h264' 12)
    )
    $c = @(Get-BenchChecks -Rows $rows | Where-Object { $_.Check -eq 'Vbr' })[0]
    Assert-Equal $c.Result 'PASS'
    Assert-Equal ($c.Detail -like '*mean +0.50, p5 -0.90*') $true
    # a mean even slightly under the 12 Mbps point fails (no tolerance)
    $rows[3].VmafMean = 89.95
    Assert-Equal (@(Get-BenchChecks -Rows $rows | Where-Object { $_.Check -eq 'Vbr' })[0]).Result 'FAIL'
}
Assert-Test 'Get-BenchChecks: curves and checks never mix groups' {
    $rows = @(
        (New-Row 'off' 10 90.0 80.0 'h264' 12 $false @() 'g1'), (New-Row 'off' 20 96.0 88.0 'h264' 20 $false @() 'g1'),
        (New-Row 'vbr' 15 95.0 85.0 'h264' 16 $false @() 'g1'),
        (New-Row 'vbr' 15 95.0 85.0 'h264' 14 $false @() 'g2')     # g2 has no CBR run of its own
    )
    $checks = @(Get-BenchChecks -Rows $rows | Where-Object { $_.Check -eq 'Vbr' })
    Assert-Equal @($checks | Where-Object { $_.Run -eq 'h264_16M_vbr' })[0].Result 'PASS'
    Assert-Equal @($checks | Where-Object { $_.Run -eq 'h264_14M_vbr' })[0].Result 'n/a'
}
Assert-Test 'Get-BenchChecks: a failed run is n/a and an INVALID off run is not on the curve' {
    $failed = New-Row 'vbr' 15 90.0 80.0; $failed.Failed = $true
    $badOff = New-Row 'off' 12 50.0 40.0 'h264' 12 $true @('out of order')
    $rows = @((New-Row 'off' 10 90.0 80.0 'h264' 12), (New-Row 'off' 20 96.0 88.0 'h264' 20), $badOff, $failed, (New-Row 'vbr' 15 95.0 85.0))
    $checks = @(Get-BenchChecks -Rows $rows)
    Assert-Equal @($checks | Where-Object { $_.Check -eq 'Vbr' -and $_.Run -like '*vbr' })[0].Result 'n/a'
    $c = @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off')
    Assert-Equal $c.Count 2
}

# ---- validity
Assert-Test 'validity: a valid CBR run has no reasons' {
    $row = New-ValidRow 'off'
    Assert-Equal @(Get-RunInvalidReasons -Row $row -ReferencePackets 600).Count 0
}
Assert-Test 'validity: low latency off is INVALID when low latency stayed on or anything fell back' {
    $on = New-ValidRow 'lowlatency-off' @{ applied = [ordered]@{ rate = 'CBR'; lowLatency = $true; fallback = $null } }
    $r = @(Get-RunInvalidReasons -Row $on)
    Assert-Equal $r.Count 1
    Assert-Equal ($r[0] -like '*low latency did not go off*') $true
    $fb = New-ValidRow 'lowlatency-off' @{ applied = [ordered]@{ rate = 'CBR'; lowLatency = $false; fallback = 'low latency stays on (reads back on)' } }
    $r2 = @(Get-RunInvalidReasons -Row $fb)
    Assert-Equal $r2.Count 1
    Assert-Equal ($r2[0] -like 'fallback: low latency stays on*') $true
    $ok = New-ValidRow 'lowlatency-off' @{ applied = [ordered]@{ rate = 'CBR'; lowLatency = $false; fallback = $null } }
    Assert-Equal @(Get-RunInvalidReasons -Row $ok).Count 0
    $none = New-ValidRow 'vbr-lowlatency-off' @{ applied = $null }
    Assert-Equal (@(Get-RunInvalidReasons -Row $none).Count -ge 1) $true
}
Assert-Test 'validity: a vbr mode needs applied VBR and a verdict that says it took effect' {
    $cbr = New-ValidRow 'vbr' @{ vbrApplicable = $true; vbrTookEffect = $true; applied = [ordered]@{ rate = 'CBR'; lowLatency = $true; fallback = 'rate mode VBR not applied (Differs), using CBR' } }
    $r = @(Get-RunInvalidReasons -Row $cbr)
    Assert-Equal ($r -join '|' -like "*applied rate mode is 'CBR', not VBR*") $true
    $flat = New-ValidRow 'vbr-lowlatency-off' @{ vbrApplicable = $true; vbrTookEffect = $false; vbrVerdict = 'VBR did not take effect: peak 1 s is not above 1.05 x the mean'
        applied = [ordered]@{ rate = 'PeakConstrainedVBR'; lowLatency = $false; fallback = $null } }
    $r2 = @(Get-RunInvalidReasons -Row $flat)
    Assert-Equal $r2.Count 1
    Assert-Equal ($r2[0] -like 'VBR did not take effect*') $true
    $ok = New-ValidRow 'vbr' @{ vbrApplicable = $true; vbrTookEffect = $true; applied = [ordered]@{ rate = 'PeakConstrainedVBR'; lowLatency = $true; fallback = $null } }
    Assert-Equal @(Get-RunInvalidReasons -Row $ok).Count 0
}
Assert-Test 'validity: out-of-order outputs make a run INVALID' {
    $row = New-ValidRow 'off' @{ outOfOrderOutputs = 2 }
    $r = @(Get-RunInvalidReasons -Row $row)
    Assert-Equal $r.Count 1
    Assert-Equal ($r[0] -like '2 output frame(s) out of order') $true
}
Assert-Test 'validity: frame alignment against the reference packet count (whole clip only) and the VMAF frame count' {
    $row = New-ValidRow 'off'
    Assert-Equal @(Get-RunInvalidReasons -Row $row -ReferencePackets 600).Count 0
    $r = @(Get-RunInvalidReasons -Row $row -ReferencePackets 601)
    Assert-Equal $r.Count 1
    Assert-Equal ($r[0] -like 'frame alignment: the reference has 601 packets*decoded 600*') $true
    Assert-Equal @(Get-RunInvalidReasons -Row $row -ReferencePackets $null).Count 0
    $part = New-ValidRow 'off' @{ secondsLimit = 5.0; decodedFrames = 300; framesOut = 298 }
    Assert-Equal @(Get-RunInvalidReasons -Row $part -ReferencePackets 600).Count 0
    $m = ConvertTo-BenchRow -Json (New-Json -Mode 'off' -Over @{ framesOut = 598 }) -Metrics ([pscustomobject]@{ VmafMean = 93.5; VmafP5 = 88.0; VmafFrames = 600 })
    $r3 = @(Get-RunInvalidReasons -Row $m)
    Assert-Equal $r3.Count 1
    Assert-Equal ($r3[0] -like 'frame alignment: VMAF scored 600 frames but the encoder gave 598') $true
    $good = ConvertTo-BenchRow -Json (New-Json -Mode 'off') -Metrics ([pscustomobject]@{ VmafMean = 93.5; VmafP5 = 88.0; VmafFrames = 600 })
    Assert-Equal @(Get-RunInvalidReasons -Row $good).Count 0
}
Assert-Test 'Set-RunValidity marks the row and a failed run is left alone' {
    $row = New-ValidRow 'off' @{ outOfOrderOutputs = 1 }
    Set-RunValidity -Row $row -ReferencePackets 600
    Assert-Equal $row.Invalid $true
    Assert-Equal $row.InvalidReasons.Count 1
    $failed = ConvertTo-BenchRow -Json ('{"name":"x","mode":"off","codec":"h264","requestedMbps":12,"error":"boom"}' | ConvertFrom-Json) -Metrics $null
    Set-RunValidity -Row $failed -ReferencePackets 1
    Assert-Equal $failed.Invalid $false
}

Assert-Test 'validity: dropped frames make a run INVALID' {
    $row = New-ValidRow 'off' @{ dropped = 3 }
    $r = @(Get-RunInvalidReasons -Row $row)
    Assert-Equal $r.Count 1
    Assert-Equal ($r[0] -eq 'encoder dropped 3 frames') $true
    Assert-Equal @(Get-RunInvalidReasons -Row (New-ValidRow 'off' @{ dropped = 0 })).Count 0
}

# ---- metrics cache
$script:FfV = 'ffmpeg version 7.0 Copyright (c) 2000-2024'
Assert-Test 'metrics cache is reused only while the MP4, the reference, libvmaf, ffmpeg and the filter graph match' {
    $m = [pscustomobject]@{ Mp4Bytes = 1000; Mp4ModifiedTicks = '638000000000000000'; RefBytes = 5000; RefModifiedTicks = '637000000000000000'; VmafMean = 90
        HasVmaf = $true; FfmpegVersion = $script:FfV; GraphVersion = 'g2' }
    function Test-Cur { param($M, $Mp4Bytes = 1000, $Mp4Ticks = '638000000000000000', $RefBytes = 5000, $RefTicks = '637000000000000000', $HasVmaf = $true, $Ff = $script:FfV, $Graph = 'g2')
        Test-MetricsCurrent -Metrics $M -Mp4Bytes $Mp4Bytes -Mp4Ticks $Mp4Ticks -RefBytes $RefBytes -RefTicks $RefTicks -HasVmaf $HasVmaf -FfmpegVersion $Ff -GraphVersion $Graph }
    Assert-Equal (Test-Cur $m) $true
    Assert-Equal (Test-Cur $m -Mp4Bytes 1001) $false
    Assert-Equal (Test-Cur $m -Mp4Ticks '638000000000000001') $false
    Assert-Equal (Test-Cur $m -RefBytes 5001) $false
    Assert-Equal (Test-Cur $m -RefTicks '637000000000000001') $false
    Assert-Equal (Test-Cur $m -Ff 'ffmpeg version 7.1') $false
    Assert-Equal (Test-Cur $m -Graph 'g3') $false
    Assert-Equal (Test-Cur $m -HasVmaf $false) $false
    Assert-Equal (Test-Cur ([pscustomobject]@{ VmafMean = 90 })) $false
    Assert-Equal (Test-Cur $null) $false
    # a cache from before the stamp had these fields is stale
    $old = [pscustomobject]@{ Mp4Bytes = 1000; Mp4ModifiedTicks = '638000000000000000'; RefBytes = 5000; RefModifiedTicks = '637000000000000000'; VmafMean = 90 }
    Assert-Equal (Test-Cur $old) $false
}
Assert-Test 'metrics cache: a cached null VMAF is stale whenever libvmaf is present, and fine when it is not' {
    $noVmaf = [pscustomobject]@{ Mp4Bytes = 1000; Mp4ModifiedTicks = 'a'; RefBytes = 5000; RefModifiedTicks = 'b'; VmafMean = $null; HasVmaf = $false; FfmpegVersion = $script:FfV; GraphVersion = 'g2' }
    Assert-Equal (Test-MetricsCurrent -Metrics $noVmaf -Mp4Bytes 1000 -Mp4Ticks 'a' -RefBytes 5000 -RefTicks 'b' -HasVmaf $false -FfmpegVersion $script:FfV -GraphVersion 'g2') $true
    Assert-Equal (Test-MetricsCurrent -Metrics $noVmaf -Mp4Bytes 1000 -Mp4Ticks 'a' -RefBytes 5000 -RefTicks 'b' -HasVmaf $true -FfmpegVersion $script:FfV -GraphVersion 'g2') $false
    $nullButStamped = [pscustomobject]@{ Mp4Bytes = 1000; Mp4ModifiedTicks = 'a'; RefBytes = 5000; RefModifiedTicks = 'b'; VmafMean = $null; HasVmaf = $true; FfmpegVersion = $script:FfV; GraphVersion = 'g2' }
    Assert-Equal (Test-MetricsCurrent -Metrics $nullButStamped -Mp4Bytes 1000 -Mp4Ticks 'a' -RefBytes 5000 -RefTicks 'b' -HasVmaf $true -FfmpegVersion $script:FfV -GraphVersion 'g2') $false
}
Assert-Test 'metrics cache: only a complete measurement is written' {
    Assert-Equal (Test-MetricsCacheable -Metrics @{ VmafMean = 91.0 } -HasVmaf $true) $true
    Assert-Equal (Test-MetricsCacheable -Metrics @{ VmafMean = $null } -HasVmaf $true) $false
    Assert-Equal (Test-MetricsCacheable -Metrics @{ VmafMean = $null } -HasVmaf $false) $true
    Assert-Equal (Test-MetricsCacheable -Metrics $null -HasVmaf $false) $false
}
Assert-Test 'metrics cache survives a JSON round trip with the tick strings and the stamp intact' {
    $m = @{ VmafMean = 90.5; Mp4Bytes = 123456789; Mp4ModifiedTicks = '638987654321098765'; RefBytes = 987654321; RefModifiedTicks = '637123456789012345'; HasVmaf = $true; FfmpegVersion = $script:FfV; GraphVersion = $script:FilterGraphVersion }
    $back = ($m | ConvertTo-Json) | ConvertFrom-Json
    Assert-Equal (Test-MetricsCurrent -Metrics $back -Mp4Bytes 123456789 -Mp4Ticks '638987654321098765' -RefBytes 987654321 -RefTicks '637123456789012345' -HasVmaf $true -FfmpegVersion $script:FfV -GraphVersion $script:FilterGraphVersion) $true
}

# ---- which runs belong to the reference, and groups
Assert-Test 'a run JSON is used only when it was benched against this reference file' {
    $ref = 'C:\clips\ref.mp4'
    $ok = '{"name":"x","mode":"off","reference":"C:\\clips\\ref.mp4","referenceInfo":{"fileBytes":5000}}' | ConvertFrom-Json
    Assert-Equal ($null -eq (Test-RunMatchesReference -Json $ok -ReferenceFull $ref -ReferenceBytes 5000)) $true
    $case = '{"name":"x","mode":"off","reference":"c:\\CLIPS\\Ref.MP4","referenceInfo":{"fileBytes":5000}}' | ConvertFrom-Json
    Assert-Equal ($null -eq (Test-RunMatchesReference -Json $case -ReferenceFull $ref -ReferenceBytes 5000)) $true
    $dots = '{"name":"x","mode":"off","reference":"C:\\clips\\sub\\..\\ref.mp4"}' | ConvertFrom-Json
    Assert-Equal ($null -eq (Test-RunMatchesReference -Json $dots -ReferenceFull $ref -ReferenceBytes 5000)) $true    # no referenceInfo (a failed run): the path decides
    $other = '{"name":"x","mode":"off","reference":"C:\\clips\\other.mp4","referenceInfo":{"fileBytes":5000}}' | ConvertFrom-Json
    Assert-Equal ((Test-RunMatchesReference -Json $other -ReferenceFull $ref -ReferenceBytes 5000) -like '*different reference*other.mp4*') $true
    $size = '{"name":"x","mode":"off","reference":"C:\\clips\\ref.mp4","referenceInfo":{"fileBytes":4999}}' | ConvertFrom-Json
    Assert-Equal ((Test-RunMatchesReference -Json $size -ReferenceFull $ref -ReferenceBytes 5000) -like '*size changed*4999*5000*') $true
    $none = '{"name":"x","mode":"off"}' | ConvertFrom-Json
    Assert-Equal ((Test-RunMatchesReference -Json $none -ReferenceFull $ref -ReferenceBytes 5000) -like '*names no reference*') $true
}
Assert-Test 'groups: codec, seconds limit and keyframe interval make the key; rows carry it' {
    Assert-Equal (Get-GroupKey -Codec 'h264' -SecondsLimit $null -KeyframeSeconds 1.0) 'h264, whole clip, GOP 1 s'
    Assert-Equal (Get-GroupKey -Codec 'hevc' -SecondsLimit 5.5 -KeyframeSeconds 2) 'hevc, first 5.5 s, GOP 2 s'
    Assert-Equal (New-ValidRow 'off').Group 'h264, whole clip, GOP 1 s'
    Assert-Equal (New-ValidRow 'off' @{ secondsLimit = 5.0 }).Group 'h264, first 5 s, GOP 1 s'
    Assert-Equal (New-ValidRow 'off' @{ keyframeSeconds = 2.0 }).Group 'h264, whole clip, GOP 2 s'
    Assert-Equal (New-ValidRow 'off').ClinkVersion '1.2.3'
    Assert-Near (New-ValidRow 'off').AchievedMbps 15.5
    Assert-Near (New-ValidRow 'off').FileMbps 15.9
}
Assert-Test 'groups: Get-RunGroups splits the rows; a codec with several groups and a group with several builds are warned about' {
    $a = New-ValidRow 'off'
    $b = New-ValidRow 'off' @{ name = 'h264_12M_off'; secondsLimit = 5.0 }
    $c = New-ValidRow 'off' @{ name = 'hevc_14M_off'; codec = 'hevc' }
    $g = @(Get-RunGroups -Rows @($a, $b, $c))
    Assert-Equal $g.Count 3
    Assert-Equal @($g | Where-Object { $_.Codec -eq 'h264' }).Count 2
    $w = @(Get-GroupWarnings -Rows @($a, $b, $c))
    Assert-Equal $w.Count 1
    Assert-Equal ($w[0] -like '*h264 was benched in 2 different groups*first 5 s*whole clip*') $true
    Assert-Equal @(Get-GroupWarnings -Rows @($a, $c)).Count 0
    # one group, two builds (and one run from a bench that did not record it)
    $d = New-ValidRow 'vbr' @{ clinkVersion = '1.2.4' }
    $e = New-ValidRow 'lowlatency-off' @{ clinkVersion = '' }
    $w2 = @(Get-GroupWarnings -Rows @($a, $d, $e))
    Assert-Equal $w2.Count 1
    Assert-Equal ($w2[0] -like '*mixes Clink builds: (not recorded), 1.2.3, 1.2.4*') $true
    Assert-Equal @(Get-GroupWarnings -Rows @($a, (New-ValidRow 'vbr'))).Count 0
}
Assert-Test 'reference colour: warns when not BT.709 limited range, quiet for untagged or matching tags' {
    $ok = [pscustomobject]@{ ColorRange = 'tv'; ColorSpace = 'bt709'; ColorPrimaries = 'bt709'; ColorTransfer = 'bt709' }
    Assert-Equal ($null -eq (Get-ColorWarning -Ref $ok)) $true
    Assert-Equal ($null -eq (Get-ColorWarning -Ref ([pscustomobject]@{ ColorRange = ''; ColorSpace = 'unknown'; ColorPrimaries = ''; ColorTransfer = $null }))) $true
    Assert-Equal ($null -eq (Get-ColorWarning -Ref ([pscustomobject]@{ Codec = 'h264' }))) $true
    Assert-Equal ($null -eq (Get-ColorWarning -Ref $null)) $true
    $pc = [pscustomobject]@{ ColorRange = 'pc'; ColorSpace = 'bt709'; ColorPrimaries = 'bt709'; ColorTransfer = 'bt709' }
    Assert-Equal ((Get-ColorWarning -Ref $pc) -like '*not BT.709 limited*color_range = pc (expected tv)*') $true
    $bt2020 = [pscustomobject]@{ ColorRange = 'tv'; ColorSpace = 'bt2020nc'; ColorPrimaries = 'bt2020'; ColorTransfer = 'smpte2084' }
    $w = Get-ColorWarning -Ref $bt2020
    Assert-Equal ($w -like '*color_space = bt2020nc*color_primaries = bt2020*color_transfer = smpte2084*') $true
    # and it is part of the reference warnings
    $ref = [pscustomobject]@{ Codec = 'h264'; VideoMbps = 60.0; OverallMbps = 61.0; ClinkBrand = $false; ColorRange = 'pc'; ColorSpace = 'bt709'; ColorPrimaries = 'bt709'; ColorTransfer = 'bt709' }
    Assert-Equal @(Get-ReferenceWarnings -Ref $ref -MaxBenchMbps 20 -RunCodecs @('h264')).Count 1
}
Assert-Test 'ffprobe JSON of the reference also carries the colour tags' {
    $text = '{"streams":[{"codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"60/1","color_range":"tv","color_space":"bt709","color_primaries":"bt709","color_transfer":"bt709"}],"format":{}}'
    $p = Read-ProbeJson -Text $text
    Assert-Equal $p.ColorRange 'tv'
    Assert-Equal $p.ColorSpace 'bt709'
    Assert-Equal $p.ColorPrimaries 'bt709'
    Assert-Equal $p.ColorTransfer 'bt709'
    Assert-Equal ((Read-ProbeJson -Text '{"streams":[{"codec_name":"h264"}]}').ColorRange) ''
}

# ---- reference
Assert-Test 'reference warnings: bitrate below 2x the highest bench rate, and a Clink recording of the same codec' {
    $ok = [pscustomobject]@{ Codec = 'h264'; VideoMbps = 60.0; OverallMbps = 61.0; ClinkBrand = $false }
    Assert-Equal @(Get-ReferenceWarnings -Ref $ok -MaxBenchMbps 20 -RunCodecs @('h264')).Count 0
    $low = [pscustomobject]@{ Codec = 'h264'; VideoMbps = 30.0; OverallMbps = 31.0; ClinkBrand = $false }
    $w = @(Get-ReferenceWarnings -Ref $low -MaxBenchMbps 20 -RunCodecs @('h264'))
    Assert-Equal $w.Count 1
    Assert-Equal ($w[0] -like '*only 30.0 Mbps*below 2x*') $true
    # exactly 2x is enough; the overall bitrate stands in when ffprobe gave no stream bitrate
    Assert-Equal @(Get-ReferenceWarnings -Ref ([pscustomobject]@{ Codec = 'h264'; VideoMbps = $null; OverallMbps = 40.0; ClinkBrand = $false }) -MaxBenchMbps 20 -RunCodecs @('h264')).Count 0
    $clink = [pscustomobject]@{ Codec = 'h264'; VideoMbps = 60.0; OverallMbps = 61.0; ClinkBrand = $true }
    $w2 = @(Get-ReferenceWarnings -Ref $clink -MaxBenchMbps 20 -RunCodecs @('h264'))
    Assert-Equal $w2.Count 1
    Assert-Equal ($w2[0] -like '*Clink recording (clk1*') $true
    Assert-Equal @(Get-ReferenceWarnings -Ref $clink -MaxBenchMbps 20 -RunCodecs @('hevc')).Count 0
    Assert-Equal @(Get-ReferenceWarnings -Ref ([pscustomobject]@{ Codec = 'h264'; VideoMbps = $null; OverallMbps = $null; ClinkBrand = $false }) -MaxBenchMbps 20 -RunCodecs @('h264')).Count 1
    Assert-Equal @(Get-ReferenceWarnings -Ref $null -MaxBenchMbps 20 -RunCodecs @('h264')).Count 1
}
Assert-Test 'ffprobe JSON of the reference: codec, size, fps, bitrates and packet count' {
    $text = '{"programs":[],"streams":[{"codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"60/1","bit_rate":"48000000","nb_read_packets":"3600"}],"format":{"duration":"60.0","bit_rate":"48500000"}}'
    $p = Read-ProbeJson -Text $text
    Assert-Equal $p.Codec 'h264'
    Assert-Equal $p.Width 1920
    Assert-Near $p.Fps 60
    Assert-Near $p.VideoMbps 48
    Assert-Near $p.OverallMbps 48.5
    Assert-Equal $p.Packets 3600
    $na = Read-ProbeJson -Text '{"streams":[{"codec_name":"hevc","width":2560,"height":1440,"avg_frame_rate":"30000/1001","bit_rate":"N/A","nb_read_packets":"10"}],"format":{}}'
    Assert-Near $na.Fps 29.97 0.01
    Assert-Equal ($null -eq $na.VideoMbps) $true
    Assert-Equal ($null -eq $na.OverallMbps) $true
    Assert-Equal ($null -eq (Read-ProbeJson -Text 'not json')) $true
    Assert-Equal ($null -eq (Read-ProbeJson -Text '{"streams":[]}')) $true
}

# ---- parsing
Assert-Test 'ffmpeg summary lines are parsed (the last one wins)' {
    $text = "[Parsed_ssim_5 @ 0000] SSIM Y:0.98 (17.0) U:0.99 (20.0) V:0.99 (20.1) All:0.985000 (18.2)`n[Parsed_psnr_6 @ 0000] PSNR y:41.2 u:44.0 v:44.5 average:42.250000 min:30.1 max:60.0`n"
    $r = Read-SsimPsnr -StderrText $text
    Assert-Near $r.Ssim 0.985
    Assert-Near $r.Psnr 42.25
    Assert-Equal $r.PsnrInfinite $false
    $none = Read-SsimPsnr -StderrText 'nothing'
    Assert-Equal ($null -eq $none.Ssim -and $null -eq $none.Psnr) $true
}
Assert-Test 'PSNR infinity is null plus a flag, so the cached JSON stays valid' {
    $text = "[Parsed_ssim_5 @ 0000] SSIM Y:1.0 (inf) U:1.0 (inf) V:1.0 (inf) All:1.000000 (inf)`n[Parsed_psnr_6 @ 0000] PSNR y:inf u:inf v:inf average:inf min:inf max:inf`n"
    $r = Read-SsimPsnr -StderrText $text
    Assert-Equal ($null -eq $r.Psnr) $true
    Assert-Equal $r.PsnrInfinite $true
    $json = @{ Psnr = $r.Psnr; PsnrInfinite = $r.PsnrInfinite } | ConvertTo-Json
    Assert-Equal ($json -match 'Infinity') $false
    $back = $json | ConvertFrom-Json
    Assert-Equal $back.PsnrInfinite $true
    $row = ConvertTo-BenchRow -Json (New-Json -Mode 'off') -Metrics $back
    Assert-Equal $row.PsnrInfinite $true
    Assert-Equal ($null -eq $row.Psnr) $true
}
Assert-Test 'libvmaf JSON log: mean, 5th percentile and frame count' {
    $scores = 0..100 | ForEach-Object { $_ }
    $json = @{ frames = @($scores | ForEach-Object { @{ frameNum = $_; metrics = @{ vmaf = [double]$_ } } }) } | ConvertTo-Json -Depth 6
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('encoder-bench-test-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        [System.IO.File]::WriteAllText($path, $json)
        $v = Read-VmafLog -Path $path
        Assert-Equal $v.Frames 101
        Assert-Near $v.Mean 50
        Assert-Near $v.P5 5
    }
    finally { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    Assert-Equal ($null -eq (Read-VmafLog -Path (Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-vmaf-log.json'))) $true
}
Assert-Test 'a bench run JSON becomes a row, with its metrics' {
    $j = '{"name":"h264_16M_vbr","mode":"vbr","codec":"h264","requestedMbps":16,"error":null,"fileSizeBytes":1048576,"achievedMbps":15.5,"peakMbps1s":20.0,"meanMbps1s":15.0,"peakOverMean":1.333,"dropped":0,"framesHeldAtEnd":2,"maxFramesInFlight":3,"framesIn":600,"framesOut":598,"decodedFrames":600,"outOfOrderOutputs":0,"secondsLimit":null,"encodeSpeedFps":210.5,"sourceFps":60,"maxWaitForRoomMs":12.5,"vbrApplicable":true,"vbrTookEffect":true,"vbrVerdict":"ok","applied":{"rate":"PeakConstrainedVBR","lowLatency":true,"fallback":null}}' | ConvertFrom-Json
    $row = ConvertTo-BenchRow -Json $j -Metrics ([pscustomobject]@{ VmafMean = 93.5; VmafP5 = 88.0; VmafFrames = 598; Ssim = 0.97; Psnr = 41.0; FfprobeMbps = 15.4 })
    Assert-Equal $row.Mode 'vbr'
    Assert-Equal $row.Failed $false
    Assert-Equal $row.VbrTookEffect $true
    Assert-Equal $row.Applied 'PeakConstrainedVBR, LL on'
    Assert-Equal $row.AppliedRate 'PeakConstrainedVBR'
    Assert-Equal $row.HasApplied $true
    Assert-Equal $row.Held 2
    Assert-Equal $row.DecodedFrames 600
    Assert-Equal ($null -eq $row.SecondsLimit) $true
    Assert-Near $row.EncodeSpeedFps 210.5
    Assert-Near $row.MaxWaitMs 12.5
    Assert-Equal $row.VmafFrames 598
    Assert-Near $row.VmafMean 93.5
    Assert-Equal @(Get-RunInvalidReasons -Row $row -ReferencePackets 600).Count 0
    $failed = ConvertTo-BenchRow -Json ('{"name":"x","mode":"off","codec":"h264","requestedMbps":12,"error":"InvalidOperationException: boom"}' | ConvertFrom-Json) -Metrics $null
    Assert-Equal $failed.Failed $true
}

# ---- the bench-only quality mode
# A quality row: New-Row plus what ConvertTo-BenchRow reads from the qualityVsSpeed object of the run JSON.
function New-QualityRow {
    param([double]$Rate, $Mean, $P5, [double]$Mbps = 16, $ReadBack = 100, [bool]$HasQvs = $true, [string]$Group = '', [double]$Fps = 300)
    $r = New-Row 'quality' $Rate $Mean $P5 'h264' $Mbps $false @() $Group
    $r | Add-Member -NotePropertyName HasQvs -NotePropertyValue $HasQvs
    $r | Add-Member -NotePropertyName QvsReadBack -NotePropertyValue $ReadBack
    $r | Add-Member -NotePropertyName EncodeSpeedFps -NotePropertyValue $Fps
    $r | Add-Member -NotePropertyName OutOfOrder -NotePropertyValue 0
    $r | Add-Member -NotePropertyName Drops -NotePropertyValue 0
    return $r
}
Assert-Test 'quality: its baseline is the off curve, only for the Quality check' {
    Assert-Equal (Get-BaselineMode -Mode 'quality' -Kind 'Quality') 'off'
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'quality' -Kind 'Vbr')) $true
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'quality' -Kind 'LowLatency')) $true
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'off' -Kind 'Quality')) $true
    Assert-Equal ($null -eq (Get-BaselineMode -Mode 'vbr' -Kind 'Quality')) $true
    Assert-Equal ($script:ModeOrder -contains 'quality') $true
}
Assert-Test 'quality: a quality run is never a point of any curve' {
    $rows = @((New-Row 'off' 10 90.0 80.0), (New-Row 'off' 20 96.0 88.0), (New-QualityRow 15 99.0 99.0), (New-QualityRow 25 99.5 99.5))
    $c = @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'off')
    Assert-Equal $c.Count 2
    Assert-Near $c[1].Mbps 20
    Assert-Equal @(Get-QualityCurve -Rows $rows -Codec 'h264' -Mode 'vbr').Count 0
    # and the checks of every other row use the same two points
    $checks = @(Get-BenchChecks -Rows $rows)
    Assert-Equal @($checks | Where-Object { $_.Against -ne 'off curve' }).Count 0
}
Assert-Test 'quality: judged against the off curve by branch A (>= the curve at Mbps / 0.9, p5 within 1.0)' {
    $c = New-Curve
    $at = Get-CurveAt -Curve $c -Mbps (10 / 0.9)
    $r = Test-BenchQuality (New-QualityRow 10 $at.Mean ($at.P5 - 1.0)) $c
    Assert-Equal $r.Result 'PASS'
    Assert-Equal ($r.Detail -like '*branch A*') $true
    Assert-Equal (Test-BenchQuality (New-QualityRow 10 ($at.Mean - 0.05) $at.P5) $c).Result 'FAIL'
    Assert-Equal (Test-BenchQuality (New-QualityRow 10 ($at.Mean + 0.05) ($at.P5 - 1.05)) $c).Result 'FAIL'
}
Assert-Test 'quality: branch B (1.0 VMAF over the curve at its own Mbps), equal-quality size, outside, n/a' {
    $c = New-Curve
    $eq = Get-CurveAt -Curve $c -Mbps 15
    $r = Test-BenchQuality (New-QualityRow 15 ($eq.Mean + 1.0) ($eq.P5 - 5)) $c
    Assert-Equal $r.Result 'PASS'
    Assert-Equal ($r.Detail -like '*branch B*') $true
    Assert-Equal ($r.Detail -like '*equal-quality size: *% of CBR*') $true
    Assert-Equal (Test-BenchQuality (New-QualityRow 15 ($eq.Mean + 0.9) ($eq.P5 - 5)) $c).Result 'FAIL'
    Assert-Equal (Test-BenchQuality (New-QualityRow 25 99.0 99.0) $c).Result 'outside the CBR range'
    Assert-Equal (Test-BenchQuality (New-QualityRow 15 $null $null) $c).Result 'n/a'
    Assert-Equal (Test-BenchQuality (New-QualityRow 15 95.0 85.0) @()).Result 'n/a'
}
Assert-Test 'quality: Get-BenchChecks adds one Quality check against the off curve of the same group, no peak cap' {
    $rows = @(
        (New-Row 'off' 10 90.0 80.0 'h264' 12 $false @() 'g1'), (New-Row 'off' 20 96.0 88.0 'h264' 20 $false @() 'g1'),
        (New-QualityRow 15 97.5 90.0 16 100 $true 'g1'),
        (New-QualityRow 15 97.5 90.0 14 100 $true 'g2')       # g2 has no off run
    )
    $checks = @(Get-BenchChecks -Rows $rows)
    $mine = @($checks | Where-Object { $_.Run -eq 'h264_16M_quality' })
    Assert-Equal $mine.Count 1
    Assert-Equal $mine[0].Check 'Quality'
    Assert-Equal $mine[0].Against 'off curve'
    Assert-Equal $mine[0].Result 'PASS'
    Assert-Equal @($checks | Where-Object { $_.Run -eq 'h264_14M_quality' })[0].Result 'n/a'
    Assert-Equal @($checks | Where-Object { $_.Check -eq 'PeakCap' }).Count 0
}
Assert-Test 'quality: an INVALID quality run is reported INVALID whatever its numbers say' {
    $c = New-Curve
    $bad = New-QualityRow 15 99.0 99.0
    $bad.Invalid = $true; $bad.InvalidReasons = @('QualityVsSpeed reads back 33 after streaming started, not 100')
    $r = Test-BenchQuality $bad $c
    Assert-Equal $r.Result 'INVALID'
    Assert-Equal ($r.Detail -like '*reads back 33*') $true
}
Assert-Test 'quality: INVALID unless the read-back after streaming started is 100' {
    Assert-Equal @(Get-RunInvalidReasons -Row (New-QualityRow 15 90.0 80.0) -ReferencePackets $null).Count 0
    $r33 = @(Get-RunInvalidReasons -Row (New-QualityRow 15 90.0 80.0 16 33) -ReferencePackets $null)
    Assert-Equal $r33.Count 1
    Assert-Equal ($r33[0] -like '*reads back 33 after streaming started, not 100*') $true
    $none = @(Get-RunInvalidReasons -Row (New-QualityRow 15 90.0 80.0 16 $null) -ReferencePackets $null)
    Assert-Equal ($none[0] -like '*could not be read back*') $true
    $noInfo = @(Get-RunInvalidReasons -Row (New-QualityRow 15 90.0 80.0 16 $null $false) -ReferencePackets $null)
    Assert-Equal ($noInfo[0] -like '*no QualityVsSpeed read-back was recorded*') $true
    # an off run is not asked for a read-back (it has no HasQvs and is valid)
    Assert-Equal @(Get-RunInvalidReasons -Row (New-ValidRow 'off') -ReferencePackets $null).Count 0
    # and the rule reaches the row through Set-RunValidity
    $row = New-QualityRow 15 90.0 80.0 16 66
    Set-RunValidity -Row $row -ReferencePackets $null
    Assert-Equal $row.Invalid $true
}
Assert-Test 'quality: the run JSON''s qualityVsSpeed becomes the row''s read-back' {
    $q = [ordered]@{ requested = 100; stage = 'before-types'; readBack = 100; setResult = '0x00000000' }
    $row = ConvertTo-BenchRow -Json (New-Json -Mode 'quality' -Over @{ qualityVsSpeed = $q }) -Metrics $null
    Assert-Equal $row.Mode 'quality'
    Assert-Equal $row.HasQvs $true
    Assert-Near $row.QvsRequested 100
    Assert-Equal $row.QvsStage 'before-types'
    Assert-Near $row.QvsReadBack 100
    Assert-Equal $row.QvsText 'qvs 100 before-types, reads back 100'
    Assert-Equal @(Get-RunInvalidReasons -Row $row -ReferencePackets $null).Count 0
    $q33 = [ordered]@{ requested = 100; stage = 'before-types'; readBack = 33 }
    $row33 = ConvertTo-BenchRow -Json (New-Json -Mode 'quality' -Over @{ qualityVsSpeed = $q33 }) -Metrics $null
    Assert-Equal @(Get-RunInvalidReasons -Row $row33 -ReferencePackets $null).Count 1
    $unread = ConvertTo-BenchRow -Json (New-Json -Mode 'quality' -Over @{ qualityVsSpeed = ([ordered]@{ requested = 100; stage = 'before-types'; readBack = $null }) }) -Metrics $null
    Assert-Equal ($null -eq $unread.QvsReadBack) $true
    Assert-Equal $unread.HasQvs $true
    # an older run JSON has none
    Assert-Equal (ConvertTo-BenchRow -Json (New-Json -Mode 'off') -Metrics $null).HasQvs $false
}
Assert-Test 'quality: encode speed of quality and off side by side, with the ratio' {
    $rows = @(
        (New-Row 'off' 10 90.0 80.0 'h264' 12 $false @() 'g1'), (New-Row 'off' 20 96.0 88.0 'h264' 16 $false @() 'g1'),
        (New-QualityRow 11 91.0 81.0 12 100 $true 'g1' 150), (New-QualityRow 19 95.0 87.0 16 100 $true 'g1' 300),
        (New-QualityRow 14 95.0 87.0 14 100 $true 'g1' 200)      # no off run at 14
    )
    $rows[0] | Add-Member -NotePropertyName EncodeSpeedFps -NotePropertyValue 300.0
    $rows[1] | Add-Member -NotePropertyName EncodeSpeedFps -NotePropertyValue 400.0
    $p = @(Get-QualitySpeedPairs -Rows $rows)
    Assert-Equal $p.Count 2
    Assert-Near $p[0].RequestedMbps 12
    Assert-Near $p[0].OffFps 300
    Assert-Near $p[0].QualityFps 150
    Assert-Near $p[0].Ratio 0.5
    Assert-Near $p[1].Ratio 0.75
    $rows[0].EncodeSpeedFps = 0.0
    Assert-Equal ($null -eq @(Get-QualitySpeedPairs -Rows $rows)[0].Ratio) $true
}

Assert-Test 'the script resolves -OutFolder and -Reference with ProviderPath (a UNC path works on Windows PowerShell 5.1)' {
    $src = Get-Content -LiteralPath (Join-Path $toolsDir 'encoder-bench.ps1') -Raw
    Assert-Equal ($src -match '\$OutFolder = \(Resolve-Path -LiteralPath \$OutFolder\)\.ProviderPath') $true
    Assert-Equal ($src -match '\$Reference = \(Resolve-Path -LiteralPath \$Reference\)\.ProviderPath') $true
    Assert-Equal ($src -match 'Resolve-Path[^\r\n]*\)\.Path\b') $false
    $dir = (Resolve-Path -LiteralPath $PSScriptRoot).ProviderPath
    Assert-Equal ($dir -like 'Microsoft.PowerShell.Core*') $false
}
Assert-Test 'the speed table says the ratio is a bound when decode or the feeder sets the pace' {
    $src = Get-Content -LiteralPath (Join-Path $toolsDir 'encoder-bench.ps1') -Raw
    Assert-Equal ($src -match 'the ratio is a bound') $true
}
Write-Host "encoder-bench tests: $($script:Pass) passed, $($script:Fail) failed"
if ($script:Fail -gt 0) { exit 1 }
exit 0
