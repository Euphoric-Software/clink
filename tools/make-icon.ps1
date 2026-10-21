# Generates Assets\clink.ico (multi-size, PNG-compressed entries) with System.Drawing.
param([string]$Out = "$PSScriptRoot\..\Clink\Assets\clink.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    # rounded dark square
    $r = [Math]::Max(2, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $path.AddArc($rect.X, $rect.Y, $r*2, $r*2, 180, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Y, $r*2, $r*2, 270, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Bottom - $r*2, $r*2, $r*2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r*2, $r*2, $r*2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x21, 0x27))
    $g.FillPath($bg, $path)
    # cyan ring with a gap (a "C" for Clink)
    $pw = [Math]::Max(1.5, $s * 0.11)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x4C, 0xC9, 0xF0)), $pw
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inset = $s * 0.24
    $g.DrawArc($pen, $inset, $inset, $s - 2*$inset, $s - 2*$inset, 35, 290)
    # white "click" dot
    $d = $s * 0.16
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($dot, $s/2 - $d/2, $s/2 - $d/2, $d, $d)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $s; Data = $ms.ToArray() }
    $bmp.Dispose()
}
$dir = Split-Path $Out
New-Item -ItemType Directory -Force $dir | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$sz); $bw.Write([byte]$sz); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$p.Data.Length); $bw.Write([UInt32]$offset)
    $offset += $p.Data.Length
}
foreach ($p in $pngs) { $bw.Write($p.Data) }
$bw.Flush(); $fs.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes)"
