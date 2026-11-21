<#
.SYNOPSIS
    Generates crisp, original Microsoft Store visual assets (PNG tiles, icons,
    unplated taskbar targetsize icons, splash screen) and multi-size Tidejar.ico
    for Tidejar - Video & Stream Saver.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$storeAssetsDir = Join-Path $repoRoot "Packaging\Assets"
$appAssetsDir = Join-Path $repoRoot "YTDownloadTool\Assets"

New-Item -ItemType Directory -Force -Path $storeAssetsDir, $appAssetsDir | Out-Null

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [math]::Min($r * 2.0, [math]::Min($w, $h))
    if ($d -le 0.5) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($x, $y, $w, $h))
        return $path
    }
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-TidejarEmblem([System.Drawing.Graphics]$g, [float]$cx, [float]$cy, [float]$size, [bool]$drawPlate) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    if ($drawPlate) {
        $plateSize = $size * 0.92
        $px = $cx - $plateSize / 2.0
        $py = $cy - $plateSize / 2.0
        $platePath = New-RoundedRectPath $px $py $plateSize $plateSize ($plateSize * 0.24)
        $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            [System.Drawing.PointF]::new($px, $py),
            [System.Drawing.PointF]::new($px + $plateSize, $py + $plateSize),
            [System.Drawing.Color]::FromArgb(255, 22, 24, 46),
            [System.Drawing.Color]::FromArgb(255, 11, 13, 26))
        $g.FillPath($plateBrush, $platePath)

        # Subtle luminous outer border
        $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(95, 0, 210, 255), [math]::Max(1.0, $size * 0.02))
        $g.DrawPath($borderPen, $platePath)

        $borderPen.Dispose()
        $plateBrush.Dispose()
        $platePath.Dispose()
    }

    # Jar vessel dimensions (centered)
    $s = if ($drawPlate) { $size * 0.62 } else { $size * 0.84 }
    $jarW = $s * 0.72
    $jarH = $s * 0.72
    $jarX = $cx - $jarW / 2.0
    $jarY = $cy - $s * 0.22

    # Neck / Lid collar
    $neckW = $jarW * 0.62
    $neckH = $s * 0.14
    $neckX = $cx - $neckW / 2.0
    $neckY = $jarY - $neckH * 0.82

    # Soft cyan ambient glow behind vessel
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowR = $s * 0.62
    $glowPath.AddEllipse($cx - $glowR, $cy - $glowR * 0.88, $glowR * 2.0, $glowR * 1.9)
    $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(72, 0, 210, 255)
    $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 120, 215))
    $g.FillPath($glowBrush, $glowPath)
    $glowBrush.Dispose()
    $glowPath.Dispose()

    # Vessel body shape
    $bodyPath = New-RoundedRectPath $jarX $jarY $jarW $jarH ($jarW * 0.26)
    $bodyBg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new($jarX, $jarY),
        [System.Drawing.PointF]::new($jarX, $jarY + $jarH),
        [System.Drawing.Color]::FromArgb(55, 0, 210, 255),
        [System.Drawing.Color]::FromArgb(30, 12, 34, 70))
    $g.FillPath($bodyBg, $bodyPath)
    $bodyBg.Dispose()

    # Clipped luminous tidal wave stream inside the jar
    $savedState = $g.Save()
    $g.SetClip($bodyPath)

    $waveTop = $jarY + $jarH * 0.44
    $wavePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $wavePath.StartFigure()
    $wavePath.AddBezier(
        [System.Drawing.PointF]::new($jarX - 4, $waveTop + $jarH * 0.04),
        [System.Drawing.PointF]::new($jarX + $jarW * 0.28, $waveTop - $jarH * 0.18),
        [System.Drawing.PointF]::new($jarX + $jarW * 0.62, $waveTop + $jarH * 0.20),
        [System.Drawing.PointF]::new($jarX + $jarW + 4, $waveTop - $jarH * 0.04))
    $wavePath.AddLine($jarX + $jarW + 4, $jarY + $jarH + 4, $jarX - 4, $jarY + $jarH + 4)
    $wavePath.CloseFigure()

    $waveBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new($jarX, $waveTop - $jarH * 0.15),
        [System.Drawing.PointF]::new($jarX + $jarW, $jarY + $jarH),
        [System.Drawing.Color]::FromArgb(245, 0, 229, 255),
        [System.Drawing.Color]::FromArgb(255, 0, 114, 214))
    $g.FillPath($waveBrush, $wavePath)
    $waveBrush.Dispose()
    $wavePath.Dispose()

    # Secondary wave crest highlight
    $crestPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $crestY = $jarY + $jarH * 0.62
    $crestPath.AddBezier(
        [System.Drawing.PointF]::new($jarX - 4, $crestY + $jarH * 0.08),
        [System.Drawing.PointF]::new($jarX + $jarW * 0.35, $crestY + $jarH * 0.16),
        [System.Drawing.PointF]::new($jarX + $jarW * 0.68, $crestY - $jarH * 0.14),
        [System.Drawing.PointF]::new($jarX + $jarW + 4, $crestY + $jarH * 0.02))
    $crestPath.AddLine($jarX + $jarW + 4, $jarY + $jarH + 4, $jarX - 4, $jarY + $jarH + 4)
    $crestPath.CloseFigure()

    $crestBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new($jarX, $crestY),
        [System.Drawing.PointF]::new($jarX, $jarY + $jarH),
        [System.Drawing.Color]::FromArgb(145, 130, 245, 255),
        [System.Drawing.Color]::FromArgb(180, 0, 92, 185))
    $g.FillPath($crestBrush, $crestPath)
    $crestBrush.Dispose()
    $crestPath.Dispose()

    $g.Restore($savedState)

    # Glass jar outline stroke
    $strokeW = [math]::Max(1.5, $s * 0.075)
    $rimBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.PointF]::new($jarX, $neckY),
        [System.Drawing.PointF]::new($jarX + $jarW, $jarY + $jarH),
        [System.Drawing.Color]::FromArgb(255, 165, 244, 255),
        [System.Drawing.Color]::FromArgb(255, 0, 186, 255))
    $rimPen = New-Object System.Drawing.Pen($rimBrush, $strokeW)
    $rimPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($rimPen, $bodyPath)

    # Jar neck / rim collar
    $neckPath = New-RoundedRectPath $neckX $neckY $neckW $neckH ($neckH * 0.45)
    $neckFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 28, 34, 62))
    $g.FillPath($neckFill, $neckPath)
    $g.DrawPath($rimPen, $neckPath)
    $neckFill.Dispose()
    $neckPath.Dispose()

    # Subtle vertical download/capture stream arrow entering the jar neck
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [math]::Max(1.4, $s * 0.07))
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $arrowTop = $neckY - $s * 0.14
    $arrowMid = $jarY + $jarH * 0.27
    $g.DrawLine($arrowPen, $cx, $arrowTop, $cx, $arrowMid)
    $wing = $s * 0.115
    $g.DrawLines($arrowPen, @(
        [System.Drawing.PointF]::new($cx - $wing, $arrowMid - $wing * 0.92),
        [System.Drawing.PointF]::new($cx, $arrowMid),
        [System.Drawing.PointF]::new($cx + $wing, $arrowMid - $wing * 0.92)
    ))
    $arrowPen.Dispose()

    # Glass vertical specular highlight
    if ($size -ge 32) {
        $specPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 255, 255, 255), [math]::Max(1.0, $s * 0.04))
        $specPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $specPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $sx = $jarX + $jarW * 0.18
        $g.DrawLine($specPen, $sx, $jarY + $jarH * 0.22, $sx, $jarY + $jarH * 0.72)
        $specPen.Dispose()
    }

    $rimPen.Dispose()
    $rimBrush.Dispose()
    $bodyPath.Dispose()
}

