# Устанавливает или обновляет Tempest VPN: файлы в Program Files, служба TempestVPN (LocalSystem, автозапуск),
# правило брандмауэра для sing-box (TUN-стек принимает соединения на адаптере TUN), ярлык в «Пуске».
# Ядра из комплекта (cores-bundled) ставятся, только если они новее уже установленных: обновлённые в программе не откатываются.
# Установку под прежним именем PVpn снимает (сеть возвращает её служба) и переносит её данные и более новые ядра.
# Глобальную политику брандмауэра не трогает. Запускать от администратора из папки комплекта (out\TempestVPN)
# или установщиком из папки программы (тогда Source = Target).
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Tempest VPN'),
    # Кому разрешено управлять службой. По умолчанию — пользователь, вошедший в систему, и те, кому было разрешено раньше.
    [string[]]$UserSid,
    # Журнал установки (его пишет установщик).
    [string]$Log
)
$ErrorActionPreference = 'Stop'
if ($Log) { Start-Transcript -Path $Log -Append | Out-Null }
# В журнал — где именно упало.
trap { Write-Host "Ошибка: $_`n$($_.ScriptStackTrace)"; break }
# При запуске через powershell -File значение по умолчанию из $PSScriptRoot бывает пустым.
if (-not $Source) { $Source = Split-Path -Parent $MyInvocation.MyCommand.Path }

$productName = 'Tempest VPN'
$serviceName = 'TempestVPN'
$data = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'TempestVPN'
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows\Start Menu\Programs'
# Прежнее имя (до пункта H).
$legacyService = 'PVpn'
$legacyTarget = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PVpn'
$legacyData = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'PVpn'

if (-not (Test-Path (Join-Path $Source 'TempestVPN.Service.exe'))) { throw "В $Source нет TempestVPN.Service.exe — сначала scripts\build.ps1" }

function Stop-AppProcess([string]$Name, [string]$Folder) {
    # Запущенный интерфейс держит свои файлы: закрываем его (системный прокси он вернёт при следующем запуске).
    Get-Process $Name -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Folder '*') } | ForEach-Object {
        Write-Host "Закрываю $Name.exe ($($_.Id))"
        $_ | Stop-Process -Force
        $_.WaitForExit(10000) | Out-Null
    }
}

function Stop-ServiceAndWait([string]$Name) {
    $service = Get-Service $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Останавливаю службу $Name"
        Stop-Service $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    $service
}

function Read-AllowedSids([string]$Folder) {
    $file = Join-Path $Folder 'service.json'
    if (-not (Test-Path $file)) { return @() }
    try { @((Get-Content $file -Raw | ConvertFrom-Json).allowedUserSids) } catch { @() }
}

