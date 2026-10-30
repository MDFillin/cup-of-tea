# Выполняется В ВМ: поддельная «страница входа» на 127.0.0.1:80 (www.msftconnecttest.com указан в hosts).
# C:\PVpnSetup\portal\mode.txt: portal — перенаправление на страницу входа, ok — ответ как у настоящего NCSI.
# Живёт 15 минут.
param([string]$Dir = 'C:\PVpnSetup\portal')
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 80)
$listener.Start()
$deadline = (Get-Date).AddMinutes(15)
try {
    while ((Get-Date) -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 100; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            # Браузер открывает соединения впрок и ничего в них не шлёт: без таймаута однопоточный сервер повис бы.
            $stream.ReadTimeout = 2000
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
            $request = $reader.ReadLine()
            while ($reader.ReadLine()) { }
            Add-Content "$Dir\requests.log" "$(Get-Date -Format HH:mm:ss) $request"
            $mode = Get-Content "$Dir\mode.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($mode -eq 'ok' -and $request -match 'connecttest\.txt') {
                $body = 'Microsoft Connect Test'; $head = "HTTP/1.1 200 OK`r`nContent-Type: text/plain"
            }
            elseif ($mode -eq 'ok') {
                $body = 'ok'; $head = "HTTP/1.1 200 OK`r`nContent-Type: text/plain"
            }
            else {
                $body = '<html><body>Войдите в сеть</body></html>'; $head = "HTTP/1.1 302 Found`r`nLocation: http://login.portal.test/`r`nContent-Type: text/html"
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
            $h = [Text.Encoding]::ASCII.GetBytes("$head`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($h, 0, $h.Length)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        catch { }
        finally { $client.Close() }
    }
}
finally { $listener.Stop() }
