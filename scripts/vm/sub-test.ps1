# Подписки (пункт D) в тестовой ВМ. Панель — guest\sub-server.ps1 на 127.0.0.1 в самой ВМ: она отдаёт
# ссылки my-*, которые уже есть в ВМ, под именами sub-*, так что ключи не покидают ВМ.
#   1) sub add: base64-список, трафик и срок из subscription-userinfo, название из profile-title;
#   2) подключение (TUN) к серверу из подписки;
#   3) обновление: сервер пропал, сервер добавился, у оставшихся Id прежний (выбор не теряется);
#   4) ошибки панели (403, веб-страница): серверы остаются, ошибка видна в списке;
#   5) интерфейс: подписка в настройках и её название у серверов;
#   6) sub remove убирает серверы подписки, ручные остаются.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$dir = 'C:\PVpnSetup\sub'
$url = '<subscription-url-removed>'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"
$failures = 0

# Запускать без перенаправления *>: в PowerShell 5.1 stderr из ВМ тогда становится ошибкой и прерывает vm.ps1.
function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function PvpnCli([string]$Arguments) { ((Guest -AsUser "$cli $Arguments 2>&1 | ForEach-Object { `$_.ToString() }; 'EXIT=' + `$LASTEXITCODE") -join "`n").Trim() }
function Mode([string]$Mode) { Guest "Set-Content $dir\mode.txt $Mode" | Out-Null }
# Список ссылок панели: my-* из ВМ под именами sub-*; -Skip — без этих серверов, -Extra — ещё ссылки.
function Body([string[]]$Skip = @(), [string]$Extra = '') {
    $skipRe = if ($Skip) { '(' + ($Skip -join '|') + ')$' } else { '^$' }
    Guest -AsUser ("`$l = @($cli export --filter my-) -match '^(vless|hy2|hysteria2)://' | ForEach-Object { `$_ -replace '#.*(my-[A-Za-z0-9-]+)\s*$', '#sub-`$1' } | " +
        "Where-Object { `$_ -notmatch '$skipRe' }; `$l = @(`$l) + @('$Extra' -split ' ' | Where-Object { `$_ }); " +
        "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(`$l -join [char]10)) | Set-Content $dir\body.txt; 'ссылок: ' + `$l.Count")
}

