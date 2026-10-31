# Автоподключение, доверенные сети и страница входа в сеть (пункт F) в тестовой ВМ через интерфейс:
#   1) имена сетей: TUN CupOfTea в них не попадает (иначе «сеть» менялась бы при подключении);
#   2) автоподключение при запуске программы;
#   3) «Доверять этой сети» — VPN отключается; убрать из доверенных — подключается снова;
#   4) ручное отключение: автоматика в этой сети молчит (проверка через 70 с);
#   5) другая сеть (профиль сети переименован, линк ВМ выключен и включён): автоподключение снова работает;
#   6) страница входа: поддельный портал (hosts → 127.0.0.1:80) — автоподключение ждёт, кнопка «Войти в сеть»
#      открывает страницу, после «входа» VPN подключается сам.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui'), [string]$VmName = 'PVpn-Test-Win11')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$failures = 0
$hosts = 'C:\Windows\System32\drivers\etc\hosts'

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function Ui([string[]]$Steps) { & (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps $Steps | Out-Null }
function State { ((Guest -AsUser "$cli svc status") | Select-Object -First 1) -join '' }
function StopApp { Guest -AsUser 'Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1' | Out-Null }
function Networks { ((Guest -AsUser "@($cli netinfo) -match '^Сети|^Интернет'") -join ' | ') }
# Настройки pvpn: автоподключение и доверенные сети (JSON копируется с хоста — в команде для ВМ нельзя двойные кавычки).
function Settings([bool]$Auto) {
    $file = Join-Path $env:TEMP 'pvpn-f-settings.json'
    @{ AutoConnect = $Auto; DisconnectOnTrusted = $true; TrustedNetworks = @() } | ConvertTo-Json | Set-Content $file -Encoding utf8
    & $vm copy -Source $file -Dest C:\PVpnSetup
    Remove-Item $file
    Guest -AsUser 'Copy-Item C:\PVpnSetup\pvpn-f-settings.json C:\Users\pvpn\AppData\Roaming\CupOfTea\settings.json -Force' | Out-Null
}

StopApp
Guest -AsUser "$cli svc disconnect" | Out-Null
Guest -AsUser 'Remove-Item C:\PVpnSetup\ui.log -ErrorAction SilentlyContinue' | Out-Null

Step 'Имена сетей: без VPN и с VPN одинаковые'
$off = Networks
Guest -AsUser -TimeoutSec 150 "$cli svc connect my-xhttp --test 300" | Out-Null
$on = Networks
Guest -AsUser "$cli svc disconnect" | Out-Null
$name = if ($off -match 'Сети \(для доверенных\): «([^»]+)»') { $Matches[1] } else { '' }
Result 'TUN не попадает в сети' ($name -and ($on -replace '\s*\|\s*Интернет.*', '') -eq ($off -replace '\s*\|\s*Интернет.*', '')) "без VPN: $off; с VPN: $on"

Step 'Автоподключение при запуске'
Settings $false
Ui 'start', 'select:my-xhttp', 'wait:2'
StopApp
Settings $true
Ui 'start', 'wait:15'
$s = State
Result 'подключилось само' ($s -match 'Connected') $s

Step 'Доверенная сеть'
Ui 'trustHere', 'wait:8', 'shot:f-trusted'
$s = State
Result '«Доверять этой сети» — VPN отключён' ($s -match 'Disconnected') $s
Ui 'untrust', 'wait:15'
$s = State
Result 'убрана из доверенных — VPN снова включён' ($s -match 'Connected') $s

Step 'Ручное отключение: автоматика молчит в этой сети'
Ui 'connect', 'wait:70'
$s = State
Result 'через 70 с всё ещё отключено' ($s -match 'Disconnected') $s

Step 'Другая сеть: профиль переименован, линк ВМ выключен и включён'
$profileKey = (Guest ("Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles' | " +
    "Where-Object { (Get-ItemProperty `$_.PSPath).ProfileName -eq '$name' } | Select-Object -First 1 | ForEach-Object PSChildName")) -join ''
Guest "Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\$profileKey' ProfileName 'PVpnTestNet'" | Out-Null
& $vbm controlvm $VmName setlinkstate1 off
Start-Sleep 5
& $vbm controlvm $VmName setlinkstate1 on
Start-Sleep 30
$net = Networks
$s = State
Result 'новая сеть — подключилось само' ($s -match 'Connected' -and $net -match 'PVpnTestNet') "$s; $net"
Guest "Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\$profileKey' ProfileName '$name'" | Out-Null

Step 'Страница входа в сеть (поддельный портал)'
StopApp
Guest -AsUser "$cli svc disconnect" | Out-Null
& $vm copy -Source (Join-Path $PSScriptRoot 'guest\portal-server.ps1') -Dest C:\PVpnSetup
Guest ("New-Item -ItemType Directory -Force C:\PVpnSetup\portal | Out-Null; Set-Content C:\PVpnSetup\portal\mode.txt portal; " +
    "Copy-Item $hosts C:\PVpnSetup\portal\hosts.bak -Force; Add-Content $hosts '127.0.0.1 www.msftconnecttest.com'; ipconfig /flushdns | Out-Null; " +
    "Register-ScheduledTask -TaskName PVpnPortal -Force -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\PVpnSetup\portal-server.ps1') " +
    "-Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount) | Out-Null; Start-ScheduledTask -TaskName PVpnPortal; Start-Sleep 3") | Out-Null
try {
    $net = Networks
    Ui 'start', 'wait:12', 'dump', 'shot:f-portal'
    $s = State
    $log = (Guest -AsUser 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | Select-Object -Last 80') -join "`n"
    Result 'портал найден, автоподключение ждёт' ($s -match 'Disconnected' -and $net -match 'Portal' -and $log -match 'Войти в сеть \(гостиница, кафе\)') "$s; $net"
    Ui 'login', 'wait:4'
    Guest 'Set-Content C:\PVpnSetup\portal\mode.txt ok' | Out-Null
    Ui 'wait:20', 'dump', 'shot:f-after-login'
    $s = State
    $log = (Guest -AsUser 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | Select-Object -Last 80') -join "`n"
    Result 'после входа VPN подключился сам' ($s -match 'Connected' -and $log -match 'Вход в сеть выполнен') $s
}
finally {
    Guest ("Stop-ScheduledTask PVpnPortal -ErrorAction SilentlyContinue; Unregister-ScheduledTask PVpnPortal -Confirm:`$false -ErrorAction SilentlyContinue; " +
        "Copy-Item C:\PVpnSetup\portal\hosts.bak $hosts -Force; ipconfig /flushdns | Out-Null; Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force") | Out-Null
}

StopApp
Settings $false
Guest -AsUser "$cli svc disconnect" | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
