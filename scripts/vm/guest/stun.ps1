# STUN binding request over UDP: prints the public IP that the STUN server sees (empty if no answer).
# Used to check that UDP goes through the VPN (via xudp/mux for VLESS, via QUIC for Hysteria2).
param([string]$Server = 'stun.l.google.com', [int]$Port = 19302, [int]$TimeoutMs = 5000)
$udp = New-Object System.Net.Sockets.UdpClient
try {
    $udp.Client.ReceiveTimeout = $TimeoutMs
    $request = New-Object byte[] 20
    $request[1] = 1                                   # Binding Request
    [byte[]]$cookie = 0x21, 0x12, 0xA4, 0x42
    [Array]::Copy($cookie, 0, $request, 4, 4)
    (New-Object Random).NextBytes(($tid = New-Object byte[] 12)); [Array]::Copy($tid, 0, $request, 8, 12)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [void]$udp.Send($request, $request.Length, $Server, $Port)
        try {
            $remote = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0
            $r = $udp.Receive([ref]$remote)
        }
        catch { continue }
        $i = 20
        while ($i + 4 -le $r.Length) {
            $type = $r[$i] * 256 + $r[$i + 1]; $len = $r[$i + 2] * 256 + $r[$i + 3]
            if ($type -eq 0x20 -and $r[$i + 5] -eq 1) {   # XOR-MAPPED-ADDRESS, IPv4
                $ip = 0..3 | ForEach-Object { $r[$i + 8 + $_] -bxor $cookie[$_] }
                return ($ip -join '.')
            }
            $i += 4 + [Math]::Ceiling($len / 4) * 4
        }
    }
    ''
}
finally { $udp.Close() }
