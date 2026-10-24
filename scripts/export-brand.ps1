# Regenerate PNG and multi-resolution ICO from the same vector used by WPF.
# Run with Windows PowerShell (STA): powershell -STA -File scripts/export-brand.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assets = Join-Path $PSScriptRoot '..\GameHelper.Desktop\Assets'
$dictionary = [Windows.Markup.XamlReader]::Load([Xml.XmlReader]::Create((Join-Path $assets 'Brand.xaml')))
$drawing = $dictionary['BrandMark'].Drawing
function Render-Brand([int] $size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $scale = $size / 64
    $context.PushTransform((New-Object Windows.Media.ScaleTransform $scale, $scale))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $assets 'gamehelper-logo.png'), (Render-Brand 512))
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { ,(Render-Brand $_) })
$file = [IO.File]::Create((Join-Path $assets 'gamehelper.ico'))
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = [byte]($sizes[$i] % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
