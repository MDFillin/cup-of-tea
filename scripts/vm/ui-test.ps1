# Проверка интерфейса The Cup of Tea в тестовой ВМ: запуск CupOfTea.exe в сеансе pvpn, действия через UI Automation
# (guest\ui-action.ps1 через задачи планировщика — guestcontrol не видит рабочий стол), снимки экрана ВМ.
# Перед запуском: deploy-and-test.ps1 (служба, интерфейс и ссылки в ВМ).
#   ui-test.ps1 -Steps 'start','select:my-hy2','connect','wait:15','dump','shot:connected','tab:4','shot:logs','tab:0','connect'
#   Шаги: start, dump, connect, check, proxy, tun, select:<сервер>, tab:<номер страницы 0–4>, open:<страница настроек>, wait:<с>, shot:<имя>,
#         splitOn, onlyMode, excludeMode, apply, domain:<сайт>, running:<часть пути программы>
#   Вкладки: 0 — подключение, 1 — раздельное туннелирование, 2 — логи, 3 — настройки.
[CmdletBinding()]
param(
    [string[]]$Steps = @('start', 'dump', 'shot:start'),
    [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui')
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
New-Item -ItemType Directory -Force $ShotDir | Out-Null

& $vm copy -Source (Join-Path $PSScriptRoot 'guest\ui-action.ps1') -Dest C:\PVpnSetup

# Задача от имени pvpn в его интерактивном сеансе (пароль не нужен: пользователь уже вошёл).
function Start-AsUser([string]$Name, [string]$Exe, [string]$Arguments) {
    & $vm run -TimeoutSec 60 -Command ("`$a = New-ScheduledTaskAction -Execute '$Exe'" + $(if ($Arguments) { " -Argument '$Arguments'" } else { '' }) + "; " +
        "`$p = New-ScheduledTaskPrincipal -UserId pvpn -LogonType Interactive -RunLevel Limited; " +
        "Register-ScheduledTask -TaskName $Name -Action `$a -Principal `$p -Force | Out-Null; Start-ScheduledTask -TaskName $Name; " +
        "`$t = Get-Date; while ((Get-ScheduledTask -TaskName $Name).State -eq 'Running' -and ((Get-Date) - `$t).TotalSeconds -lt 90) { Start-Sleep -Milliseconds 300 }")
}

foreach ($step in $Steps) {
    $kind, $arg = $step -split ':', 2
    Write-Host "=== $step" -ForegroundColor Cyan
    switch ($kind) {
        'start' {
            # Экран ВМ не гаснет, иначе снимок не сделать.
            & $vm run -TimeoutSec 60 -Command 'powercfg /change monitor-timeout-ac 0; powercfg /change standby-timeout-ac 0; Remove-Item C:\PVpnSetup\ui.log -ErrorAction SilentlyContinue'
            & $vm run -TimeoutSec 60 -Command "Register-ScheduledTask -TaskName PVpnApp -Force -Action (New-ScheduledTaskAction -Execute 'C:\Program Files\The Cup of Tea\CupOfTea.exe') -Principal (New-ScheduledTaskPrincipal -UserId pvpn -LogonType Interactive -RunLevel Limited) | Out-Null; Start-ScheduledTask -TaskName PVpnApp"
            Start-Sleep 6
        }
        'wait' { Start-Sleep ([int]$arg) }
        'shot' {
            $file = Join-Path $ShotDir "$arg.png"
            & $vbm controlvm PVpn-Test-Win11 screenshotpng $file
            Write-Host "снимок: $file"
        }
        'select' { Start-AsUser PVpnUi 'powershell.exe' "-NoProfile -File C:\PVpnSetup\ui-action.ps1 -Action select -Server $arg" }
        'tab' { Start-AsUser PVpnUi 'powershell.exe' "-NoProfile -File C:\PVpnSetup\ui-action.ps1 -Action tab -Tab $arg" }
        { $_ -in 'domain', 'running', 'subAdd', 'paste', 'edit', 'dblclick', 'open' } { Start-AsUser PVpnUi 'powershell.exe' "-NoProfile -File C:\PVpnSetup\ui-action.ps1 -Action $kind -Server $arg" }
        default { Start-AsUser PVpnUi 'powershell.exe' "-NoProfile -File C:\PVpnSetup\ui-action.ps1 -Action $kind" }
    }
}
Write-Host '=== журнал UI'
& $vm run -TimeoutSec 60 -Command 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 -ErrorAction SilentlyContinue'
