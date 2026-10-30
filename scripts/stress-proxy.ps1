# Нагрузочная проверка режима прокси: последовательные и параллельные запросы плюс скачивание через SOCKS.
# Запуск: scripts\stress-proxy.ps1 -Profile my-xhttp   (профиль должен быть импортирован в tempest-cli)
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Profile,
    [int]$Sequential = 15,
    [int]$Parallel = 12,
    [int]$DownloadBytes = 5000000,
    [switch]$NoFragment
)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot '..\src\PVpn.Cli\bin\Debug\net10.0-windows\tempest-cli.exe'
$out = Join-Path ([IO.Path]::GetTempPath()) "pvpn-stress-$PID.out"
$cliArgs = @('proxy', $Profile, '--seconds', '300')
if ($NoFragment) { $cliArgs += '--no-fragment' }
$proc = Start-Process $cli -ArgumentList $cliArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $out
try {
    $deadline = (Get-Date).AddSeconds(15)
    $port = $null
    while (-not $port -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $line = Get-Content $out -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String 'SOCKS 127\.0\.0\.1:(\d+)'
        if ($line) { $port = $line.Matches[0].Groups[1].Value }
    }
    if (-not $port) { throw 'Прокси не запустился' }
    $socks = "127.0.0.1:$port"

    $ok = 0; $times = @()
    for ($i = 0; $i -lt $Sequential; $i++) {
        $t = curl.exe -s -o NUL -m 15 -w '%{http_code} %{time_total}' --socks5-hostname $socks https://www.gstatic.com/generate_204
        $code, $sec = $t -split ' '
        if ($code -eq '204') { $ok++; $times += [double]::Parse($sec, [Globalization.CultureInfo]::InvariantCulture) }
    }
    $avg = if ($times) { [math]::Round(($times | Measure-Object -Average).Average * 1000) } else { '-' }
    "Последовательно: $ok/$Sequential, среднее $avg мс"

    $jobs = 1..$Parallel | ForEach-Object {
        Start-Job -ArgumentList $socks -ScriptBlock { param($s) curl.exe -s -o NUL -m 20 -w '%{http_code}' --socks5-hostname $s https://www.google.com/ }
    }
    $codes = $jobs | Wait-Job -Timeout 40 | Receive-Job
    $jobs | Remove-Job -Force
    "Параллельно: $(@($codes | Where-Object { $_ -match '^(200|301|302)$' }).Count)/$Parallel"

    $dl = curl.exe -s -o NUL -m 60 -w '%{http_code} %{size_download} %{speed_download}' --socks5-hostname $socks "https://speed.cloudflare.com/__down?bytes=$DownloadBytes"
    $code, $size, $speed = $dl -split ' '
    "Скачивание: код $code, $size байт, $([math]::Round([double]::Parse($speed, [Globalization.CultureInfo]::InvariantCulture) * 8 / 1MB, 1)) Мбит/с"
}
finally {
    Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
    Get-Content $out -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String 'Warning|Error|error' | Select-Object -Last 5 | ForEach-Object { "  лог: $($_.Line)" }
    Remove-Item $out -ErrorAction SilentlyContinue
}
