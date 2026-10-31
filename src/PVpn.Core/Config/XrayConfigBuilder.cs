using System.Text.Json.Nodes;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Config;

public enum XrayMode
{
    /// <summary>Режим системного прокси: SOCKS и HTTP без пароля на 127.0.0.1.</summary>
    Proxy,

    /// <summary>Бэкенд для TUN: только SOCKS с паролем, весь трафик в туннель (решает sing-box).</summary>
    TunBackend,
}

public sealed record XrayOptions
{
    public XrayMode Mode { get; init; } = XrayMode.Proxy;
    public int SocksPort { get; init; } = 10808;

    /// <summary>0 — без HTTP-входа.</summary>
    public int HttpPort { get; init; }
    public string? SocksUser { get; init; }
    public string? SocksPassword { get; init; }

    /// <summary>Имя физического адаптера для sockopt.interface (чтобы соединение с сервером не ушло в TUN).</summary>
    public string? BindInterface { get; init; }

    /// <summary>IP сервера, если в профиле домен (в TUN-режиме домен разрешается заранее, до поднятия TUN).</summary>
    public string? ServerAddressOverride { get; init; }

    /// <summary>Обход блокировок: фрагментация, mux, отпечаток, режим XHTTP, шумы (уже проверенные <see cref="BypassRules.Validate"/>).</summary>
    public BypassOptions Bypass { get; init; } = BypassOptions.Default;

    public string LogLevel { get; init; } = "warning";

    /// <summary>Порт metrics на 127.0.0.1 (статистика трафика, HTTP /debug/vars); 0 — без статистики.</summary>
    public int MetricsPort { get; init; }

    /// <summary>
    /// Раздельное туннелирование в режиме прокси (уже проверенное). Действуют только сайты: системный прокси
    /// не знает, какая программа к нему обращается, поэтому правила по программам здесь не применить.
    /// </summary>
    public SplitTunnelOptions? SplitTunnel { get; init; }

    /// <summary>
    /// Для серверов с полным конфигом из подписки (<see cref="XrayConfigProfile"/>): применять их маршруты
    /// (российские сайты напрямую и т. п.). false — всё через сервер, кроме локальных сетей.
    /// </summary>
    public bool UseServerRouting { get; init; } = true;

    /// <summary>
    /// Заранее разрешённые адреса серверов из конфига подписки (домен → IP): попадают в dns.hosts, и xray не зависит
    /// от DNS при подключении к серверам. Без этого с привязкой к адаптеру проверки автовыбора часто не проходили.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ResolvedHosts { get; init; }

    /// <summary>Профиль маршрутизации (уже проверенный <see cref="RoutingProfiles.Validate"/>); null — всё через VPN.</summary>
    public RoutingProfile? Routing { get; init; }
}

public sealed class ConfigException(string message) : Exception(message);

