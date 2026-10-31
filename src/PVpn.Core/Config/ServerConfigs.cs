using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Config;

/// <summary>Что нашлось в подписке с полными конфигами: серверы, служебные строки панели и ошибки.</summary>
public sealed record ServerConfigImport(IReadOnlyList<Profile> Profiles, IReadOnlyList<string> Notes, IReadOnlyList<ImportError> Errors);

/// <summary>
/// Серверы в виде полного конфига xray — так некоторые панели отдают подписку: у каждого сервера свои маршруты
/// (российские сайты напрямую и т. п.), DNS, фрагментация (finalmask), автовыбор (балансировщики) и цепочки.
/// Всё это используется как есть. От себя клиент добавляет только входы (SOCKS для TUN или SOCKS/HTTP для режима прокси),
/// привязку соединений к физическому адаптеру и статистику. Раздельное туннелирование приложения стоит выше
/// маршрутов сервера (в TUN его правила срабатывают в sing-box раньше, чем трафик попадает в xray), а настройки
/// «Обход блокировок» к таким серверам не применяются — у них свои.
/// </summary>
public static class ServerConfigs
{
    public const int MaxConfigBytes = 256 * 1024;
    public const int MaxOutbounds = 64;

    /// <summary>Теги входов: правила подписок ссылаются на них (например, «socks, http, порт 53 → DNS»).</summary>
    public const string SocksInbound = "socks", HttpInbound = "http";

    /// <summary>Свой прямой выход для правил раздельного туннелирования в режиме прокси.</summary>
    private const string OwnDirect = "own-direct";

    // Что берётся из конфига. Всё остальное (inbounds, log с путями к файлам, api, stats, reverse…) отбрасывается:
    // служба запускает xray от имени SYSTEM, и конфиг из интернета не должен ни писать файлы, ни открывать порты.
    private static readonly HashSet<string> TopLevelKeys = ["outbounds", "routing", "dns", "observatory", "burstObservatory", "remarks", "meta"];
    private static readonly HashSet<string> RuntimeDropped = ["remarks", "meta"];
    private static readonly HashSet<string> OutboundKeys = ["tag", "protocol", "settings", "streamSettings", "mux", "proxySettings"];
    private static readonly HashSet<string> RoutingKeys = ["domainStrategy", "rules", "balancers"];

    private static readonly HashSet<string> Protocols =
        ["vless", "vmess", "trojan", "shadowsocks", "hysteria", "wireguard", "socks", "http", "freedom", "blackhole", "dns", "loopback"];

    /// <summary>Выходы, которые сами в сеть не ходят.</summary>
    private static readonly HashSet<string> Local = ["blackhole", "dns", "loopback"];

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
        { "id", "password", "pass", "auth", "shortId", "pinnedPeerCertSha256", "privateKey", "secretKey", "preSharedKey", "psk" };

    /// <summary>Ответ панели похож на JSON с конфигами, а не на список ссылок.</summary>
    public static bool LooksLikeConfigs(string body)
    {
        var t = body.TrimStart();
        return t.StartsWith('[') || t.StartsWith('{');
    }

    /// <summary>Разбирает ответ панели: массив конфигов (или один конфиг).</summary>
    public static ServerConfigImport ParseMany(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException e)
        {
            throw new ConfigException($"Подписка пришла в виде JSON, но разобрать его не удалось: {e.Message}");
        }
        var items = root switch
        {
            JsonArray array => array.ToList(),
            JsonObject one => [one],
            _ => [],
        };

