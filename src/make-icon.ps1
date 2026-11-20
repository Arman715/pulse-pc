param([string]$IcoOut, [string]$PreviewOut)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

function Color($hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush($hex) { $b = New-Object System.Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }

# Рисует иконку нужного размера в стиле «Чертёж»: лист бумаги в рамке тушью, миллиметровка и красная линия пульса.
function Line($dc, $pen, $x1, $y1, $x2, $y2) { $dc.DrawLine($pen, (New-Object System.Windows.Point $x1, $y1), (New-Object System.Windows.Point $x2, $y2)) }

function Render([int]$S) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $k = $S / 256.0

    # Лист: почти квадратные углы, как у чертёжного формата.
    $pad = if ($S -le 24) { 0.0 } else { [math]::Round($S * 0.04) }
    $radius = if ($S -le 24) { $S * 0.08 } else { $S * 0.05 }
    $side = $S - 2 * $pad
    $tile = New-Object System.Windows.Rect $pad, $pad, $side, $side
    $dc.DrawRoundedRectangle((Brush '#F2EEE5'), $null, $tile, $radius, $radius)

    # Миллиметровка: тонкая сетка и каждая пятая линия плотнее (только там, где её видно).
    if ($S -ge 48) {
        $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry $tile, $radius, $radius))
        $n = 16; $step = $side / $n
        $minor = New-Object System.Windows.Media.Pen (Brush '#E2DBCD'), ([math]::Max(0.6, $S * 0.004))
        $major = New-Object System.Windows.Media.Pen (Brush '#CFC6B4'), ([math]::Max(0.8, $S * 0.006))
        for ($i = 1; $i -lt $n; $i++) {
            $p = $pad + $i * $step
            $pen = if ($i % 4 -eq 0) { $major } else { $minor }
            Line $dc $pen $p $pad $p ($pad + $side)
            Line $dc $pen $pad $p ($pad + $side) $p
        }
        $dc.Pop()
    }

    # Рамка тушью; на крупных размерах ещё внутренняя рамка поля и метки совмещения в углах.
    $bw = if ($S -le 24) { [math]::Max(1.0, $S * 0.07) } else { [math]::Max(1.2, $S * 0.022) }
    $ink = New-Object System.Windows.Media.Pen (Brush '#1A1917'), $bw
    $frame = New-Object System.Windows.Rect ($pad + $bw / 2), ($pad + $bw / 2), ($side - $bw), ($side - $bw)
    $dc.DrawRoundedRectangle($null, $ink, $frame, ([math]::Max(0, $radius - $bw / 2)), ([math]::Max(0, $radius - $bw / 2)))
    if ($S -ge 64) {
        $m = $pad + $S * 0.085; $len = $S * 0.07
        $thin = New-Object System.Windows.Media.Pen (Brush '#45423C'), ([math]::Max(1.0, $S * 0.008))
        foreach ($c in @(@($m, $m, 1, 1), @(($S - $m), $m, -1, 1), @($m, ($S - $m), 1, -1), @(($S - $m), ($S - $m), -1, -1))) {
            Line $dc $thin $c[0] $c[1] ($c[0] + $c[2] * $len) $c[1]
            Line $dc $thin $c[0] $c[1] $c[0] ($c[1] + $c[3] * $len)
        }
    }

    # Линия пульса. На мелких размерах она толще относительно листа, чтобы читаться.
    $pts = @(@(38, 138), @(90, 138), @(112, 82), @(140, 190), @(164, 108), @(178, 138), @(218, 138))
    $w = if ($S -ge 128) { $S * 0.054 } elseif ($S -ge 40) { $S * 0.08 } else { [math]::Max(1.9, $S * 0.115) }
    $line = New-Object System.Windows.Media.Pen (Brush '#C23B22'), $w
    $line.LineJoin = 'Miter'; $line.MiterLimit = 3; $line.StartLineCap = 'Flat'; $line.EndLineCap = 'Flat'
    $geo = New-Object System.Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $first = $true
    foreach ($p in $pts) {
        $pt = New-Object System.Windows.Point ($p[0] * $k), ($p[1] * $k)
        if ($first) { $ctx.BeginFigure($pt, $false, $false); $first = $false } else { $ctx.LineTo($pt, $true, $true) }
    }
    $ctx.Close(); $geo.Freeze()
    $dc.DrawGeometry($null, $line, $geo)
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $S, $S, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv); $rtb.Freeze()
    $rtb
}

function PngBytes($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream; $enc.Save($ms); $ms.ToArray()
}

# Кадр в формате BMP (для мелких размеров - так иконку понимает любой компонент Windows).
function BmpBytes($bmp, [int]$S) {
    $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bmp, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $S * 4
    $px = New-Object byte[] ($stride * $S)
    $conv.CopyPixels($px, $stride, 0)
    $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$S); $bw.Write([int]($S * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $S - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $stride, $stride) }      # строки снизу вверх
    $maskStride = [int]([math]::Ceiling($S / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $S)))                                # маска: прозрачность задаёт альфа-канал
    $bw.Flush(); $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($s in $sizes) { $frames[$s] = Render $s }

$data = @{}
foreach ($s in $sizes) { if ($s -ge 64) { $data[$s] = [byte[]](PngBytes $frames[$s]) } else { $data[$s] = [byte[]](BmpBytes $frames[$s] $s) } }

$fs = [System.IO.File]::Create($IcoOut); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bytes = [byte[]]$data[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$bytes.Length); $w.Write([int]$offset)
    $offset += $bytes.Length
}
foreach ($s in $sizes) { $w.Write([byte[]]$data[$s]) }
$w.Flush(); $fs.Close()

# Предпросмотр: крупно и в настоящих размерах на тёмном (панель задач) и светлом (обои) фоне.
$dv = New-Object System.Windows.Media.DrawingVisual; $dc = $dv.RenderOpen()
$dc.DrawRectangle((Brush '#1C1C1C'), $null, (New-Object System.Windows.Rect 0, 0, 640, 300))
$dc.DrawRectangle((Brush '#C9A27A'), $null, (New-Object System.Windows.Rect 0, 300, 640, 120))
$dc.DrawImage($frames[256], (New-Object System.Windows.Rect 22, 22, 256, 256))
$x = 310
foreach ($s in 128, 64, 48, 32, 24, 16) { $dc.DrawImage($frames[$s], (New-Object System.Windows.Rect $x, (278 - $s), $s, $s)); $x += $s + 14 }
$x = 22
foreach ($s in 128, 64, 48, 32, 24, 16) { $dc.DrawImage($frames[$s], (New-Object System.Windows.Rect $x, (410 - $s), $s, $s)); $x += $s + 18 }
$dc.Close()
$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap 640, 420, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($dv)
[System.IO.File]::WriteAllBytes($PreviewOut, (PngBytes $rtb))
"ico: " + (Get-Item $IcoOut).Length + " байт, кадров " + $sizes.Count
