# make-msix-assets.ps1
#
# Outputs: the MSIX logo images in MusicAssistant\Assets\Package, at every display
# scale Windows asks for (scale-100/125/150/200/400) plus the fixed icon sizes
# Start, search, the taskbar and Explorer pick from (targetsize-16 ... 256, in plain,
# unplated and light-unplated forms). Without these Windows stretches a single 44px
# image on any display above 100% and the icon looks blurry.
#
# Used for: regenerating the package logos after the app icon changes. The manifest
# keeps referring to the plain names (Assets\Package\Square44x44Logo.png); the
# package's resource index maps them to the right qualified file.
#
# Source: MusicAssistant\Assets\app-512.png, the 512px frame of
# music-assistant-fluent.ico in the repo root.
#
# Depends on: System.Drawing (ships with Windows PowerShell / .NET)
#   https://learn.microsoft.com/windows/apps/design/style/iconography/app-icon-construction
#
# Usage: .\tools\make-msix-assets.ps1

Add-Type -AssemblyName System.Drawing
$src  = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\app-512.png"
$dest = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\Package"
New-Item -ItemType Directory -Force $dest | Out-Null
$image = [System.Drawing.Image]::FromFile((Resolve-Path $src))

# Draw the icon centered on a transparent canvas and save it
function Save-Logo([string] $name, [int] $width, [int] $height, [int] $size) {
    $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($image, [int](($width - $size) / 2), [int](($height - $size) / 2), $size, $size)
    $g.Dispose()
    $bmp.Save((Join-Path $dest $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Remove the previous set, so no unqualified file collides with a qualified one in the resource index
Get-ChildItem $dest -Filter *.png | Remove-Item

# Logos: base name, width and height at 100%, icon size at 100%, scales to generate.
# The large tile and splash stop at 200% so the 512px source is never enlarged.
$logos = @(
    @("Square44x44Logo",   44,  44,  44,  @(100, 125, 150, 200, 400)),
    @("Square71x71Logo",   71,  71,  56,  @(100, 125, 150, 200, 400)),
    @("Square150x150Logo", 150, 150, 100, @(100, 125, 150, 200, 400)),
    @("Wide310x150Logo",   310, 150, 100, @(100, 125, 150, 200, 400)),
    @("StoreLogo",         50,  50,  50,  @(100, 125, 150, 200, 400)),
    @("Square310x310Logo", 310, 310, 200, @(100, 125, 150, 200)),
    @("SplashScreen",      620, 300, 200, @(100, 125, 150, 200))
)

foreach ($logo in $logos) {
    $name, $w, $h, $size, $scales = $logo
    foreach ($scale in $scales) {
        $f = $scale / 100
        Save-Logo "$name.scale-$scale.png" ([int]($w * $f)) ([int]($h * $f)) ([int]($size * $f))
    }
    "wrote $name at $($scales -join ', ')%"
}

# Fixed icon sizes for the app list, search, taskbar and jump lists, full bleed
$targets = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256
foreach ($t in $targets) {
    Save-Logo "Square44x44Logo.targetsize-$t.png" $t $t $t
    Save-Logo "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $t
    Save-Logo "Square44x44Logo.targetsize-${t}_altform-lightunplated.png" $t $t $t
}
"wrote Square44x44Logo target sizes $($targets -join ', ')"

$image.Dispose()
