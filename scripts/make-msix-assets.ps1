<#
.SYNOPSIS
    Generates the MSIX visual assets from the source logo PNG.
.DESCRIPTION
    MSIX requires a specific set of named images at specific sizes; Windows picks between them
    by context and display scaling. Generating them keeps a single source of truth (the logo)
    rather than a folder of hand-exported files that quietly drift apart.

    Square/wide assets are drawn onto a transparent canvas with the logo centred, so the
    non-square tiles don't stretch it.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\logo.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\ZhongwenLens.App\Images')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$null = New-Item -ItemType Directory -Force -Path $Destination
$Destination = (Resolve-Path $Destination).Path

$sourceImage = [System.Drawing.Image]::FromFile($Source)

function Write-Asset {
    param([string]$Name, [int]$Width, [int]$Height, [double]$Inset = 1.0)

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Fit the logo inside the smaller dimension so wide tiles centre it rather than stretch.
        $size = [Math]::Round([Math]::Min($Width, $Height) * $Inset)
        $x = [Math]::Round(($Width - $size) / 2)
        $y = [Math]::Round(($Height - $size) / 2)

        $graphics.DrawImage($sourceImage, (New-Object System.Drawing.Rectangle $x, $y, $size, $size))
    } finally {
        $graphics.Dispose()
    }

    $path = Join-Path $Destination $Name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Host ("  {0,-52} {1}x{2}" -f $Name, $Width, $Height) -ForegroundColor DarkGray
}

Write-Host "Generating MSIX assets -> $Destination" -ForegroundColor White

try {
    # App list / taskbar icon, plus the scale variants Windows asks for on high-DPI displays.
    Write-Asset 'Square44x44Logo.png' 44 44
    Write-Asset 'Square44x44Logo.scale-200.png' 88 88
    Write-Asset 'Square44x44Logo.scale-400.png' 176 176

    # Unplated target sizes: used on the taskbar and in the Start list, without a background
    # plate behind them.
    foreach ($size in 16, 24, 32, 48, 256) {
        Write-Asset "Square44x44Logo.targetsize-$size`_altform-unplated.png" $size $size
    }

    # Start menu tiles.
    Write-Asset 'Square71x71Logo.png' 71 71
    Write-Asset 'Square150x150Logo.png' 150 150
    Write-Asset 'Square150x150Logo.scale-200.png' 300 300
    Write-Asset 'Square310x310Logo.png' 310 310

    # Wide tile and splash: logo centred on a transparent canvas, never stretched.
    Write-Asset 'Wide310x150Logo.png' 310 150 0.85
    Write-Asset 'SplashScreen.png' 620 300 0.7
    Write-Asset 'SplashScreen.scale-200.png' 1240 600 0.7

    # Shown in the Store listing and in Settings > Apps.
    Write-Asset 'StoreLogo.png' 50 50
    Write-Asset 'LockScreenLogo.png' 24 24
} finally {
    $sourceImage.Dispose()
}

$count = (Get-ChildItem $Destination -Filter '*.png').Count
Write-Host ""
Write-Host "wrote $count assets" -ForegroundColor Green
