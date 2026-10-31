# Удаляет The Cup of Tea: служба возвращает сеть (маршруты, DNS, фильтры), затем удаляются служба, правило брандмауэра,
# ярлык, автозапуск и файлы. Системный прокси возвращает cupoftea-cli. Серверы и настройки пользователя
# (%APPDATA%\CupOfTea) остаются. Глобальную политику брандмауэра не трогает.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Target = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'The Cup of Tea'),
    # Оставить данные службы (C:\ProgramData\CupOfTea: журналы, разрешённые пользователи).
    [switch]$KeepData,
    # Не удалять папку программы (её удаляет установщик).
    [switch]$KeepFiles
)
$ErrorActionPreference = 'Stop'
$serviceName = 'CupOfTea'

# Интерфейс держит свои файлы: закрываем его, системный прокси затем возвращает cupoftea-cli.
Get-Process CupOfTea -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target '*') } | ForEach-Object {
    $_ | Stop-Process -Force
    $_.WaitForExit(10000) | Out-Null
}

$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
$exe = Join-Path $Target 'CupOfTea.Service.exe'
if (Test-Path $exe) { & $exe --repair }
if ($service) { sc.exe delete $serviceName | Out-Null }
Get-NetFirewallRule -Group $serviceName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Прокси и автозапуск — у пользователя, вошедшего в систему. Прокси хранится в его HKCU,
# поэтому cupoftea-cli вызываем, только если установку запустил он сам (обычный случай для администратора).
$account = (Get-CimInstance Win32_ComputerSystem).UserName
if ($account) {
    $cli = Join-Path $Target 'cupoftea-cli.exe'
    if ((Test-Path $cli) -and $account -eq [Security.Principal.WindowsIdentity]::GetCurrent().Name) { & $cli restore-proxy }
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
    $run = "Registry::HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Run"
    if (Test-Path $run) { Remove-ItemProperty $run -Name $serviceName -ErrorAction SilentlyContinue }
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows\Start Menu\Programs\The Cup of Tea.lnk') -ErrorAction SilentlyContinue
if (-not $KeepFiles -and (Test-Path $Target)) { Remove-Item $Target -Recurse -Force }
if (-not $KeepData) { Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CupOfTea') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'The Cup of Tea удалён'
