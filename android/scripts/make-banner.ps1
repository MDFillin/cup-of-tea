# Рисует баннер для лаунчера ТВ (640×360, xhdpi) и значок (192×192) — та же чашка, что в scripts\make-icons.ps1
# десктопа. Результат хранится в репозитории (app\src\main\res\drawable-xhdpi, mipmap-xxxhdpi).
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$res = Join-Path $scriptDir '..\app\src\main\res'

$bg = [System.Drawing.Color]::FromArgb(255, 9, 11, 10)
$accent = [System.Drawing.Color]::FromArgb(255, 52, 211, 153)
$text = [System.Drawing.Color]::FromArgb(255, 242, 245, 243)
$muted = [System.Drawing.Color]::FromArgb(255, 155, 165, 159)

# Чашка в квадрате size×size с левым верхним углом (ox, oy): корпус, ручка, блюдце, пар.
function Draw-Cup($g, [single]$ox, [single]$oy, [single]$s) {
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]($ox + $x * $s)), ([single]($oy + $y * $s)) }
    $brush = New-Object System.Drawing.SolidBrush $accent
    $cup = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cup.AddLine((P 0.20 0.40), (P 0.70 0.40))
    $cup.AddBezier((P 0.70 0.40), (P 0.70 0.66), (P 0.62 0.78), (P 0.45 0.78))
    $cup.AddBezier((P 0.45 0.78), (P 0.28 0.78), (P 0.20 0.66), (P 0.20 0.40))
    $g.FillPath($brush, $cup)
    $pen = New-Object System.Drawing.Pen $accent, ([single](0.06 * $s))
    $g.DrawArc($pen, [single]($ox + 0.60 * $s), [single]($oy + 0.45 * $s), [single](0.20 * $s), [single](0.18 * $s), -90, 180)
    $saucer = New-Object System.Drawing.Pen $accent, ([single](0.05 * $s))
    $saucer.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $saucer.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($saucer, (P 0.14 0.86), (P 0.76 0.86))
    $steam = New-Object System.Drawing.Pen $accent, ([single](0.045 * $s))
    $steam.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $steam.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($x in 0.36, 0.52) { $g.DrawBezier($steam, (P $x 0.33), (P ($x - 0.06) 0.27), (P ($x + 0.06) 0.21), (P $x 0.14)) }
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $bmp, $g
}

# Баннер 640×360: чашка слева, название справа.
$bmp, $g = New-Canvas 640 360
$g.Clear($bg)
Draw-Cup $g 34 70 220
$font = New-Object System.Drawing.Font 'Segoe UI Semibold', 46, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$small = New-Object System.Drawing.Font 'Segoe UI', 26, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('The Cup', $font, (New-Object System.Drawing.SolidBrush $text), 262, 108)
$g.DrawString('of Tea', $font, (New-Object System.Drawing.SolidBrush $accent), 262, 164)
$g.DrawString('VPN для телевизора', $small, (New-Object System.Drawing.SolidBrush $muted), 264, 232)
$dir = Join-Path $res 'drawable-xhdpi'
New-Item -ItemType Directory -Force $dir | Out-Null
$bmp.Save((Join-Path $dir 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# Значок 192×192: тёмный скруглённый квадрат и чашка.
$bmp, $g = New-Canvas 192 192
$g.Clear([System.Drawing.Color]::Transparent)
$r = 44
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc(191 - $r, 0, $r, $r, 270, 90)
$path.AddArc(191 - $r, 191 - $r, $r, $r, 0, 90); $path.AddArc(0, 191 - $r, $r, $r, 90, 90); $path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)
Draw-Cup $g 14 8 164
$dir = Join-Path $res 'mipmap-xxxhdpi'
New-Item -ItemType Directory -Force $dir | Out-Null
$bmp.Save((Join-Path $dir 'ic_launcher.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host 'Готово: drawable-xhdpi\banner.png, mipmap-xxxhdpi\ic_launcher.png'
