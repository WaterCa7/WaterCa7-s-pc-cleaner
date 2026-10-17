# Draws the app logo (a water drop with cat ears on the accent-green tile) and writes
#   src/PcCleaner/Assets/app.ico   - exe, window and taskbar icon (16..256 px)
#   src/PcCleaner/Assets/logo.svg  - the same drawing as a vector file
# Run from any folder with Windows PowerShell 5.1:  powershell -File tools\make-icon.ps1
# The geometry below is also used by the sidebar logo in MainWindow.xaml: keep them in sync.
param([string]$PreviewDir)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assets = Join-Path $PSScriptRoot '..\src\PcCleaner\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# 100 x 100 design grid.
$tileColor = '#34D399'   # AccentColor
$inkColor = '#06261A'    # AccentTextBrush
$tileRadius = 22
$body = 'M27,18 L40,35 Q50,31 60,35 L73,18 L75,45 C80,52 78,58 78,62 A28,28 0 0 1 22,62 C22,58 20,52 25,45 Z'
$eyes = @(@{ X = 40; Y = 60 }, @{ X = 60; Y = 60 })
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Get-EyeSize([int]$size) {
    # Below 24 px the eyes blur into the drop; at 24-32 px they are drawn a bit larger to stay visible.
    if ($size -lt 24) { return $null }
    if ($size -le 32) { return @{ Rx = 5; Ry = 7 } }
    return @{ Rx = 4; Ry = 6 }
}

function New-Brush([string]$hex) {
    $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $brush.Freeze()
    return $brush
}

function Render-Logo([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $scale = $size / 100.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $dc.DrawRoundedRectangle((New-Brush $tileColor), $null, (New-Object System.Windows.Rect 0, 0, 100, 100), $tileRadius, $tileRadius)
    $ink = New-Brush $inkColor
    $dc.DrawGeometry($ink, $null, [System.Windows.Media.Geometry]::Parse($body))
    $eye = Get-EyeSize $size
    if ($eye) {
        foreach ($e in $eyes) {
            $dc.DrawEllipse((New-Brush $tileColor), $null, (New-Object System.Windows.Point $e.X, $e.Y), $eye.Rx, $eye.Ry)
        }
    }
    $dc.Pop()
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    # ICO stores straight (not premultiplied) alpha.
    return New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
}

function Get-Png($bitmap) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# Classic 32-bit DIB entry (BITMAPINFOHEADER + bottom-up BGRA + AND mask): read by every Windows shell path.
function Get-Dib($bitmap, [int]$size) {
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $bitmap.CopyPixels($pixels, $stride, 0)
    $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
    $mask = New-Object byte[] ($maskStride * $size)
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($pixels.Length + $mask.Length)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $w.Write($pixels, $y * $stride, $stride)
    }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
        $w.Write($row)
    }
    $w.Flush()
    return , $out.ToArray()
}

$images = foreach ($size in $sizes) {
    $bitmap = Render-Logo $size
    if ($PreviewDir) {
        New-Item -ItemType Directory -Force $PreviewDir | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $PreviewDir "logo-$size.png"), (Get-Png $bitmap))
    }
    # Large sizes as PNG keep the file small; the small ones as DIB for the widest compatibility.
    $data = if ($size -ge 64) { Get-Png $bitmap } else { Get-Dib $bitmap $size }
    [pscustomobject]@{ Size = $size; Data = $data }
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$img.Data.Length); $w.Write([int]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $w.Write($img.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $ico.ToArray())

$eye = Get-EyeSize 256
$eyeSvg = ($eyes | ForEach-Object { "  <ellipse cx=`"$($_.X)`" cy=`"$($_.Y)`" rx=`"$($eye.Rx)`" ry=`"$($eye.Ry)`" fill=`"$tileColor`"/>" }) -join "`n"
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="256" height="256">
  <rect width="100" height="100" rx="$tileRadius" fill="$tileColor"/>
  <path d="$body" fill="$inkColor"/>
$eyeSvg
</svg>
"@
[System.IO.File]::WriteAllText((Join-Path $assets 'logo.svg'), $svg + "`n", (New-Object System.Text.UTF8Encoding $false))

"app.ico: $($ico.Length) bytes, sizes $($sizes -join ', ')"