Step 'Панель подписки в ВМ'
$local = Join-Path $env:TEMP ('pvpn-sub-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $local | Out-Null
try {
    $expire = [DateTimeOffset]::UtcNow.AddDays(30).ToUnixTimeSeconds()
    $title = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Тестовая панель'))
    @("subscription-userinfo: upload=1073741824; download=2147483648; total=10737418240; expire=$expire",
        "profile-title: base64:$title", 'profile-update-interval: 6', '') -join "`r`n" | Set-Content (Join-Path $local 'headers.txt') -Encoding ascii -NoNewline
    Guest "Get-ScheduledTask PVpnSub -ErrorAction SilentlyContinue | Stop-ScheduledTask; Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $dir | Out-Null; icacls $dir /grant pvpn:M | Out-Null"
    & $vm copy -Source (Join-Path $local 'headers.txt') -Dest $dir
    & $vm copy -Source (Join-Path $PSScriptRoot 'guest\sub-server.ps1') -Dest C:\PVpnSetup
}
finally { Remove-Item $local -Recurse -Force }
Mode 'ok'
Body | ForEach-Object { "  $_" }
Guest ("Register-ScheduledTask -TaskName PVpnSub -Force -Action (New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\PVpnSetup\sub-server.ps1') " +
    "-Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount) | Out-Null; Start-ScheduledTask -TaskName PVpnSub; Start-Sleep 3") | Out-Null
PvpnCli 'sub remove --all' | Out-Null
Guest -AsUser "@($cli list) -match 'sub-' | ForEach-Object { $cli remove (`$_.Trim() -split '\s+')[0] } | Out-Null" | Out-Null

Step 'sub add'
$out = PvpnCli "sub add $url --name sub-test"
Result 'добавлена' ($out -match 'серверов новых [1-9]' -and $out -match 'EXIT=0') ($out -replace '\s+', ' ')
$list = PvpnCli 'sub list'
Result 'трафик и срок' ($list -match 'трафик 3 из 10 ГБ' -and $list -match 'раз в 6 ч' -and $list -notmatch 'бессрочно') ($list -replace '\s+', ' ')
$servers = PvpnCli 'list'
$count = ([regex]::Matches($servers, 'sub-my-')).Count
Result 'серверы в списке' ($count -ge 2) "серверов sub-*: $count"

Step 'Подключение к серверу из подписки (TUN)'
$c = (Guest -AsUser -TimeoutSec 150 "$cli svc connect sub-my-xhttp --test 300; $cli svc status") -join "`n"
Result 'sub-my-xhttp' ($c -match 'Состояние: Connected' -and $c -match "внешний IP: $([regex]::Escape($VpnIp))") (($c -split "`n" | Select-String 'Состояние|внешний IP') -join ' ')

Step 'Обновление: sub-my-hy2 пропал, добавился сервер; обновление при включённом VPN'
Body -Skip 'my-hy2' -Extra '<server-link-removed>' | ForEach-Object { "  $_" }
$out = PvpnCli 'sub update sub-test'
Result 'обновлена' ($out -match 'новых 1' -and $out -match 'удалено 1' -and $out -match 'EXIT=0') ($out -replace '\s+', ' ')
$servers = PvpnCli 'list'
Result 'состав' ($servers -match 'sub-extra' -and $servers -notmatch 'sub-my-hy2' -and $servers -match 'sub-my-xhttp') ''
Guest -AsUser "$cli svc disconnect" | Out-Null

Step 'Ошибки панели'
Mode '403'
$out = PvpnCli 'sub update sub-test'
$list = PvpnCli 'sub list'
Result '403' ($out -match '403' -and $out -match 'EXIT=1' -and $list -match 'ошибка: Панель подписки ответила 403' -and (PvpnCli 'list') -match 'sub-extra') ($out -replace '\s+', ' ')
Mode 'html'
$out = PvpnCli 'sub update sub-test'
Result 'веб-страница' ($out -match 'веб-страница') ($out -replace '\s+', ' ')
Mode 'ok'
$out = PvpnCli 'sub update'
Result 'снова работает' ($out -match 'обновлено' -and (PvpnCli 'sub list') -notmatch 'ошибка') ($out -replace '\s+', ' ')

Step 'Интерфейс: подписка в настройках, название у серверов'
Guest -AsUser 'Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item C:\PVpnSetup\ui.log -ErrorAction SilentlyContinue' | Out-Null
& (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir (Join-Path $env:TEMP 'pvpn-ui') -Steps 'start', 'wait:2', 'tab:1', 'dump', 'shot:sub-servers', 'open:subs', 'dump', 'shot:sub-settings' | Out-Null
$ui = (Guest -AsUser "Get-Content C:\PVpnSetup\ui.log -Encoding UTF8") -join "`n"
Result 'серверы в группе подписки' ($ui -match '(?m)^\s+sub-test\s*$') ''
Result 'подписка в настройках' ($ui -match 'трафик 3 из 10 ГБ' -and $ui -match 'серверов \d+') ''

function App([string[]]$Steps) { & (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir (Join-Path $env:TEMP 'pvpn-ui') -Steps $Steps | Out-Null }
function StopApp { Guest -AsUser 'Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1' | Out-Null }

Step 'Автообновление в программе: подписке пора обновиться, панель убрала sub-my-vision'
StopApp
Body -Skip 'my-vision' | ForEach-Object { "  $_" }
# Последняя попытка — «давно»: правим зашифрованный profiles.dat от имени pvpn (DPAPI).
Guest -AsUser ("Add-Type -AssemblyName System.Security; `$f = 'C:\Users\pvpn\AppData\Roaming\TempestVPN\profiles.dat'; " +
    "`$e = [Text.Encoding]::UTF8.GetBytes('PVpn.profiles.v1'); " +
    "`$j = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes(`$f), `$e, 'CurrentUser')); " +
    "`$j = `$j -replace '(LastAttempt.: .)[0-9T:.+-]+', '`${1}2020-01-01T00:00:00+00:00'; " +
    "[IO.File]::WriteAllBytes(`$f, [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes(`$j), `$e, 'CurrentUser'))") | Out-Null
$requests = @(Guest "Get-Content $dir\requests.log").Count
App 'start', 'wait:12'
$newRequests = @(Guest "Get-Content $dir\requests.log").Count - $requests
StopApp
$servers = PvpnCli 'list'
Result 'обновилась сама' ($newRequests -ge 1 -and $servers -notmatch 'sub-my-vision' -and $servers -match 'sub-my-xhttp') "запросов к панели: $newRequests"

Step 'Добавление в интерфейсе: поле в настройках'
PvpnCli 'sub remove --all' | Out-Null
Body | Out-Null
App 'start', "subAdd:$url", 'shot:sub-ui-add'
StopApp
$list = PvpnCli 'sub list'
Result 'кнопка «Добавить»' ($list -match 'Тестовая панель: серверов 3') ($list -replace '\s+', ' ')

Step 'Добавление в интерфейсе: ссылка подписки в буфере и «Вставить ссылки»'
App 'start', "paste:$url/second", 'open:subs', 'shot:sub-ui-paste'
StopApp
$list = PvpnCli 'sub list'
Result 'буфер обмена' ($list -match 'Тестовая панель \(2\): серверов 3') ($list -replace '\s+', ' ')
PvpnCli 'sub remove --all' | Out-Null
PvpnCli "sub add $url --name sub-test" | Out-Null

Step 'sub remove'
$out = PvpnCli 'sub remove sub-test'
$servers = PvpnCli 'list'
Result 'серверы подписки удалены, ручные остались' ($out -match 'EXIT=0' -and $servers -notmatch 'sub-' -and $servers -match 'my-xhttp') ''

Guest "Stop-ScheduledTask PVpnSub; Unregister-ScheduledTask PVpnSub -Confirm:`$false; Remove-Item $dir -Recurse -Force; Remove-Item C:\PVpnSetup\sub-server.ps1" | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
