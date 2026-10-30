# Замер открытия сайтов через текущее подключение (curl): DNS, TCP, TLS и первый байт по каждому сайту.
# Запускать при включённом VPN. Сравнить два случая: окно Tempest VPN открыто и процесс TempestVPN.exe
# снят в диспетчере задач (служба при этом держит подключение).
# Пример: powershell -ExecutionPolicy Bypass -File scripts\site-timing.ps1 -Rounds 3
[CmdletBinding()]
param(
    [string[]]$Sites = @('https://www.google.com/', 'https://www.youtube.com/', 'https://github.com/', 'https://www.wikipedia.org/',
        'https://web.telegram.org/', 'https://ya.ru/'),
    [int]$Rounds = 2
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$format = '%{time_namelookup} %{time_connect} %{time_appconnect} %{time_starttransfer} %{http_code}'
$rows = foreach ($round in 1..$Rounds) {
    foreach ($site in $Sites) {
        $out = curl.exe -s -o NUL -m 30 -w $format $site
        $t = "$out".Split(' ')
        [pscustomobject]@{
            'Круг'       = $round
            'Сайт'       = ([Uri]$site).Host
            'DNS, мс'    = [int]([double]$t[0] * 1000)
            'TCP, мс'    = [int]([double]$t[1] * 1000)
            'TLS, мс'    = [int]([double]$t[2] * 1000)
            'Ответ, мс'  = [int]([double]$t[3] * 1000)
            'HTTP'       = $t[4]
        }
    }
}
$rows | Format-Table -AutoSize
$tun = Get-NetAdapter -Name TempestVPN -ErrorAction SilentlyContinue
"Адаптер TUN: $(if ($tun) { $tun.Status } else { 'нет' }); окно Tempest VPN: $(if (Get-Process TempestVPN -ErrorAction SilentlyContinue) { 'запущено' } else { 'закрыто' })"
