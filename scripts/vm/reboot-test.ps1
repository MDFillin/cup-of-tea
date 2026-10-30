# Этап 4 в тестовой ВМ: перезагрузка во время подключения (с kill switch).
# -Hard — аппаратный сброс ВМ (как внезапное отключение питания), иначе обычная перезагрузка Windows.
# После загрузки сеть должна быть чистой: нет фильтров kill switch, адаптера TempestVPN и маршрута к серверу, прямой интернет работает.
[CmdletBinding()]
param(
    [string]$Profile = 'my-hy2',
    [switch]$Hard,
    [string]$ServerIp = '203.0.113.10'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"
function Step([string]$Text) { Write-Host "`n=== $(Get-Date -Format HH:mm:ss) $Text" -ForegroundColor Cyan }

Step "Подключение $Profile"
& $vm run -AsUser -TimeoutSec 120 -Command "$cli svc connect $Profile --test 900"
& $vm run -TimeoutSec 60 -Command "$cli killswitch"

if ($Hard) {
    Step 'Аппаратный сброс ВМ'
    & $vbm controlvm PVpn-Test-Win11 reset
}
else {
    Step 'Перезагрузка Windows'
    & $vm run -NoWait -TimeoutSec 60 -Command 'Restart-Computer -Force'
    # Ждём, пока гостевые дополнения пропадут (ВМ ушла в перезагрузку).
    $deadline = (Get-Date).AddMinutes(3)
    do { Start-Sleep 5; $users = (& $vbm guestproperty get PVpn-Test-Win11 /VirtualBox/GuestInfo/OS/LoggedInUsers) -join '' }
    while ($users -match 'Value: [1-9]' -and (Get-Date) -lt $deadline)
}

Step 'Жду загрузки и входа pvpn'
Start-Sleep 20
$deadline = (Get-Date).AddMinutes(8)
do {
    Start-Sleep 10
    $users = (& $vbm guestproperty get PVpn-Test-Win11 /VirtualBox/GuestInfo/OS/LoggedInUsers) -join ''
    $ga = (& $vbm guestproperty get PVpn-Test-Win11 /VirtualBox/GuestAdd/Version) -join ''
} while (-not ($users -match 'Value: [1-9]' -and $ga -match 'Value') -and (Get-Date) -lt $deadline)
Start-Sleep 20

Step 'Состояние после загрузки'
& $vm run -TimeoutSec 120 -Command ("$cli svc status; $cli killswitch; " +
    "'Адаптеров TempestVPN: ' + @(Get-NetAdapter -IncludeHidden | Where-Object Name -eq TempestVPN).Count; " +
    "'Маршрутов к серверу: ' + @(Get-NetRoute -DestinationPrefix $ServerIp/32 -ErrorAction SilentlyContinue).Count; " +
    "'Внешний IP: ' + ((curl.exe -s -m 10 https://api.ipify.org) -join ''); " +
    "'DNS: ' + ((Resolve-DnsName example.com -Type A -DnsOnly -ErrorAction SilentlyContinue | Where-Object IPAddress | ForEach-Object IPAddress) -join ', '); " +
    "Get-Content (Get-ChildItem C:\ProgramData\TempestVPN\logs | Select-Object -Last 1).FullName -Encoding UTF8 -Tail 8")
