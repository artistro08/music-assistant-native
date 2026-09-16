# make-icons.ps1
#
# Outputs: the three .ico files the app loads at runtime, in MusicAssistant\Assets.
#   app.ico             the colored icon, for the window, the taskbar and the tray
#   app-mono-light.ico  white silhouette, for the tray on a dark taskbar
#   app-mono-dark.ico   black silhouette, for the tray on a light taskbar
#
# Each holds 16, 20, 24, 32, 40, 48, 64, 128 and 256px frames. The 20, 24 and 40px
# ones are what the notification area asks for at 125%, 150% and 250% scaling; without
# them the shell stretches the nearest frame and the tray icon looks blurry.
#
# Used for: the app icon and the "monochrome tray icon" setting.
#
# Sources: MusicAssistant\Assets\app-512.png for the colored icon, and
# music-assistant-fluent.svg in the repo root for the two monochrome ones. Both of the
# SVG's paths go into one figure with the even-odd fill rule, so the bars and the wave
# knock out of the house shape and the icon reads as a single silhouette in one flat color.
#
# Depends on: System.Drawing (ships with Windows PowerShell / .NET). The SVG only uses
# absolute M, L, C and Z commands, which is all the parser below handles.
#   https://learn.microsoft.com/windows/win32/menurc/icon-resource
#
# Usage: .\tools\make-icons.ps1

Add-Type -AssemblyName System.Drawing

$svgPath   = Join-Path $PSScriptRoot "..\music-assistant-fluent.svg"
$colorPath = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\app-512.png"
$dest      = Join-Path $PSScriptRoot "..\MusicAssistant\Assets"
$sizes     = 16, 20, 24, 32, 40, 48, 64, 128, 256

# The SVG's user space; every frame scales down from it
$viewBox = 256

# Turn one SVG path's d attribute into a GraphicsPath
function ConvertTo-GraphicsPath([string] $d) {
    $path  = New-Object System.Drawing.Drawing2D.GraphicsPath
    $point = New-Object System.Drawing.PointF 0, 0
    $start = $point

    foreach ($segment in [regex]::Matches($d, '([MLCZ])([^MLCZ]*)')) {
        $command = $segment.Groups[1].Value
        $numbers = @([regex]::Matches($segment.Groups[2].Value, '-?[0-9]*\.?[0-9]+') | ForEach-Object { [float] $_.Value })

        switch ($command) {
            # Move, then any further pairs are implicit lines
            'M' {
                $path.StartFigure()
                $point = New-Object System.Drawing.PointF $numbers[0], $numbers[1]
                $start = $point
                for ($i = 2; $i -lt $numbers.Count; $i += 2) {
                    $next = New-Object System.Drawing.PointF $numbers[$i], $numbers[$i + 1]
                    $path.AddLine($point, $next)
                    $point = $next
                }
            }
            'L' {
                for ($i = 0; $i -lt $numbers.Count; $i += 2) {
                    $next = New-Object System.Drawing.PointF $numbers[$i], $numbers[$i + 1]
                    $path.AddLine($point, $next)
                    $point = $next
                }
            }
            'C' {
                for ($i = 0; $i -lt $numbers.Count; $i += 6) {
                    $c1   = New-Object System.Drawing.PointF $numbers[$i],     $numbers[$i + 1]
                    $c2   = New-Object System.Drawing.PointF $numbers[$i + 2], $numbers[$i + 3]
                    $next = New-Object System.Drawing.PointF $numbers[$i + 4], $numbers[$i + 5]
                    $path.AddBezier($point, $c1, $c2, $next)
                    $point = $next
                }
            }
            'Z' {
                $path.CloseFigure()
                $point = $start
            }
        }
    }
    return $path
}

# Collect every path in the SVG into one even-odd figure, with its group's matrix applied
function New-SilhouettePath([string] $svg) {
    $silhouette = New-Object System.Drawing.Drawing2D.GraphicsPath
    $silhouette.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate

    foreach ($group in [regex]::Matches($svg, '(?s)<g transform="matrix\(([^)]+)\)">(.*?)</g>')) {
        $m      = @($group.Groups[1].Value -split ',' | ForEach-Object { [float] $_ })
        $matrix = New-Object System.Drawing.Drawing2D.Matrix $m[0], $m[1], $m[2], $m[3], $m[4], $m[5]

        foreach ($d in [regex]::Matches($group.Groups[2].Value, '\sd="([^"]+)"')) {
            $path = ConvertTo-GraphicsPath $d.Groups[1].Value
            $path.Transform($matrix)
            $silhouette.AddPath($path, $false)
            $path.Dispose()
        }
        $matrix.Dispose()
    }
    return $silhouette
}

