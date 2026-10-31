# Этап 4 в тестовой ВМ: падения ядер, смена сети и падение службы не должны давать утечек мимо туннеля.
# Наблюдатель в ВМ каждые ~0,5 с спрашивает внешний IP. Пока подключение должно защищать,
# допустимы только «203.0.113.10» и «-» (нет ответа). Любой другой адрес — утечка.
# После падения службы Recovery по договорённости возвращает прямой интернет — это уже не утечка.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки в ВМ).
[CmdletBinding()]
param(
    [string]$Profile = 'my-hy2',
    [string]$ExpectedIp = '203.0.113.10'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$vbm = Join-Path (Get-ItemProperty 'HKLM:\SOFTWARE\Oracle\VirtualBox').InstallDir 'VBoxManage.exe'
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$log = 'C:\PVpnSetup\leak.log'
$events = [System.Collections.Generic.List[object]]::new()

# Метка события пишется прямо в журнал наблюдателя в ВМ: разбор идёт по порядку строк, а не по часам
# (после паузы ВМ её часы отстают от хоста, сравнивать время нельзя).
function Mark([string]$Text) {
    $events.Add($Text)
    Write-Host "`n=== $(Get-Date -Format HH:mm:ss) $Text" -ForegroundColor Cyan
    if (Test-Path variable:monitorStarted) { & $vm run -TimeoutSec 60 -Command "Add-Content $log 'MARK $($events.Count - 1)'" }
}
function Guest([string]$Command, [switch]$AsUser) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec 120 }
function Status { Guest "$cli svc status; $cli killswitch" }

Guest "Remove-Item $log -ErrorAction SilentlyContinue"
Write-Host "`n=== $(Get-Date -Format HH:mm:ss) Подключение $Profile" -ForegroundColor Cyan
Guest "$cli svc connect $Profile --test 900" -AsUser

# Наблюдатель: ~4 минуты работы ВМ, пишет «время IP» (время — по часам ВМ, они синхронизированы с хостом).
& $vm run -NoWait -TimeoutSec 900 -Command ("`$end = (Get-Date).AddSeconds(360); while ((Get-Date) -lt `$end) { " +
    "`$ip = (curl.exe -s -m 2 https://api.ipify.org) -join ''; if (-not `$ip) { `$ip = '-' }; " +
    "Add-Content $log ((Get-Date -Format o) + ' ' + `$ip); Start-Sleep -Milliseconds 300 }")
$monitorStarted = $true
Start-Sleep 10

Mark 'Убиваю sing-box'
Guest 'Stop-Process -Name sing-box -Force'
Start-Sleep 20
Status

Mark 'Убиваю xray'
Guest 'Stop-Process -Name xray -Force'
Start-Sleep 15
Status

Mark 'Сеть ВМ: кабель отключён'
& $vbm controlvm PVpn-Test-Win11 setlinkstate1 off
Start-Sleep 10
Mark 'Сеть ВМ: кабель подключён'
& $vbm controlvm PVpn-Test-Win11 setlinkstate1 on
Start-Sleep 30
Status

# Сна в ВМ нет (powercfg /a: ни S3, ни S0): приостановка ВМ даёт то же для гостя — скачок времени и протухшие соединения.
Mark 'ВМ приостановлена на 90 с (вместо сна)'
& $vbm controlvm PVpn-Test-Win11 pause
Start-Sleep 90
& $vbm controlvm PVpn-Test-Win11 resume
Mark 'ВМ продолжила работу'
Start-Sleep 40
Status

Mark 'Убиваю службу CupOfTea'
Guest 'Stop-Process -Name CupOfTea.Service -Force'
Start-Sleep 25
Status

Mark 'Конец наблюдения'
Start-Sleep 5
$lines = @(Guest "Get-Content $log")

# Строки журнала по порядку: «MARK n» — событие, иначе «время IP». Окно события — до следующей метки.
$windows = @{ -1 = [System.Collections.Generic.List[object]]::new() }
$current = -1
foreach ($l in $lines) {
    $text = "$l".Trim()
    if ($text -match '^MARK (\d+)$') { $current = [int]$Matches[1]; $windows[$current] = [System.Collections.Generic.List[object]]::new(); continue }
    $parts = $text -split ' ', 2
    if ($parts.Count -lt 2) { continue }
    $windows[$current].Add([pscustomobject]@{ Time = [datetime]::Parse($parts[0]); Ip = $parts[1] })
}
$serviceKilled = $events.IndexOf('Убиваю службу CupOfTea')
$protected = @($windows.Keys | Where-Object { $_ -lt $serviceKilled } | ForEach-Object { $windows[$_] })
$leaks = @($protected | Where-Object { $_.Ip -ne $ExpectedIp -and $_.Ip -ne '-' })

Write-Host "`nНаблюдений под защитой: $($protected.Count): через VPN $(@($protected | Where-Object Ip -eq $ExpectedIp).Count), без ответа $(@($protected | Where-Object Ip -eq '-').Count), утечек $($leaks.Count)"
foreach ($i in ($windows.Keys | Sort-Object)) {
    $name = if ($i -lt 0) { 'После подключения' } else { $events[$i] }
    $window = $windows[$i]
    $summary = ($window | Group-Object Ip | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', '
    $firstOk = $window | Where-Object Ip -eq $ExpectedIp | Select-Object -First 1
    $firstFail = $window | Where-Object Ip -ne $ExpectedIp | Select-Object -First 1
    $back = if ($firstFail -and $firstOk -and $firstOk.Time -gt $firstFail.Time) { ", VPN снова через {0:N1} с" -f ($firstOk.Time - $firstFail.Time).TotalSeconds } else { '' }
    Write-Host "$name`: $summary$back"
}
if ($leaks.Count -gt 0) {
    Write-Host 'УТЕЧКИ:' -ForegroundColor Red
    $leaks | ForEach-Object { Write-Host ("  {0:HH:mm:ss.fff} {1}" -f $_.Time, $_.Ip) }
}
exit $leaks.Count
