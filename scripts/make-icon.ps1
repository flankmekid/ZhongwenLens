<#
.SYNOPSIS
    Builds a multi-resolution .ico from the source logo PNG.
.DESCRIPTION
    System.Drawing can only save a single-size icon, so the ICO container is assembled by
    hand. Each entry carries a PNG payload, which Windows Vista and later accept at every
    size, so no BMP/AND-mask handling is needed.

    Sizes cover every place Windows asks for one: 16/20/24/32 for the tray and title bar
    (16 and 32 are the ones actually rendered most), 40/48/64 for Explorer views, and
    128/256 for large icons and the Alt+Tab switcher.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\logo.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\ZhongwenLens.App\Assets\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$destinationDir = Split-Path $Destination -Parent
$null = New-Item -ItemType Directory -Force -Path $destinationDir
$Destination = Join-Path (Resolve-Path $destinationDir).Path (Split-Path $Destination -Leaf)

Write-Host "source      $Source" -ForegroundColor Cyan

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# Not $source: PowerShell variable names are case-insensitive, so that would overwrite the
# $Source path parameter with the Image object and break the path on any later use.
$sourceImage = [System.Drawing.Image]::FromFile($Source)

try {
    $images = @()

    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($sourceImage, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
        } finally {
            $graphics.Dispose()
        }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()

        $images += , @{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()

        Write-Host ("  {0,3}x{1,-3} {2,7:N0} bytes" -f $size, $size, $images[-1].Bytes.Length) -ForegroundColor DarkGray
    }

    $output = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $output
    try {
        # ICONDIR
        $writer.Write([uint16]0)                 # reserved
        $writer.Write([uint16]1)                 # type: 1 = icon
        $writer.Write([uint16]$images.Count)

        # Image data starts after the directory and all its entries.
        $offset = 6 + (16 * $images.Count)

        foreach ($image in $images) {
            # 256 is stored as 0 in the single-byte dimension fields.
            $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }

            $writer.Write([byte]$dimension)      # width
            $writer.Write([byte]$dimension)      # height
            $writer.Write([byte]0)               # palette size (0 = truecolour)
            $writer.Write([byte]0)               # reserved
            $writer.Write([uint16]1)             # colour planes
            $writer.Write([uint16]32)            # bits per pixel
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)

            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) { $writer.Write($image.Bytes) }

        $writer.Flush()
        [System.IO.File]::WriteAllBytes($Destination, $output.ToArray())
    } finally {
        $writer.Dispose()
        $output.Dispose()
    }
} finally {
    $sourceImage.Dispose()
}

Write-Host ""
Write-Host ("wrote {0} ({1:N0} bytes, {2} sizes)" -f $Destination, (Get-Item $Destination).Length, $sizes.Count) -ForegroundColor Green
