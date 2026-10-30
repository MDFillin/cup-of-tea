using System.Net;
using System.Text.Json.Serialization;

namespace PVpn.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter<DnsProvider>))]
public enum DnsProvider
{
    Cloudflare,
    Google,
    Quad9,

    /// <summary>AdGuard DNS: блокирует рекламу и трекеры.</summary>
    AdGuard,

    /// <summary>Свой адрес DNS-over-HTTPS (<see cref="DnsOptions.CustomUrl"/>).</summary>
    Custom,
}

/// <summary>
/// DNS в режиме TUN: запросы всех программ перехватывает sing-box и отправляет по DNS-over-HTTPS через туннель.
/// В режиме прокси адреса сайтов разрешает сервер VPN, выбор DNS там не действует.
/// Свойства с set — см. <see cref="BypassOptions"/>.
/// </summary>
public sealed record DnsOptions
{
    public DnsProvider Provider { get; set; } = DnsProvider.Cloudflare;

    /// <summary>Для <see cref="DnsProvider.Custom"/>: «https://dns.example.com/dns-query» или «1.2.3.4».</summary>
    public string? CustomUrl { get; set; }
}

/// <summary>Сервер DNS-over-HTTPS. <see cref="Host"/> — IP или домен; <see cref="ServerName"/> — имя для TLS, если хост — IP.</summary>
public sealed record DohServer(string Host, int Port, string Path, string? ServerName)
{
    public bool HostIsIp => IPAddress.TryParse(Host, out _);
    public override string ToString() => $"https://{(ServerName ?? Host)}{(Port == 443 ? "" : $":{Port}")}{Path}";
}

public static class DnsRules
{
    /// <summary>Готовые серверы: по IP (не нужен отдельный DNS, чтобы найти сам DNS) и с именем для проверки сертификата.</summary>
    public static DohServer Preset(DnsProvider provider) => provider switch
    {
        DnsProvider.Cloudflare => new("1.1.1.1", 443, "/dns-query", "cloudflare-dns.com"),
        DnsProvider.Google => new("8.8.8.8", 443, "/dns-query", "dns.google"),
        DnsProvider.Quad9 => new("9.9.9.9", 443, "/dns-query", "dns.quad9.net"),
        DnsProvider.AdGuard => new("94.140.14.14", 443, "/dns-query", "dns.adguard-dns.com"),
        _ => throw new ConfigException("У своего DNS нет готового адреса"),
    };

    /// <summary>
    /// Как называется сеть (ASN) у серверов этого провайдера в базах геолокации: по этим словам проверка утечки
    /// узнаёт «свои» DNS. Для своего адреса — null: проверить нечем.
    /// </summary>
    public static IReadOnlyList<string>? OrgKeywords(DnsProvider provider) => provider switch
    {
        DnsProvider.Cloudflare => ["cloudflare"],
        DnsProvider.Google => ["google"],
        DnsProvider.Quad9 => ["quad9", "woodynet", "pch"],
        // Серверы AdGuard DNS стоят в сети DataCamp (CDN77) и в базах значатся под её именем.
        DnsProvider.AdGuard => ["adguard", "datacamp"],
        _ => null,
    };

    public static DohServer Resolve(DnsOptions? options) => options is { Provider: DnsProvider.Custom }
        ? ParseCustom(options.CustomUrl)
        : Preset(options?.Provider ?? DnsProvider.Cloudflare);

    /// <summary>Проверяет настройки (служба не доверяет тому, что пришло по каналу).</summary>
    public static DnsOptions Validate(DnsOptions options)
    {
        if (!Enum.IsDefined(options.Provider))
            throw new ConfigException("Неизвестный DNS-провайдер");
        if (options.Provider != DnsProvider.Custom)
            return options with { CustomUrl = options.CustomUrl?.Trim() };
        return options with { CustomUrl = ParseCustom(options.CustomUrl).ToString() };
    }

    /// <summary>«https://host[:порт]/путь», «host» или «1.2.3.4» (путь по умолчанию /dns-query). Только HTTPS.</summary>
    public static DohServer ParseCustom(string? url)
    {
        var s = url?.Trim() ?? "";
        if (s.Length == 0)
            throw new ConfigException("Свой DNS: укажите адрес DNS-over-HTTPS, например https://dns.example.com/dns-query");
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ConfigException($"Свой DNS: «{url}» — нужен адрес DNS-over-HTTPS (https://…)");
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
            throw new ConfigException($"Свой DNS: «{url}» — неверный адрес");
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
            throw new ConfigException("Свой DNS: адрес IPv6 не поддерживается (туннель работает только по IPv4)");
        var path = uri.AbsolutePath is "" or "/" ? "/dns-query" : uri.AbsolutePath;
        if (uri.Query.Length > 0)
            path += uri.Query;
        return new DohServer(host, uri.Port, path, null);
    }
}
