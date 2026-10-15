# V1.5.3 E2E helper - generates a synthetic 1920x1080 screenshot containing a
# classic menu bar (File | View | Insert | Format | Tools | Window | Help), a
# red Close (X) button in the top-right, and NO Photoshop button.
# Also writes menu-positions.json with the pixel bounds of each drawn label so
# the E2E runner can assert the AI targets the right element.

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$width = 1920
$height = 1080

$bmp = New-Object System.Drawing.Bitmap($width, $height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

# Window chrome background
$g.Clear([System.Drawing.Color]::FromArgb(240, 240, 240))
$g.FillRectangle([System.Drawing.Brushes]::White, 0, 60, $width, $height - 60)

# Title bar
$titleFont = New-Object System.Drawing.Font('Segoe UI', 12)
$g.DrawString('Demo Application', $titleFont, [System.Drawing.Brushes]::Black, 20, 12)

# Menu bar: File | View | Insert | Format | Tools | Window | Help
# Drawn on a light strip so the menu row is visually distinct for the VLM.
$menuTop = 24
$menuFont = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Regular)
$g.FillRectangle([System.Drawing.Brushes]::Gainsboro, 0, $menuTop, $width, 44)
$menus = @('File', 'View', 'Insert', 'Format', 'Tools', 'Window', 'Help')
$x = 30
$positions = @{}
foreach ($menu in $menus) {
    $size = $g.MeasureString($menu, $menuFont)
    $g.DrawString($menu, $menuFont, [System.Drawing.Brushes]::Black, $x, $menuTop + 8)
    # Tolerance box around the text (grow by 8px each side)
    $positions[$menu] = @{
        x      = [int][Math]::Floor($x - 8)
        y      = [int]($menuTop + 8 - 8)
        width  = [int][Math]::Ceiling($size.Width + 16)
        height = [int][Math]::Ceiling($size.Height + 16)
    }
    $x += [int]($size.Width + 48)
}

# Close (X) button - red square top-right of the window
$closeX = $width - 80
$g.FillRectangle([System.Drawing.Brushes]::Firebrick, $closeX, 16, 48, 36)
$closeFont = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
$g.DrawString('Close', $closeFont, [System.Drawing.Brushes]::White, $closeX + 2, 20)
$positions['Close'] = @{ x = $closeX; y = 16; width = 48; height = 36 }

# Deliberately no Photoshop button anywhere in the image.

$g.Dispose()
$pngPath = Join-Path $outDir 'synthetic-app.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$json = $positions | ConvertTo-Json -Depth 3
$jsonPath = Join-Path $outDir 'menu-positions.json'
Set-Content -Path $jsonPath -Value $json -Encoding UTF8

Write-Output "PNG: $pngPath"
Write-Output "JSON: $jsonPath"
