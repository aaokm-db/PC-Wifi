Add-Type -AssemblyName System.Drawing

function New-HotspotBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [float]($size * 0.06)
    $rectSize = [float]($size - ($pad * 2))
    $radius = [float]($size * 0.24)

    # Rounded square path (Windows 11 squircle style)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $rectSize - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $rectSize - $d, $pad + $rectSize - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $rectSize - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Windows 11 Cyan-Blue Gradient fill
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, $rectSize, $rectSize)
    $c1 = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)  # #38BDF8
    $c2 = [System.Drawing.Color]::FromArgb(255, 0, 103, 192)   # #0067C0
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45.0)
    $g.FillPath($brush, $path)

    # Subtle inner border
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [Math]::Max(1.0, $size * 0.02))
    $g.DrawPath($borderPen, $path)

    # Draw Wi-Fi / Hotspot concentric arcs centered slightly below middle
    $cx = [float]($size * 0.5)
    $cy = [float]($size * 0.58)
    $stroke = [Math]::Max(1.6, [float]($size * 0.075))
    $whitePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $stroke)
    $whitePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $whitePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Outer arc
    $r1 = [float]($size * 0.30)
    $g.DrawArc($whitePen, $cx - $r1, $cy - $r1, $r1 * 2, $r1 * 2, 200, 140)

    # Inner arc
    $r2 = [float]($size * 0.18)
    $g.DrawArc($whitePen, $cx - $r2, $cy - $r2, $r2 * 2, $r2 * 2, 200, 140)

    # Center glowing dot
    $dotR = [Math]::Max(1.8, [float]($size * 0.065))
    $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($whiteBrush, $cx - $dotR, $cy - $dotR, $dotR * 2, $dotR * 2)

    # Small green "Always-On" shield dot in bottom-right corner
    $badgeR = [Math]::Max(2.2, [float]($size * 0.12))
    $bx = [float]($size * 0.76)
    $by = [float]($size * 0.76)
    $bgBadgeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 24, 24))
    $g.FillEllipse($bgBadgeBrush, $bx - $badgeR * 1.25, $by - $badgeR * 1.25, $badgeR * 2.5, $badgeR * 2.5)
    $greenBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 108, 203, 95)) # Windows 11 #6CCB5F
    $g.FillEllipse($greenBrush, $bx - $badgeR, $by - $badgeR, $badgeR * 2, $badgeR * 2)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 32, 48, 64, 256)
$pngStreams = @()
foreach ($s in $sizes) {
    $bmp = New-HotspotBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$icoPath = Join-Path $PSScriptRoot "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR header
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $pngStreams[$i]
    $wByte = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $hByte = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write($wByte)
    $bw.Write($hByte)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngStreams[$i])
}

$bw.Flush()
$bw.Close()
$fs.Close()
Write-Output "Generated $icoPath ($((Get-Item $icoPath).Length) bytes)"
