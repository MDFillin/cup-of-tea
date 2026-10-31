using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PVpn.Core.Config;

namespace PVpn.Core.Routing;

/// <summary>
/// Профиль маршрутизации: какие сайты и адреса идут напрямую, через VPN или блокируются. Формат JSON — как у
/// распространённых клиентов (DirectSites, ProxyIp, BlockSites…, GlobalProxy), поэтому профиль можно получить
/// от панели подписки (заголовок routing) или ссылкой. Правила понимают geosite:/geoip: (базы рядом с xray),
/// домены (с поддоменами), full:/domain:/keyword:/regexp: и IP/CIDR.
/// Действует на серверы по ссылкам; у серверов с полным конфигом из подписки — их собственные маршруты.
/// </summary>
public sealed record RoutingProfile
{
    public string Name { get; set; } = "Свой профиль";

    /// <summary>Что не попало ни под одно правило: true — через VPN, false — напрямую.</summary>
    public bool GlobalProxy { get; set; } = true;

    public List<string> DirectSites { get; set; } = [];
    public List<string> DirectIp { get; set; } = [];
    public List<string> ProxySites { get; set; } = [];
    public List<string> ProxyIp { get; set; } = [];
    public List<string> BlockSites { get; set; } = [];
    public List<string> BlockIp { get; set; } = [];

    /// <summary>AsIs, IPIfNonMatch или IPOnDemand (как в xray).</summary>
    public string DomainStrategy { get; set; } = "AsIs";

    /// <summary>Unix-время последнего изменения у источника: новее — профиль обновляется.</summary>
    public long LastUpdated { get; set; }

    [JsonIgnore]
    public int RuleCount => DirectSites.Count + DirectIp.Count + ProxySites.Count + ProxyIp.Count + BlockSites.Count + BlockIp.Count;
}

public static partial class RoutingProfiles
{
    public const int MaxRules = 5000;
    public const string DirectTag = "route-direct";
    public const string BlockTag = "route-block";

    /// <summary>Готовый профиль: российские сайты и адреса — напрямую (госуслуги, банки, маркетплейсы не любят чужой IP).</summary>
    public static RoutingProfile RussiaDirect => new()
    {
        Name = "Россия напрямую",
        DirectSites = ["geosite:category-ru", "regexp:\\.ru$", "regexp:\\.su$", "regexp:\\.xn--p1ai$"],
        DirectIp = ["geoip:ru"],
    };

    /// <summary>Готовый профиль: российские сайты напрямую и без рекламы и трекеров.</summary>
    public static RoutingProfile RussiaDirectNoAds => RussiaDirect with
    {
        Name = "Россия напрямую, без рекламы",
        BlockSites = ["geosite:category-ads-all"],
    };

    /// <summary>Готовый профиль: только блокировка рекламы и трекеров, всё остальное как обычно.</summary>
    public static RoutingProfile NoAds => new() { Name = "Без рекламы", BlockSites = ["geosite:category-ads-all"] };

    public static IReadOnlyList<RoutingProfile> Presets => [RussiaDirect, RussiaDirectNoAds, NoAds];

