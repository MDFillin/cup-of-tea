# Выполняется В ВМ: игрушечная панель подписки на 127.0.0.1 (без HttpListener — ему нужны права на адрес).
# Отдаёт C:\PVpnSetup\sub\body.txt с заголовками из headers.txt; mode.txt: ok | 403 | html. Живёт 15 минут.
param([int]$Port = 18080, [string]$Dir = 'C:\PVpnSetup\sub')
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
$deadline = (Get-Date).AddMinutes(15)
try {
    while ((Get-Date) -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 100; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
            $request = $reader.ReadLine()
            while ($reader.ReadLine()) { }
            Add-Content "$Dir\requests.log" "$(Get-Date -Format HH:mm:ss) $request"
            $mode = (Get-Content "$Dir\mode.txt" -ErrorAction SilentlyContinue | Select-Object -First 1)
            $status = '200 OK'; $extra = ''
            switch ($mode) {
                '403' { $status = '403 Forbidden'; $body = 'expired' }
                'html' { $body = '<!DOCTYPE html><html><body>Подписка</body></html>' }
                default { $body = Get-Content "$Dir\body.txt" -Raw; $extra = (Get-Content "$Dir\headers.txt" -Raw) }
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
            $head = "HTTP/1.1 $status`r`nContent-Type: text/plain; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n$extra`r`n"
            $h = [Text.Encoding]::ASCII.GetBytes($head)
            $stream.Write($h, 0, $h.Length)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $client.Close() }
    }
}
finally { $listener.Stop() }
