param([string]$SourceImage = (Join-Path $PSScriptRoot '../assets/branding/holeinone.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $SourceImage).Path)

function Get-ResizedPng([int]$Width, [int]$Height) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $stream = [IO.MemoryStream]::new()
    $attributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $scale = [Math]::Min($Width / $source.Width, $Height / $source.Height)
        $w = [int][Math]::Round($source.Width * $scale)
        $h = [int][Math]::Round($source.Height * $scale)
        $rect = [Drawing.Rectangle]::new([int](($Width - $w) / 2), [int](($Height - $h) / 2), $w, $h)
        $graphics.DrawImage($source, $rect, 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $attributes.Dispose(); $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

try {
    # Package alpha-preserving PNG frames in a Windows ICO directory.
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = @($sizes | ForEach-Object { ,(Get-ResizedPng $_ $_) })
    $iconPath = Join-Path $root 'assets/branding/holeinone.ico'
    $file = [IO.File]::Create($iconPath)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $root 'assets/branding/preview.png'), (Get-ResizedPng 256 256))

    # Existing filenames are part of the built-in theme contract.
    foreach ($mode in @('Desktop', 'Fullscreen')) {
        $images = Join-Path $root "source/Playnite.${mode}App/Themes/$mode/Default/Images"
        foreach ($target in Get-ChildItem -LiteralPath $images -Filter 'applogo*.ico') {
            Copy-Item -LiteralPath $iconPath -Destination $target.FullName -Force
        }
        foreach ($target in Get-ChildItem -LiteralPath $images -Filter 'applogo*.png') {
            $old = [Drawing.Bitmap]::new($target.FullName)
            $width = $old.Width; $height = $old.Height; $old.Dispose()
            [IO.File]::WriteAllBytes($target.FullName, (Get-ResizedPng $width $height))
        }
        [IO.File]::WriteAllBytes((Join-Path $root "source/Playnite.${mode}App/SplashScreen.png"), (Get-ResizedPng 320 320))
    }
    foreach ($target in @('source/Playnite.BrowserProcess/app.ico', 'source/Playnite/Resources/playnite_extension.ico')) {
        Copy-Item -LiteralPath $iconPath -Destination (Join-Path $root $target) -Force
    }
    Write-Host 'HoleInOne branding applied: 9 ICO sizes, theme images, tray icons, splash screens, browser and extension icons.'
} finally { $source.Dispose() }
