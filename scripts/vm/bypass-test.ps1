# Обход блокировок (пункт B) в тестовой ВМ на настоящем сервере:
#   1) режим прокси: все протоколы с настройками по умолчанию и с изменёнными (режим XHTTP, xmux, отпечаток,
#      фрагментация, mux, шумы) — внешний IP должен быть IP сервера;
#   2) шум Hysteria2 действительно уходит в сеть: pktmon ловит первый UDP-пакет к серверу (шум — 50–100 байт,
#      без шума первым идёт QUIC Initial от 1200 байт);
#   3) TUN: Vision (UDP через xudp/mux), Hysteria2 с шумом, XHTTP packet-up — HTTP и UDP (STUN) идут через VPN;
#   4) смена портов Hysteria2 каждые 5 с: соединение живёт 40 с. Сервер слушает один порт, поэтому ссылка
#      «host:443,443» проверяет смену локального сокета, а перебор портов сервера — только конфиг (модульные тесты).
# Перед запуском: deploy-and-test.ps1 (служба и ссылки my-* в ВМ).
[CmdletBinding()]
param([string]$VpnIp = '203.0.113.10', [switch]$SkipProxy, [switch]$SkipTun)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$vm = Join-Path $PSScriptRoot 'vm.ps1'
$setup = 'C:\PVpnSetup'
# Команды для ВМ — без двойных кавычек (см. vm.ps1).
$cli = "& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe'"
$failures = 0

# Запускать без перенаправления *>: в PowerShell 5.1 stderr из ВМ тогда становится ошибкой и прерывает vm.ps1.
function Guest([string]$Command, [switch]$AsUser, [int]$TimeoutSec = 180) { & $vm run -Command $Command -AsUser:$AsUser -TimeoutSec $TimeoutSec }
function Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }
function Result([string]$Name, [bool]$Ok, [string]$Details) {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("  {0,-4} {1}: {2}" -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Details)
}

# Варианты настроек (как в IPC, camelCase); остальное — по умолчанию.
$variants = [ordered]@{
    'xhttp-packetup'  = @{ xhttpMode = 'packet-up'; fingerprint = 'firefox'; xmux = @{ maxConnections = '1' } }
    'xhttp-streamone' = @{ xhttpMode = 'stream-one'; xmux = @{ maxConcurrency = '4-8'; hKeepAlivePeriod = 20 } }
    'frag-records'    = @{ fragmentPackets = '1-3'; fragmentLength = '3-5'; fragmentInterval = '1'; fingerprint = 'safari' }
    'plain'           = @{ fragment = $false; mux = $false; noises = $false }
    'noise-str'       = @{ noiseType = 'str'; noisePacket = 'hello-from-pvpn'; noiseDelay = '0' }
    'hop5'            = @{ hopIntervalSeconds = 5 }
}
$local = Join-Path $env:TEMP ('pvpn-bypass-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $local | Out-Null
try {
    foreach ($k in $variants.Keys) { $variants[$k] | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $local "$k.json") -Encoding ascii }
    Copy-Item (Join-Path $PSScriptRoot 'guest\stun.ps1') $local
    Guest "Remove-Item $setup\bypass -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $setup\bypass | Out-Null"
    & $vm copy -Source $local -Dest "$setup\bypass"
}
finally { Remove-Item $local -Recurse -Force }

Step 'Профиль hoptest: my-hy2 с портами 443,443 (смена портов без доработки сервера)'
Guest -AsUser ("$cli remove hoptest 2>`$null | Out-Null; " +
    "`$l = @(& 'C:\Program Files\The Cup of Tea\cupoftea-cli.exe' export --filter my-hy2) -match '^(hy2|hysteria2)://' | Select-Object -First 1; " +
    "`$l -replace ':443(/?\?)', ':443,443`$1' -replace '#.*$', '#hoptest' | Set-Content $setup\bypass\hop.txt; " +
    "$cli import $setup\bypass\hop.txt; Remove-Item $setup\bypass\hop.txt; $cli list")

function Test-Proxy([string]$Server, [string]$Variant) {
    $bypass = if ($Variant) { " --bypass $setup\bypass\$Variant.json" } else { '' }
    $out = (Guest -AsUser "$cli test $Server$bypass") -join "`n"
    $ip = if ($out -match 'IP (\S+),') { $Matches[1] } else { '—' }
    Result "прокси $Server $(if ($Variant) { $Variant } else { 'по умолчанию' })" ($ip -eq $VpnIp) ($out.Trim() -replace '\s+', ' ')
}

