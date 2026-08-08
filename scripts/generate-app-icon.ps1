[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $repositoryRoot 'src\ArknightsPainter.App\Assets\app-icon.ico'
$previewPath = Join-Path $repositoryRoot 'docs\app-icon.png'
$fontFamily = [System.Drawing.FontFamily]::new('Segoe Fluent Icons')
$glyph = [string][char]0xE790
$foreground = [System.Drawing.ColorTranslator]::FromHtml('#003E92')

function New-GlyphBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $format = [System.Drawing.StringFormat]::GenericTypographic
    $emSize = [single]($Size * 0.82)
    $path.AddString(
        $glyph,
        $fontFamily,
        [int][System.Drawing.FontStyle]::Regular,
        $emSize,
        [System.Drawing.PointF]::new(0, 0),
        $format)
    $bounds = $path.GetBounds()
    $matrix = [System.Drawing.Drawing2D.Matrix]::new()
    $matrix.Translate(
        [single](($Size - $bounds.Width) / 2 - $bounds.X),
        [single](($Size - $bounds.Height) / 2 - $bounds.Y))
    $path.Transform($matrix)

    $brush = [System.Drawing.SolidBrush]::new($foreground)
    $graphics.FillPath($brush, $path)

    $brush.Dispose()
    $matrix.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return $bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-GlyphBitmap -Size $size
    try {
        [pscustomobject]@{ Size = $size; Bytes = ConvertTo-PngBytes -Bitmap $bitmap }
    } finally {
        $bitmap.Dispose()
    }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $iconPath) -Force | Out-Null
$fileStream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
} finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

New-Item -ItemType Directory -Path (Split-Path -Parent $previewPath) -Force | Out-Null
$preview = New-GlyphBitmap -Size 256
try {
    $preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $preview.Dispose()
    $fontFamily.Dispose()
}

Write-Host "Generated $iconPath"
Write-Host "Generated $previewPath"