# A transparent square canvas to draw one frame on
function New-Canvas([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g      = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    return @{ Bitmap = $bitmap; Graphics = $g }
}

# Fill the silhouette at one size in one flat color
function Get-SilhouetteFrame($silhouette, [System.Drawing.Color] $color, [int] $size) {
    $canvas = New-Canvas $size

    $frame = $silhouette.Clone()
    $scale = New-Object System.Drawing.Drawing2D.Matrix
    $scale.Scale($size / $viewBox, $size / $viewBox)
    $frame.Transform($scale)

    $brush = New-Object System.Drawing.SolidBrush $color
    $canvas.Graphics.FillPath($brush, $frame)

    $brush.Dispose(); $scale.Dispose(); $frame.Dispose(); $canvas.Graphics.Dispose()
    return $canvas.Bitmap
}

# Scale the colored render down to one size
function Get-ImageFrame($image, [int] $size) {
    $canvas = New-Canvas $size
    $canvas.Graphics.DrawImage($image, 0, 0, $size, $size)
    $canvas.Graphics.Dispose()
    return $canvas.Bitmap
}

# One icon frame as a bottom-up 32bpp DIB: header, BGRA pixels, then an empty AND mask.
# Every tool reads this form; a PNG frame is only safe from Vista on and GDI+ still refuses it.
function Get-FrameDib($bitmap) {
    $size   = $bitmap.Width
    $out    = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $out

    # BITMAPINFOHEADER; the height counts the color rows and the mask rows together
    $writer.Write([uint32] 40)
    $writer.Write([int32] $size)
    $writer.Write([int32] ($size * 2))
    $writer.Write([uint16] 1)             # color planes
    $writer.Write([uint16] 32)            # bits per pixel
    $writer.Write([uint32] 0)             # BI_RGB, no compression
    $writer.Write([uint32] ($size * $size * 4))
    0..3 | ForEach-Object { $writer.Write([uint32] 0) }

    # Pixels, bottom row first
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row  = New-Object byte[] ($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
        $writer.Write($row)
    }
    $bitmap.UnlockBits($data)

    # AND mask: all zeros, since the alpha channel already carries the transparency. Rows pad to 4 bytes.
    $maskRow = [Math]::Ceiling($size / 32) * 4
    $writer.Write((New-Object byte[] ($maskRow * $size)))

    $writer.Flush()
    $bytes = $out.ToArray()
    $writer.Dispose()

    # The comma keeps PowerShell from unrolling the array into single bytes on the way out
    return ,$bytes
}

# Pack the rendered frames into an .ico file and dispose them
function Save-Ico([string] $file, $bitmaps) {
    $frames = @($bitmaps | ForEach-Object {
        $frame = @{ Size = $_.Width; Data = Get-FrameDib $_ }
        $_.Dispose()
        $frame
    })

    $out    = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $out

    # ICONDIR: reserved, type 1 (icon), frame count
    $writer.Write([uint16] 0)
    $writer.Write([uint16] 1)
    $writer.Write([uint16] $frames.Count)

    # ICONDIRENTRY per frame; 256px is written as 0, and the data follows the directory
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte] $dimension)      # width
        $writer.Write([byte] $dimension)      # height
        $writer.Write([byte] 0)               # palette entries (none, it is 32bpp)
        $writer.Write([byte] 0)               # reserved
        $writer.Write([uint16] 1)             # color planes
        $writer.Write([uint16] 32)            # bits per pixel
        $writer.Write([uint32] $frame.Data.Length)
        $writer.Write([uint32] $offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) { $writer.Write($frame.Data) }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $dest $file), $out.ToArray())
    $writer.Dispose()
    "wrote $file at $($sizes -join ', ')px"
}

# Monochrome pair, from the SVG
$silhouette = New-SilhouettePath (Get-Content (Resolve-Path $svgPath) -Raw)
Save-Ico "app-mono-light.ico" @($sizes | ForEach-Object { Get-SilhouetteFrame $silhouette ([System.Drawing.Color]::White) $_ })
Save-Ico "app-mono-dark.ico"  @($sizes | ForEach-Object { Get-SilhouetteFrame $silhouette ([System.Drawing.Color]::Black) $_ })
$silhouette.Dispose()

# Colored icon, from the 512px render
$colored = [System.Drawing.Image]::FromFile((Resolve-Path $colorPath))
Save-Ico "app.ico" @($sizes | ForEach-Object { Get-ImageFrame $colored $_ })
$colored.Dispose()
