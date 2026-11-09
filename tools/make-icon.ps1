# Regenerates src/Snip/snip.ico (PNG-compressed ICO entries).
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.2)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $m = [single]($s * 0.03); $w = [single]($s - 2 * $m); $d = 2 * $r
    $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(0, 170, 255)), ([System.Drawing.Color]::FromArgb(0, 100, 230))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.5, $s * 0.09))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $a = [single]($s * 0.24); $b = [single]($s * 0.76); $l = [single]($s * 0.2)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($a, $a + $l), [System.Drawing.PointF]::new($a, $a), [System.Drawing.PointF]::new($a + $l, $a)))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($b - $l, $b), [System.Drawing.PointF]::new($b, $b), [System.Drawing.PointF]::new($b, $b - $l)))
    $dot = [single]($s * 0.16)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($s * 0.5 - $dot / 2), [single]($s * 0.5 - $dot / 2), $dot, $dot)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\src\Snip\snip.ico'), $out.ToArray())
