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

function Read-BigEndianUInt32 {
    param(
        [byte[]]$Bytes,
        [int]$Offset
    )

    return [uint32]((
        ([uint64]$Bytes[$Offset] -shl 24) -bor
        ([uint64]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint64]$Bytes[$Offset + 2] -shl 8) -bor
        [uint64]$Bytes[$Offset + 3]))
}

function Assert-PngHeader {
    param(
        [byte[]]$Bytes,
        [int]$ExpectedWidth,
        [int]$ExpectedHeight,
        [string]$Description
    )

    $pngSignature = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    if ($Bytes.Length -lt 33) {
        throw "$Description is too short to be a PNG image."
    }

    for ($index = 0; $index -lt $pngSignature.Length; $index++) {
        if ($Bytes[$index] -ne $pngSignature[$index]) {
            throw "$Description has an invalid PNG signature."
        }
    }

    $imageHeaderLength = Read-BigEndianUInt32 -Bytes $Bytes -Offset 8
    $imageHeaderType = [System.Text.Encoding]::ASCII.GetString($Bytes, 12, 4)
    $width = Read-BigEndianUInt32 -Bytes $Bytes -Offset 16
    $height = Read-BigEndianUInt32 -Bytes $Bytes -Offset 20
    if ($imageHeaderLength -ne 13 -or
        $imageHeaderType -ne 'IHDR' -or
        $width -ne $ExpectedWidth -or
        $height -ne $ExpectedHeight -or
        $Bytes[24] -ne 8 -or
        $Bytes[25] -ne 6 -or
        $Bytes[26] -ne 0 -or
        $Bytes[27] -ne 0 -or
        $Bytes[28] -ne 0) {
        throw "$Description does not contain the expected $($ExpectedWidth)x$ExpectedHeight 32-bit RGBA image."
    }
}