# ── Кому разрешено управлять службой ──
$account = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $account) { $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name }
if (-not $UserSid) {
    $UserSid = @((New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value) +
        (Read-AllowedSids $data) + (Read-AllowedSids $legacyData) | Where-Object { $_ } | Select-Object -Unique
    Write-Host "Управлять службой сможет: $account$(if (@($UserSid).Count -gt 1) { " и ещё $(@($UserSid).Count - 1)" })"
}

# ── Прежняя установка PVpn: служба возвращает сеть и удаляется ──
$legacy = Get-CimInstance Win32_Service -Filter "Name='$legacyService'" -ErrorAction SilentlyContinue
if ($legacy -or (Test-Path $legacyTarget)) {
    Write-Host 'Найдена установка под прежним именем PVpn — переношу'
    Stop-AppProcess 'PVpn' $legacyTarget
    if ($legacy) {
        Stop-ServiceAndWait $legacyService | Out-Null
        $legacyExe = $legacy.PathName.Trim('"')
        if (Test-Path $legacyExe) { & $legacyExe --repair | Out-Null }
        sc.exe delete $legacyService | Out-Null
    }
    Get-NetFirewallRule -Group 'PVpn' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Remove-Item (Join-Path $startMenu 'PVpn.lnk') -ErrorAction SilentlyContinue
    # Журналы и настройки службы переезжают, если у новой их ещё нет.
    if ((Test-Path $legacyData) -and -not (Test-Path $data)) { Move-Item $legacyData $data }
}

# ── Файлы ──
$existing = Stop-ServiceAndWait $serviceName
Stop-AppProcess 'TempestVPN' $Target
New-Item -ItemType Directory -Force $Target | Out-Null
if ((Resolve-Path $Source).Path -ne (Resolve-Path $Target).Path) {
    Write-Host "Копирую файлы в $Target"
    Copy-Item (Join-Path $Source '*') $Target -Recurse -Force
}

# ── Ядра: из установленных, прежней установки и комплекта — самое новое по ответу на «version» ──
# Заодно это первый запуск ядер, пока сеть открыта: антивирус проверяет незнакомый exe в облаке,
# а при подключении kill switch уже закрыл бы ему сеть, и старт xray затянулся бы на десятки секунд.
function Get-CoreVersion([string]$Exe, [string]$Pattern) {
    if (-not (Test-Path $Exe)) { return $null }
    $ErrorActionPreference = 'Continue'
    try { $text = & $Exe version 2>$null | Out-String } catch { return $null }
    if ($LASTEXITCODE -eq 0 -and $text -match $Pattern) { [version]$Matches[1] } else { $null }
}

$cores = Join-Path $Target 'cores'
$bundled = Join-Path $Target 'cores-bundled'
$previous = Join-Path $Target 'cores-previous'
New-Item -ItemType Directory -Force $cores | Out-Null
$versions = foreach ($core in @(
        @{ Title = 'xray'; Exe = 'xray.exe'; License = 'LICENSE-xray.txt'; Pattern = '\bXray (\d+\.\d+\.\d+)' },
        @{ Title = 'sing-box'; Exe = 'sing-box.exe'; License = 'LICENSE-sing-box.txt'; Pattern = 'sing-box version (\d+\.\d+\.\d+)' })) {
    # При равных версиях остаётся уже установленное ядро (оно первое в списке).
    $best = $null
    foreach ($dir in $cores, (Join-Path $legacyTarget 'cores'), $bundled) {
        $version = Get-CoreVersion (Join-Path $dir $core.Exe) $core.Pattern
        if ($version -and (-not $best -or $version -gt $best.Version)) { $best = @{ Dir = $dir; Version = $version } }
    }
    if (-not $best) { throw "Нет рабочего $($core.Exe) ни в комплекте, ни в установленной программе" }
    if ($best.Dir -ne $cores) {
        $current = Join-Path $cores $core.Exe
        if (Test-Path $current) {
            # Как при обновлении из программы: прежнее ядро — в cores-previous, к нему можно откатиться.
            New-Item -ItemType Directory -Force $previous | Out-Null
            Move-Item $current (Join-Path $previous $core.Exe) -Force
        }
        Copy-Item (Join-Path $best.Dir $core.Exe) $cores -Force
        $license = Join-Path $best.Dir $core.License
        if (Test-Path $license) { Copy-Item $license $cores -Force }
    }
    $from = if ($best.Dir -eq $cores) { 'установленное' } elseif ($best.Dir -eq $bundled) { 'из комплекта' } else { 'из прежней установки' }
    Write-Host "Ядро $($core.Title) $($best.Version) ($from)"
    "$($core.Title) $($best.Version)"
}
$versions | Set-Content (Join-Path $cores 'VERSIONS.txt') -Encoding utf8
if (Test-Path $bundled) { Remove-Item $bundled -Recurse -Force }
if (Test-Path $legacyTarget) { Remove-Item $legacyTarget -Recurse -Force }
if (Test-Path $legacyData) { Remove-Item $legacyData -Recurse -Force }

# ── Ярлык в меню «Пуск» для всех пользователей ──
$app = Join-Path $Target 'TempestVPN.exe'
if (Test-Path $app) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $startMenu "$productName.lnk"))
    $link.TargetPath = $app
    $link.WorkingDirectory = $Target
    $link.Description = "$productName — личный VPN"
    $link.Save()
}

# ── Служба ──
New-Item -ItemType Directory -Force $data | Out-Null
@{ allowedUserSids = @($UserSid) } | ConvertTo-Json | Set-Content (Join-Path $data 'service.json') -Encoding utf8

Get-NetFirewallRule -Group $serviceName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName "$productName sing-box (TUN)" -Group $serviceName -Direction Inbound -Action Allow -Profile Any `
    -Program (Join-Path $cores 'sing-box.exe') | Out-Null

$binary = '"' + (Join-Path $Target 'TempestVPN.Service.exe') + '"'
if (-not $existing) {
    Write-Host 'Регистрирую службу'
    New-Service -Name $serviceName -BinaryPathName $binary -DisplayName $productName -StartupType Automatic `
        -Description 'VPN-клиент: TUN, маршруты, kill switch' | Out-Null
}
else {
    sc.exe config $serviceName binPath= $binary start= auto DisplayName= $productName | Out-Null
}
# Если служба упадёт, SCM перезапустит её; при старте она сама восстановит сеть.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Служба $productName запущена"
