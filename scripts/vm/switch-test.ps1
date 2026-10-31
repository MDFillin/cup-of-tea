# Двойной щелчок по серверу в списке (тестовая ВМ, интерфейс):
#   1) VPN выключен: двойной щелчок подключает к этому серверу;
#   2) VPN включён: двойной щелчок по другому серверу — переключение на него (TUN);
#   3) одиночный щелчок по другому серверу — только выбор, подключение не трогается;
#   4) двойной щелчок по серверу, к которому уже подключены, — без переподключения;
#   5) двойной щелчок во время подключения — подключение к новому серверу;
#   6) режим прокси: двойной щелчок переключает сервер.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$failures = 0

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function Ui([string[]]$Steps) { & (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps $Steps | Out-Null }
function Server { $s = (Guest -AsUser "$cli svc status") -join ' '; if ($s -notmatch 'Состояние: Connected') { 'не подключено' } elseif ($s -match 'Сервер: (?:\S+ )?(my-[\w-]+)') { $Matches[1] } else { '?' } }
function Connects { @(Guest -AsUser "@($cli svc logs) -match 'Подключение: '").Count }

Guest -AsUser "Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force; $cli svc disconnect | Out-Null; Remove-Item C:\PVpnSetup\ui.log, C:\Users\pvpn\AppData\Roaming\CupOfTea\settings.json -ErrorAction SilentlyContinue" | Out-Null

Step 'VPN выключен: двойной щелчок подключает'
Ui 'start', 'tun', 'dblclick:my-xhttp', 'wait:12'
$s = Server
Result 'подключено к my-xhttp' ($s -eq 'my-xhttp') $s

Step 'VPN включён: двойной щелчок по другому серверу'
Ui 'dblclick:my-hy2', 'wait:15'
$s = Server
Result 'переключено на my-hy2' ($s -eq 'my-hy2') $s

Step 'Одиночный щелчок — только выбор'
$before = Connects
Ui 'select:my-xhttp', 'wait:6'
$s = Server
Result 'остались на my-hy2' ($s -eq 'my-hy2' -and (Connects) -eq $before) $s

Step 'Двойной щелчок по текущему серверу'
Ui 'dblclick:my-hy2', 'wait:6'
$s = Server
Result 'без переподключения' ($s -eq 'my-hy2' -and (Connects) -eq $before) "$s, подключений в журнале службы было $before, стало $(Connects)"

Step 'Двойной щелчок во время подключения — переключает на новый сервер'
Ui 'connect', 'wait:5', 'dblclick:my-hy2', 'wait:1', 'dblclick:my-xhttp', 'wait:12'
$s = Server
Result 'подключено к my-xhttp' ($s -eq 'my-xhttp') $s

Step 'Режим прокси (сервер — по порту: my-xhttp 443, my-vision 8443)'
# Трафик через HTTP-вход и порты соединений xray пользователя с сервером.
function ProxyPorts {
    # Соединение смотрим во время медленной загрузки: у Vision без mux оно закрывается сразу после запроса.
    ((Guest -AsUser ("`$c = Start-Process curl.exe -PassThru -WindowStyle Hidden -ArgumentList '-s -o NUL -m 20 --limit-rate 100k -x http://127.0.0.1:10829 https://speed.cloudflare.com/__down?bytes=3000000'; Start-Sleep 3; " +
        "`$x = (Get-Process xray -ErrorAction SilentlyContinue | Where-Object { `$_.SessionId -ne 0 }).Id; " +
        "(Get-NetTCPConnection -RemoteAddress $VpnIp -ErrorAction SilentlyContinue | Where-Object { `$x -contains `$_.OwningProcess } | ForEach-Object RemotePort | Sort-Object -Unique) -join ','; Stop-Process -Id `$c.Id -ErrorAction SilentlyContinue")) -join '').Trim()
}
Ui 'connect', 'wait:5', 'proxy', 'dblclick:my-xhttp', 'wait:6'
$a = ProxyPorts
Ui 'dblclick:my-vision', 'wait:6', 'shot:switch-proxy'
$b = ProxyPorts
Result 'двойной щелчок подключает и переключает' ($a -eq '443' -and $b -eq '8443') "my-xhttp → порты $a; my-vision → порты $b"

Ui 'connect', 'wait:3', 'tun'
Guest -AsUser 'Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
