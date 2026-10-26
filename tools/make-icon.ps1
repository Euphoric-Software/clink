# Generates the Clink app icon from the brand SVGs (docs\brand\clink-icon*.svg) with WPF. Needs only Windows PowerShell 5.1:
# no ffmpeg, no SDK. Keep this file ASCII-only (PowerShell 5.1 reads BOM-less files as ANSI).
#
# Writes
#   Clink\Assets\clink.ico        16..256, every frame 32-bit BGRA with alpha; 256 is stored as PNG, the rest as uncompressed DIBs.
#                                 The sizes are the ones Windows asks for at 100 / 125 / 150 / 175 / 200 % display scaling, so the shell
#                                 has no reason to resample: tray and title bar 16 20 24 28 32, taskbar button 24 30 36 42 48,
#                                 large icons 32 40 48 56 64, Explorer medium icons 48 60 72 84 96; and 54 / 72 / 80 for the
#                                 taskbar and large icons at 225 / 250 %. 128 and 256 cover the big Explorer views.
#   docs\brand\clink-icon-512.png the same art as a transparent 512 px PNG
#
# For each size n it renders docs\brand\clink-icon-<n>.svg if that file exists (the small-size cuts for 16, 20, 24, 28, 30 and 32, spark
# filled solid), otherwise the master docs\brand\clink-icon.svg. Only a small SVG subset is read: <svg viewBox>, <path d fill [fill-rule]> and <circle cx cy r fill>,
# painted in document order; <title>, <desc>, <metadata> and comments are skipped; anything else is an error.
# Each size is drawn at n x Supersample and box-filtered down, which gives exact edge coverage (WPF's own 1x anti-aliasing is
# off by up to 77 levels on edge pixels). The output is byte-identical between runs.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
param(
    [string]$Brand = "$PSScriptRoot\..\docs\brand",
    [string]$Out = "$PSScriptRoot\..\Clink\Assets\clink.ico",
    [int[]]$Sizes = @(16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 54, 56, 60, 64, 72, 80, 84, 96, 128, 256),
    [int]$Supersample = 8,
    [string]$Png512 = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Png512) { $Png512 = Join-Path $Brand 'clink-icon-512.png' }
$Out = [IO.Path]::GetFullPath($Out); $Png512 = [IO.Path]::GetFullPath($Png512)   # tidy names in the output (no ..)
if ($Supersample -lt 1) { throw 'Supersample must be at least 1' }
Add-Type -AssemblyName PresentationCore, WindowsBase
Add-Type -TypeDefinition @'
public static class IconBox
{
    // Box-filter a premultiplied BGRA image of (w*f) x (w*f) down to w x w: exact area coverage, deterministic.
    public static byte[] Down(byte[] src, int w, int f)
    {
        int sw = w * f; var d = new byte[w * w * 4]; int n = f * f;
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int s = 0;
                    for (int j = 0; j < f; j++) { int row = ((y * f + j) * sw + x * f) * 4 + c; for (int i = 0; i < f; i++) s += src[row + i * 4]; }
                    d[(y * w + x) * 4 + c] = (byte)((s + n / 2) / n);
                }
        return d;
    }
}
'@
$inv = [Globalization.CultureInfo]::InvariantCulture
$M = [System.Windows.Media.PixelFormats]

function Num([string]$v) { [double]::Parse($v, $inv) }

# SVG -> straight-alpha Bgra32 bitmap of size x size
function Render([string]$svgPath, [int]$size) {
    $x = [xml][IO.File]::ReadAllText($svgPath)
    $vb = @($x.svg.viewBox -split '[ ,]+' | ForEach-Object { Num $_ })
    $big = $size * $Supersample
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $tg = New-Object System.Windows.Media.TransformGroup
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform (-$vb[0]), (-$vb[1])))
    $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform ($big / $vb[2]), ($big / $vb[3])))
    $dc.PushTransform($tg)
    foreach ($n in $x.svg.ChildNodes) {
        if ($n.NodeType -ne 'Element') { continue }                          # comments, whitespace
        if ($n.LocalName -in 'title', 'desc', 'metadata') { continue }
        $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($n.fill))
        switch ($n.LocalName) {
            'path' { $rule = if ($n.GetAttribute('fill-rule') -eq 'evenodd') { 'F0 ' } else { 'F1 ' }; $g = [System.Windows.Media.Geometry]::Parse($rule + $n.d) }
            'circle' { $g = New-Object System.Windows.Media.EllipseGeometry (New-Object System.Windows.Point (Num $n.cx), (Num $n.cy)), (Num $n.r), (Num $n.r) }
            default { throw "unsupported SVG element <$($n.LocalName)> in $svgPath" }
        }
        $dc.DrawGeometry($brush, $null, $g)
    }
    $dc.Pop(); $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $big, $big, 96, 96, $M::Pbgra32
    $rtb.Render($dv)
    $buf = New-Object byte[] ($big * $big * 4)
    $rtb.CopyPixels($buf, $big * 4, 0)
    $pre = [System.Windows.Media.Imaging.BitmapSource]::Create($size, $size, 96, 96, $M::Pbgra32, $null, [IconBox]::Down($buf, $size, $Supersample), $size * 4)
    # straight (non-premultiplied) alpha for the ICO and the PNG
    New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $pre, $M::Bgra32, $null, 0
}

function PngBytes($bmp) {
    $e = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $e.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream; $e.Save($ms); , $ms.ToArray()
}

# ICO image: BITMAPINFOHEADER (height = 2n), bottom-up BGRA XOR bitmap, 1-bpp AND mask (1 = transparent)
function DibBytes($bmp, [int]$s) {
    $px = New-Object byte[] ($s * $s * 4); $bmp.CopyPixels($px, $s * 4, 0)
    $maskStride = [int][Math]::Ceiling($s / 32) * 4
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]($s * $s * 4 + $maskStride * $s)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $s * 4, $s * 4) }
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $s; $x++) { if ($px[($y * $s + $x) * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) } }
        $w.Write($row)
    }
    $w.Flush(); , $ms.ToArray()
}

$master = Join-Path $Brand 'clink-icon.svg'
if (-not (Test-Path $master)) { throw "missing $master" }
function SourceFor([int]$s) {
    $hinted = Join-Path $Brand "clink-icon-$s.svg"
    if (Test-Path $hinted) { $hinted } else { $master }
}

$frames = @()
foreach ($s in $Sizes) {
    $src = SourceFor $s
    $bmp = Render $src $s
    $data = if ($s -ge 256) { PngBytes $bmp } else { DibBytes $bmp $s }
    $frames += , @{ Size = $s; Data = $data; Src = (Split-Path $src -Leaf); Kind = $(if ($s -ge 256) { 'PNG' } else { 'DIB' }) }
}

New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($Out))) | Out-Null
$fs = [IO.File]::Create($Out)
try {
    $bw = New-Object IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $d = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$f.Data.Length); $bw.Write([UInt32]$offset)
        $offset += $f.Data.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Data) }
    $bw.Flush()
}
finally { $fs.Close() }

[IO.File]::WriteAllBytes($Png512, (PngBytes (Render (SourceFor 512) 512)))

foreach ($f in $frames) { '{0,4} px  32 bpp  {1}  {2,-20} {3,7} bytes' -f $f.Size, $f.Kind, $f.Src, $f.Data.Length }
"wrote $Out ($((Get-Item $Out).Length) bytes) and $Png512 ($((Get-Item $Png512).Length) bytes)"