# Первый UDP-пакет к серверу при подключении hy2 (pktmon от администратора, само подключение — от pvpn).
function First-UdpLength([string]$Variant) {
    Guest ("pktmon stop 2>`$null | Out-Null; pktmon filter remove | Out-Null; " +
        "pktmon filter add PVpnHy2 -t UDP -i $VpnIp -p 443 | Out-Null; " +
        "pktmon start --capture --pkt-size 64 --file-name $setup\hy2.etl | Out-Null")
    $bypass = if ($Variant) { " --bypass $setup\bypass\$Variant.json" } else { '' }
    Guest -AsUser "$cli test my-hy2$bypass" | Out-Null
    # Каждый пакет pktmon показывает на нескольких уровнях стека: повторы подряд схлопываются.
    $lengths = @(Guest ("pktmon stop | Out-Null; pktmon etl2txt $setup\hy2.etl --out $setup\hy2.txt | Out-Null; pktmon filter remove | Out-Null; " +
        "`$prev = ''; Get-Content $setup\hy2.txt | ForEach-Object { if (`$_ -match '(\S+) > $([regex]::Escape($VpnIp))\.443: UDP, length (\d+)' -and `$_ -ne `$prev) { `$Matches[2]; `$prev = `$_ } } | Select-Object -First 5"))
    Write-Host "      исходящие UDP-пакеты к серверу, байт: $($lengths -join ', ')"
    if ($lengths.Count) { [int]$lengths[0] } else { 0 }
}

if (-not $SkipProxy) {
    Step 'Режим прокси: настройки по умолчанию'
    foreach ($p in 'my-xhttp', 'my-vision', 'my-hy2', 'hoptest') { Test-Proxy $p $null }

    Step 'Режим прокси: изменённые настройки'
    Test-Proxy 'my-xhttp' 'xhttp-packetup'
    Test-Proxy 'my-xhttp' 'xhttp-streamone'
    Test-Proxy 'my-xhttp' 'frag-records'
    Test-Proxy 'my-vision' 'frag-records'
    Test-Proxy 'my-vision' 'plain'
    Test-Proxy 'my-hy2' 'noise-str'
    Test-Proxy 'my-hy2' 'plain'

    Step 'Шум Hysteria2 в сети (pktmon)'
    $noise = First-UdpLength $null
    Result 'первый UDP-пакет с шумом (rand 50-100)' ($noise -ge 50 -and $noise -le 100) "$noise байт"
    $plain = First-UdpLength 'plain'
    Result 'первый UDP-пакет без шума (QUIC Initial)' ($plain -ge 1200) "$plain байт"
}

function Test-Tun([string]$Server, [string]$Variant, [int]$Rounds = 1, [int]$PauseSec = 0) {
    $bypass = if ($Variant) { " --bypass $setup\bypass\$Variant.json" } else { '' }
    $name = "TUN $Server $(if ($Variant) { $Variant } else { 'по умолчанию' })"
    $connect = (Guest -AsUser -TimeoutSec 150 "$cli svc connect $Server --test 300$bypass; $cli svc status") -join "`n"
    if ($connect -notmatch 'Состояние: Connected') {
        Result $name $false ($connect.Trim() -replace '\s+', ' ')
        Guest -AsUser "$cli svc disconnect" | Out-Null
        return
    }
    for ($i = 1; $i -le $Rounds; $i++) {
        $r = Guest -AsUser -TimeoutSec 60 ("`$h = (& curl.exe -4 -s -m 10 https://api.ipify.org) -join ''; `$u = & $setup\bypass\stun.ps1; " +
            "'HTTP=' + `$h + ' UDP=' + `$u")
        $line = ($r -join ' ').Trim()
        Result "$name, проверка $i" ($line -eq "HTTP=$VpnIp UDP=$VpnIp") $line
        if ($PauseSec -gt 0 -and $i -lt $Rounds) { Start-Sleep $PauseSec }
    }
    Guest -AsUser "$cli svc disconnect" | Out-Null
}

if (-not $SkipTun) {
    Step 'TUN: HTTP и UDP (STUN) через VPN'
    Test-Tun 'my-vision' $null
    Test-Tun 'my-hy2' $null
    Test-Tun 'my-xhttp' 'xhttp-packetup'
    Test-Tun 'my-vision' 'plain'

    Step 'TUN: смена портов Hysteria2 каждые 5 с, 40 с работы'
    Test-Tun 'hoptest' 'hop5' -Rounds 8 -PauseSec 3

    Step 'Журнал службы: смена портов и ошибки xray'
    Guest -AsUser "$cli svc logs" | Select-String -Pattern 'hop|udphop|mask|noise|failed|ошибк' -SimpleMatch:$false | Select-Object -Last 15 | ForEach-Object { "  $_" }
}

Guest -AsUser "$cli remove hoptest" | Out-Null
Write-Host "`nПровалено проверок: $failures"
exit $failures
