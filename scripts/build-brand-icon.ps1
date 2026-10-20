param([string]$Source = "$PSScriptRoot/../src/StudyHarness.Desktop/Assets/deep-study.png", [string]$Output = "$PSScriptRoot/../src/StudyHarness.Desktop/Assets/deep-study.ico")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$original = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($Source))
try {
    if ($original.GetPixel(0,0).A -ne 0) { throw 'Logo must have a transparent background' }
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $frames = @(foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($original,[Drawing.Rectangle]::new(0,0,$size,$size))
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            ,$stream.ToArray()
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    })
    $file = [IO.File]::Create([IO.Path]::GetFullPath($Output))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
    Write-Output "Transparent logo: $($original.Width)x$($original.Height); ICO sizes: $($sizes -join ', ')"
} finally { $original.Dispose() }