public static class XrayConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string FragmentTag = "fragment";

    internal static readonly string[] PrivateCidrs =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.168.0.0/16", "224.0.0.0/4", "255.255.255.255/32", "::1/128", "fc00::/7", "fe80::/10",
    ];

    public static JsonObject Build(Profile profile, XrayOptions options)
    {
        if (profile is XrayConfigProfile server)
            return ServerConfigs.BuildRuntime(server, options);
        var inbounds = new JsonArray();
        var bypass = options.Bypass;
        var fragment = bypass.Fragment && profile is StreamProfile { Security: "tls" or "reality" };
        var outbounds = new JsonArray { BuildOutbound(profile, ProxyTag, options.BindInterface, options.ServerAddressOverride, fragment, bypass) };
        if (fragment)
            outbounds.Add(BuildFragmentOutbound(options.BindInterface, bypass));
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = options.LogLevel, ["access"] = "none", ["dnsLog"] = false },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
        };
        AddMetrics(config, options.MetricsPort);

        if (options.Mode == XrayMode.TunBackend)
        {
            if (string.IsNullOrEmpty(options.SocksUser) || string.IsNullOrEmpty(options.SocksPassword))
                throw new ConfigException("Для TUN-режима нужен логин и пароль SOCKS");
            inbounds.Add(new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = options.SocksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject
                {
                    ["auth"] = "password",
                    ["accounts"] = new JsonArray { new JsonObject { ["user"] = options.SocksUser, ["pass"] = options.SocksPassword } },
                    ["udp"] = true,
                    ["ip"] = "127.0.0.1",
                },
            });
            outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
            if (options.Routing is { } tunRouting)
            {
                // Домен для правил — из SNI/HTTP (sing-box передаёт в xray уже IP); соединение идёт на тот же IP.
                ((JsonObject)inbounds[0]!)["sniffing"] = Sniffing();
                RoutingProfiles.Apply(config, tunRouting, ProxyTag, options.BindInterface, 0);
            }
            return config;
        }

        inbounds.Add(new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = options.SocksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" },
        });
        if (options.HttpPort > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["port"] = options.HttpPort,
                ["protocol"] = "http",
            });
        }

        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
        var rules = new JsonArray
        {
            new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("localhost"), ["outboundTag"] = "direct" },
            new JsonObject { ["type"] = "field", ["ip"] = ToJsonArray(PrivateCidrs), ["outboundTag"] = "direct" },
        };
        if (options.SplitTunnel is { IsEffective: true } split)
        {
            var onlySelected = split.Mode == SplitTunnelMode.OnlySelected;
            if (split.Domains.Count > 0)
            {
                // «domain:» — сам домен и все поддомены.
                rules.Add(new JsonObject
                {
                    ["type"] = "field",
                    ["domain"] = ToJsonArray(split.Domains.Select(d => "domain:" + d)),
                    ["outboundTag"] = onlySelected ? ProxyTag : "direct",
                });
            }
            if (onlySelected)
                rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "direct" });
        }
        config["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules };
        if (options.Routing is { } routing)
        {
            foreach (var inbound in inbounds.OfType<JsonObject>())
                inbound["sniffing"] = Sniffing();
            // После своих правил (локальные сети, сайты раздельного туннелирования), но до «остальное напрямую»
            // режима «только выбранное».
            var onlySelected = options.SplitTunnel is { IsEffective: true, Mode: SplitTunnelMode.OnlySelected };
            RoutingProfiles.Apply(config, routing, ProxyTag, options.BindInterface, onlySelected ? rules.Count - 1 : rules.Count);
        }
        return config;
    }

    private static JsonObject Sniffing() => new()
    {
        ["enabled"] = true,
        ["destOverride"] = new JsonArray("http", "tls", "quic"),
        ["routeOnly"] = true,
    };

    /// <summary>
    /// Статистика трафика (metrics на 127.0.0.1, HTTP /debug/vars); 0 — без неё. Считаются и выходы (у обычного
    /// сервера — «proxy»), и входы: у серверов из подписки с автовыбором своего «proxy» может не быть.
    /// </summary>
    internal static void AddMetrics(JsonObject config, int metricsPort)
    {
        if (metricsPort <= 0)
            return;
        config["stats"] = new JsonObject();
        config["policy"] = new JsonObject
        {
            ["system"] = new JsonObject
            {
                ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true,
                ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true,
            },
        };
        config["metrics"] = new JsonObject { ["tag"] = "metrics", ["listen"] = $"127.0.0.1:{metricsPort}" };
    }

    /// <param name="viaFragment">Устанавливать соединение через исходящий «fragment» (см. BuildFragmentOutbound).</param>
    /// <param name="bypass">Настройки обхода; null — по умолчанию.</param>
    public static JsonObject BuildOutbound(Profile profile, string tag, string? bindInterface, string? addressOverride = null,
        bool viaFragment = false, BypassOptions? bypass = null) =>
        profile switch
        {
            StreamProfile sp => BuildStreamOutbound(sp, tag, bindInterface, addressOverride, viaFragment, bypass ?? BypassOptions.Default),
            ShadowsocksProfile ss => BuildShadowsocks(ss, tag, bindInterface, addressOverride),
            Hysteria2Profile h => BuildHysteria2(h, tag, bindInterface, addressOverride, bypass ?? BypassOptions.Default),
            _ => throw new ConfigException($"Неподдерживаемый профиль: {profile.GetType().Name}"),
        };

    /// <summary>
    /// Вспомогательный freedom, через который основной исходящий открывает TCP: режет TLS ClientHello
    /// на куски (по умолчанию 100–200 байт с паузами 10–20 мс).
    /// </summary>
    public static JsonObject BuildFragmentOutbound(string? bindInterface, BypassOptions? bypass = null)
    {
        bypass ??= BypassOptions.Default;
        var outbound = new JsonObject
        {
            ["tag"] = FragmentTag,
            ["protocol"] = "freedom",
            ["settings"] = new JsonObject
            {
                ["fragment"] = new JsonObject
                {
                    ["packets"] = bypass.FragmentPackets,
                    ["length"] = bypass.FragmentLength,
                    ["interval"] = bypass.FragmentInterval,
                },
            },
        };
        if (!string.IsNullOrEmpty(bindInterface))
            outbound["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = bindInterface } };
        return outbound;
    }

    /// <summary>
    /// VLESS, Trojan или VMess: свои settings у протокола, а транспорт (raw, xhttp, ws, grpc, httpupgrade) и защита
    /// (TLS, REALITY) — общие. Настройки обхода: отпечаток TLS и режим XHTTP заменяют ссылочные, mux — только для raw.
    /// </summary>
    private static JsonObject BuildStreamOutbound(StreamProfile p, string tag, string? bindInterface, string? addressOverride, bool viaFragment, BypassOptions bypass)
    {
        var address = addressOverride ?? p.Host;
        var (protocol, settings) = p switch
        {
            // У VLESS в xray 26 — плоская запись.
            VlessProfile v => ("vless", new JsonObject
            {
                ["address"] = address, ["port"] = p.Port, ["id"] = v.Uuid, ["encryption"] = v.Encryption,
            }.Also(o => { if (!string.IsNullOrEmpty(v.Flow)) o["flow"] = v.Flow; })),
            TrojanProfile t => ("trojan", new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = p.Port, ["password"] = t.Password }),
            }),
            VmessProfile m => ("vmess", new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = address, ["port"] = p.Port,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = m.Uuid, ["security"] = m.Cipher }),
                }),
            }),
            _ => throw new ConfigException($"Неподдерживаемый профиль: {p.GetType().Name}"),
        };

        var stream = BuildStream(p, bypass);
        AddSockopt(stream, bindInterface);
        if (viaFragment)
        {
            stream["sockopt"] ??= new JsonObject();
            stream["sockopt"]!["dialerProxy"] = FragmentTag;
        }
        var outbound = new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = protocol,
            ["settings"] = settings,
            ["streamSettings"] = stream,
        };
        // У XHTTP своё мультиплексирование (xmux), у gRPC — своё; mux.cool — только поверх raw.
        if (bypass.Mux && p.Network == "raw")
        {
            var vision = p is VlessProfile { Flow: "xtls-rprx-vision" };
            outbound["mux"] = new JsonObject
            {
                ["enabled"] = true,
                // Vision не пускает TCP через mux (сервер рвёт такие соединения): через mux идёт только UDP.
                ["concurrency"] = vision ? -1 : bypass.MuxConcurrency,
                ["xudpConcurrency"] = bypass.XudpConcurrency,
                // QUIC (UDP 443) — отдельным соединением, без mux. Блокирует его, если нужно, sing-box (BlockQuic).
                ["xudpProxyUDP443"] = "skip",
            };
        }
        return outbound;
    }

    private static JsonObject BuildStream(StreamProfile p, BypassOptions bypass)
    {
        var fingerprint = bypass.Fingerprint ?? p.Fingerprint ?? "chrome";
        var stream = new JsonObject
        {
            ["network"] = p.Network,
            ["security"] = p.Security,
        };
        string[] Alpn() => (p.Alpn ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        switch (p.Security)
        {
            case "reality":
                var reality = new JsonObject
                {
                    ["serverName"] = p.Sni,
                    ["fingerprint"] = fingerprint,
                    ["publicKey"] = p.PublicKey,
                    ["shortId"] = p.ShortId ?? "",
                };
                if (!string.IsNullOrEmpty(p.SpiderX))
                    reality["spiderX"] = p.SpiderX;
                stream["realitySettings"] = reality;
                break;
            case "tls":
                var tls = new JsonObject
                {
                    ["serverName"] = p.Sni ?? p.HostHeader ?? p.XhttpHost ?? p.Host,
                    ["fingerprint"] = fingerprint,
                };
                if (Alpn() is { Length: > 0 } alpn)
                    tls["alpn"] = ToJsonArray(alpn);
                if (!string.IsNullOrEmpty(p.PinSha256))
                    tls["pinnedPeerCertSha256"] = p.PinSha256;
                if (!string.IsNullOrEmpty(p.VerifyName))
                    tls["verifyPeerCertByName"] = p.VerifyName;
                stream["tlsSettings"] = tls;
                break;
        }

        switch (p.Network)
        {
            case "xhttp":
                var xhttp = new JsonObject
                {
                    ["path"] = p.XhttpPath ?? "/",
                    ["mode"] = bypass.XhttpMode ?? p.XhttpMode ?? "auto",
                };
                if (!string.IsNullOrEmpty(p.XhttpHost))
                    xhttp["host"] = p.XhttpHost;
                var extra = string.IsNullOrEmpty(p.XhttpExtra) ? null : JsonNode.Parse(p.XhttpExtra) as JsonObject;
                if (bypass.Xmux is { } xmux)
                {
                    // Если extra задан, Xray берёт настройки XHTTP (кроме host, path и mode) только из него,
                    // поэтому xmux кладётся туда.
                    extra ??= new JsonObject();
                    extra["xmux"] = BuildXmux(xmux);
                }
                if (extra != null)
                    xhttp["extra"] = extra;
                stream["xhttpSettings"] = xhttp;
                break;
            case "ws":
            case "httpupgrade":
                var ws = new JsonObject { ["path"] = p.Path ?? "/" };
                if (!string.IsNullOrEmpty(p.HostHeader))
                    ws["host"] = p.HostHeader;
                stream[p.Network == "ws" ? "wsSettings" : "httpupgradeSettings"] = ws;
                break;
            case "grpc":
                var grpc = new JsonObject { ["serviceName"] = p.ServiceName ?? "" };
                if (p.GrpcMulti)
                    grpc["multiMode"] = true;
                if (!string.IsNullOrEmpty(p.HostHeader))
                    grpc["authority"] = p.HostHeader;
                stream["grpcSettings"] = grpc;
                break;
        }
        return stream;
    }

    /// <summary>Shadowsocks (AEAD и 2022): без транспорта и TLS — шифрует сам протокол.</summary>
    private static JsonObject BuildShadowsocks(ShadowsocksProfile p, string tag, string? bindInterface, string? addressOverride)
    {
        var stream = new JsonObject();
        AddSockopt(stream, bindInterface);
        var outbound = new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = "shadowsocks",
            ["settings"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["address"] = addressOverride ?? p.Host, ["port"] = p.Port, ["method"] = p.Method, ["password"] = p.Password,
                }),
            },
        };
        if (stream.Count > 0)
            outbound["streamSettings"] = stream;
        return outbound;
    }

    private static JsonObject BuildXmux(XmuxOptions x)
    {
        var xmux = new JsonObject();
        if (x.MaxConcurrency != null)
            xmux["maxConcurrency"] = x.MaxConcurrency;
        if (x.MaxConnections != null)
            xmux["maxConnections"] = x.MaxConnections;
        if (x.CMaxReuseTimes != null)
            xmux["cMaxReuseTimes"] = x.CMaxReuseTimes;
        if (x.HMaxRequestTimes != null)
            xmux["hMaxRequestTimes"] = x.HMaxRequestTimes;
        if (x.HMaxReusableSecs != null)
            xmux["hMaxReusableSecs"] = x.HMaxReusableSecs;
        if (x.HKeepAlivePeriod > 0)
            xmux["hKeepAlivePeriod"] = x.HKeepAlivePeriod;
        return xmux;
    }

    private static JsonObject BuildHysteria2(Hysteria2Profile p, string tag, string? bindInterface, string? addressOverride, BypassOptions bypass)
    {
        var tls = new JsonObject
        {
            ["serverName"] = p.Sni ?? p.Host,
            ["alpn"] = ToJsonArray(string.IsNullOrEmpty(p.Alpn)
                ? ["h3"]
                : p.Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        };
        if (!string.IsNullOrEmpty(p.PinSha256))
            tls["pinnedPeerCertSha256"] = p.PinSha256;

        var stream = new JsonObject
        {
            ["network"] = "hysteria",
            ["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = p.Auth },
            ["security"] = "tls",
            ["tlsSettings"] = tls,
        };
        AddSockopt(stream, bindInterface);

        // Последняя ступень маскировки UDP: первый элемент — внутренний слой.
        var udpMasks = new JsonArray();
        // Salamander шифрует сами пакеты QUIC — самый внутренний слой; шум и смена портов работают поверх него.
        if (p.ObfsPassword != null)
            udpMasks.Add(new JsonObject { ["type"] = "salamander", ["settings"] = new JsonObject { ["password"] = p.ObfsPassword } });
        if (bypass.Noises)
            udpMasks.Add(new JsonObject { ["type"] = "noise", ["settings"] = new JsonObject { ["noise"] = new JsonArray { BuildNoise(bypass) } } });
        if (p.HopPorts != null)
        {
            udpMasks.Add(new JsonObject
            {
                ["type"] = "udphop",
                ["settings"] = new JsonObject
                {
                    // Как у официального клиента Hysteria: по таймеру новый локальный сокет и новый порт сервера из диапазона.
                    ["mode"] = "intervallocal,intervalremote",
                    ["interval"] = bypass.HopIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["remotePorts"] = p.HopPorts,
                },
            });
        }
        if (udpMasks.Count > 0)
            stream["finalmask"] = new JsonObject { ["udp"] = udpMasks };

        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = "hysteria",
            ["settings"] = new JsonObject
            {
                ["version"] = 2,
                ["address"] = addressOverride ?? p.Host,
                ["port"] = p.Port,
            },
            ["streamSettings"] = stream,
        };
    }

    private static JsonObject BuildNoise(BypassOptions bypass)
    {
        var item = new JsonObject();
        if (bypass.NoiseType == "rand")
        {
            item["rand"] = bypass.NoisePacket;
        }
        else
        {
            item["type"] = bypass.NoiseType;
            item["packet"] = bypass.NoisePacket;
        }
        item["delay"] = bypass.NoiseDelay;
        return item;
    }

    private static void AddSockopt(JsonObject stream, string? bindInterface)
    {
        if (!string.IsNullOrEmpty(bindInterface))
            stream["sockopt"] = new JsonObject { ["interface"] = bindInterface };
    }

    internal static JsonArray ToJsonArray(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);
}

internal static class JsonObjectExtensions
{
    /// <summary>Донастроить объект прямо в выражении.</summary>
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
