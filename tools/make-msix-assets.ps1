# make-msix-assets.ps1
#
# Generates the PNG logos the MSIX manifest needs (Square44x44, Square150x150,
# Wide310x150, StoreLogo, SplashScreen) from the 1024 px app icon render.
# Uses System.Drawing only.
#
# Usage: .\tools\make-msix-assets.ps1

Add-Type -AssemblyName System.Drawing
$src  = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\app-1024.png"
$dest = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\Package"
New-Item -ItemType Directory -Force $dest | Out-Null
$image = [System.Drawing.Image]::FromFile((Resolve-Path $src))

# name, canvas width, canvas height, icon size (icon centered on a transparent canvas)
$specs = @(
    @("Square44x44Logo.png",   44,  44,  44),
    @("Square44x44Logo.targetsize-44_altform-unplated.png", 44, 44, 44),
    @("Square71x71Logo.png",   71,  71,  56),
    @("Square150x150Logo.png", 150, 150, 100),
    @("Square310x310Logo.png", 310, 310, 200),
    @("Wide310x150Logo.png",   310, 150, 100),
    @("StoreLogo.png",         50,  50,  50),
    @("SplashScreen.png",      620, 300, 200)
)

foreach ($s in $specs) {
    $name, $w, $h, $size = $s
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($image, [int](($w - $size) / 2), [int](($h - $size) / 2), $size, $size)
    $g.Dispose()
    $bmp.Save((Join-Path $dest $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "wrote $name"
}
$image.Dispose()