    /// <summary>
    /// Профиль из того, что дала панель или пользователь: JSON, base64 от JSON или ссылка вида
    /// «схема://routing/onadd/&lt;base64&gt;» (также add/…). null — это не профиль.
    /// </summary>
    public static RoutingProfile? TryParse(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;
        var link = RoutingLinkRegex().Match(s);
        if (link.Success)
            s = Uri.UnescapeDataString(link.Groups[1].Value);
        if (!s.StartsWith('{') && Profiles.LinkParser.TryDecodeBase64(s, out var decoded))
            s = decoded.Trim();
        if (!s.StartsWith('{'))
            return null;
        try
        {
            var node = JsonNode.Parse(s) as JsonObject;
            return node == null ? null : Validate(FromJson(node));
        }
        catch (Exception e) when (e is JsonException or ConfigException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Поля читаются без учёта регистра; значения бывают строками («"true"») — как их пишут панели.</summary>
    private static RoutingProfile FromJson(JsonObject o)
    {
        JsonNode? Get(params string[] names) =>
            o.FirstOrDefault(kv => names.Any(n => string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase))).Value;
        static string? Str(JsonNode? n) => n is JsonValue v ? v.ToString() : null;
        static List<string> List(JsonNode? n) => n switch
        {
            JsonArray a => a.Select(x => Str(x)?.Trim()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList(),
            JsonValue v => v.ToString().Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            _ => [],
        };
        return new RoutingProfile
        {
            Name = Str(Get("Name"))?.Trim() is { Length: > 0 } name ? name : "Профиль подписки",
            GlobalProxy = !string.Equals(Str(Get("GlobalProxy")), "false", StringComparison.OrdinalIgnoreCase),
            DirectSites = List(Get("DirectSites")),
            DirectIp = List(Get("DirectIp", "DirectIP")),
            ProxySites = List(Get("ProxySites")),
            ProxyIp = List(Get("ProxyIp", "ProxyIP")),
            BlockSites = List(Get("BlockSites")),
            BlockIp = List(Get("BlockIp", "BlockIP")),
            DomainStrategy = Str(Get("DomainStrategy")) ?? "AsIs",
            LastUpdated = long.TryParse(Str(Get("LastUpdated")), out var t) ? t : 0,
        };
    }

    /// <summary>
    /// Проверяет правила (служба не доверяет тому, что пришло по каналу): только встроенные базы geosite/geoip,
    /// домены, IP и CIDR; никаких ext:-файлов. Неверные элементы — ошибка, а не молчаливый пропуск.
    /// </summary>
    public static RoutingProfile Validate(RoutingProfile p)
    {
        if (p.RuleCount > MaxRules)
            throw new ConfigException($"В профиле маршрутизации больше {MaxRules} правил");
        if (p.DomainStrategy is not ("AsIs" or "IPIfNonMatch" or "IPOnDemand"))
            throw new ConfigException($"Неизвестная стратегия DomainStrategy «{p.DomainStrategy}»");
        return p with
        {
            Name = p.Name.Length > 60 ? p.Name[..60] : p.Name,
            DirectSites = Sites(p.DirectSites), ProxySites = Sites(p.ProxySites), BlockSites = Sites(p.BlockSites),
            DirectIp = Ips(p.DirectIp), ProxyIp = Ips(p.ProxyIp), BlockIp = Ips(p.BlockIp),
        };
    }

    private static List<string> Sites(IEnumerable<string>? items) => (items ?? []).Select(Site).Distinct(StringComparer.Ordinal).ToList();

    private static List<string> Ips(IEnumerable<string>? items) => (items ?? []).Select(Ip).Distinct(StringComparer.Ordinal).ToList();

    private static string Site(string raw)
    {
        var s = raw.Trim();
        if (GeoTagRegex().IsMatch(s) && s.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
            return s.ToLowerInvariant();
        foreach (var prefix in new[] { "regexp:", "keyword:", "full:", "domain:" })
        {
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = s[prefix.Length..];
            if (value.Length is 0 or > 300)
                break;
            if (prefix == "regexp:")
            {
                _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                return prefix + value;
            }
            return prefix == "keyword:" ? prefix + value.ToLowerInvariant() : prefix + SplitTunnelRules.NormalizeDomain(value);
        }
        try
        {
            return "domain:" + SplitTunnelRules.NormalizeDomain(s);
        }
        catch (FormatException)
        {
            throw new ConfigException($"Профиль маршрутизации: «{raw}» — не сайт и не geosite");
        }
    }

    private static string Ip(string raw)
    {
        var s = raw.Trim();
        if (GeoTagRegex().IsMatch(s) && s.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase))
            return s.ToLowerInvariant();
        try
        {
            var (address, prefix) = Net.NetworkInfo.ParseCidr(s);
            return s.Contains('/') ? $"{address}/{prefix}" : address.ToString();
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ConfigException($"Профиль маршрутизации: «{raw}» — не IP, не подсеть и не geoip");
        }
    }

    /// <summary>
    /// Добавляет правила профиля в конфиг xray (сервер по ссылке). Правила ставятся после <paramref name="insertAt"/>
    /// (служебных правил клиента — локальные сети, раздельное туннелирование), но перед «всё остальное».
    /// Прямой выход привязывается к физическому адаптеру — иначе в TUN он ушёл бы в тот же туннель.
    /// </summary>
    /// <param name="balancerTag">«Через VPN» — на балансировщик (автовыбор), а не на выход <paramref name="proxyTag"/>.</param>
    internal static void Apply(JsonObject config, RoutingProfile profile, string proxyTag, string? bindInterface, int insertAt,
        string? balancerTag = null)
    {
        var outbounds = (JsonArray)config["outbounds"]!;
        var direct = new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" };
        if (!string.IsNullOrEmpty(bindInterface))
            direct["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = bindInterface } };
        outbounds.Add(direct);
        outbounds.Add(new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" });

        var routing = config["routing"] as JsonObject ?? new JsonObject { ["domainStrategy"] = "AsIs" };
        config["routing"] = routing;
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        routing["rules"] = rules;
        if (profile.DomainStrategy != "AsIs")
            routing["domainStrategy"] = profile.DomainStrategy;

        // Порядок как у других клиентов: блокировка, затем через VPN, затем напрямую.
        var added = new List<JsonObject>();
        void Add(string key, List<string> items, string outbound)
        {
            if (items.Count == 0)
                return;
            var rule = new JsonObject { ["type"] = "field", [key] = XrayConfigBuilder.ToJsonArray(items) };
            if (outbound == proxyTag && balancerTag != null)
                rule["balancerTag"] = balancerTag;
            else
                rule["outboundTag"] = outbound;
            added.Add(rule);
        }
        Add("domain", profile.BlockSites, BlockTag);
        Add("ip", profile.BlockIp, BlockTag);
        Add("domain", profile.ProxySites, proxyTag);
        Add("ip", profile.ProxyIp, proxyTag);
        Add("domain", profile.DirectSites, DirectTag);
        Add("ip", profile.DirectIp, DirectTag);
        if (!profile.GlobalProxy)
            added.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = DirectTag });

        var at = Math.Clamp(insertAt, 0, rules.Count);
        foreach (var rule in added)
            rules.Insert(at++, rule);
    }

    /// <summary>
    /// Автообновляемый профиль: «схема://autorouting/onadd/https://…» (или просто https://…) → адрес, откуда его брать.
    /// null — это не такая ссылка. Только https.
    /// </summary>
    public static Uri? AutoroutingUrl(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        var m = AutoroutingLinkRegex().Match(s);
        if (m.Success)
            s = Uri.UnescapeDataString(m.Groups[1].Value);
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
    }

    /// <summary>Скачивает профиль по адресу автообновления (до 2 МБ, только https); null — там не профиль.</summary>
    public static async Task<RoutingProfile?> FetchAsync(HttpClient http, Uri url, CancellationToken ct = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            return null;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 2 * 1024 * 1024)
            return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length > 2 * 1024 * 1024 ? null : TryParse(body);
    }

    /// <summary>Профиль как JSON — для экспорта и хранения у подписки.</summary>
    public static string ToJson(RoutingProfile p) => JsonSerializer.Serialize(p, RoutingJsonContext.Default.RoutingProfile);

    [GeneratedRegex(@"^(geosite|geoip):!?[a-z0-9._@!-]{1,64}$", RegexOptions.IgnoreCase)]
    private static partial Regex GeoTagRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://routing/(?:onadd|add)/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoutingLinkRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://autorouting/(?:onadd|add)/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AutoroutingLinkRegex();
}

[JsonSerializable(typeof(RoutingProfile))]
internal partial class RoutingJsonContext : JsonSerializerContext;
