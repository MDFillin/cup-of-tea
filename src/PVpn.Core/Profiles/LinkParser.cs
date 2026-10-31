using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PVpn.Core.Profiles;

public sealed class LinkParseException(string message) : Exception(message);

public sealed record ImportError(int Line, string Message);

public sealed record ImportResult(IReadOnlyList<Profile> Profiles, IReadOnlyList<ImportError> Errors);

/// <summary>Разбор ссылок vless:// и hy2:// (hysteria2:// — то же самое).</summary>
public static partial class LinkParser
{
    [GeneratedRegex(@"(?:vless|hy2|hysteria2)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// <summary>
    /// Достаёт все ссылки из произвольного текста (по одной или несколько в строке, с подписями вокруг).
    /// Если ссылок нет, пробует раскодировать текст как base64-подписку.
    /// </summary>
    public static ImportResult ParseMany(string text)
    {
        var profiles = new List<Profile>();
        var errors = new List<ImportError>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match m in LinkRegex().Matches(lines[i]))
            {
                try
                {
                    profiles.Add(Parse(m.Value));
                }
                catch (LinkParseException e)
                {
                    errors.Add(new ImportError(i + 1, e.Message));
                }
            }
        }

        if (profiles.Count == 0 && errors.Count == 0 && TryDecodeBase64(text.Trim(), out var decoded) && LinkRegex().IsMatch(decoded))
            return ParseMany(decoded);

        return new ImportResult(profiles, errors);
    }

    public static Profile Parse(string link)
    {
        link = link.Trim();
        var raw = Split(link);
        return raw.Scheme switch
        {
            "vless" => ParseVless(raw, link),
            "hy2" or "hysteria2" => ParseHysteria2(raw, link),
            _ => throw new LinkParseException($"Неизвестный тип ссылки: {raw.Scheme}://"),
        };
    }

    private static VlessProfile ParseVless(RawLink r, string link)
    {
        if (string.IsNullOrEmpty(r.UserInfo))
            throw new LinkParseException("В ссылке vless нет идентификатора пользователя (UUID)");

        var q = r.Query;
        var network = (q.GetValueOrDefault("type") ?? "tcp").ToLowerInvariant() switch
        {
            "tcp" or "raw" => "raw",
            "xhttp" or "splithttp" => "xhttp",
            var other => throw new LinkParseException($"Транспорт «{other}» пока не поддерживается (есть tcp и xhttp)"),
        };

        var headerType = q.GetValueOrDefault("headerType");
        if (network == "raw" && headerType is not (null or "" or "none"))
            throw new LinkParseException($"Маскировка tcp headerType={headerType} не поддерживается");

        var security = (q.GetValueOrDefault("security") ?? "none").ToLowerInvariant();
        if (security is not ("reality" or "tls" or "none"))
            throw new LinkParseException($"Защита «{security}» не поддерживается (есть reality, tls и none)");

        var publicKey = q.GetValueOrDefault("pbk");
        var sni = NullIfEmpty(q.GetValueOrDefault("sni")) ?? NullIfEmpty(q.GetValueOrDefault("peer"));
        if (security == "reality" && string.IsNullOrEmpty(publicKey))
            throw new LinkParseException("В ссылке REALITY нет открытого ключа сервера (pbk)");
        if (security == "reality" && sni == null)
            throw new LinkParseException("В ссылке REALITY нет имени сайта для маскировки (sni)");

        var extra = q.GetValueOrDefault("extra");
        if (!string.IsNullOrEmpty(extra))
        {
            try { using var _ = JsonDocument.Parse(extra); }
            catch (JsonException) { throw new LinkParseException("Параметр extra в ссылке — не JSON"); }
        }

        return new VlessProfile
        {
            Name = NameOrDefault(r),
            Host = r.Host,
            Port = r.Port,
            SourceLink = link,
            Uuid = r.UserInfo,
            Encryption = NullIfEmpty(q.GetValueOrDefault("encryption")) ?? "none",
            Flow = NullIfEmpty(q.GetValueOrDefault("flow")),
            Network = network,
            Security = security,
            Sni = sni,
            Fingerprint = NullIfEmpty(q.GetValueOrDefault("fp")),
            Alpn = NullIfEmpty(q.GetValueOrDefault("alpn")),
            PublicKey = NullIfEmpty(publicKey),
            ShortId = NullIfEmpty(q.GetValueOrDefault("sid")),
            SpiderX = NullIfEmpty(q.GetValueOrDefault("spx")),
            XhttpPath = network == "xhttp" ? NullIfEmpty(q.GetValueOrDefault("path")) ?? "/" : null,
            XhttpHost = network == "xhttp" ? NullIfEmpty(q.GetValueOrDefault("host")) : null,
            XhttpMode = network == "xhttp" ? NullIfEmpty(q.GetValueOrDefault("mode")) ?? "auto" : null,
            XhttpExtra = network == "xhttp" ? NullIfEmpty(extra) : null,
        };
    }

