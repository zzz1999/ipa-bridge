param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\IPABridge\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[void][System.IO.Directory]::CreateDirectory($resolvedOutput)
$pngPath = Join-Path $resolvedOutput 'IPA-Bridge.png'
$iconPath = Join-Path $resolvedOutput 'IPA-Bridge.ico'

$bitmap = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $backgroundPath.AddArc(8, 8, 114, 114, 180, 90)
        $backgroundPath.AddArc(134, 8, 114, 114, 270, 90)
        $backgroundPath.AddArc(134, 134, 114, 114, 0, 90)
        $backgroundPath.AddArc(8, 134, 114, 114, 90, 90)
        $backgroundPath.CloseFigure()

        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Point]::new(22, 20),
            [System.Drawing.Point]::new(234, 238),
            [System.Drawing.ColorTranslator]::FromHtml('#1A8CFF'),
            [System.Drawing.ColorTranslator]::FromHtml('#7459E8'))
        try {
            $graphics.FillPath($gradient, $backgroundPath)
        }
        finally {
            $gradient.Dispose()
        }

        $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Point]::new(20, 12),
            [System.Drawing.Point]::new(182, 190),
            [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        try {
            $graphics.FillPath($glow, $backgroundPath)
        }
        finally {
            $glow.Dispose()
        }
    }
    finally {
        $backgroundPath.Dispose()
    }

    $arch = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 20)
    $deck = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(222, 255, 255, 255), 14)
    try {
        $arch.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arch.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $deck.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $deck.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawBezier($arch, 75, 143, 94, 92, 160, 92, 181, 143)
        $graphics.DrawLine($deck, 75, 143, 181, 143)
    }
    finally {
        $arch.Dispose()
        $deck.Dispose()
    }

    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $leftInner = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#3A73F5'))
    $rightInner = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#665DEB'))
    try {
        $graphics.FillEllipse($white, 52, 120, 46, 46)
        $graphics.FillEllipse($white, 158, 120, 46, 46)
        $graphics.FillEllipse($leftInner, 68, 136, 14, 14)
        $graphics.FillEllipse($rightInner, 174, 136, 14, 14)
    }
    finally {
        $white.Dispose()
        $leftInner.Dispose()
        $rightInner.Dispose()
    }

    $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]1)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$pngBytes.Length)
    $writer.Write([uint32]22)
    $writer.Write($pngBytes)
}
finally {
    $writer.Dispose()
}

Write-Output "Generated $iconPath"
