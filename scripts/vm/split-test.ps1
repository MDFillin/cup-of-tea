# Раздельное туннелирование в тестовой ВМ (через tempest-cli, без интерфейса).
# «Программа» — копия curl.exe в C:\SplitTest\app-1.0.1 (как Discord в папке версии), правило — по папке C:\SplitTest.
# Перед запуском: deploy-and-test.ps1 (служба и ссылки в ВМ).
[CmdletBinding()]
param(
    [string]$Profile = 'my-hy2',
    [string]$VpnIp = '203.0.113.10'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$cli = "& 'C:\Program Files\Tempest VPN\tempest-cli.exe'"
$failures = 0

function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Guest([string]$Command, [switch]$AsUser) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec 180 }

# Внешний IP, как его видит данная программа (curl), и сайт для проверки правила по домену.
function Check([string]$Name, [string]$Exe, [string]$Url, [string]$Expect) {
    $ip = ((Guest "(& '$Exe' -4 -s -m 10 $Url) -join ''" -AsUser) -join '').Trim()
    $ok = if ($Expect -eq 'vpn') { $ip -eq $VpnIp } else { $ip -match '^\d+\.\d+\.\d+\.\d+$' -and $ip -ne $VpnIp }
    if (-not $ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2} (ожидалось: {3})" -f $(if ($ok) { 'OK' } else { 'FAIL' }), $Name, $ip, $(if ($Expect -eq 'vpn') { 'VPN' } else { 'напрямую' }))
}

Step 'Подготовка: «программа» в папке версии и правила'
Guest ("New-Item -ItemType Directory -Force C:\SplitTest\app-1.0.1 | Out-Null; Copy-Item C:\Windows\System32\curl.exe C:\SplitTest\app-1.0.1\curl.exe -Force; " +
    "Remove-Item C:\SplitTest\app-1.0.2 -Recurse -Force -ErrorAction SilentlyContinue; " +
    "foreach (`$m in 'ExcludeSelected','OnlySelected') { @{ mode = `$m; apps = @(@{ path = 'C:\SplitTest'; isFolder = `$true }); domains = @('icanhazip.com') } | " +
    "ConvertTo-Json -Depth 4 | Set-Content (Join-Path C:\PVpnSetup ('split-' + `$m + '.json')) -Encoding ascii }; Get-Content C:\PVpnSetup\split-ExcludeSelected.json")

$system = 'C:\Windows\System32\curl.exe'
$app = 'C:\SplitTest\app-1.0.1\curl.exe'
$updated = 'C:\SplitTest\app-1.0.2\curl-new.exe'

Step "Режим 1: всё через VPN, кроме C:\SplitTest и icanhazip.com ($Profile)"
Guest "$cli svc connect $Profile --test 900 --split C:\PVpnSetup\split-ExcludeSelected.json" -AsUser
Check 'системный curl' $system 'https://api.ipify.org' 'vpn'
Check 'curl из C:\SplitTest' $app 'https://api.ipify.org' 'direct'
Check 'сайт icanhazip.com' $system 'https://icanhazip.com' 'direct'
Guest "New-Item -ItemType Directory -Force C:\SplitTest\app-1.0.2 | Out-Null; Copy-Item $app $updated -Force"
Check '«обновлённая» программа' $updated 'https://api.ipify.org' 'direct'

Step 'Режим 2 без переподключения: через VPN только выбранное'
Guest "$cli svc reconfigure --split C:\PVpnSetup\split-OnlySelected.json" -AsUser
Check 'системный curl' $system 'https://api.ipify.org' 'direct'
Check 'curl из C:\SplitTest' $app 'https://api.ipify.org' 'vpn'
Check 'сайт icanhazip.com' $system 'https://icanhazip.com' 'vpn'
Check '«обновлённая» программа' $updated 'https://api.ipify.org' 'vpn'

Step 'Kill switch и DNS мимо туннеля'
Guest "$cli killswitch; `$dns = (Get-DnsClientServerAddress -InterfaceAlias Ethernet -AddressFamily IPv4).ServerAddresses[0]; try { Resolve-DnsName example.com -Server `$dns -DnsOnly -QuickTimeout -ErrorAction Stop | Out-Null; 'DNS к роутеру: ОТВЕТИЛ' } catch { 'DNS к роутеру: заблокирован' }"

Step 'Раздельное туннелирование выключено без переподключения'
Guest "$cli svc reconfigure" -AsUser
Check 'curl из C:\SplitTest' $app 'https://api.ipify.org' 'vpn'
Check 'сайт icanhazip.com' $system 'https://icanhazip.com' 'vpn'

Step 'Журнал: перезапуски sing-box были плановыми'
Guest "$cli svc disconnect; Get-Content (Get-ChildItem C:\ProgramData\TempestVPN\logs | Select-Object -Last 1).FullName -Encoding UTF8 -Tail 120 | Select-String 'Раздельное|неожиданно|не применились|Kill switch' | ForEach-Object Line"
Write-Host "`nПровалено проверок: $failures"
exit $failures
