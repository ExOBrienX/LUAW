param([string]$OutDir)
# Logo de LUAW (ADR-083): ala en flecha y torre de control. Dibujado con System.Drawing (GDI+), sin recursos externos.
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Logo([int]$size) {
    $S = 1024.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / $S, $size / $S)

    # Fondo: cuadrado redondeado con degradado azul noche.
    $r = 220.0
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, $r, $r, 180, 90); $bg.AddArc($S - $r, 0, $r, $r, 270, 90)
    $bg.AddArc($S - $r, $S - $r, $r, $r, 0, 90); $bg.AddArc(0, $S - $r, $r, $r, 90, 90); $bg.CloseFigure()
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($S, $S)),
        ([System.Drawing.Color]::FromArgb(255, 14, 39, 64)), ([System.Drawing.Color]::FromArgb(255, 7, 18, 31))
    $g.FillPath($bgBrush, $bg)

    # Diseño 2 (ADR-083, basado en la propuesta de Marco): avión de frente con la torre detrás, arco azul y suelo.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 247, 252))
    $shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 196, 210, 226))
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 27, 46))
    $blueBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 150, 245))

    # Arco azul detrás (media elipse) con degradado.
    $arcPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 46, 150, 245)), 42
    $arcPen.StartCap = 'Round'; $arcPen.EndCap = 'Round'
    $g.DrawArc($arcPen, 150, 230, 724, 600, 192, 156)

    # Torre: fuste, cabina con tres ventanales, techo y antena.
    $g.FillPolygon($shade, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new(450, 640)), ([System.Drawing.PointF]::new(468, 395)),
        ([System.Drawing.PointF]::new(556, 395)), ([System.Drawing.PointF]::new(574, 640))))
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new(380, 255)), ([System.Drawing.PointF]::new(644, 255)),
        ([System.Drawing.PointF]::new(612, 380)), ([System.Drawing.PointF]::new(412, 380))))
    foreach ($x in 410, 482, 554) {
        $g.FillPolygon($dark, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new($x, 278)), ([System.Drawing.PointF]::new($x + 62, 278)),
            ([System.Drawing.PointF]::new($x + 58, 350)), ([System.Drawing.PointF]::new($x + 4, 350))))
    }
    $g.FillRectangle($white, 396, 376, 232, 34)
    $g.FillRectangle($white, 400, 226, 224, 32)
    $g.FillRectangle($white, 500, 150, 24, 80)

    # Deriva (detrás del fuselaje).
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new(498, 640)), ([System.Drawing.PointF]::new(512, 470)),
        ([System.Drawing.PointF]::new(526, 640))))

    # Alas rectas con leve diedro y winglets.
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new(512, 640)), ([System.Drawing.PointF]::new(150, 610)),
        ([System.Drawing.PointF]::new(130, 540)), ([System.Drawing.PointF]::new(160, 540)), ([System.Drawing.PointF]::new(176, 588)),
        ([System.Drawing.PointF]::new(512, 610)), ([System.Drawing.PointF]::new(848, 588)), ([System.Drawing.PointF]::new(864, 540)),
        ([System.Drawing.PointF]::new(894, 540)), ([System.Drawing.PointF]::new(874, 610))))

    # Motores bajo las alas.
    foreach ($cx in 330, 694) {
        $g.FillEllipse($white, $cx - 58, 600, 116, 116)
        $g.FillEllipse($dark, $cx - 40, 618, 80, 80)
        $g.FillEllipse($shade, $cx - 10, 648, 20, 20)
    }

    # Fuselaje de frente con el parabrisas.
    $g.FillEllipse($white, 412, 520, 200, 200)
    $g.FillEllipse($shade, 432, 600, 160, 112)
    $g.FillEllipse($white, 418, 522, 188, 170)
    $g.FillPolygon($dark, [System.Drawing.PointF[]]@(([System.Drawing.PointF]::new(452, 572)), ([System.Drawing.PointF]::new(572, 572)),
        ([System.Drawing.PointF]::new(562, 604)), ([System.Drawing.PointF]::new(462, 604))))
    $g.FillRectangle($white, 508, 570, 8, 36)

    # Suelo: línea azul bajo el avión.
    $groundPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 46, 150, 245)), 26
    $groundPen.StartCap = 'Round'; $groundPen.EndCap = 'Round'
    $g.DrawLine($groundPen, 170, 790, 854, 790)
    $g.Dispose()
    return $bmp
}

$sizes = 256, 128, 64, 48, 32, 16
$pngs = @{}
foreach ($s in $sizes) {
    $b = New-Logo $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    if ($s -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $OutDir 'simatc-256.png'), $pngs[$s]) }
    if ($s -eq 64) { [System.IO.File]::WriteAllBytes((Join-Path $OutDir 'simatc-64.png'), $pngs[$s]) }
    $b.Dispose()
}
(New-Logo 1024).Save((Join-Path $OutDir 'simatc-1024.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO con imágenes PNG (Windows Vista en adelante).
$fs = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$pngs[$s].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $w.Write($pngs[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'simatc.ico'), $fs.ToArray())
Write-Output "Logo listo en $OutDir"
