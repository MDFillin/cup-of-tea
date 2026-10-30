# Диагностика: после серии запросов через прокси проверяет, проходят ли голые TCP-соединения к серверу.
# Помогает отличить проблему клиента от блокировки новых соединений по пути (DPI провайдера).
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Profile,
    [int]$Requests = 10
)
$ErrorActionPreference = 'Stop'
$cli = Join-Path $PSScriptRoot '..\src\PVpn.Cli\bin\Debug\net10.0-windows\tempest-cli.exe'
$out = Join-Path ([IO.Path]::GetTempPath()) "pvpn-probe-$PID.out"
$proc = Start-Process $cli -ArgumentList 'proxy', $Profile, '--seconds', '300' -WindowStyle Hidden -PassThru -RedirectStandardOutput $out
try {
    $deadline = (Get-Date).AddSeconds(15)
    $port = $null
    while (-not $port -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $line = Get-Content $out -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String 'SOCKS 127\.0\.0\.1:(\d+)'
        if ($line) { $port = $line.Matches[0].Groups[1].Value }
    }
    if (-not $port) { throw 'Прокси не запустился' }

    $failedAt = $null
    for ($i = 1; $i -le $Requests; $i++) {
        $code = curl.exe -s -o NUL -m 6 -w '%{http_code}' --socks5-hostname "127.0.0.1:$port" https://www.gstatic.com/generate_204
        "{0:HH:mm:ss} запрос {1}: {2}" -f (Get-Date), $i, $code
        if ($code -ne '204') { $failedAt = $i; break }
    }
    if ($failedAt) {
        "Сразу после сбоя — голые TCP-пинги через Wi-Fi:"
        & $cli ping --count 5
        "Через 30 с:"
        Start-Sleep -Seconds 30
        & $cli ping --count 5
    }
}
finally {
    Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue
    Remove-Item $out -ErrorAction SilentlyContinue
}
