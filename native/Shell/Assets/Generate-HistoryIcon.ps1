# Optional asset regeneration only; normal builds consume the checked-in ICO.
# The geometry is Microsoft's MIT-licensed Fluent System Icons History 20 Regular.
Add-Type -AssemblyName PresentationCore,WindowsBase
$svg = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'history.svg') -Raw)
$geometry = [System.Windows.Media.Geometry]::Parse($svg.svg.path.d)
$images = @()
foreach ($size in @(16,20,24,32,48,64)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform([System.Windows.Media.ScaleTransform]::new($size/20.0,$size/20.0))
    # Neutral gray keeps the outline legible on both light and dark Shell backgrounds.
    $brush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(118,118,118))
    $context.DrawGeometry($brush,$null,$geometry); $context.Pop(); $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream; $encoder.Save($stream)
    $images += @{size=$size;bytes=$stream.ToArray()}; $stream.Dispose()
}
$file = [IO.File]::Create((Join-Path $PSScriptRoot 'history.ico')); $writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count);$offset=6+16*$images.Count
    foreach ($image in $images) {
        $writer.Write([byte]$image.size);$writer.Write([byte]$image.size);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$image.bytes.Length);$writer.Write([uint32]$offset);$offset+=$image.bytes.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image.bytes) }
} finally { $writer.Dispose() }
