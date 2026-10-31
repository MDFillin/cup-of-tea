# Установщик в тестовой ВМ (пункт H). В ВМ должна стоять прежняя установка PVpn с импортированными серверами.
#   1) обновление с PVpn: старая служба, правило, ярлык и папки сняты; данные, серверы и автозапуск перенесены;
#   2) установщик со старыми ядрами поверх: установленные (более новые) ядра не откатываются;
#   3) удаление: служба вернула сеть, всё снято, серверы пользователя остались;
#   4) чистая установка со старыми ядрами, затем обычная поверх: ядра обновились, прежние — в cores-previous;
#   5) подключение и e2e-check.
# Установщик со старыми ядрами (sing-box и Xray на версию ниже, официальные архивы) собирается заранее:
#   ISCC /DAppVersion=<версия> /DBundle=<комплект с этими ядрами в cores-bundled> /FCupOfTea-Setup-oldcores installer\CupOfTea.iss
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$OldCoresSetup,
    [string]$Profile = 'my-hy2',
    [string]$VpnIp = '203.0.113.10',
    [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui')
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$failures = 0
$setupDir = 'C:\PVpnSetup'
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 300) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Detail)
}

# Состояние ВМ строками «ключ=значение». В командах для ВМ — без двойных кавычек (см. vm.ps1).
$machineProbe = @(
    'function V($e, $p) { if (Test-Path $e) { $t = (& $e version 2>$null) -join '' ''; if ($t -match $p) { $Matches[1] } } }'
    '''svcOld='' + (Get-Service PVpn -ErrorAction SilentlyContinue).Status'
    '''svcNew='' + (Get-Service CupOfTea -ErrorAction SilentlyContinue).Status'
    '''dirOld='' + (Test-Path ''C:\Program Files\PVpn'')'
    '''dirNew='' + (Test-Path ''C:\Program Files\The Cup of Tea'')'
    '''dataOld='' + (Test-Path C:\ProgramData\PVpn)'
    '''dataNew='' + (Test-Path C:\ProgramData\CupOfTea)'
    '''logsNew='' + @(Get-ChildItem C:\ProgramData\CupOfTea\logs -ErrorAction SilentlyContinue).Count'
    '''fwOld='' + @(Get-NetFirewallRule -Group PVpn -ErrorAction SilentlyContinue).Count'
    '''fwNew='' + @(Get-NetFirewallRule -Group CupOfTea -ErrorAction SilentlyContinue).Count'
    '''lnkOld='' + (Test-Path ''C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PVpn.lnk'')'
    '''lnkNew='' + (Test-Path ''C:\ProgramData\Microsoft\Windows\Start Menu\Programs\The Cup of Tea.lnk'')'
    '''apps='' + (Get-ItemProperty ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{2C59343E-7C14-469F-B429-ABD7B09B9D63}_is1'' -ErrorAction SilentlyContinue).DisplayName'
    '''xray='' + (V ''C:\Program Files\The Cup of Tea\cores\xray.exe'' ''Xray (\S+)'')'
    '''singbox='' + (V ''C:\Program Files\The Cup of Tea\cores\sing-box.exe'' ''sing-box version (\S+)'')'
    '''prevXray='' + (V ''C:\Program Files\The Cup of Tea\cores-previous\xray.exe'' ''Xray (\S+)'')'
    '''bundled='' + (Test-Path ''C:\Program Files\The Cup of Tea\cores-bundled'')'
    '''adapters='' + @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object Name -match ''^(PVpn|CupOfTea)$'').Count'
    '''sids='' + ((Get-Content C:\ProgramData\CupOfTea\service.json -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json).allowedUserSids -join '','')'
    '''pvpnSid='' + (Get-LocalUser pvpn).SID.Value'
    '''log='' + ((Get-Content ''C:\Program Files\The Cup of Tea\install.log'' -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String ''^(Ядро|Найдена|Управлять)'').Line -join '' | '')'
) -join '; '
# От pvpn. Папки проверяются до вызова cupoftea-cli: он сам переносит данные из %APPDATA%\PVpn.
$userProbe = @(
    '$run = Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    '''roamOld='' + (Test-Path $env:APPDATA\PVpn)'
    '''roamNew='' + (Test-Path $env:APPDATA\CupOfTea\profiles.dat)'
    '''runOld='' + $run.PVpn'
    '''runNew='' + $run.CupOfTea'
    '''ip='' + ((curl.exe -4 -s -m 10 https://api.ipify.org) -join '''')'
    '''servers='' + $(if (Test-Path ''C:\Program Files\The Cup of Tea\cupoftea-cli.exe'') { @(& ''C:\Program Files\The Cup of Tea\cupoftea-cli.exe'' list | Select-String ''my-'').Count } else { ''нет cli'' })'
    '''roamOldAfterCli='' + (Test-Path $env:APPDATA\PVpn)'
) -join '; '

function Probe {
    $state = @{}
    foreach ($line in @(Guest $machineProbe) + @(Guest $userProbe -AsUser)) {
        if ($line -match '^(\w+)=(.*)$') { $state[$Matches[1]] = $Matches[2] }
    }
    Write-Host ('  ' + (($state.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; '))
    $state
}

function Install([string]$File) {
    $name = Split-Path $File -Leaf
    & $vm copy -Source $File -Dest $setupDir
    $code = (Guest ("Remove-Item $setupDir\setup.log -ErrorAction SilentlyContinue; " +
            "(Start-Process $setupDir\$name -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=$setupDir\setup.log' -Wait -PassThru).ExitCode") | Select-Object -Last 1)
    Check "установщик $name" ($code -eq '0') "код $code"
    Guest "Remove-Item $setupDir\$name -ErrorAction SilentlyContinue" | Out-Null
}

function Uninstall {
    Guest ("Start-Process 'C:\Program Files\The Cup of Tea\unins000.exe' -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait; " +
        "for (`$i = 0; `$i -lt 90 -and (Test-Path 'C:\Program Files\The Cup of Tea'); `$i++) { Start-Sleep 1 }") | Out-Null
}

Step 'Исходное состояние: PVpn с серверами; автозапуск PVpn включён'
Guest -AsUser "Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run PVpn 'C:\Program Files\PVpn\PVpn.exe --tray'" | Out-Null
$s = Probe
Check 'стоит PVpn' ($s.svcOld -eq 'Running' -and $s.dirOld -eq 'True') "служба $($s.svcOld)"

Step '1) Обновление с PVpn (ядра в комплекте те же)'
Install $Setup
$s = Probe
Check 'служба PVpn удалена, CupOfTea работает' ($s.svcOld -eq '' -and $s.svcNew -eq 'Running') "PVpn=$($s.svcOld) CupOfTea=$($s.svcNew)"
Check 'папки PVpn удалены' ($s.dirOld -eq 'False' -and $s.dataOld -eq 'False') "Program Files=$($s.dirOld) ProgramData=$($s.dataOld)"
Check 'журналы службы перенесены' ([int]$s.logsNew -gt 1) "файлов: $($s.logsNew)"
Check 'правило брандмауэра заменено' ($s.fwOld -eq '0' -and $s.fwNew -eq '1') "PVpn=$($s.fwOld) CupOfTea=$($s.fwNew)"
Check 'ярлык заменён' ($s.lnkOld -eq 'False' -and $s.lnkNew -eq 'True') "PVpn=$($s.lnkOld) Tempest=$($s.lnkNew)"
Check 'в «Приложениях»' ($s.apps -eq 'The Cup of Tea') $s.apps
Check 'ядра 26.10.21 / 1.14.4' ($s.xray -eq '26.10.21' -and $s.singbox -eq '1.14.4') "$($s.xray) / $($s.singbox)"
Check 'cores-bundled убран' ($s.bundled -eq 'False') $s.bundled
Check 'доступ к службе у pvpn' ($s.sids -like "*$($s.pvpnSid)*") $s.sids
Check 'серверы перенесены' ([int]$s.servers -ge 1 -and $s.roamOldAfterCli -eq 'False') "my-*: $($s.servers); PVpn до/после cli: $($s.roamOld)/$($s.roamOldAfterCli)"
Write-Host "  журнал: $($s.log)"

Step 'Запуск интерфейса: автозапуск переходит на новое имя'
& (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps 'start', 'wait:4', 'shot:h-upgraded' | Out-Null
$s = Probe
Check 'автозапуск CupOfTea' ($s.runOld -eq '' -and $s.runNew -like '*The Cup of Tea\CupOfTea.exe*--tray') "PVpn='$($s.runOld)' CupOfTea='$($s.runNew)'"
Guest -AsUser 'Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null

Step '2) Установщик со старыми ядрами поверх: установленные новее — остаются'
Install $OldCoresSetup
$s = Probe
Check 'ядра не откатились' ($s.xray -eq '26.10.21' -and $s.singbox -eq '1.14.4' -and $s.prevXray -eq '') "$($s.xray) / $($s.singbox), cores-previous: '$($s.prevXray)'"
Check 'служба работает' ($s.svcNew -eq 'Running') $s.svcNew
Write-Host "  журнал: $($s.log)"

Step '3) Удаление'
Uninstall
$s = Probe
Check 'служба и папки удалены' ($s.svcNew -eq '' -and $s.dirNew -eq 'False' -and $s.dataNew -eq 'False') "служба='$($s.svcNew)' Program Files=$($s.dirNew) ProgramData=$($s.dataNew)"
Check 'правило, ярлык, «Приложения», автозапуск' ($s.fwNew -eq '0' -and $s.lnkNew -eq 'False' -and $s.apps -eq '' -and $s.runNew -eq '') "fw=$($s.fwNew) lnk=$($s.lnkNew) apps='$($s.apps)' run='$($s.runNew)'"
Check 'сеть: адаптера нет, интернет напрямую' ($s.adapters -eq '0' -and $s.ip -match '^\d+\.\d+\.\d+\.\d+$' -and $s.ip -ne $VpnIp) "адаптеров $($s.adapters), IP $($s.ip)"
Check 'серверы пользователя остались' ($s.roamNew -eq 'True') $s.roamNew

Step '4) Чистая установка со старыми ядрами, затем обычная поверх'
Install $OldCoresSetup
$s = Probe
Check 'ядра из комплекта' ($s.xray -eq '26.3.27' -and $s.singbox -eq '1.14.3') "$($s.xray) / $($s.singbox)"
Install $Setup
$s = Probe
Check 'ядра обновились, прежнее — в cores-previous' ($s.xray -eq '26.10.21' -and $s.singbox -eq '1.14.4' -and $s.prevXray -eq '26.3.27') "$($s.xray) / $($s.singbox), cores-previous: $($s.prevXray)"
Check 'серверы на месте' ([int]$s.servers -ge 1) $s.servers
Write-Host "  журнал: $($s.log)"

Step "5) Подключение $Profile и e2e-check"
$out = @(Guest -AsUser -TimeoutSec 400 ("$cli svc connect $Profile --test 150; & 'C:\Program Files\The Cup of Tea\e2e-check.ps1'; `$e = `$LASTEXITCODE; " +
        "$cli svc disconnect | Out-Null; 'e2e=' + `$e"))
$out | Where-Object { $_ -match 'FAIL|Подключено|e2e=' } | ForEach-Object { Write-Host "  $_" }
$e2e = ($out | Where-Object { $_ -match '^e2e=' } | Select-Object -Last 1) -replace '^e2e=', ''
Check 'e2e-check' ($e2e -eq '0') "провалено: $e2e"

Write-Host "`nПровалено проверок: $failures"
exit $failures
