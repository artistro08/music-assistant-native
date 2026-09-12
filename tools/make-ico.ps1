# make-ico.ps1
#
# Builds Assets\app.ico (9 sizes, 16 to 256 px, PNG-compressed frames) and a
# 256 px preview PNG from a square PNG. Used to regenerate the app, window and
# tray icon after editing Assets\music-assistant-fluent.svg.
#
# Depends on System.Drawing only (ships with Windows PowerShell / .NET).
# To rasterize the SVG first, headless Edge works without any install:
#   msedge --headless=new --window-size=1024,1024 --default-background-color=00000000 --screenshot=icon.png page.html
# where page.html is a zero-margin page showing the SVG at 1024 x 1024.
#
# Usage: .\tools\make-ico.ps1 -Source .\Assets\app-1024.png -Out .\Assets\app.ico
param([string]$Source, [string]$Out)

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile($Source)

$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path (Split-Path $Out) "app-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    ,@{ Size = $s; Bytes = $ms.ToArray() }
}
$src.Dispose()

# ICO container: ICONDIR + ICONDIRENTRY[] + frame data
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush(); $fs.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes, $($frames.Count) sizes)"
