# Удаляет Tempest VPN: служба возвращает сеть (маршруты, DNS, фильтры), затем удаляются служба, правило брандмауэра,
# ярлык, автозапуск и файлы. Системный прокси возвращает tempest-cli. Серверы и настройки пользователя
# (%APPDATA%\TempestVPN) остаются. Глобальную политику брандмауэра не трогает.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Target = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Tempest VPN'),
    # Оставить данные службы (C:\ProgramData\TempestVPN: журналы, разрешённые пользователи).
    [switch]$KeepData,
    # Не удалять папку программы (её удаляет установщик).
    [switch]$KeepFiles
)
$ErrorActionPreference = 'Stop'
$serviceName = 'TempestVPN'

# Интерфейс держит свои файлы: закрываем его, системный прокси затем возвращает tempest-cli.
Get-Process TempestVPN -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target '*') } | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(10000) | Out-Null
}

$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$exe = Join-Path $Target 'TempestVPN.Service.exe'
if (Test-Path $exe) { & $exe --repair }
if ($service) { sc.exe delete $serviceName | Out-Null }
Get-NetFirewallRule -Group $serviceName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Прокси и автозапуск — у пользователя, вошедшего в систему. Прокси хранится в его HKCU,
# поэтому tempest-cli вызываем, только если установку запустил он сам (обычный случай для администратора).
$account = (Get-CimInstance Win32_ComputerSystem).UserName
if ($account) {
    $cli = Join-Path $Target 'tempest-cli.exe'
    if ((Test-Path $cli) -and $account -eq [Security.Principal.WindowsIdentity]::GetCurrent().Name) { & $cli restore-proxy }
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
    $run = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
    if (Test-Path $run) { Remove-ItemProperty $run -Name $serviceName -ErrorAction SilentlyContinue }
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows\Start Menu\Programs\Tempest VPN.lnk') -ErrorAction SilentlyContinue
if (-not $KeepFiles -and (Test-Path $Target)) { Remove-Item $Target -Recurse -Force }
if (-not $KeepData) { Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'TempestVPN') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Tempest VPN удалён'
