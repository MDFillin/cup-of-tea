using System.Net;
using System.Text.Json.Nodes;
using PVpn.Core.Routing;

namespace PVpn.Core.Config;

public sealed record SingBoxOptions
{
    /// <summary>Локальный SOCKS-вход xray, куда sing-box отправляет весь туннелируемый трафик.</summary>
    public required int SocksPort { get; init; }
    public required string SocksUser { get; init; }
    public required string SocksPassword { get; init; }

    /// <summary>IP серверов VPN: исключаются из маршрутов TUN, чтобы не было петли.</summary>
    public required IReadOnlyList<IPAddress> ServerAddresses { get; init; }

    public string InterfaceName { get; init; } = Product.Id;
    public int Mtu { get; init; } = 1500;

    /// <summary>Блокировать QUIC (UDP 443): браузеры сразу уходят на TCP, который лучше идёт через VLESS.</summary>
    public bool BlockQuic { get; init; } = true;

    /// <summary>Подсети, которые идут мимо туннеля, кроме частных (например, 26.0.0.0/8 у Radmin VPN).</summary>
    public IReadOnlyList<string> DirectCidrs { get; init; } = [];

    /// <summary>Сервер DNS-over-HTTPS; запросы к нему идут через туннель (в раздельном туннелировании — и напрямую).</summary>
    public DohServer Dns { get; init; } = DnsRules.Preset(DnsProvider.Cloudflare);

    /// <summary>
    /// DNS физического адаптера (роутер, провайдер) — для сайтов, которые раздельное туннелирование отправляет напрямую.
    /// Пусто — тот же DoH, что и для VPN, но напрямую (у российских провайдеров соединения с Cloudflare часто тормозят).
    /// </summary>
    public IReadOnlyList<IPAddress> DirectDns { get; init; } = [];

    public string Stack { get; init; } = "gvisor";
    public bool StrictRoute { get; init; } = true;
    public string LogLevel { get; init; } = "warn";

    /// <summary>Раздельное туннелирование (уже проверенное <see cref="SplitTunnelRules.Validate"/>); null — выключено.</summary>
    public SplitTunnelOptions? SplitTunnel { get; init; }
}

