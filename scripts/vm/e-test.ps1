# Удобство (пункт E, функции) в тестовой ВМ через интерфейс:
#   1) скорость и трафик за сеанс в TUN и в режиме прокси (во время загрузки файла);
#   2) горячая клавиша Ctrl+Alt+P подключает и отключает;
#   3) QR сервера → снимок в буфер обмена → «Вставить ссылки» читает код и находит тот же сервер;
#   4) правка сервера: новое имя видно в списке и в CLI;
#   5) «Сохранить диагностику»: архив на рабочем столе без ключей.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [string]$ShotDir = (Join-Path $env:TEMP 'pvpn-ui'))
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"
$failures = 0

function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}
function Ui([string[]]$Steps) { & (Join-Path $PSScriptRoot 'ui-test.ps1') -ShotDir $ShotDir -Steps $Steps | Out-Null }
# Последний снимок текстов окна из журнала UI.
function LastDump {
    $log = @(Guest -AsUser 'Get-Content C:\PVpnSetup\ui.log -Encoding UTF8')
    $start = ($log | Select-String '^--- ' | Select-Object -Last 1).LineNumber
    ($log[($start - 1)..($log.Count - 1)]) -join "`n"
}
# 30 МБ через VPN (в TUN — напрямую, в режиме прокси — через HTTP-вход), затем снимок окна.
function Download([string]$Extra = '') {
    $r = (Guest -AsUser "curl.exe -s -o NUL -m 60 $Extra -w '%{size_download}' 'https://speed.cloudflare.com/__down?bytes=30000000'") -join ''
    Write-Host "      скачано байт: $r"
}
# «За сеанс»: ↓ не меньше 25 МБ, ↑ меньше 1 МБ (счётчики не путают направления).
function TrafficOk([string]$Dump) {
    if ($Dump -cnotmatch '↓ ([\d,]+) МБ\s+↑ ([\d,]+) (Б|КБ)') { return $false }
    [double]::Parse($Matches[1].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) -ge 25
}
function State { ((Guest -AsUser "$cli svc status") -join ' ') }

Guest -AsUser ('Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item C:\PVpnSetup\ui.log, C:\Users\pvpn\AppData\Roaming\TempestVPN\settings.json -ErrorAction SilentlyContinue; ' +
    'Remove-Item C:\Users\pvpn\Desktop\Tempest-VPN-*.zip -ErrorAction SilentlyContinue') | Out-Null

Step 'Скорость и трафик: TUN, загрузка 30 МБ'
Ui 'start', 'tun', 'select:my-xhttp', 'connect', 'wait:12'
Download
Ui 'dump', 'shot:e-speed-tun'
$dump = LastDump
$line = ($dump -split "`n" | Select-String '↓' | ForEach-Object { $_.Line.Trim() }) -join ' | '
Result 'TUN: трафик за сеанс' (TrafficOk $dump) $line


Step 'Горячая клавиша Ctrl+Alt+P (окно не на переднем плане)'
Ui 'hotkey', 'wait:6', 'shot:e-hotkey-off'
$s = State
Result 'отключила' ($s -match 'Disconnected') ($s -replace '\s+', ' ')
Ui 'hotkey', 'wait:12', 'shot:e-hotkey-on'
$s = State
Result 'подключила' ($s -match 'Connected' -and $s -match [regex]::Escape($VpnIp)) (($s -replace '\s+', ' ').Substring(0, [Math]::Min(120, $s.Length)))
Ui 'connect', 'wait:5'

Step 'Скорость и трафик: режим прокси'
Ui 'proxy', 'connect', 'wait:6'
Download '-x http://127.0.0.1:10829'
Ui 'dump', 'shot:e-speed-proxy'
$dump = LastDump
$line = ($dump -split "`n" | Select-String '↓' | ForEach-Object { $_.Line.Trim() }) -join ' | '
Result 'прокси: трафик за сеанс' (TrafficOk $dump) $line
Ui 'connect', 'wait:3', 'tun'

Step 'QR: показать сервер кодом, прочитать код из буфера обмена'
Ui 'select:my-hy2', 'qrShow', 'shot:e-qr', 'qrToClipboard', 'pasteClipboard', 'dump'
$dump = LastDump
Result 'код прочитан, сервер найден' ($dump -match 'Серверов: новых 0, обновлено 1') (($dump -split "`n" | Select-String 'Серверов:|QR') -join ' ')

Step 'Правка сервера: новое имя'
Ui 'select:my-hy2', 'edit:my-hy2-renamed', 'dump'
$list = (Guest -AsUser "$cli list") -join "`n"
Result 'переименован' ($list -match 'my-hy2-renamed' -and (LastDump) -match 'my-hy2-renamed') ''
Ui 'select:my-hy2-renamed', 'edit:my-hy2'

Step 'Сохранить диагностику'
Ui 'diag'
$check = (Guest -AsUser ("`$z = Get-ChildItem C:\Users\pvpn\Desktop\Tempest-VPN-*.zip | Select-Object -First 1; if (-not `$z) { 'НЕТ АРХИВА'; return }; " +
    "Add-Type -AssemblyName System.IO.Compression.FileSystem; `$a = [IO.Compression.ZipFile]::OpenRead(`$z.FullName); " +
    "`$text = (`$a.Entries | ForEach-Object { (New-Object IO.StreamReader (`$_.Open())).ReadToEnd() }) -join ' '; 'файлы: ' + ((`$a.Entries | ForEach-Object Name) -join ', '); `$a.Dispose(); " +
    "`$secrets = @($cli export) | ForEach-Object { if (`$_ -match '://([^@]+)@') { `$Matches[1] -split ':' } } | Where-Object { `$_.Length -ge 4 }; " +
    "'секретов проверено: ' + `$secrets.Count; 'найдено секретов: ' + @(`$secrets | Where-Object { `$text.Contains(`$_) }).Count; " +
    "'ссылок: ' + ([regex]::Matches(`$text, '(vless|hy2)://[^*]')).Count")) -join "`n"
Write-Host ($check -replace '(?m)^', '    ')
Result 'архив без ключей' ($check -match 'service-log.txt' -and $check -match 'найдено секретов: 0' -and $check -match 'ссылок: 0' -and $check -notmatch 'секретов проверено: 0') ''

Guest -AsUser 'Get-Process TempestVPN -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null
Guest -AsUser "$cli svc disconnect" | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
