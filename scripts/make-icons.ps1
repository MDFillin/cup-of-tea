# Рисует значки The Cup of Tea (чашка чая на скруглённом квадрате) в src\PVpn.App\Assets: приложение и состояния для трея.
# ICO с PNG-кадрами 16–256 px. Запускать после правки цветов или формы; результат хранится в репозитории.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\PVpn.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# Чашка чая: корпус, ручка, блюдце и (на крупных кадрах) пар. Координаты — доли размера кадра.
function Add-Cup($g, [single]$s, $brush, [bool]$steam) {
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]($x * $s)), ([single]($y * $s)) }
    $cup = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cup.AddLine((P 0.20 0.40), (P 0.70 0.40))
    $cup.AddBezier((P 0.70 0.40), (P 0.70 0.66), (P 0.62 0.78), (P 0.45 0.78))
    $cup.AddBezier((P 0.45 0.78), (P 0.28 0.78), (P 0.20 0.66), (P 0.20 0.40))
    $cup.CloseFigure()
    $g.FillPath($brush, $cup)
    $pen = New-Object System.Drawing.Pen $brush, ([single](0.065 * $s))
    $g.DrawArc($pen, [single](0.60 * $s), [single](0.45 * $s), [single](0.20 * $s), [single](0.18 * $s), -90, 180)
    $g.FillRectangle($brush, [single](0.12 * $s), [single](0.81 * $s), [single](0.66 * $s), [single](0.06 * $s))
    if ($steam) {
        $steamPen = New-Object System.Drawing.Pen $brush, ([single](0.045 * $s))
        $steamPen.StartCap = 'Round'; $steamPen.EndCap = 'Round'
        foreach ($x in 0.36, 0.52) {
            $g.DrawBezier($steamPen, (P $x 0.33), (P ($x - 0.06) 0.27), (P ($x + 0.06) 0.21), (P $x 0.14))
        }
    }
}

function New-RoundedPath([single]$s, [double]$m, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single](2 * $r * $s)
    $x0 = [single]($m * $s); $x1 = [single]((1 - $m) * $s) - $d
    $path.AddArc($x0, $x0, $d, $d, 180, 90)
    $path.AddArc($x1, $x0, $d, $d, 270, 90)
    $path.AddArc($x1, $x1, $d, $d, 0, 90)
    $path.AddArc($x0, $x1, $d, $d, 90, 90)
    $path.CloseFigure()
    Write-Output -NoEnumerate $path
}

# Значок состояния для трея: скруглённый квадрат цвета состояния и тёмная чашка.
# Значок приложения ($app): тёмный квадрат с зелёной каймой и зелёная чашка с паром.
function New-IconPng([int]$size, [System.Drawing.Color]$color, [bool]$app = $false) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    $m = if ($size -le 20) { 0.0 } else { 0.04 }
    $path = New-RoundedPath $s $m 0.24
    $light = [System.Drawing.Color]::FromArgb(255, [Math]::Min(255, $color.R + 40), [Math]::Min(255, $color.G + 30), [Math]::Min(255, $color.B + 10))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), $light, $color
    if ($app) {
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 14, 12))), $path)
        $g.DrawPath((New-Object System.Drawing.Pen $grad, ([single]([Math]::Max(1, 0.045 * $s)))), $path)
        Add-Cup $g $s $grad ($size -ge 32)
    }
    else {
        $g.FillPath($grad, $path)
        Add-Cup $g $s (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 14, 12))) ($size -ge 40)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output -NoEnumerate $ms.ToArray()
}

function Write-Ico([string]$file, [System.Drawing.Color]$color, [bool]$app = $false) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($sz in $sizes) { $frames.Add([byte[]](New-IconPng $sz $color $app)) }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($f in $frames) { $w.Write($f) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets $file), $out.ToArray())
}

Write-Ico 'pvpn.ico' ([System.Drawing.Color]::FromArgb(255, 34, 197, 94)) $true   # тёмный с зелёной чашкой — значок приложения
Write-Ico 'tray-off.ico' ([System.Drawing.Color]::FromArgb(255, 138, 147, 166))  # серый — отключено
Write-Ico 'tray-busy.ico' ([System.Drawing.Color]::FromArgb(255, 245, 181, 68))  # янтарный — подключение
Write-Ico 'tray-on.ico' ([System.Drawing.Color]::FromArgb(255, 52, 211, 153))    # зелёный — подключено
Write-Ico 'tray-error.ico' ([System.Drawing.Color]::FromArgb(255, 248, 113, 113)) # красный — ошибка
# Логотип в окне — отдельный PNG: из ICO WPF берёт мелкий кадр и рисует его размыто.
[IO.File]::WriteAllBytes((Join-Path $assets 'logo.png'), [byte[]](New-IconPng 256 ([System.Drawing.Color]::FromArgb(255, 34, 197, 94)) $true))
Get-ChildItem $assets -File | Select-Object Name, Length