function Save-SquareAsset([string]$path, [int]$size, [bool]$transparentBg, [bool]$drawPlate) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        if ($transparentBg) {
            $g.Clear([System.Drawing.Color]::Transparent)
        } else {
            $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                [System.Drawing.Point]::new(0, 0),
                [System.Drawing.Point]::new($size, $size),
                [System.Drawing.Color]::FromArgb(255, 13, 13, 21),
                [System.Drawing.Color]::FromArgb(255, 25, 25, 48))
            $g.FillRectangle($bgBrush, 0, 0, $size, $size)
            $bgBrush.Dispose()
        }

        Draw-TidejarEmblem $g ($size / 2.0) ($size / 2.0) ([float]$size) $drawPlate
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

function Save-WideAsset([string]$path, [int]$width, [int]$height, [bool]$isSplash) {
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            [System.Drawing.Point]::new(0, 0),
            [System.Drawing.Point]::new($width, $height),
            [System.Drawing.Color]::FromArgb(255, 13, 13, 21),
            [System.Drawing.Color]::FromArgb(255, 25, 25, 48))
        $g.FillRectangle($bgBrush, 0, 0, $width, $height)
        $bgBrush.Dispose()

        $titleFontSize = if ($isSplash) { [float]($height * 0.14) } else { [float]($height * 0.21) }
        $subFontSize = if ($isSplash) { [float]($height * 0.055) } else { [float]($height * 0.088) }
        $titleFont = New-Object System.Drawing.Font("Segoe UI", $titleFontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $subFont = New-Object System.Drawing.Font("Segoe UI", $subFontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $cyanBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 210, 255))

        if ($isSplash) {
            $iconSize = [float]($height * 0.42)
            Draw-TidejarEmblem $g ($width / 2.0) ($height * 0.36) $iconSize $true

            $sf = New-Object System.Drawing.StringFormat
            $sf.Alignment = [System.Drawing.StringAlignment]::Center
            $g.DrawString("TIDEJAR", $titleFont, $whiteBrush, [System.Drawing.RectangleF]::new(0, $height * 0.60, $width, $height * 0.20), $sf)
            $g.DrawString("Video & Stream Saver", $subFont, $cyanBrush, [System.Drawing.RectangleF]::new(0, $height * 0.77, $width, $height * 0.12), $sf)
            $sf.Dispose()
        } else {
            $iconSize = [float]($height * 0.64)
            $iconCx = $width * 0.22
            Draw-TidejarEmblem $g $iconCx ($height / 2.0) $iconSize $true

            $textX = $width * 0.39
            $g.DrawString("TIDEJAR", $titleFont, $whiteBrush, $textX, $height * 0.31)
            $g.DrawString("Video & Stream Saver", $subFont, $cyanBrush, $textX + 1, $height * 0.56)
        }

        $titleFont.Dispose()
        $subFont.Dispose()
        $whiteBrush.Dispose()
        $cyanBrush.Dispose()

        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