/// <summary>
/// sing-box только как TUN-фронтенд: TUN, DNS и маршрутизация. Весь внешний трафик уходит в SOCKS xray.
/// Fakeip не используется: DNS возвращает настоящие адреса.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunAddress4 = "172.18.79.1/30";
    public const string TunAddress6 = "fdfe:dcba:7979::1/126";

    /// <summary>
    /// QUIC режется только у серверов поверх TCP (VLESS): там UDP идёт внутри TCP-соединения и тормозит, браузеру
    /// лучше сразу уйти на TCP. Hysteria2 сама работает по UDP и пропускает QUIC без потерь — резать его незачем:
    /// браузер ждал бы ответа на QUIC, и первый заход на каждый сайт тянулся бы секунды.
    /// </summary>
    public static bool BlocksQuic(Profiles.Profile server, bool setting) => setting && server.Transport == Profiles.ServerTransport.Tcp;

    public static JsonObject Build(SingBoxOptions o)
    {
        if (o.ServerAddresses.Count == 0)
            throw new ConfigException("Не задан IP сервера для обхода TUN");

        var serverCidrs = o.ServerAddresses
            .Select(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{a}/128" : $"{a}/32")
            .ToArray();

        var split = o.SplitTunnel is { IsEffective: true } s ? s : null;
        var onlySelected = split?.Mode == SplitTunnelMode.OnlySelected;
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_cidr"] = XrayConfigBuilder.ToJsonArray(serverCidrs), ["outbound"] = "direct" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };
        if (o.DirectCidrs.Count > 0)
            rules.Add(new JsonObject { ["ip_cidr"] = XrayConfigBuilder.ToJsonArray(o.DirectCidrs), ["outbound"] = "direct" });
        if (split != null)
            AddSplitRules(rules, split, o.BlockQuic);
        // IPv6 захватывается TUN (адрес v6 у адаптера) и отбрасывается: утечки мимо туннеля нет, приложения быстро уходят на IPv4.
        rules.Add(new JsonObject { ["ip_version"] = 6, ["action"] = "reject" });
        // QUIC режем только у трафика, который идёт через VPN (в режиме «только выбранное» — в AddSplitRules).
        if (o.BlockQuic && !onlySelected)
            rules.Add(QuicReject(new JsonObject()));

        var dnsServers = new JsonArray();
        AddDohServer(dnsServers, o.Dns, "remote", "proxy");
        var dnsRules = new JsonArray();
        // По записи HTTPS (тип 65) Chrome узнаёт о QUIC ещё до первого запроса и пробует его первым. Раз QUIC
        // отклоняется, такие записи не отдаём: браузер сразу идёт по TCP, а не ждёт ответа на QUIC.
        if (o.BlockQuic)
            dnsRules.Add(new JsonObject { ["query_type"] = new JsonArray("HTTPS"), ["action"] = "predefined", ["rcode"] = "NOERROR" });
        if (split != null)
        {
            // DNS-сервер без detour ходит напрямую (через физический адаптер, auto_detect_interface).
            if (o.DirectDns.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) is { } local)
                dnsServers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "direct-dns", ["server"] = local.ToString() });
            else
                AddDohServer(dnsServers, o.Dns, "direct-dns", null);
            // Сайты, выбранные «напрямую», и резолвятся напрямую (адреса ближе к нам). Всё остальное — через туннель:
            // DNS-запросы программ идут от службы DNS-клиента Windows, правила по программам к ним не применить, а ответ
            // провайдера для заблокированного сайта бывает подменён — программе, идущей через VPN, он бы помешал.
            if (split.Domains.Count > 0 && !onlySelected)
            {
                dnsRules.Add(new JsonObject
                {
                    ["domain_suffix"] = XrayConfigBuilder.ToJsonArray(split.Domains),
                    ["action"] = "route",
                    ["server"] = "direct-dns",
                });
            }
        }

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = o.LogLevel, ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = "remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = o.InterfaceName,
                    ["address"] = new JsonArray(TunAddress4, TunAddress6),
                    ["mtu"] = o.Mtu,
                    ["auto_route"] = true,
                    ["strict_route"] = o.StrictRoute,
                    ["route_exclude_address"] = XrayConfigBuilder.ToJsonArray(serverCidrs),
                    ["stack"] = o.Stack,
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "proxy",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = o.SocksPort,
                    ["version"] = "5",
                    ["username"] = o.SocksUser,
                    ["password"] = o.SocksPassword,
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = WithResolver(new JsonObject
            {
                ["rules"] = rules,
                ["final"] = onlySelected ? "direct" : "proxy",
                ["auto_detect_interface"] = true,
            }, split != null ? "direct-dns" : dnsServers.Count > 1 ? "remote" : null),
        };
    }

    /// <summary>
    /// Сервер DoH. Если в адресе домен, его самого надо сначала разрешить: для этого рядом ставится Cloudflare
    /// по IP с тем же маршрутом (через туннель или напрямую), обычный DNS провайдера не используется.
    /// </summary>
    private static void AddDohServer(JsonArray servers, DohServer doh, string tag, string? detour)
    {
        var server = new JsonObject
        {
            ["type"] = "https",
            ["tag"] = tag,
            ["server"] = doh.Host,
            ["server_port"] = doh.Port,
            ["path"] = doh.Path,
        };
        if (doh.ServerName != null)
            server["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = doh.ServerName };
        if (detour != null)
            server["detour"] = detour;
        servers.Add(server);
        if (!doh.HostIsIp)
        {
            server["domain_resolver"] = tag + "-bootstrap";
            servers.Add(Bootstrap(tag + "-bootstrap", detour));
        }
    }

    private static JsonObject Bootstrap(string tag, string? detour)
    {
        var cloudflare = DnsRules.Preset(DnsProvider.Cloudflare);
        var server = new JsonObject
        {
            ["type"] = "https",
            ["tag"] = tag,
            ["server"] = cloudflare.Host,
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = cloudflare.ServerName },
        };
        if (detour != null)
            server["detour"] = detour;
        return server;
    }

    /// <summary>
    /// Отказ для QUIC (UDP 443). Без no_drop sing-box после 50 отказов за 30 с сам переходит на «молча выбрасывать»,
    /// а одна страница YouTube даёт столько попыток QUIC легко — и браузер снова ждёт секунды.
    /// </summary>
    private static JsonObject QuicReject(JsonObject rule)
    {
        rule["network"] = "udp";
        rule["port"] = 443;
        rule["action"] = "reject";
        rule["no_drop"] = true;
        return rule;
    }

    /// <summary>
    /// При нескольких DNS-серверах sing-box 1.14 требует явно указать, чем исходящие разрешают домены. Домены
    /// разрешает только «direct» (через VPN домен уходит в xray как есть) — значит, прямым DNS; без раздельного
    /// туннелирования доменов у «direct» нет, и годится любой сервер.
    /// </summary>
    private static JsonObject WithResolver(JsonObject route, string? resolver)
    {
        if (resolver != null)
            route["default_domain_resolver"] = resolver;
        return route;
    }

    /// <summary>
    /// Правила по программам и по сайтам — отдельные: внутри одного правила sing-box объединяет их через «И».
    /// Программа определяется по пути exe (sing-box смотрит владельца соединения в таблицах TCP/UDP Windows).
    /// </summary>
    private static void AddSplitRules(JsonArray rules, SplitTunnelOptions split, bool blockQuic)
    {
        var matchers = new List<JsonObject>();
        if (split.Apps.Count > 0)
            matchers.Add(new JsonObject { ["process_path_regex"] = XrayConfigBuilder.ToJsonArray(split.Apps.Select(SplitTunnelRules.PathRegex)) });
        if (split.Domains.Count > 0)
            matchers.Add(new JsonObject { ["domain_suffix"] = XrayConfigBuilder.ToJsonArray(split.Domains) });

        if (split.Mode == SplitTunnelMode.OnlySelected)
        {
            if (blockQuic)
            {
                foreach (var m in matchers)
                    rules.Add(QuicReject((JsonObject)m.DeepClone()));
            }
            foreach (var m in matchers)
            {
                m["outbound"] = "proxy";
                rules.Add(m);
            }
            return;
        }

        foreach (var m in matchers)
        {
            m["outbound"] = "direct";
            rules.Add(m);
        }
    }
}
