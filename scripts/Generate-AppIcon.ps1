# Render the existing WPF brand mark; no image service or extra packages required.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script with powershell -NoProfile -STA -File <script-path>.'
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$projectDirectory = Join-Path $PSScriptRoot '..\src\TCast.Desktop'
$assetDirectory = Join-Path $projectDirectory 'Assets'
$palettePath = Join-Path $projectDirectory 'Themes\Palette.xaml'
$reader = [System.Xml.XmlReader]::Create($palettePath)
try {
    $palette = [System.Windows.Markup.XamlReader]::Load($reader)
}
finally {
    $reader.Dispose()
}

# Match the navigation brand: blue rounded tile and the white Icon.Tv geometry.
$glyph = [System.Windows.Shapes.Path]::new()
$glyph.Data = $palette['Icon.Tv']
$glyph.Stroke = [System.Windows.Media.Brushes]::White
$glyph.StrokeThickness = 1.7
$glyph.StrokeStartLineCap = [System.Windows.Media.PenLineCap]::Round
$glyph.StrokeEndLineCap = [System.Windows.Media.PenLineCap]::Round
$glyph.StrokeLineJoin = [System.Windows.Media.PenLineJoin]::Round
$glyph.Stretch = [System.Windows.Media.Stretch]::Uniform
$glyph.Width = 22
$glyph.Height = 22
$glyph.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Center
$glyph.VerticalAlignment = [System.Windows.VerticalAlignment]::Center

$tile = [System.Windows.Controls.Border]::new()
$tile.Width = 40
$tile.Height = 40
$tile.CornerRadius = [System.Windows.CornerRadius]::new(12)
$tile.Background = $palette['App.Primary']
$tile.Child = $glyph

# A transparent margin keeps the icon clear of its bounding box in the shell.
$canvas = [System.Windows.Controls.Grid]::new()
$canvas.Width = 44
$canvas.Height = 44
$canvas.Children.Add($tile) | Out-Null
$viewbox = [System.Windows.Controls.Viewbox]::new()
$viewbox.Child = $canvas

function Get-IconPng {
    param([int] $Size)

    $viewbox.Width = $Size
    $viewbox.Height = $Size
    $viewbox.Measure([System.Windows.Size]::new($Size, $Size))
    $viewbox.Arrange([System.Windows.Rect]::new(0, 0, $Size, $Size))
    $viewbox.UpdateLayout()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($viewbox)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$pngPath = Join-Path $assetDirectory 'app-icon.png'
[System.IO.File]::WriteAllBytes($pngPath, (Get-IconPng -Size 512))

# PNG-backed ICO entries preserve full alpha at each Windows/DPI size.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @{}
foreach ($size in $sizes) {
    $frames[$size] = Get-IconPng -Size $size
}

$iconPath = Join-Path $assetDirectory 'app-icon.ico'
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16] 0) # Reserved.
    $writer.Write([uint16] 1) # Icon, not cursor.
    $writer.Write([uint16] $sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte] $dimension)
        $writer.Write([byte] $dimension)
        $writer.Write([byte] 0) # True color, no palette.
        $writer.Write([byte] 0) # Reserved.
        $writer.Write([uint16] 1)
        $writer.Write([uint16] 32)
        $writer.Write([uint32] $frames[$size].Length)
        $writer.Write([uint32] $offset)
        $offset += $frames[$size].Length
    }
    foreach ($size in $sizes) {
        $writer.Write([byte[]] $frames[$size])
    }
}
finally {
    $writer.Dispose()
}

Write-Output "Created $iconPath ($($sizes -join ', ') px)"
Write-Output "Created $pngPath (512 px preview)"
