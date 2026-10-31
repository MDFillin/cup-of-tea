# DNS (пункт C) в тестовой ВМ на настоящем сервере:
#   1) TUN с каждым готовым DNS: проверка утечки (bash.ws) видит только резолверы выбранного провайдера,
#      внешний IP — IP сервера;
#   2) свой адрес DoH по домену (https://dns.google/dns-query — нужен bootstrap): резолверы Google;
#   3) без VPN: проверка видит другие DNS (иначе она ничего не проверяет);
#   4) неверный свой адрес служба отвергает.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
# $VpnAsn: в режиме прокси сервер ходит к bash.ws по IPv6, и внешний адрес — его IPv6 из той же сети.
param([string]$VpnIp = '203.0.113.10', [string]$VpnAsn = 'AS213877', [string]$Server = 'my-xhttp')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$failures = 0

# Запускать без перенаправления *>: в PowerShell 5.1 stderr из ВМ тогда становится ошибкой и прерывает vm.ps1.
function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}

# Проверка утечки от pvpn; --dns — чьи резолверы ожидать. Код 0 — только ожидаемые, 2 — посторонние.
function Leak([string]$Expect) {
    $out = (Guest -AsUser -TimeoutSec 90 "$cli dnsleak --dns $Expect; 'EXIT=' + `$LASTEXITCODE") -join "`n"
    $out.Trim()
}

function Test-Dns([string]$Dns, [string]$Expect) {
    $connect = (Guest -AsUser -TimeoutSec 150 "$cli svc connect $Server --test 300 --dns $Dns; $cli svc status") -join "`n"
    if ($connect -notmatch 'Состояние: Connected') {
        Result "TUN, DNS $Dns" $false ($connect.Trim() -replace '\s+', ' ')
        Guest -AsUser "$cli svc disconnect" | Out-Null
        return
    }
    $out = Leak $Expect
    $ok = $out -match 'EXIT=0' -and $out -match "Внешний IP: $([regex]::Escape($VpnIp))" -and $out -match 'Утечки нет'
    Result "TUN, DNS $Dns" $ok ($out -replace '\s*\r?\n\s*', ' | ')
    Guest -AsUser "$cli svc disconnect" | Out-Null
}

Step 'Без VPN: проверка видит не Cloudflare (иначе она ничего не доказывает)'
$out = Leak 'cloudflare'
Result 'без VPN — посторонние DNS' ($out -match 'EXIT=2') ($out -replace '\s*\r?\n\s*', ' | ')

Step "TUN ($Server): готовые DNS"
Test-Dns 'cloudflare' 'cloudflare'
Test-Dns 'google' 'google'
Test-Dns 'quad9' 'quad9'
Test-Dns 'adguard' 'adguard'

Step 'TUN: свой DoH по домену (bootstrap через туннель)'
Test-Dns 'https://dns.google/dns-query' 'google'

Step 'Неверный свой адрес'
$out = (Guest -AsUser "$cli svc connect $Server --test 300 --dns http://dns.google/dns-query 2>&1 | ForEach-Object { `$_.ToString() }; 'EXIT=' + `$LASTEXITCODE") -join "`n"
Result 'служба отвергает http://' ($out -match 'EXIT=1' -and $out -match 'DNS-over-HTTPS') ($out.Trim() -replace '\s+', ' ')

Step 'Интерфейс: AdGuard, «Проверить утечку DNS» в TUN и в режиме прокси'
$settings = Join-Path $env:TEMP 'pvpn-dns-settings.json'
'{ "Dns": { "Provider": "AdGuard" } }' | Set-Content $settings -Encoding ascii
& $vm copy -Source $settings -Dest C:\PVpnSetup
Remove-Item $settings
Guest -AsUser ('Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item C:\PVpnSetup\ui.log -ErrorAction SilentlyContinue; ' +
    'Copy-Item C:\PVpnSetup\pvpn-dns-settings.json C:\Users\pvpn\AppData\Roaming\CupOfTea\settings.json -Force')
& (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir (Join-Path $env:TEMP 'pvpn-ui') -Steps 'start', 'tun', 'select:my-xhttp', 'connect', 'wait:12',
    'dnsleak', 'shot:dns-tun', 'connect', 'wait:5', 'proxy', 'connect', 'wait:8', 'dnsleak', 'shot:dns-proxy',
    'connect', 'wait:3', 'tun' | Out-Null
# В журнале UI после каждой проверки — все тексты окна: берём вердикт и строки списка.
$log = @(Guest -AsUser ("Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | " +
    "Where-Object { `$_ -match '^--- .* dnsleak|Утечки нет|Возможна утечка|Режим прокси:|VPN выключен|Внешний IP|DNS: ' }"))
$log | ForEach-Object { "    $_" }
# Первый кусок — тексты окна до первой проверки.
$sections = @(($log -join "`n") -split '--- [^\n]* dnsleak' | Select-Object -Skip 1)
Result 'интерфейс, TUN + AdGuard' ($sections.Count -ge 1 -and $sections[0] -match 'Утечки нет' -and $sections[0] -match 'DataCamp') ''
Result 'интерфейс, режим прокси' ($sections.Count -ge 2 -and $sections[1] -match 'Режим прокси' -and $sections[1] -match "Внешний IP: .* $VpnAsn ") ''

Step 'Журнал службы'
Guest -AsUser "$cli svc logs" | Select-String -Pattern 'DNS |dns' | Select-Object -Last 8 | ForEach-Object { "  $_" }

Write-Host "`nПровалено проверок: $failures"
exit $failures
