param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\IPABridge\Assets'),
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$geometry = [ordered]@{
    BackgroundX = 8
    BackgroundY = 8
    BackgroundSize = 240
    BackgroundRadius = 57
    LeftPylonBottomX = 77
    LeftPylonBottomY = 184
    LeftPylonTopX = 116
    LeftPylonTopY = 102
    RightPylonTopX = 140
    RightPylonTopY = 102
    RightPylonBottomX = 179
    RightPylonBottomY = 184
    DeckStartX = 91
    DeckStartY = 164
    DeckControlOneX = 111
    DeckControlOneY = 151
    DeckControlTwoX = 145
    DeckControlTwoY = 151
    DeckEndX = 165
    DeckEndY = 164
    PylonWidth = 22
    DeckWidth = 16
}
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

function New-RoundedRectanglePath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$PixelSize)

    $bitmap = [System.Drawing.Bitmap]::new(
        $PixelSize,
        $PixelSize,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = [single]($PixelSize / 256.0)
        $graphics.ScaleTransform($scale, $scale)

        $backgroundPath = New-RoundedRectanglePath `
            -X $geometry.BackgroundX `
            -Y $geometry.BackgroundY `
            -Width $geometry.BackgroundSize `
            -Height $geometry.BackgroundSize `
            -Radius $geometry.BackgroundRadius
        try {
            $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.PointF]::new(18, 16),
                [System.Drawing.PointF]::new(240, 242),
                [System.Drawing.ColorTranslator]::FromHtml('#1A8CFF'),
                [System.Drawing.ColorTranslator]::FromHtml('#7459E8'))
            try {
                $colorBlend = [System.Drawing.Drawing2D.ColorBlend]::new()
                $colorBlend.Colors = [System.Drawing.Color[]]@(
                    [System.Drawing.ColorTranslator]::FromHtml('#1A8CFF'),
                    [System.Drawing.ColorTranslator]::FromHtml('#3E68F3'),
                    [System.Drawing.ColorTranslator]::FromHtml('#7459E8'))
                $colorBlend.Positions = [single[]]@(0.0, 0.54, 1.0)
                $gradient.InterpolationColors = $colorBlend
                $graphics.FillPath($gradient, $backgroundPath)
            }
            finally {
                $gradient.Dispose()
            }

            $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.PointF]::new(22, 14),
                [System.Drawing.PointF]::new(190, 198),
                [System.Drawing.Color]::FromArgb(68, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
            try {
                $graphics.FillPath($glow, $backgroundPath)
            }
            finally {
                $glow.Dispose()
            }

            $glassPlane = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $glassBrush = [System.Drawing.SolidBrush]::new(
                [System.Drawing.Color]::FromArgb(48, 234, 229, 255))
            try {
                $glassPlane.AddPolygon([System.Drawing.PointF[]]@(
                    [System.Drawing.PointF]::new(120, 248),
                    [System.Drawing.PointF]::new(248, 128),
                    [System.Drawing.PointF]::new(248, 248)))
                $graphics.SetClip($backgroundPath)
                $graphics.FillPath($glassBrush, $glassPlane)
                $graphics.ResetClip()
            }
            finally {
                $glassBrush.Dispose()
                $glassPlane.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $pylonPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $geometry.PylonWidth)
        $deckPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $geometry.DeckWidth)
        try {
            foreach ($pen in @($pylonPen, $deckPen)) {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            }

            $graphics.DrawLine(
                $pylonPen,
                $geometry.LeftPylonBottomX,
                $geometry.LeftPylonBottomY,
                $geometry.LeftPylonTopX,
                $geometry.LeftPylonTopY)
            $graphics.DrawLine(
                $pylonPen,
                $geometry.RightPylonTopX,
                $geometry.RightPylonTopY,
                $geometry.RightPylonBottomX,
                $geometry.RightPylonBottomY)
            $graphics.DrawBezier(
                $deckPen,
                $geometry.DeckStartX,
                $geometry.DeckStartY,
                $geometry.DeckControlOneX,
                $geometry.DeckControlOneY,
                $geometry.DeckControlTwoX,
                $geometry.DeckControlTwoY,
                $geometry.DeckEndX,
                $geometry.DeckEndY)
        }
        finally {
            $pylonPen.Dispose()
            $deckPen.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Convert-BitmapToPngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return [byte[]]$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MultiResolutionIcon {
    param([string]$Path)

    $frames = @()
    foreach ($size in $iconSizes) {
        $frameBitmap = New-IconBitmap -PixelSize $size
        try {
            $frames += [pscustomobject]@{
                Size = $size
                Bytes = [byte[]](Convert-BitmapToPngBytes -Bitmap $frameBitmap)
            }
        }
        finally {
            $frameBitmap.Dispose()
        }
    }

    $stream = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[void][System.IO.Directory]::CreateDirectory($resolvedOutput)
$generationDirectory = Join-Path $resolvedOutput ('.icon-generation-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($generationDirectory)

try {
    $generatedPngPath = Join-Path $generationDirectory 'IPA-Bridge.png'
    $generatedSvgPath = Join-Path $generationDirectory 'IPA-Bridge.svg'
    $generatedIconPath = Join-Path $generationDirectory 'IPA-Bridge.ico'

    $masterBitmap = New-IconBitmap -PixelSize 1024
    try {
        $masterBitmap.Save($generatedPngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $masterBitmap.Dispose()
    }

    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 256 256" role="img" aria-labelledby="title description">
  <title id="title">IPA Bridge icon</title>
  <desc id="description">An abstract white bridge gateway on a rounded blue and violet square.</desc>
  <defs>
    <linearGradient id="background" x1="18" y1="16" x2="240" y2="242" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#1A8CFF" />
      <stop offset="0.54" stop-color="#3E68F3" />
      <stop offset="1" stop-color="#7459E8" />
    </linearGradient>
    <linearGradient id="glow" x1="22" y1="14" x2="190" y2="198" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.27" />
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0" />
    </linearGradient>
    <clipPath id="rounded-square">
      <rect x="$($geometry.BackgroundX)" y="$($geometry.BackgroundY)" width="$($geometry.BackgroundSize)" height="$($geometry.BackgroundSize)" rx="$($geometry.BackgroundRadius)" />
    </clipPath>
  </defs>
  <rect x="$($geometry.BackgroundX)" y="$($geometry.BackgroundY)" width="$($geometry.BackgroundSize)" height="$($geometry.BackgroundSize)" rx="$($geometry.BackgroundRadius)" fill="url(#background)" />
  <rect x="$($geometry.BackgroundX)" y="$($geometry.BackgroundY)" width="$($geometry.BackgroundSize)" height="$($geometry.BackgroundSize)" rx="$($geometry.BackgroundRadius)" fill="url(#glow)" />
  <path d="M 120 248 L 248 128 L 248 248 Z" fill="#EAE5FF" fill-opacity="0.19" clip-path="url(#rounded-square)" />
  <path d="M $($geometry.LeftPylonBottomX) $($geometry.LeftPylonBottomY) L $($geometry.LeftPylonTopX) $($geometry.LeftPylonTopY) M $($geometry.RightPylonTopX) $($geometry.RightPylonTopY) L $($geometry.RightPylonBottomX) $($geometry.RightPylonBottomY)" fill="none" stroke="#FFFFFF" stroke-width="$($geometry.PylonWidth)" stroke-linecap="round" stroke-linejoin="round" />
  <path d="M $($geometry.DeckStartX) $($geometry.DeckStartY) C $($geometry.DeckControlOneX) $($geometry.DeckControlOneY), $($geometry.DeckControlTwoX) $($geometry.DeckControlTwoY), $($geometry.DeckEndX) $($geometry.DeckEndY)" fill="none" stroke="#FFFFFF" stroke-width="$($geometry.DeckWidth)" stroke-linecap="round" />
</svg>
"@
    $normalizedSvg = $svg.Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
    [System.IO.File]::WriteAllText(
        $generatedSvgPath,
        $normalizedSvg,
        [System.Text.UTF8Encoding]::new($false))

    Write-MultiResolutionIcon -Path $generatedIconPath

    $assetNames = @('IPA-Bridge.png', 'IPA-Bridge.svg', 'IPA-Bridge.ico')
    if ($Verify) {
        foreach ($assetName in $assetNames) {
            $checkedInPath = Join-Path $resolvedOutput $assetName
            $generatedPath = Join-Path $generationDirectory $assetName
            if (-not (Test-Path -LiteralPath $checkedInPath -PathType Leaf)) {
                throw "Generated brand asset is missing: $checkedInPath"
            }

            $checkedInHash = (Get-FileHash -LiteralPath $checkedInPath -Algorithm SHA256).Hash
            $generatedHash = (Get-FileHash -LiteralPath $generatedPath -Algorithm SHA256).Hash
            if ($checkedInHash -ne $generatedHash) {
                throw "Generated brand asset is out of date: $checkedInPath"
            }
        }

        Write-Output 'Verified generated IPA Bridge icon assets.'
    }
    else {
        foreach ($assetName in $assetNames) {
            Move-Item `
                -LiteralPath (Join-Path $generationDirectory $assetName) `
                -Destination (Join-Path $resolvedOutput $assetName) `
                -Force
        }

        Write-Output "Generated unified IPA Bridge icon assets in $resolvedOutput"
    }
}
finally {
    if (Test-Path -LiteralPath $generationDirectory) {
        Remove-Item -LiteralPath $generationDirectory -Recurse -Force
    }
}
