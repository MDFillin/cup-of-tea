# Рисует значки Tempest VPN (молния на скруглённом квадрате) в src\PVpn.App\Assets: приложение и состояния для трея.
# ICO с PNG-кадрами 16–256 px. Запускать после правки цветов или формы; результат хранится в репозитории.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\PVpn.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconPng([int]$size, [System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]($x * $s)), ([single]($y * $s)) }
    # Скруглённый квадрат цвета состояния (у мелких кадров поля меньше, чтобы значок не казался крошечным)
    $m = if ($size -le 20) { 0.0 } else { 0.04 }
    $r = 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single](2 * $r * $s)
    $x0 = [single]($m * $s); $x1 = [single]((1 - $m) * $s) - $d
    $path.AddArc($x0, $x0, $d, $d, 180, 90)
    $path.AddArc($x1, $x0, $d, $d, 270, 90)
    $path.AddArc($x1, $x1, $d, $d, 0, 90)
    $path.AddArc($x0, $x1, $d, $d, 90, 90)
    $path.CloseFigure()
    $light = [System.Drawing.Color]::FromArgb(255, [Math]::Min(255, $color.R + 28), [Math]::Min(255, $color.G + 28), [Math]::Min(255, $color.B + 28))
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P 1 1), $light, $color
    $g.FillPath($fill, $path)
    # Молния
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 11, 13, 18))
    $g.FillPolygon($dark, [System.Drawing.PointF[]]@((P 0.58 0.12), (P 0.26 0.56), (P 0.47 0.56), (P 0.40 0.88), (P 0.74 0.42), (P 0.53 0.42), (P 0.63 0.12)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output -NoEnumerate $ms.ToArray()
}

function Write-Ico([string]$file, [System.Drawing.Color]$color) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($sz in $sizes) { $frames.Add([byte[]](New-IconPng $sz $color)) }
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

Write-Ico 'pvpn.ico' ([System.Drawing.Color]::FromArgb(255, 52, 211, 153))       # зелёный — значок приложения
Write-Ico 'tray-off.ico' ([System.Drawing.Color]::FromArgb(255, 138, 147, 166))  # серый — отключено
Write-Ico 'tray-busy.ico' ([System.Drawing.Color]::FromArgb(255, 245, 181, 68))  # янтарный — подключение
Write-Ico 'tray-on.ico' ([System.Drawing.Color]::FromArgb(255, 52, 211, 153))    # зелёный — подключено
Write-Ico 'tray-error.ico' ([System.Drawing.Color]::FromArgb(255, 248, 113, 113)) # красный — ошибка
Get-ChildItem $assets -Filter *.ico | Select-Object Name, Length
