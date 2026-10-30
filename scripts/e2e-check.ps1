# Проверка критериев приёмки MVP при подключённом TUN. Запускать от обычного пользователя.
# Выход: число проваленных проверок (0 — всё хорошо).
[CmdletBinding()]
param(
    [string]$ExpectedIp = '203.0.113.10',
    [string]$TunAlias = 'TempestVPN'
)
$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Name, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Проверка = $Name; Итог = $(if ($Ok) { 'OK' } else { 'FAIL' }); Подробности = $Detail })
}
function Test-Fake([string]$ip) {
    $b = ([Net.IPAddress]$ip).GetAddressBytes()
    return ($b[0] -eq 198 -and ($b[1] -eq 18 -or $b[1] -eq 19)) -or $b[0] -ge 240
}
function Get-NslookupAnswers([string]$name, [string]$server) {
    $out = if ($server) { nslookup -type=A $name $server 2>$null } else { nslookup -type=A $name 2>$null }
    $text = ($out -join "`n")
    # Ответ идёт после строки с именем запрошенного узла (блок про сам DNS-сервер — до неё).
    $idx = $text.IndexOf($name, [StringComparison]::OrdinalIgnoreCase)
    $answer = if ($idx -ge 0) { $text.Substring($idx) } else { '' }
    return [pscustomobject]@{
        Answers = @([regex]::Matches($answer, '\b\d{1,3}(\.\d{1,3}){3}\b') | ForEach-Object Value)
    }
}

# 1. TUN поднят и забирает маршрут по умолчанию
$tun = Get-NetAdapter -Name $TunAlias -ErrorAction SilentlyContinue
Add-Result 'Адаптер TUN' ($tun -and $tun.Status -eq 'Up') $(if ($tun) { "$($tun.InterfaceDescription), $($tun.Status)" } else { 'нет адаптера' })
$route = Find-NetRoute -RemoteIPAddress 1.1.1.1 -ErrorAction SilentlyContinue | Select-Object -First 1
Add-Result 'Трафик идёт в TUN' ($route.InterfaceAlias -eq $TunAlias) "1.1.1.1 → $($route.InterfaceAlias)"
$serverRoute = Find-NetRoute -RemoteIPAddress $ExpectedIp -ErrorAction SilentlyContinue | Select-Object -First 1
Add-Result 'Сервер мимо TUN' ($serverRoute -and $serverRoute.InterfaceAlias -ne $TunAlias) "$ExpectedIp → $($serverRoute.InterfaceAlias)"

# 2. DNS отдаёт настоящие адреса (системный, сторонний и Resolve-DnsName)
foreach ($probe in @(@{ Name = 'example.com'; Server = $null }, @{ Name = 'www.google.com'; Server = $null }, @{ Name = 'example.com'; Server = '8.8.8.8' })) {
    $r = Get-NslookupAnswers $probe.Name $probe.Server
    $fake = @($r.Answers | Where-Object { Test-Fake $_ })
    $label = "nslookup $($probe.Name)" + $(if ($probe.Server) { " $($probe.Server)" } else { '' })
    Add-Result $label ($r.Answers.Count -gt 0 -and $fake.Count -eq 0) ($r.Answers -join ', ')
}
# DNS-сервер физической сети (роутер) напрямую недоступен: запросы мимо туннеля закрыты.
$lanDns = if ($serverRoute) { (Get-DnsClientServerAddress -InterfaceAlias $serverRoute.InterfaceAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses | Select-Object -First 1 }
if ($lanDns) {
    $leaked = $null
    try { $leaked = Resolve-DnsName example.com -Server $lanDns -Type A -DnsOnly -QuickTimeout -ErrorAction Stop | Where-Object IPAddress } catch { }
    Add-Result "DNS мимо туннеля ($lanDns)" (-not $leaked) $(if ($leaked) { "роутер ответил: $($leaked.IPAddress -join ', ')" } else { 'заблокирован' })
}
try {
    $ips = @(Resolve-DnsName www.wikipedia.org -Type A -DnsOnly -ErrorAction Stop | Where-Object IPAddress | ForEach-Object IPAddress)
    Add-Result 'Resolve-DnsName' ($ips.Count -gt 0 -and -not ($ips | Where-Object { Test-Fake $_ })) ($ips -join ', ')
}
catch { Add-Result 'Resolve-DnsName' $false $_.Exception.Message }

# 3. Внешний IP и сайты
$ip = (curl.exe -s -m 15 https://api.ipify.org) -join ''
Add-Result 'Внешний IP' ($ip -eq $ExpectedIp) "получен '$ip', ожидался $ExpectedIp"
foreach ($site in 'https://www.google.com/', 'https://ya.ru/', 'https://www.wikipedia.org/', 'https://github.com/') {
    $code = (curl.exe -s -o NUL -m 20 -w '%{http_code}' $site) -join ''
    Add-Result "Сайт $site" ($code -match '^(200|301|302|303|307|308)$') "HTTP $code"
}

# 4. Утечки IPv6
$v6 = (curl.exe -6 -s -m 6 https://api6.ipify.org) -join ''
Add-Result 'Нет утечки IPv6' ([string]::IsNullOrEmpty($v6)) $(if ($v6) { "IPv6 наружу: $v6" } else { 'IPv6 не выходит' })

$results | Format-Table -AutoSize -Wrap | Out-String -Width 200
$failed = @($results | Where-Object Итог -eq 'FAIL').Count
Write-Host $(if ($failed -eq 0) { 'Все проверки пройдены' } else { "Провалено проверок: $failed" })
exit $failed