    private static Hysteria2Profile ParseHysteria2(RawLink r, string link)
    {
        if (string.IsNullOrEmpty(r.UserInfo))
            throw new LinkParseException("В ссылке hy2 нет пароля (часть до @)");

        var q = r.Query;
        // Диапазон портов: в адресе (официальный формат Hysteria) или в параметре mport (так пишут многие клиенты).
        var hopPorts = r.PortSpec;
        if (NullIfEmpty(q.GetValueOrDefault("mport")) is { } mport)
        {
            if (NormalizePorts(mport) is { } fromParam)
                hopPorts = fromParam;
            else if (!(int.TryParse(mport.Trim(), out var single) && single is >= 1 and <= 65535))
                throw new LinkParseException($"Неверный диапазон портов mport=«{mport}»");
        }

        var obfs = q.GetValueOrDefault("obfs")?.ToLowerInvariant();
        string? obfsPassword = null;
        if (obfs == "salamander")
        {
            obfsPassword = NullIfEmpty(q.GetValueOrDefault("obfs-password"))
                           ?? throw new LinkParseException("Обфускация salamander без пароля (obfs-password)");
        }
        else if (obfs is not (null or "" or "none"))
            throw new LinkParseException($"Обфускация hy2 «{obfs}» не поддерживается (есть salamander)");

        var pin = NullIfEmpty(q.GetValueOrDefault("pinSHA256"));
        if (pin != null && !IsSha256Hex(pin))
            throw new LinkParseException("pinSHA256 в ссылке hy2 — не SHA-256 в hex");

        var insecure = q.GetValueOrDefault("insecure") is "1" or "true";
        if (insecure && pin == null)
            throw new LinkParseException("Ссылка hy2 с insecure=1 без pinSHA256: Xray не подключается без проверки сертификата");

        return new Hysteria2Profile
        {
            Name = NameOrDefault(r),
            Host = r.Host,
            Port = r.Port,
            SourceLink = link,
            Auth = r.UserInfo,
            Sni = NullIfEmpty(q.GetValueOrDefault("sni")) ?? NullIfEmpty(q.GetValueOrDefault("peer")),
            PinSha256 = pin,
            Insecure = insecure,
            Alpn = NullIfEmpty(q.GetValueOrDefault("alpn")),
            HopPorts = hopPorts,
            ObfsPassword = obfsPassword,
        };
    }

    /// <param name="PortSpec">Несколько портов в адресе (только для hy2), например «443,20000-30000»; иначе null.</param>
    private sealed record RawLink(string Scheme, string? UserInfo, string Host, int Port, string? PortSpec,
        Dictionary<string, string> Query, string Fragment);

    private static RawLink Split(string link)
    {
        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            throw new LinkParseException("Это не ссылка: нет «://»");
        var scheme = link[..schemeEnd].ToLowerInvariant();
        var rest = link[(schemeEnd + 3)..];

        var fragment = "";
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            fragment = Unescape(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var queryText = "";
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            queryText = rest[(question + 1)..];
            rest = rest[..question];
        }

        string? userInfo = null;
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            userInfo = Unescape(rest[..at]);
            rest = rest[(at + 1)..];
        }

        var slash = rest.IndexOf('/');
        if (slash >= 0)
            rest = rest[..slash];

        string host, portText;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                throw new LinkParseException("Неверный IPv6-адрес сервера");
            host = rest[1..close];
            portText = rest[(close + 1)..].TrimStart(':');
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            if (colon < 0)
                throw new LinkParseException("В ссылке нет порта сервера");
            host = rest[..colon];
            portText = rest[(colon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(host))
            throw new LinkParseException("В ссылке нет адреса сервера");
        string? portSpec = null;
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            // Hysteria2 умеет менять порты: «host:443,20000-30000». Основной порт — первый.
            portSpec = scheme is "hy2" or "hysteria2" ? NormalizePorts(portText) : null;
            if (portSpec == null)
                throw new LinkParseException(scheme is "hy2" or "hysteria2"
                    ? $"Неверный порт «{portText}» (нужен номер или диапазон, например 20000-30000)"
                    : $"Неверный порт «{portText}» (несколько портов бывают только у hy2)");
            port = int.Parse(portSpec.Split(',', '-')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Unescape(eq >= 0 ? pair[..eq] : pair);
            var value = eq >= 0 ? Unescape(pair[(eq + 1)..]) : "";
            query[key] = value;
        }

        return new RawLink(scheme, userInfo, host, port, portSpec, query, fragment);
    }

    /// <summary>
    /// Список портов «443,20000-30000» (до 16 частей, порты 1–65535; «:» как разделитель диапазона тоже допустим)
    /// в виде для Xray; null — неверный формат или это один порт.
    /// </summary>
    internal static string? NormalizePorts(string text)
    {
        var parts = text.Replace(" ", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 16)
            return null;
        var result = new List<string>();
        var total = 0;
        foreach (var part in parts)
        {
            var bounds = part.Split('-', ':');
            if (bounds.Length > 2
                || !int.TryParse(bounds[0], out var from) || from is < 1 or > 65535)
                return null;
            var to = from;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1], out to) || to < from || to > 65535))
                return null;
            total += to - from + 1;
            result.Add(from == to ? from.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{from}-{to}");
        }
        return total > 1 ? string.Join(',', result) : null;
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    private static string NameOrDefault(RawLink r) =>
        string.IsNullOrWhiteSpace(r.Fragment) ? $"{r.Host}:{r.Port}" : r.Fragment.Trim();

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    internal static bool IsSha256Hex(string value)
    {
        var hex = value.Replace(":", "");
        return hex.Length == 64 && hex.All(Uri.IsHexDigit);
    }

    internal static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = "";
        if (text.Length == 0 || text.Contains("://"))
            return false;
        var normalized = text.Replace('-', '+').Replace('_', '/').Replace("\n", "").Replace("\r", "");
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
