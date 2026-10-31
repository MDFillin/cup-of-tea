# Обновление ядер из программы (пункт G) в тестовой ВМ на настоящих релизах GitHub:
#   1) в ВМ ставятся ядра старше текущих (официальные архивы, SHA256 сверяется) — как будто программа давно не обновлялась;
#   2) «проверить» видит новые версии, «обновить» при включённом VPN: подключение не рвётся;
#   3) переподключение — уже на новых ядрах; прежние лежат в cores-previous;
#   4) «откатить» возвращает прежние, повторное «обновить» — снова новые;
#   5) пользователь не может писать ни в cores, ни в cores-previous (ядра запускаются от имени системы);
#   6) раздел «Ядра» в интерфейсе.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param(
    [string]$VpnIp = '203.0.113.10',
    [string]$OldXray = '26.9.30', [string]$OldXraySha = 'b17a619343c11b89d8faf36278298856749b15c52277d875d32051b33dcda617',
    [string]$OldSingBox = '1.14.3', [string]$OldSingBoxSha = 'd8b86ef58936dba9230ec6a57ffa4243933f3c14ef4809828b6c553440ed1518',
    [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui')
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$coresDir = 'C:\Program Files\The Cup of Tea\cores'
$failures = 0

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function Cores([string]$Action = 'status') { ((Guest -AsUser -TimeoutSec 600 "$cli svc cores $Action 2>&1 | ForEach-Object { `$_.ToString() }") -join ' | ').Trim() }
function Ip { ((Guest -AsUser "(& curl.exe -4 -s -m 10 https://api.ipify.org) -join ''") -join '').Trim() }

Step "Старые ядра: xray $OldXray, sing-box $OldSingBox (с GitHub, SHA256 сверяется)"
Guest -AsUser "Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force; $cli svc disconnect" | Out-Null
$install = (Guest -TimeoutSec 600 (
    "`$ProgressPreference = 'SilentlyContinue'; `$w = 'C:\PVpnSetup\oldcores'; Remove-Item `$w -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory `$w | Out-Null; " +
    "Remove-Item 'C:\Program Files\The Cup of Tea\cores-previous' -Recurse -Force -ErrorAction SilentlyContinue; " +
    "foreach (`$c in @(@('xray.exe', 'https://github.com/XTLS/Xray-core/releases/download/v$OldXray/Xray-windows-64.zip', '$OldXraySha'), " +
    "@('sing-box.exe', 'https://github.com/SagerNet/sing-box/releases/download/v$OldSingBox/sing-box-$OldSingBox-windows-amd64.zip', '$OldSingBoxSha'))) { " +
    "  `$zip = Join-Path `$w (`$c[0] + '.zip'); Invoke-WebRequest `$c[1] -OutFile `$zip -UseBasicParsing; " +
    "  if ((Get-FileHash `$zip -Algorithm SHA256).Hash.ToLower() -ne `$c[2]) { throw ('SHA256 ' + `$c[0]) }; " +
    "  Expand-Archive `$zip (Join-Path `$w `$c[0]); `$exe = Get-ChildItem (Join-Path `$w `$c[0]) -Recurse -Filter `$c[0] | Select-Object -First 1; " +
    "  Copy-Item `$exe.FullName (Join-Path '$coresDir' `$c[0]) -Force }; Remove-Item `$w -Recurse -Force; 'установлено'")) -join ' '
$s = Cores
Result 'старые версии стоят' ($s -match "xray: $([regex]::Escape($OldXray))" -and $s -match "sing-box: $([regex]::Escape($OldSingBox))") "$install; $s"

Step 'Проверить обновления'
$s = Cores check
Result 'видны новые версии' ($s -match 'xray: .*\(есть обновление\)' -and $s -match 'sing-box: .*\(есть обновление\)') $s
$latestXray = if ($s -match 'xray: [\d.]+, на GitHub ([\d.]+)') { $Matches[1] } else { '?' }
$latestSingBox = if ($s -match 'sing-box: [\d.]+, на GitHub ([\d.]+)') { $Matches[1] } else { '?' }

Step 'Подключение на старых ядрах'
$c = (Guest -AsUser -TimeoutSec 150 "$cli svc connect my-xhttp --test 600") -join ' '
$ipBefore = Ip
Result 'подключено' ($c -match 'Состояние: Connected' -and $ipBefore -eq $VpnIp) "IP $ipBefore"

Step 'Обновить при включённом VPN'
$s = Cores update
$ipDuring = Ip
$st = (Guest -AsUser "$cli svc status") -join ' '
Result 'обновлено' ($s -match "xray: $([regex]::Escape($latestXray))," -and $s -match "sing-box: $([regex]::Escape($latestSingBox))," -and $s -match "для отката $([regex]::Escape($OldXray))" -and $s -match 'следующем подключении') $s
Result 'подключение не прервалось' ($st -match 'Состояние: Connected' -and $ipDuring -eq $VpnIp) "IP $ipDuring"

Step 'Переподключение — на новых ядрах'
Guest -AsUser "$cli svc disconnect" | Out-Null
$c = (Guest -AsUser -TimeoutSec 150 "$cli svc connect my-xhttp --test 600") -join ' '
$ip = Ip
$running = ((Guest "Get-Process xray, sing-box -ErrorAction SilentlyContinue | ForEach-Object { `$_.Name + ' ' + `$_.Path }") -join '; ')
Result 'подключено на новых' ($c -match 'Состояние: Connected' -and $ip -eq $VpnIp -and $running -match 'cores\\xray.exe') "IP $ip; $running"
Guest -AsUser "$cli svc disconnect" | Out-Null

Step 'Права: пользователь не может подменить ядро'
$acl = ((Guest -AsUser ("foreach (`$p in '$coresDir\xray.exe', 'C:\Program Files\The Cup of Tea\cores-previous\xray.exe', 'C:\Program Files\The Cup of Tea\cores-previous\evil.exe') { " +
    "try { [IO.File]::OpenWrite(`$p).Close(); 'ЗАПИСАЛ ' + `$p } catch { 'отказано ' + (Split-Path `$p -Leaf) } }")) -join '; ')
Result 'запись запрещена' ($acl -notmatch 'ЗАПИСАЛ') $acl

Step 'Откатить и обновить снова'
$s = Cores rollback
Result 'откат' ($s -match "xray: $([regex]::Escape($OldXray))," -and $s -match "sing-box: $([regex]::Escape($OldSingBox))," -and $s -match "для отката $([regex]::Escape($latestXray))") $s
$s = Cores update
Result 'снова новые' ($s -match "xray: $([regex]::Escape($latestXray))," -and $s -match "sing-box: $([regex]::Escape($latestSingBox)),") $s
$s = Cores update
Result 'повторно — «последняя версия»' ($s -match 'последняя версия' -and $s -notmatch 'Ошибка') $s

Step 'Интерфейс: раздел «Ядра»'
& (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps 'start', 'wait:4', 'open:cores', 'dump', 'shot:g-cores' | Out-Null
$log = (Guest -AsUser 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8 | Select-Object -Last 150') -join "`n"
Result 'версии видны' ($log -match "xray $([regex]::Escape($latestXray))" -and $log -match "sing-box $([regex]::Escape($latestSingBox))") ''
Guest -AsUser 'Get-Process CupOfTea -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null

Write-Host "`nПровалено проверок: $failures"
exit $failures
