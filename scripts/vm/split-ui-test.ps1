# Раздельное туннелирование через интерфейс в тестовой ВМ: включение, добавление программы из запущенных
# (по папке) и сайта, подключение, смена режима с «Применить сейчас». Маршрут проверяется curl-ом от pvpn.
# Перед запуском: deploy-and-test.ps1.
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$ui = Join-Path $PSScriptRoot 'ui-test.ps1'
$failures = 0

function Guest([string]$Command, [switch]$AsUser) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec 180 }
function Ui([string[]]$Steps) { & $ui -ShotDir $ShotDir -Steps $Steps | Out-Null }
function Check([string]$Name, [string]$Exe, [string]$Url, [string]$Expect) {
    $ip = ((Guest "(& '$Exe' -4 -s -m 10 $Url) -join ''" -AsUser) -join '').Trim()
    $ok = if ($Expect -eq 'vpn') { $ip -eq $VpnIp } else { $ip -match '^\d+\.\d+\.\d+\.\d+$' -and $ip -ne $VpnIp }
    if (-not $ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2} (ожидалось: {3})" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $Name, $ip, $(if ($Expect -eq 'vpn') { 'VPN' } else { 'напрямую' }))
}
$system = 'C:\Windows\System32\curl.exe'
$app = 'C:\SplitTest\app-1.0.1\curl.exe'

Write-Host '=== Подготовка: чистые настройки pvpn и запущенная «программа» C:\SplitTest\app-1.0.1\pinger.exe' -ForegroundColor Cyan
Guest ("Get-Process TempestVPN, pinger -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item C:\Users\pvpn\AppData\Roaming\TempestVPN\settings.json -ErrorAction SilentlyContinue; " +
    "New-Item -ItemType Directory -Force C:\SplitTest\app-1.0.1 | Out-Null; Copy-Item C:\Windows\System32\curl.exe C:\SplitTest\app-1.0.1\curl.exe -Force; " +
    "Copy-Item C:\Windows\System32\PING.EXE C:\SplitTest\app-1.0.1\pinger.exe -Force; " +
    "Register-ScheduledTask -TaskName PVpnPinger -Force -Action (New-ScheduledTaskAction -Execute 'C:\SplitTest\app-1.0.1\pinger.exe' -Argument '-t 127.0.0.1') " +
    "-Principal (New-ScheduledTaskPrincipal -UserId pvpn -LogonType Interactive -RunLevel Limited) | Out-Null; Start-ScheduledTask -TaskName PVpnPinger")

Write-Host '=== Интерфейс: включить, добавить программу из запущенных и сайт, подключиться' -ForegroundColor Cyan
Ui 'start', 'splitOn', 'running:pinger', 'domain:icanhazip.com', 'wait:1', 'shot:split-exclude', 'dump', 'select:my-hy2', 'connect', 'wait:15', 'dump', 'shot:split-connected'
Check 'системный curl' $system 'https://api.ipify.org' 'vpn'
Check 'curl из папки pinger' $app 'https://api.ipify.org' 'direct'
Check 'сайт icanhazip.com' $system 'https://icanhazip.com' 'direct'

Write-Host '=== Режим «через VPN только выбранное» (список пуст → всё напрямую), «Применить сейчас»' -ForegroundColor Cyan
Ui 'onlyMode', 'wait:1', 'shot:split-only-empty', 'apply', 'wait:6', 'dump'
Check 'системный curl' $system 'https://api.ipify.org' 'direct'
Check 'curl из папки pinger' $app 'https://api.ipify.org' 'direct'

Write-Host '=== Добавить ту же программу в список «через VPN», применить' -ForegroundColor Cyan
Ui 'running:pinger', 'wait:1', 'apply', 'wait:6', 'shot:split-only', 'dump'
Check 'системный curl' $system 'https://api.ipify.org' 'direct'
Check 'curl из папки pinger' $app 'https://api.ipify.org' 'vpn'

Write-Host '=== Обратно в первый режим: список сохранился' -ForegroundColor Cyan
Ui 'excludeMode', 'wait:1', 'apply', 'wait:6', 'dump'
Check 'curl из папки pinger' $app 'https://api.ipify.org' 'direct'
Check 'сайт icanhazip.com' $system 'https://icanhazip.com' 'direct'

Ui 'connect', 'wait:6'
Guest 'Get-Content C:\Users\pvpn\AppData\Roaming\TempestVPN\settings.json; Get-Process pinger -ErrorAction SilentlyContinue | Stop-Process -Force'
Write-Host '=== журнал UI'
Guest 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | Select-String ''^---|мимо VPN|через VPN|Применить|Подключено|Отключено|Discord|pinger|SplitTest|icanhazip|напрямую'' | ForEach-Object Line'
Write-Host "`nПровалено проверок: $failures"
exit $failures
