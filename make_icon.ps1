# Рисует значок лаунчера (app.ico) во всех размерах: градиентный скруглённый квадрат и белый «▶».
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'), [string]$Preview = '')
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [Math]::Max(1, $s * 0.04)
    $box = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)),
        ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 45
    $g.FillPath($grad, $box)

    # мягкий блик сверху
    if ($s -ge 32) {
        $hl = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 255, 255, 255))
        $top = New-RoundRect $m $m ($s - 2 * $m) (($s - 2 * $m) * 0.5) ($s * 0.22)
        $g.FillPath($hl, $top)
    }

    $white = [System.Drawing.Brushes]::White
    $withText = $s -ge 48
    # треугольник «запуск»
    $cx = $s * ($(if ($withText) { 0.52 } else { 0.54 }))
    $cy = $s * ($(if ($withText) { 0.40 } else { 0.50 }))
    $t = $s * ($(if ($withText) { 0.36 } else { 0.50 }))
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx - $t * 0.42), ($cy - $t * 0.5)),
        (New-Object System.Drawing.PointF ($cx + $t * 0.5), $cy),
        (New-Object System.Drawing.PointF ($cx - $t * 0.42), ($cy + $t * 0.5)))
    $tri.AddPolygon($pts)
    $g.FillPath($white, $tri)
    if ($s -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($s * 0.05)
        $pen.LineJoin = 'Round'
        $g.DrawPath($pen, $tri)
    }

    if ($withText) {
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', ($s * 0.20), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = 'Center'
        $g.DrawString('1С', $font, $white, (New-Object System.Drawing.RectangleF 0, ($s * 0.64), $s, ($s * 0.3)), $sf)
    }
    $g.Dispose()
    return $bmp
}

#Мелкие размеры — классический BMP (32 бита + пустая маска), крупные — PNG: так читает любой код Windows.
function To-Dib($bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $row = $s * 4
    $px = New-Object byte[] ($row * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($row * $s + $maskRow * $s)); $w.Write([Int32]0); $w.Write([Int32]0)
    $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $row, $row) }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Icon $sz
    if ($Preview -and $sz -eq 256) { $b.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($sz -le 64) {
        $bytes = To-Dib $b
    } else {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    }
    $b.Dispose()
    , $bytes
}

# ICO-файл: заголовок, каталог размеров, затем сами картинки.
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"icon written: $Out"
