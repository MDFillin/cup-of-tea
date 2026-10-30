# Страница входа в сеть, когда VPN уже включён (TUN + kill switch), в тестовой ВМ:
#   1) VPN подключён через интерфейс;
#   2) «сеть отеля»: брандмауэр Windows закрывает соединения с сервером, hosts ведёт NCSI на поддельный портал
#      (guest\portal-server.ps1), линк ВМ передёргивается — как переход в новую сеть;
#   3) служба переподключается с kill switch, в окне есть «Войти в сеть»;
#   4) кнопка: VPN отключён, фильтров kill switch нет, браузер дошёл до портала;
#   5) «вход»: блокировка снята, портал отвечает как интернет — VPN подключается сам, kill switch снова включён.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui'), [string]$VmName = 'PVpn-Test-Win11')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"
$failures = 0
$hosts = 'C:\Windows\System32\drivers\etc\hosts'

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function Ui([string[]]$Steps) { & (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps $Steps | Out-Null }
function Status { ((Guest -AsUser "$cli svc status") -join ' ') -replace '\s+', ' ' }
function Filters { ((Guest "$cli killswitch") -join '').Trim() }
function UiLog { (Guest -AsUser 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | Select-Object -Last 120') -join "`n" }

Guest -AsUser "Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force; $cli svc disconnect | Out-Null; Remove-Item C:\PVpnSetup\ui.log, C:\Users\pvpn\AppData\Roaming\TempestVPN\settings.json -ErrorAction SilentlyContinue" | Out-Null

Step 'VPN включён (TUN, kill switch)'
Ui 'start', 'tun', 'select:my-xhttp', 'connect', 'wait:12'
$s = Status
Result 'подключено' ($s -match 'Connected' -and $s -match [regex]::Escape($VpnIp) -and $s -match 'Kill switch: включён') $s

& $vm copy -Source (Join-Path $PSScriptRoot 'guest\portal-server.ps1') -Dest C:\PVpnSetup
try {
    Step 'Сеть отеля: сервер закрыт, вместо интернета — страница входа, переход в новую сеть'
    Guest ("New-Item -ItemType Directory -Force C:\PVpnSetup\portal | Out-Null; Set-Content C:\PVpnSetup\portal\mode.txt portal; Remove-Item C:\PVpnSetup\portal\requests.log -ErrorAction SilentlyContinue; " +
        "Copy-Item $hosts C:\PVpnSetup\portal\hosts.bak -Force; Add-Content $hosts '127.0.0.1 www.msftconnecttest.com'; ipconfig /flushdns | Out-Null; " +
        "Register-ScheduledTask -TaskName PVpnPortal -Force -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\PVpnSetup\portal-server.ps1') " +
        "-Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount) | Out-Null; Start-ScheduledTask -TaskName PVpnPortal; " +
        "New-NetFirewallRule -DisplayName PVpnTestHotel -Direction Outbound -RemoteAddress $VpnIp -Action Block | Out-Null; Start-Sleep 2") | Out-Null
    & $vbm controlvm $VmName setlinkstate1 off
    Start-Sleep 8
    & $vbm controlvm $VmName setlinkstate1 on
    # Сторож службы замечает, что сервер не отвечает, не сразу: ждём переподключения до 2 минут.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do { Start-Sleep 5; $s = Status } until ($s -match 'Reconnecting|Error' -or $sw.Elapsed.TotalSeconds -gt 120)
    Write-Host "      служба заметила через $([int]$sw.Elapsed.TotalSeconds) с после смены сети"
    Ui 'wait:3', 'dump', 'shot:fp-hotel'
    $s = Status
    $f = Filters
    $log = UiLog
    Result 'служба не может подключиться, kill switch держит' ($s -match 'Reconnecting|Error' -and $f -match 'Фильтров kill switch: [1-9]') "$s; $f"
    Result 'в окне есть «Войти в сеть»' ($log -match 'Войти в сеть \(гостиница, кафе\)') ''

    Step 'Кнопка «Войти в сеть»'
    Ui 'login', 'wait:6', 'shot:fp-login'
    $s = Status
    $f = Filters
    $requests = (Guest 'Get-Content C:\PVpnSetup\portal\requests.log -ErrorAction SilentlyContinue') -join ' '
    Result 'VPN отключён, kill switch снят' ($s -match 'Disconnected' -and $f -match 'Фильтров kill switch: 0') "$s; $f"
    Result 'браузер дошёл до страницы входа' ($requests -match 'GET /redirect') $requests

    Step 'Вход выполнен: сервер открыт, интернет есть'
    Guest 'Remove-NetFirewallRule -DisplayName PVpnTestHotel; Set-Content C:\PVpnSetup\portal\mode.txt ok' | Out-Null
    Ui 'wait:25', 'dump', 'shot:fp-after'
    $s = Status
    $f = Filters
    Result 'VPN подключился сам, kill switch снова включён' ($s -match 'Connected' -and $s -match [regex]::Escape($VpnIp) -and $f -match 'Фильтров kill switch: [1-9]') "$s; $f"
}
finally {
    Guest ("Remove-NetFirewallRule -DisplayName PVpnTestHotel -ErrorAction SilentlyContinue; Stop-ScheduledTask PVpnPortal -ErrorAction SilentlyContinue; " +
        "Unregister-ScheduledTask PVpnPortal -Confirm:`$false -ErrorAction SilentlyContinue; Copy-Item C:\PVpnSetup\portal\hosts.bak $hosts -Force -ErrorAction SilentlyContinue; " +
        "ipconfig /flushdns | Out-Null; Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force") | Out-Null
    & $vbm controlvm $VmName setlinkstate1 on
}

Guest -AsUser "Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force; $cli svc disconnect" | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