        var profiles = new List<Profile>();
        var notes = new List<string>();
        var errors = new List<ImportError>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject config)
                continue;
            try
            {
                var (profile, info) = FromConfig(config);
                if (info)
                    notes.Add(profile.Name);
                else
                    profiles.Add(profile);
            }
            catch (ConfigException e)
            {
                errors.Add(new ImportError(i + 1, $"«{Remarks(config) ?? $"конфиг {i + 1}"}»: {e.Message}"));
            }
        }
        return new ServerConfigImport(profiles, notes, errors);
    }

    /// <summary>Сервер из одного конфига (его же получает служба). Info — служебная строка панели, а не сервер.</summary>
    public static (XrayConfigProfile Profile, bool Info) FromConfig(JsonObject raw)
    {
        var config = Sanitize(raw);
        var outbounds = Outbounds(config);
        var network = outbounds.Where(IsNetwork).ToList();
        if (network.Count == 0)
            throw new ConfigException("в конфиге нет ни одного сервера");

        var main = MainOutbound(config, outbounds) ?? network[0];
        var (host, port) = Address(main);
        // Панели показывают трафик и срок «серверами» с адресом 127.0.0.1 — подключаться к ним незачем.
        var info = network.All(o => IsPlaceholder(Address(o).Host));
        var profile = new XrayConfigProfile
        {
            Name = Remarks(raw) ?? $"{host}:{port}",
            Host = host,
            Port = port,
            SourceLink = "",
            Config = config.ToJsonString(),
            MainProtocol = Label(main),
            ServerCount = network.Count,
            Udp = (string?)main["protocol"] is "hysteria" or "wireguard",
            Description = raw["meta"]?["serverDescription"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null,
        };
        return (profile, info);
    }

    /// <summary>Служба получает конфиг по каналу и разбирает его сама, ничего не принимая на веру.</summary>
    public static XrayConfigProfile ParseRuntime(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
            throw new ConfigException("Конфиг сервера слишком большой");
        JsonObject raw;
        try
        {
            raw = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
                  ?? throw new ConfigException("Конфиг сервера — не объект JSON");
        }
        catch (JsonException e)
        {
            throw new ConfigException($"Конфиг сервера — не JSON: {e.Message}");
        }
        return FromConfig(raw).Profile;
    }

    /// <summary>
    /// Оставляет из конфига только серверы, маршруты, DNS и наблюдение за серверами; проверяет то, что могло бы
    /// навредить при запуске от имени SYSTEM: ссылки на файлы (ext:, file:, сертификаты), сокеты вместо адресов.
    /// </summary>
    public static JsonObject Sanitize(JsonObject raw)
    {
        if (Encoding.UTF8.GetByteCount(raw.ToJsonString()) > MaxConfigBytes)
            throw new ConfigException("конфиг больше 256 КБ");

        var result = new JsonObject();
        foreach (var (key, value) in raw)
        {
            if (TopLevelKeys.Contains(key) && value != null)
                result[key] = value.DeepClone();
        }

        if (result["outbounds"] is not JsonArray outbounds || outbounds.Count == 0)
            throw new ConfigException("в конфиге нет outbounds");
        if (outbounds.Count > MaxOutbounds)
            throw new ConfigException($"в конфиге больше {MaxOutbounds} выходов");
        var clean = new JsonArray();
        foreach (var node in outbounds)
        {
            if (node is not JsonObject o || (string?)o["protocol"] is not { } protocol || !Protocols.Contains(protocol))
                throw new ConfigException($"неподдерживаемый выход: {(node as JsonObject)?["protocol"]?.ToJsonString() ?? "?"}");
            var copy = new JsonObject();
            foreach (var (key, value) in o)
            {
                if (OutboundKeys.Contains(key) && value != null)
                    copy[key] = value.DeepClone();
            }
            if (copy["streamSettings"] is JsonObject stream)
            {
                if ((string?)stream["network"] is "domainsocket" or "ds" or "unix")
                    throw new ConfigException("локальные сокеты вместо сервера не поддерживаются");
                // Своя привязка к адаптеру и метки пакетов — дело клиента, а сертификаты с диска читать незачем.
                if (stream["sockopt"] is JsonObject sockopt)
                {
                    sockopt.Remove("interface");
                    sockopt.Remove("mark");
                    sockopt.Remove("tproxy");
                }
                (stream["tlsSettings"] as JsonObject)?.Remove("certificates");
            }
            foreach (var address in AddressesOf(copy))
            {
                if (address.IndexOfAny(['/', '\\']) >= 0 || address.StartsWith('@'))
                    throw new ConfigException($"адрес сервера «{address}» похож на путь к файлу");
            }
            clean.Add(copy);
        }
        result["outbounds"] = clean;

        if (result["routing"] is JsonObject routing)
        {
            var copy = new JsonObject();
            foreach (var (key, value) in routing)
            {
                if (RoutingKeys.Contains(key) && value != null)
                    copy[key] = value.DeepClone();
            }
            result["routing"] = copy;
        }
        else
            result.Remove("routing");

        if (result["dns"] is not (null or JsonObject))
            result.Remove("dns");

        // Наборы правил из файлов (ext:файл.dat:тег) и любые file:-пути — только встроенные geoip/geosite.
        if (FindString(result, s => s.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) is { } file)
            throw new ConfigException($"ссылки на файлы в конфиге не поддерживаются ({file})");
        return result;
    }

    /// <summary>Строки конфига, которые нельзя показывать в журналах: UUID, пароли, pin сертификата.</summary>
    public static IReadOnlyList<string> SecretsOf(string config)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Walk(JsonNode.Parse(config), null, secrets);
        }
        catch (JsonException)
        {
        }
        return [.. secrets];

        static void Walk(JsonNode? node, string? name, HashSet<string> found)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                        Walk(value, key, found);
                    break;
                case JsonArray a:
                    foreach (var item in a)
                        Walk(item, name, found);
                    break;
                case JsonValue v when name != null && SecretKeys.Contains(name) && v.TryGetValue<string>(out var s) && s.Length >= 4:
                    found.Add(s);
                    break;
            }
        }
    }

    /// <summary>Адреса всех серверов конфига (домены и IP): их маршруты должны идти мимо TUN.</summary>
    public static IReadOnlyList<string> ServerHosts(XrayConfigProfile profile)
    {
        var config = JsonNode.Parse(profile.Config) as JsonObject ?? new JsonObject();
        return Outbounds(config).Where(IsNetwork).SelectMany(AddressesOf).Where(a => !IsPlaceholder(a))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Разрешает домены серверов конфига (по несколько сразу, до 5 с на каждый). Не разрешился — пропускается:
    /// тогда xray спросит свой DNS сам.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveHostsAsync(XrayConfigProfile profile, CancellationToken ct = default)
    {
        var names = ServerHosts(profile).Where(h => !IPAddress.TryParse(h, out _)).Take(32).ToList();
        var results = await Task.WhenAll(names.Select(async host =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
                return (host, ip: addresses.FirstOrDefault()?.ToString());
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return (host, ip: (string?)null);
            }
        })).ConfigureAwait(false);
        return results.Where(r => r.ip != null).ToDictionary(r => r.host, r => r.ip!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Есть ли у сервера свой DNS (тогда в TUN запросы DNS отдаются ему, а не DoH приложения).</summary>
    public static bool HasDns(XrayConfigProfile profile) =>
        JsonNode.Parse(profile.Config)?["dns"] is JsonObject { Count: > 0 };

    /// <summary>
    /// Конфиг для запуска: очищенный конфиг сервера + входы клиента, привязка к адаптеру, статистика.
    /// <paramref name="options"/>.Bypass не применяется: у сервера свои настройки обхода.
    /// </summary>
    internal static JsonObject BuildRuntime(XrayConfigProfile profile, XrayOptions options)
    {
        var config = Sanitize(JsonNode.Parse(profile.Config) as JsonObject ?? throw new ConfigException("Конфиг сервера повреждён"));
        // Автовыбор, собранный клиентом из серверов по ссылкам: маршрутов сервера в нём нет, действует свой профиль.
        var auto = AutoSelect.IsClientAuto(config);
        foreach (var key in RuntimeDropped)
            config.Remove(key);
        config["log"] = new JsonObject { ["loglevel"] = options.LogLevel, ["access"] = "none", ["dnsLog"] = false };

        var outbounds = (JsonArray)config["outbounds"]!;
        foreach (var o in outbounds.OfType<JsonObject>())
            Bind(o, options.BindInterface);
        if (options.ResolvedHosts is { Count: > 0 } resolved)
        {
            var dns = config["dns"] as JsonObject ?? new JsonObject();
            config["dns"] = dns;
            var hosts = dns["hosts"] as JsonObject ?? new JsonObject();
            dns["hosts"] = hosts;
            foreach (var (host, ip) in resolved)
            {
                // Своё сопоставление из подписки главнее.
                if (!hosts.ContainsKey(host) && System.Net.IPAddress.TryParse(ip, out _))
                    hosts[host] = new JsonArray(ip);
            }
        }

        var routing = config["routing"] as JsonObject ?? new JsonObject();
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        routing["rules"] = rules;
        config["routing"] = routing;
        if (!options.UseServerRouting && !auto)
            StripServerRouting(rules);
        if (auto && options.Routing is { } own)
            RoutingProfiles.Apply(config, own, XrayConfigBuilder.ProxyTag, options.BindInterface, rules.Count - 1, AutoSelect.BalancerTag);

        var sniffing = new JsonObject
        {
            // Домен из SNI/HTTP — только для правил (routeOnly): соединение идёт на тот же IP, что разрешил DNS.
            ["enabled"] = true,
            ["destOverride"] = new JsonArray("http", "tls", "quic"),
            ["routeOnly"] = true,
        };
        var inbounds = new JsonArray();
        if (options.Mode == XrayMode.TunBackend)
        {
            if (string.IsNullOrEmpty(options.SocksUser) || string.IsNullOrEmpty(options.SocksPassword))
                throw new ConfigException("Для TUN-режима нужен логин и пароль SOCKS");
            inbounds.Add(new JsonObject
            {
                ["tag"] = SocksInbound,
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
                ["sniffing"] = sniffing,
            });
        }
        else
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = SocksInbound,
                ["listen"] = "127.0.0.1",
                ["port"] = options.SocksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["ip"] = "127.0.0.1" },
                ["sniffing"] = sniffing.DeepClone(),
            });
            if (options.HttpPort > 0)
            {
                inbounds.Add(new JsonObject
                {
                    ["tag"] = HttpInbound,
                    ["listen"] = "127.0.0.1",
                    ["port"] = options.HttpPort,
                    ["protocol"] = "http",
                    ["sniffing"] = sniffing.DeepClone(),
                });
            }
            if (options.SplitTunnel is { IsEffective: true, Domains.Count: > 0 } split)
                AddSplitRules(outbounds, rules, split, options.BindInterface);
        }
        config["inbounds"] = inbounds;
        XrayConfigBuilder.AddMetrics(config, options.MetricsPort);
        return config;
    }

    /// <summary>
    /// Выходы для пинга (<see cref="Sessions.BatchPing"/>): основной сервер конфига под тегом <paramref name="tag"/> и,
    /// если он подключается через другой выход (dialerProxy, цепочка), — тот тоже, под своим тегом. Привязаны к адаптеру.
    /// </summary>
    internal static IReadOnlyList<JsonObject> PingOutbounds(XrayConfigProfile profile, string tag, string? bindInterface)
    {
        var config = Sanitize(JsonNode.Parse(profile.Config) as JsonObject ?? throw new ConfigException("Конфиг сервера повреждён"));
        var outbounds = Outbounds(config);
        var main = (MainOutbound(config, outbounds) ?? outbounds.FirstOrDefault(IsNetwork)
                    ?? throw new ConfigException("в конфиге нет сервера")).DeepClone().AsObject();
        main["tag"] = tag;
        var result = new List<JsonObject> { main };
        var dialer = (string?)main["streamSettings"]?["sockopt"]?["dialerProxy"] ?? (string?)main["proxySettings"]?["tag"];
        if (dialer != null && outbounds.FirstOrDefault(o => (string?)o["tag"] == dialer) is { } via)
        {
            var copy = via.DeepClone().AsObject();
            copy["tag"] = tag + "-via";
            copy["streamSettings"]?["sockopt"]?.AsObject().Remove("dialerProxy");
            if (main["streamSettings"]?["sockopt"]?["dialerProxy"] != null)
                main["streamSettings"]!["sockopt"]!["dialerProxy"] = tag + "-via";
            else
                main["proxySettings"]!["tag"] = tag + "-via";
            result.Add(copy);
        }
        else if (dialer != null)
        {
            main["streamSettings"]?["sockopt"]?.AsObject().Remove("dialerProxy");
            main.Remove("proxySettings");
        }
        foreach (var o in result)
            Bind(o, bindInterface);
        return result;
    }

    /// <summary>
    /// Все выходы, что сами ходят в сеть (серверы и прямой выход), привязываются к физическому адаптеру: иначе
    /// в TUN их соединения ушли бы в тот же туннель. Выходы через dialerProxy/proxySettings открывает другой выход.
    /// </summary>
    private static void Bind(JsonObject outbound, string? bindInterface)
    {
        if (string.IsNullOrEmpty(bindInterface) || Local.Contains((string?)outbound["protocol"] ?? ""))
            return;
        if (outbound["proxySettings"]?["tag"] != null)
            return;
        var stream = outbound["streamSettings"] as JsonObject ?? new JsonObject();
        outbound["streamSettings"] = stream;
        var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
        stream["sockopt"] = sockopt;
        if (sockopt["dialerProxy"] != null)
            return;
        sockopt["interface"] = bindInterface;
    }

    /// <summary>
    /// «Маршруты подписки выключены»: всё через сервер, кроме локальных сетей. Остаются служебные правила
    /// (DNS, цепочки по inboundTag, последнее «всё остальное» — оно указывает на сервер или автовыбор).
    /// </summary>
    internal static void StripServerRouting(JsonArray rules)
    {
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is not JsonObject rule || IsStructural(rule))
                continue;
            if (rule["ip"] is JsonArray ip && ip.All(x => (string?)x is "geoip:private"))
                continue;
            rules.RemoveAt(i);
        }
    }

    private static bool IsStructural(JsonObject rule) =>
        rule["inboundTag"] != null || (string?)rule["port"] == "53" || IsCatchAll(rule);

    /// <summary>Правило «всё остальное»: без условий, кроме вида сети.</summary>
    private static bool IsCatchAll(JsonObject rule) =>
        rule.All(kv => kv.Key is "type" or "network" or "outboundTag" or "balancerTag" or "ruleTag");

    /// <summary>Сайты из раздельного туннелирования в режиме прокси — выше правил сервера, но после служебных.</summary>
    private static void AddSplitRules(JsonArray outbounds, JsonArray rules, SplitTunnelOptions split, string? bindInterface)
    {
        var direct = new JsonObject { ["tag"] = OwnDirect, ["protocol"] = "freedom" };
        Bind(direct, bindInterface);
        outbounds.Add(direct);

        var at = 0;
        while (at < rules.Count && rules[at] is JsonObject r && (r["inboundTag"] != null || (string?)r["port"] == "53"))
            at++;
        var domains = XrayConfigBuilder.ToJsonArray(split.Domains.Select(d => "domain:" + d));
        if (split.Mode == SplitTunnelMode.ExcludeSelected)
        {
            rules.Insert(at, new JsonObject { ["type"] = "field", ["domain"] = domains, ["outboundTag"] = OwnDirect });
            return;
        }
        // «Только выбранное через VPN»: выбранные сайты — туда же, куда «всё остальное» у сервера; остальное напрямую.
        var final = rules.OfType<JsonObject>().LastOrDefault(IsCatchAll);
        var toServer = new JsonObject { ["type"] = "field", ["domain"] = domains };
        if (final?["balancerTag"] is { } balancer)
            toServer["balancerTag"] = balancer.DeepClone();
        else
            toServer["outboundTag"] = final?["outboundTag"]?.DeepClone()
                                      ?? (JsonNode?)outbounds.OfType<JsonObject>().FirstOrDefault(IsNetwork)?["tag"]?.DeepClone();
        rules.Insert(at, toServer);
        rules.Insert(at + 1, new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = OwnDirect });
    }

    private static List<JsonObject> Outbounds(JsonObject config) =>
        (config["outbounds"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    /// <summary>Сервер (а не blackhole, dns, loopback или прямой выход).</summary>
    private static bool IsNetwork(JsonObject o) => (string?)o["protocol"] is { } p && !Local.Contains(p) && p != "freedom";

    /// <summary>
    /// Основной сервер — куда ведёт последнее правило «всё остальное» (напрямую или через автовыбор);
    /// без правил xray шлёт всё в первый выход.
    /// </summary>
    private static JsonObject? MainOutbound(JsonObject config, List<JsonObject> outbounds)
    {
        var final = (config["routing"]?["rules"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(IsCatchAll);
        if ((string?)final?["outboundTag"] is { } tag)
            return outbounds.FirstOrDefault(o => (string?)o["tag"] == tag && IsNetwork(o));
        if ((string?)final?["balancerTag"] is { } balancerTag
            && (config["routing"]?["balancers"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(b => (string?)b["tag"] == balancerTag) is { } balancer)
        {
            var selectors = (balancer["selector"] as JsonArray)?.Select(s => (string?)s).OfType<string>().ToList() ?? [];
            return outbounds.FirstOrDefault(o => IsNetwork(o) && (string?)o["tag"] is { } t && selectors.Any(s => t.StartsWith(s, StringComparison.Ordinal)));
        }
        return outbounds.FirstOrDefault() is { } first && IsNetwork(first) ? first : null;
    }

    /// <summary>Адрес и порт сервера: vnext/servers (старая запись) или address/port прямо в settings.</summary>
    private static (string Host, int Port) Address(JsonObject outbound)
    {
        var settings = outbound["settings"] as JsonObject;
        var entry = (settings?["vnext"] as JsonArray)?.FirstOrDefault() as JsonObject
                    ?? (settings?["servers"] as JsonArray)?.FirstOrDefault() as JsonObject
                    ?? settings;
        var host = (string?)entry?["address"] ?? "?";
        var port = entry?["port"] is JsonValue p && p.TryGetValue<int>(out var n) ? n : 0;
        return (host, port);
    }

    private static IEnumerable<string> AddressesOf(JsonObject outbound)
    {
        var settings = outbound["settings"] as JsonObject;
        if (settings == null)
            yield break;
        if ((string?)settings["address"] is { } a)
            yield return a;
        foreach (var list in new[] { settings["vnext"], settings["servers"], settings["peers"] }.OfType<JsonArray>())
        {
            foreach (var entry in list.OfType<JsonObject>())
            {
                if ((string?)(entry["address"] ?? entry["endpoint"]) is { } e)
                    yield return e.Contains(':') && !IPAddress.TryParse(e, out _) ? e[..e.LastIndexOf(':')] : e;
            }
        }
    }

    private static bool IsPlaceholder(string host) =>
        host is "127.0.0.1" or "localhost" or "0.0.0.0" or "::1" or "?" || host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>«VLESS XHTTP REALITY», «Trojan gRPC TLS», «Hysteria2».</summary>
    private static string Label(JsonObject outbound)
    {
        var protocol = (string?)outbound["protocol"] ?? "?";
        if (protocol == "hysteria")
            return "Hysteria2";
        var stream = outbound["streamSettings"] as JsonObject;
        var network = (string?)stream?["network"] ?? "tcp";
        var flow = (string?)(outbound["settings"]?["vnext"]?[0]?["users"]?[0]?["flow"] ?? outbound["settings"]?["flow"]);
        var transport = flow?.Contains("vision", StringComparison.Ordinal) == true ? "Vision" : network switch
        {
            "xhttp" or "splithttp" => "XHTTP",
            "grpc" => "gRPC",
            "ws" => "WebSocket",
            "httpupgrade" => "HTTPUpgrade",
            "raw" or "tcp" => "TCP",
            var other => other,
        };
        var security = (string?)stream?["security"] switch { "reality" => "REALITY", "tls" => "TLS", _ => "" };
        var name = protocol switch { "vless" => "VLESS", "vmess" => "VMess", "trojan" => "Trojan", "shadowsocks" => "Shadowsocks", var p => p };
        return $"{name} {transport} {security}".TrimEnd();
    }

    private static string? Remarks(JsonObject config) =>
        config["remarks"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string? FindString(JsonNode? node, Func<string, bool> match) => node switch
    {
        JsonObject o => o.Select(kv => FindString(kv.Value, match)).FirstOrDefault(s => s != null),
        JsonArray a => a.Select(x => FindString(x, match)).FirstOrDefault(s => s != null),
        JsonValue v when v.TryGetValue<string>(out var s) && match(s) => s,
        _ => null,
    };
}
