# Generates Assets/app.ico and Assets/logo.png for Port Visualizer (central node + connected nodes).
param([string]$OutDir = "$PSScriptRoot\..\src\PortVisualizer.App\Assets")
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # rounded dark tile
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 52; $d = $r * 2
    $path.AddArc(8, 8, $d, $d, 180, 90); $path.AddArc(248 - $d, 8, $d, $d, 270, 90)
    $path.AddArc(248 - $d, 248 - $d, $d, $d, 0, 90); $path.AddArc(8, 248 - $d, $d, $d, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 256, 256), [System.Drawing.Color]::FromArgb(255, 30, 35, 42), [System.Drawing.Color]::FromArgb(255, 9, 11, 14))
    $g.FillPath($bg, $path)
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 0, 229, 160)), 3
    $g.DrawPath($border, $path)

    $cx = 128; $cy = 128; $R = 78
    $pts = @()
    for ($i = 0; $i -lt 5; $i++) {
        $a = (-90 + $i * 72) * [Math]::PI / 180
        $pts += , @(($cx + $R * [Math]::Cos($a)), ($cy + $R * [Math]::Sin($a)))
    }
    $cyan = [System.Drawing.Color]::FromArgb(255, 0, 212, 255)
    $green = [System.Drawing.Color]::FromArgb(255, 0, 229, 160)
    # ring links
    $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0, 212, 255)), 3
    for ($i = 0; $i -lt 5; $i++) { $p = $pts[$i]; $q = $pts[($i + 1) % 5]; $g.DrawLine($ringPen, [single]$p[0], [single]$p[1], [single]$q[0], [single]$q[1]) }
    # spokes: glow then line
    for ($i = 0; $i -lt 5; $i++) {
        $p = $pts[$i]
        $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 0, 229, 160)), 12
        $g.DrawLine($glow, $cx, $cy, [single]$p[0], [single]$p[1])
        $c = if ($i % 2 -eq 0) { $green } else { $cyan }
        $pen = New-Object System.Drawing.Pen $c, 5
        $g.DrawLine($pen, $cx, $cy, [single]$p[0], [single]$p[1])
    }
    # satellite nodes
    for ($i = 0; $i -lt 5; $i++) {
        $p = $pts[$i]
        $c = if ($i % 2 -eq 0) { $cyan } else { [System.Drawing.Color]::FromArgb(255, 59, 130, 246) }
        $b = New-Object System.Drawing.SolidBrush $c
        $g.FillEllipse($b, [single]($p[0] - 17), [single]($p[1] - 17), 34, 34)
        $inner = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 12, 14, 18))
        $g.FillEllipse($inner, [single]($p[0] - 7), [single]($p[1] - 7), 14, 14)
    }
    # central computer node
    $halo = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 0, 229, 160))
    $g.FillEllipse($halo, $cx - 46, $cy - 46, 92, 92)
    $core = New-Object System.Drawing.SolidBrush $green
    $g.FillEllipse($core, $cx - 30, $cy - 30, 60, 60)
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 9, 11, 14))
    $g.FillRectangle($dark, $cx - 15, $cy - 11, 30, 20)          # screen
    $g.FillRectangle($core, $cx - 12, $cy - 8, 24, 14)
    $g.FillRectangle($dark, $cx - 9, $cy + 11, 18, 4)            # stand
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($sz, $ms.ToArray())
    if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = $p[0]; $data = $p[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $out.ToArray())
Write-Host "Wrote icon to $OutDir"
