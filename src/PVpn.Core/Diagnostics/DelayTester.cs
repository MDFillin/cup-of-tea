using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PVpn.Core.Diagnostics;

public sealed record DelayResult(int? Milliseconds, string? Error)
{
    public bool Ok => Milliseconds.HasValue;
    public override string ToString() => Ok ? $"{Milliseconds} мс" : $"ошибка: {Error}";
}

public static class DelayTester
{
    public const string ProbeUrl = "https://www.gstatic.com/generate_204";
    public const string IpUrl = "https://api.ipify.org";

    private const int IP_UNICAST_IF = 31;

    /// <summary>
    /// Время TCP-соединения с сервером. С <paramref name="interfaceIndex"/> соединение идёт строго через этот адаптер
    /// (IP_UNICAST_IF), то есть мимо любых TUN — так измеряется путь, которым пойдёт xray.
    /// </summary>
    public static async Task<DelayResult> TcpPingAsync(string host, int port, int? interfaceIndex, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var address = IPAddress.TryParse(host, out var ip)
                ? ip
                : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false)).First();
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (interfaceIndex is { } index && address.AddressFamily == AddressFamily.InterNetwork)
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IP_UNICAST_IF, IPAddress.HostToNetworkOrder(index));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            return new DelayResult((int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DelayResult(null, "нет ответа");
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException)
        {
            return new DelayResult(null, e.Message);
        }
    }

    /// <summary>
    /// DNS-запрос (A, <paramref name="name"/>) на <paramref name="server"/>:53 по UDP. Ответ на адрес внутри TUN даёт только
    /// sing-box этого подключения (hijack-dns), и сам ответ приходит через туннель — значит, вся цепочка работает.
    /// </summary>
    public static async Task<DelayResult> DnsProbeAsync(IPAddress server, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        query.AddRange([0, 0, 1, 0, 1]);
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query.ToArray(), new IPEndPoint(server, 53), cts.Token).ConfigureAwait(false);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (r.Buffer.Length >= 12 && r.Buffer[0] == (byte)(id >> 8) && r.Buffer[1] == (byte)id)
                    return new DelayResult((int)sw.ElapsedMilliseconds, null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DelayResult(null, "нет ответа");
        }
        catch (SocketException e)
        {
            return new DelayResult(null, e.Message);
        }
    }

    /// <summary>
    /// «Реальная задержка» через SOCKS-вход xray: два запроса generate_204 подряд, возвращается второй
    /// (первый включает установку туннеля и TLS к сайту).
    /// </summary>
    public static async Task<DelayResult> HttpDelayViaSocksAsync(int socksPort, TimeSpan timeout, CancellationToken ct = default,
        NetworkCredential? credential = null)
    {
        using var client = CreateClient(socksPort, timeout, credential);
        return await MeasureAsync(client, ct).ConfigureAwait(false);
    }

    /// <summary>Задержка запроса обычным путём по маршрутам системы (в TUN-режиме — через туннель).</summary>
    public static async Task<DelayResult> HttpDelayDirectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
        return await MeasureAsync(client, ct).ConfigureAwait(false);
    }

    /// <summary>Внешний IP обычным путём по маршрутам системы, без прокси.</summary>
    public static async Task<string?> GetPublicIpDirectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = timeout };
        try
        {
            return (await client.GetStringAsync(IpUrl, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<DelayResult> MeasureAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using (var warmup = await client.GetAsync(ProbeUrl, ct).ConfigureAwait(false))
                warmup.EnsureSuccessStatusCode();
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(ProbeUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return new DelayResult((int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new DelayResult(null, Describe(e));
        }
    }

    /// <summary>Внешний IP, каким его видят сайты, через SOCKS-вход xray.</summary>
    public static async Task<string?> GetPublicIpViaSocksAsync(int socksPort, TimeSpan timeout, CancellationToken ct = default,
        NetworkCredential? credential = null)
    {
        using var client = CreateClient(socksPort, timeout, credential);
        try
        {
            return (await client.GetStringAsync(IpUrl, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Внешний IP при запросе «как обычное приложение»: через системный прокси Windows (WinINET).
    /// Нужен в новом процессе или после смены настроек: .NET кэширует системный прокси при первом запросе.
    /// </summary>
    public static async Task<string?> GetPublicIpViaSystemProxyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = WebRequest.GetSystemWebProxy() }) { Timeout = timeout };
        try
        {
            return (await client.GetStringAsync(IpUrl, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient(int socksPort, TimeSpan timeout, NetworkCredential? credential = null)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}") { Credentials = credential },
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException => "нет ответа",
        HttpRequestException { InnerException: { } inner } => inner.Message,
        _ => e.Message,
    };
}