function Get-BitmapPixelBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $rectangle = [System.Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $bitmapData = $Bitmap.LockBits(
        $rectangle,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $rowLength = $Bitmap.Width * 4
        $pixelBytes = [byte[]]::new($rowLength * $Bitmap.Height)
        for ($row = 0; $row -lt $Bitmap.Height; $row++) {
            $sourceAddress = [IntPtr]::Add($bitmapData.Scan0, $row * $bitmapData.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy(
                $sourceAddress,
                $pixelBytes,
                $row * $rowLength,
                $rowLength)
        }
        return $pixelBytes
    }
    finally {
        $Bitmap.UnlockBits($bitmapData)
    }
}

function Compare-PngPixels {
    param(
        [byte[]]$CheckedInBytes,
        [byte[]]$GeneratedBytes,
        [string]$Description,
        [double]$AllowedMeanDifference,
        [double]$AllowedOutlierRatio
    )

    $checkedInStream = [System.IO.MemoryStream]::new()
    $generatedStream = [System.IO.MemoryStream]::new()
    $checkedInBitmap = $null
    $generatedBitmap = $null
    try {
        $checkedInStream.Write($CheckedInBytes, 0, $CheckedInBytes.Length)
        $checkedInStream.Position = 0
        $generatedStream.Write($GeneratedBytes, 0, $GeneratedBytes.Length)
        $generatedStream.Position = 0
        $checkedInBitmap = [System.Drawing.Bitmap]::new($checkedInStream)
        $generatedBitmap = [System.Drawing.Bitmap]::new($generatedStream)
        if ($checkedInBitmap.Width -ne $generatedBitmap.Width -or
            $checkedInBitmap.Height -ne $generatedBitmap.Height) {
            throw "$Description has different checked-in and generated dimensions."
        }

        $checkedInPixels = [byte[]](Get-BitmapPixelBytes -Bitmap $checkedInBitmap)
        $generatedPixels = [byte[]](Get-BitmapPixelBytes -Bitmap $generatedBitmap)
        if ($checkedInPixels.Length -ne $generatedPixels.Length) {
            throw "$Description has incompatible decoded pixel buffers."
        }

        $totalDifference = [long]0
        $comparedChannels = [long]0
        $activePixels = [long]0
        $outlierPixels = [long]0
        $maximumDifference = 0
        for ($pixelOffset = 0; $pixelOffset -lt $checkedInPixels.Length; $pixelOffset += 4) {
            $checkedInAlpha = [int]$checkedInPixels[$pixelOffset + 3]
            $generatedAlpha = [int]$generatedPixels[$pixelOffset + 3]
            if ($checkedInAlpha -eq 0 -and $generatedAlpha -eq 0) {
                continue
            }

            $activePixels++
            $pixelMaximumDifference = [Math]::Abs($checkedInAlpha - $generatedAlpha)
            $totalDifference += $pixelMaximumDifference
            $comparedChannels++
            for ($channelOffset = 0; $channelOffset -lt 3; $channelOffset++) {
                $checkedInPremultiplied = [int]((
                    ([int]$checkedInPixels[$pixelOffset + $channelOffset] * $checkedInAlpha) + 127) / 255)
                $generatedPremultiplied = [int]((
                    ([int]$generatedPixels[$pixelOffset + $channelOffset] * $generatedAlpha) + 127) / 255)
                $difference = [Math]::Abs(
                    $checkedInPremultiplied - $generatedPremultiplied)
                $totalDifference += $difference
                $comparedChannels++
                if ($difference -gt $pixelMaximumDifference) {
                    $pixelMaximumDifference = $difference
                }
            }

            if ($pixelMaximumDifference -gt 12) {
                $outlierPixels++
            }
            if ($pixelMaximumDifference -gt $maximumDifference) {
                $maximumDifference = $pixelMaximumDifference
            }
        }

        if ($comparedChannels -eq 0 -or $activePixels -eq 0) {
            throw "$Description contains no visible pixels."
        }

        $meanDifference = $totalDifference / [double]$comparedChannels
        $outlierRatio = $outlierPixels / [double]$activePixels
        if ($meanDifference -gt $AllowedMeanDifference -or
            $outlierRatio -gt $AllowedOutlierRatio) {
            throw (
                "$Description differs materially from the generated artwork: " +
                "mean=$($meanDifference.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)), " +
                "outliers=$($outlierRatio.ToString('P3', [Globalization.CultureInfo]::InvariantCulture)), " +
                "maximum=$maximumDifference.")
        }

        Write-Output (
            "$Description comparison passed: " +
            "mean=$($meanDifference.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)), " +
            "outliers=$($outlierRatio.ToString('P3', [Globalization.CultureInfo]::InvariantCulture)), " +
            "maximum=$maximumDifference.")
    }
    finally {
        if ($generatedBitmap) {
            $generatedBitmap.Dispose()
        }
        if ($checkedInBitmap) {
            $checkedInBitmap.Dispose()
        }
        $generatedStream.Dispose()
        $checkedInStream.Dispose()
    }
}

function Read-IconFrames {
    param(
        [string]$Path,
        [string]$Description
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6 -or
        [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw "$Description has an invalid ICO header."
    }

    $frameCount = [BitConverter]::ToUInt16($bytes, 4)
    if ($frameCount -ne $iconSizes.Count) {
        throw "$Description contains $frameCount frames instead of $($iconSizes.Count)."
    }

    $directoryLength = 6 + (16 * $frameCount)
    if ($bytes.Length -lt $directoryLength) {
        throw "$Description has a truncated ICO directory."
    }

    $frames = @()
    $expectedPayloadOffset = $directoryLength
    for ($frameIndex = 0; $frameIndex -lt $frameCount; $frameIndex++) {
        $entryOffset = 6 + (16 * $frameIndex)
        $width = if ($bytes[$entryOffset] -eq 0) { 256 } else { [int]$bytes[$entryOffset] }
        $height = if ($bytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$bytes[$entryOffset + 1] }
        $planes = [BitConverter]::ToUInt16($bytes, $entryOffset + 4)
        $bitsPerPixel = [BitConverter]::ToUInt16($bytes, $entryOffset + 6)
        $payloadLength = [BitConverter]::ToUInt32($bytes, $entryOffset + 8)
        $payloadOffset = [BitConverter]::ToUInt32($bytes, $entryOffset + 12)
        $expectedSize = $iconSizes[$frameIndex]

        if ($width -ne $expectedSize -or
            $height -ne $expectedSize -or
            $bytes[$entryOffset + 2] -ne 0 -or
            $bytes[$entryOffset + 3] -ne 0 -or
            $planes -ne 1 -or
            $bitsPerPixel -ne 32) {
            throw "$Description has an invalid frame directory entry at index $frameIndex."
        }

        $payloadEnd = [uint64]$payloadOffset + [uint64]$payloadLength
        if ($payloadLength -gt [int]::MaxValue -or
            $payloadOffset -ne $expectedPayloadOffset -or
            $payloadEnd -gt $bytes.Length) {
            throw "$Description has an invalid payload range for its ${expectedSize}x${expectedSize} frame."
        }

        $payload = [byte[]]::new([int]$payloadLength)
        [Array]::Copy($bytes, [int]$payloadOffset, $payload, 0, [int]$payloadLength)
        Assert-PngHeader `
            -Bytes $payload `
            -ExpectedWidth $expectedSize `
            -ExpectedHeight $expectedSize `
            -Description "$Description ${expectedSize}x${expectedSize} frame"
        $frames += [pscustomobject]@{
            Size = $expectedSize
            Bytes = $payload
        }
        $expectedPayloadOffset = [int]$payloadEnd
    }

    if ($expectedPayloadOffset -ne $bytes.Length) {
        throw "$Description contains unexpected trailing data."
    }

    return $frames
}

function Compare-IconFrames {
    param(
        [string]$CheckedInPath,
        [string]$GeneratedPath
    )

    $checkedInFrames = @(Read-IconFrames -Path $CheckedInPath -Description 'The checked-in application icon')
    $generatedFrames = @(Read-IconFrames -Path $GeneratedPath -Description 'The generated application icon')
    for ($frameIndex = 0; $frameIndex -lt $iconSizes.Count; $frameIndex++) {
        $size = $iconSizes[$frameIndex]
        if ($size -le 32) {
            $allowedMeanDifference = 4.0
            $allowedOutlierRatio = 0.10
        }
        elseif ($size -le 96) {
            $allowedMeanDifference = 2.5
            $allowedOutlierRatio = 0.04
        }
        else {
            $allowedMeanDifference = 1.5
            $allowedOutlierRatio = 0.015
        }

        Compare-PngPixels `
            -CheckedInBytes $checkedInFrames[$frameIndex].Bytes `
            -GeneratedBytes $generatedFrames[$frameIndex].Bytes `
            -Description "The application icon ${size}x${size} frame" `
            -AllowedMeanDifference $allowedMeanDifference `
            -AllowedOutlierRatio $allowedOutlierRatio
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
            if (-not (Test-Path -LiteralPath $checkedInPath -PathType Leaf)) {
                throw "Generated brand asset is missing: $checkedInPath"
            }
        }

        $checkedInSvgPath = Join-Path $resolvedOutput 'IPA-Bridge.svg'
        $checkedInSvg = [System.IO.File]::ReadAllText($checkedInSvgPath)
        $generatedSvg = [System.IO.File]::ReadAllText($generatedSvgPath)
        if ($checkedInSvg.Length -gt 0 -and $checkedInSvg[0] -eq [char]0xFEFF) {
            $checkedInSvg = $checkedInSvg.Substring(1)
        }
        $checkedInSvg = $checkedInSvg.Replace("`r`n", "`n").Replace("`r", "`n")
        $generatedSvg = $generatedSvg.Replace("`r`n", "`n").Replace("`r", "`n")
        if (-not [string]::Equals($checkedInSvg, $generatedSvg, [StringComparison]::Ordinal)) {
            throw "Generated brand asset is out of date: $checkedInSvgPath"
        }

        $checkedInPngPath = Join-Path $resolvedOutput 'IPA-Bridge.png'
        $checkedInPngBytes = [System.IO.File]::ReadAllBytes($checkedInPngPath)
        $generatedPngBytes = [System.IO.File]::ReadAllBytes($generatedPngPath)
        Assert-PngHeader `
            -Bytes $checkedInPngBytes `
            -ExpectedWidth 1024 `
            -ExpectedHeight 1024 `
            -Description 'The checked-in master icon'
        Assert-PngHeader `
            -Bytes $generatedPngBytes `
            -ExpectedWidth 1024 `
            -ExpectedHeight 1024 `
            -Description 'The generated master icon'
        Compare-PngPixels `
            -CheckedInBytes $checkedInPngBytes `
            -GeneratedBytes $generatedPngBytes `
            -Description 'The master icon' `
            -AllowedMeanDifference 1.5 `
            -AllowedOutlierRatio 0.015

        Compare-IconFrames `
            -CheckedInPath (Join-Path $resolvedOutput 'IPA-Bridge.ico') `
            -GeneratedPath $generatedIconPath

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