function Save-MultiSizeIco([string]$icoPath, [int[]]$sizes) {
    $pngBlobs = New-Object System.Collections.Generic.List[byte[]]
    foreach ($sz in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            Draw-TidejarEmblem $g ($sz / 2.0) ($sz / 2.0) ([float]$sz) $true
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngBlobs.Add($ms.ToArray())
            $ms.Dispose()
        }
        finally {
            $g.Dispose()
            $bmp.Dispose()
        }
    }

    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR header
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$sizes.Count)

        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $sz = $sizes[$i]
            $blob = $pngBlobs[$i]
            $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $bw.Write($dimByte)            # Width
            $bw.Write($dimByte)            # Height
            $bw.Write([byte]0)             # Color count
            $bw.Write([byte]0)             # Reserved
            $bw.Write([uint16]1)           # Planes
            $bw.Write([uint16]32)          # BitCount
            $bw.Write([uint32]$blob.Length)
            $bw.Write([uint32]$offset)
            $offset += $blob.Length
        }

        foreach ($blob in $pngBlobs) {
            $bw.Write($blob)
        }
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}

Write-Host "Generating original Tidejar Microsoft Store visual assets..." -ForegroundColor Cyan

# Store Logo (50x50 + scale-200 100x100)
Save-SquareAsset (Join-Path $storeAssetsDir "StoreLogo.png") 50 $true $true
Save-SquareAsset (Join-Path $storeAssetsDir "StoreLogo.scale-200.png") 100 $true $true

# Square44x44Logo (app list & taskbar — plated + unplated targetsize variants)
Save-SquareAsset (Join-Path $storeAssetsDir "Square44x44Logo.png") 44 $true $true
Save-SquareAsset (Join-Path $storeAssetsDir "Square44x44Logo.scale-200.png") 88 $true $true
foreach ($ts in @(16, 24, 32, 48, 256)) {
    Save-SquareAsset (Join-Path $storeAssetsDir "Square44x44Logo.targetsize-$ts.png") $ts $true $true
    Save-SquareAsset (Join-Path $storeAssetsDir "Square44x44Logo.targetsize-$($ts)_altform-unplated.png") $ts $true $true
}

# Start Menu Tiles (Small 71, Medium 150, Wide 310x150, Large 310)
Save-SquareAsset (Join-Path $storeAssetsDir "SmallTile.png") 71 $false $true
Save-SquareAsset (Join-Path $storeAssetsDir "SmallTile.scale-200.png") 142 $false $true
Save-SquareAsset (Join-Path $storeAssetsDir "Square150x150Logo.png") 150 $false $true
Save-SquareAsset (Join-Path $storeAssetsDir "Square150x150Logo.scale-200.png") 300 $false $true
Save-WideAsset (Join-Path $storeAssetsDir "Wide310x150Logo.png") 310 150 $false
Save-WideAsset (Join-Path $storeAssetsDir "Wide310x150Logo.scale-200.png") 620 300 $false
Save-SquareAsset (Join-Path $storeAssetsDir "LargeTile.png") 310 $false $true

# Splash Screen (620x300)
Save-WideAsset (Join-Path $storeAssetsDir "SplashScreen.png") 620 300 $true

# Multi-resolution Windows .ico for WPF exe / taskbar / installer
Save-MultiSizeIco (Join-Path $appAssetsDir "Tidejar.ico") @(16, 24, 32, 48, 256)

Write-Host "Generated $((Get-ChildItem $storeAssetsDir -File).Count) Store PNG assets + Tidejar.ico!" -ForegroundColor Green
