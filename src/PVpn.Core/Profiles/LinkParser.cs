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
    [GeneratedRegex(@"(?<![a-z0-9])(?:vless|hy2|hysteria2|trojan|vmess|ss)://\S+", RegexOptions.IgnoreCase)]
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
        // vmess:// чаще всего — base64 от JSON (формат v2rayN), а не обычная ссылка.
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return ParseVmess(link);
        // ss:// старого вида целиком в base64 («ss://base64(метод:пароль@хост:порт)#имя») — Split разобрать не сможет.
        if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            return ParseShadowsocks(link);
        var raw = Split(link);
        return raw.Scheme switch
        {
            "vless" => ParseVless(raw, link),
            "hy2" or "hysteria2" => ParseHysteria2(raw, link),
            "trojan" => ParseTrojan(raw, link),
            "ss" => ParseShadowsocks(link),
            _ => throw new LinkParseException($"Неизвестный тип ссылки: {raw.Scheme}://"),
        };
    }

    private static VlessProfile ParseVless(RawLink r, string link)
    {
        if (string.IsNullOrEmpty(r.UserInfo))
            throw new LinkParseException("В ссылке vless нет идентификатора пользователя (UUID)");
        var q = r.Query;
        var profile = new VlessProfile
        {
            Name = NameOrDefault(r),
            Host = r.Host,
            Port = r.Port,
            SourceLink = link,
            Uuid = r.UserInfo,
            Encryption = NullIfEmpty(q.GetValueOrDefault("encryption")) ?? "none",
            Flow = NullIfEmpty(q.GetValueOrDefault("flow")),
        };
        return (VlessProfile)profile.WithStream(ParseStream(q, defaultSecurity: "none"));
    }

    /// <summary>trojan://пароль@хост:порт?security=tls&amp;sni=…&amp;type=ws&amp;path=…#имя (по умолчанию TLS).</summary>
    private static TrojanProfile ParseTrojan(RawLink r, string link)
    {
        if (string.IsNullOrEmpty(r.UserInfo))
            throw new LinkParseException("В ссылке trojan нет пароля (часть до @)");
        var profile = new TrojanProfile { Name = NameOrDefault(r), Host = r.Host, Port = r.Port, SourceLink = link, Password = r.UserInfo };
        return (TrojanProfile)profile.WithStream(ParseStream(r.Query, defaultSecurity: "tls"));
    }

    /// <summary>
    /// vmess://base64(JSON) — формат v2rayN (ps, add, port, id, scy, net, type, host, path, tls, sni, alpn, fp),
    /// или обычная ссылка vmess://uuid@хост:порт?type=…&amp;security=…#имя.
    /// </summary>
    private static VmessProfile ParseVmess(string link)
    {
        var body = link[8..];
        var hash = body.IndexOf('#');
        if (TryDecodeBase64(hash >= 0 ? body[..hash] : body, out var json) && json.TrimStart().StartsWith('{'))
        {
            JsonElement o;
            try
            {
                o = JsonDocument.Parse(json).RootElement;
            }
            catch (JsonException)
            {
                throw new LinkParseException("Ссылка vmess: в base64 не JSON");
            }
            string? Get(string name) => o.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => NullIfEmpty(v.GetString()?.Trim()),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            } : null;
            var host = Get("add") ?? throw new LinkParseException("В ссылке vmess нет адреса сервера (add)");
            if (!int.TryParse(Get("port"), out var port) || port is < 1 or > 65535)
                throw new LinkParseException("В ссылке vmess неверный порт");
            var id = Get("id") ?? throw new LinkParseException("В ссылке vmess нет UUID (id)");
            var net = Get("net") ?? "tcp";
            // Параметры JSON — в виде, как у обычной ссылки, чтобы транспорт разбирался одним кодом.
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = net };
            void Put(string key, string? value)
            {
                if (value != null)
                    q[key] = value;
            }
            Put("security", Get("tls"));
            Put("sni", Get("sni"));
            Put("alpn", Get("alpn"));
            Put("fp", Get("fp"));
            Put("host", Get("host"));
            Put("path", Get("path"));
            Put("headerType", Get("type"));
            Put("pbk", Get("pbk"));
            Put("sid", Get("sid"));
            Put("allowInsecure", Get("allowInsecure"));
            Put("pcs", Get("pcs"));
            Put("vcn", Get("vcn"));
            if (net == "grpc")
            {
                Put("serviceName", Get("path"));
                Put("mode", Get("type") == "multi" ? "multi" : null);
                q.Remove("headerType");
            }
            var name = Get("ps") ?? (hash >= 0 ? Unescape(body[(hash + 1)..]).Trim() : null) ?? $"{host}:{port}";
            var profile = new VmessProfile
            {
                Name = name, Host = host.Trim('[', ']'), Port = port, SourceLink = link, Uuid = id,
                Cipher = NormalizeVmessCipher(Get("scy")),
            };
            return (VmessProfile)profile.WithStream(ParseStream(q, defaultSecurity: "none"));
        }

        var r = Split(link);
        if (string.IsNullOrEmpty(r.UserInfo))
            throw new LinkParseException("В ссылке vmess нет UUID");
        // В обычной ссылке vmess «security» — шифрование VMess, а защита транспорта — в «tls»/«security=tls|reality».
        var query = new Dictionary<string, string>(r.Query, StringComparer.OrdinalIgnoreCase);
        var cipher = query.GetValueOrDefault("encryption");
        if (query.GetValueOrDefault("security") is { } sec && sec is not ("tls" or "reality" or "none"))
        {
            cipher ??= sec;
            query.Remove("security");
        }
        var plain = new VmessProfile
        {
            Name = NameOrDefault(r), Host = r.Host, Port = r.Port, SourceLink = link, Uuid = r.UserInfo,
            Cipher = NormalizeVmessCipher(cipher),
        };
        return (VmessProfile)plain.WithStream(ParseStream(query, defaultSecurity: "none"));
    }

    private static string NormalizeVmessCipher(string? cipher) => (cipher?.Trim().ToLowerInvariant() ?? "auto") switch
    {
        "" or "auto" => "auto",
        "aes-128-gcm" or "chacha20-poly1305" or "none" or "zero" => cipher!.Trim().ToLowerInvariant(),
        var other => throw new LinkParseException($"Шифрование VMess «{other}» не поддерживается (auto, aes-128-gcm, chacha20-poly1305, none, zero)"),
    };

    private static readonly HashSet<string> ShadowsocksMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-poly1305", "chacha20-ietf-poly1305", "xchacha20-poly1305",
        "xchacha20-ietf-poly1305", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "none", "plain",
    };

    /// <summary>
    /// ss://base64(метод:пароль)@хост:порт#имя (SIP002), ss://метод:пароль@хост:порт (пароль в процентной кодировке)
    /// или старое ss://base64(метод:пароль@хост:порт)#имя. Плагины (obfs, v2ray-plugin) не поддерживаются.
    /// </summary>
    private static ShadowsocksProfile ParseShadowsocks(string link)
    {
        var body = link[5..];
        var fragment = "";
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            fragment = Unescape(body[(hash + 1)..]).Trim();
            body = body[..hash];
        }
        // Старый вид: всё до «#» — base64, внутри «метод:пароль@хост:порт».
        if (!body.Contains('@') && TryDecodeBase64(body.Split('?', '/')[0], out var legacy) && legacy.Contains('@'))
            body = legacy;
        var r = Split("ss://" + body + (fragment.Length > 0 ? "#" + Uri.EscapeDataString(fragment) : ""));
        if (NullIfEmpty(r.Query.GetValueOrDefault("plugin")) is { } plugin && !plugin.StartsWith("none", StringComparison.OrdinalIgnoreCase))
            throw new LinkParseException($"Плагин Shadowsocks «{plugin.Split(';')[0]}» не поддерживается");
        var userInfo = r.UserInfo ?? throw new LinkParseException("В ссылке ss нет метода и пароля");
        if (!userInfo.Contains(':') && TryDecodeBase64(userInfo, out var decoded))
            userInfo = decoded;
        var colon = userInfo.IndexOf(':');
        if (colon <= 0 || colon == userInfo.Length - 1)
            throw new LinkParseException("В ссылке ss нет метода и пароля («метод:пароль»)");
        var method = userInfo[..colon].Trim().ToLowerInvariant();
        if (!ShadowsocksMethods.Contains(method))
            throw new LinkParseException($"Метод Shadowsocks «{method}» не поддерживается");
        return new ShadowsocksProfile
        {
            Name = NameOrDefault(r), Host = r.Host, Port = r.Port, SourceLink = link,
            Method = method, Password = userInfo[(colon + 1)..],
        };
    }

    /// <summary>
    /// Транспорт и защита из параметров ссылки: type (tcp, xhttp, ws, grpc, httpupgrade), security (reality, tls, none),
    /// sni, fp, alpn, pbk, sid, spx, path, host, mode, serviceName, extra, allowInsecure.
    /// </summary>
    private static StreamFields ParseStream(IReadOnlyDictionary<string, string> q, string defaultSecurity)
    {
        var network = (NullIfEmpty(q.GetValueOrDefault("type")) ?? "tcp").ToLowerInvariant() switch
        {
            "tcp" or "raw" => "raw",
            "xhttp" or "splithttp" => "xhttp",
            "ws" or "websocket" => "ws",
            "grpc" or "gun" => "grpc",
            "httpupgrade" => "httpupgrade",
            var other => throw new LinkParseException($"Транспорт «{other}» не поддерживается (есть tcp, xhttp, ws, grpc и httpupgrade)"),
        };

        var headerType = q.GetValueOrDefault("headerType");
        if (network == "raw" && headerType is not (null or "" or "none"))
            throw new LinkParseException($"Маскировка tcp headerType={headerType} не поддерживается");

        var security = (NullIfEmpty(q.GetValueOrDefault("security")) ?? defaultSecurity).ToLowerInvariant();
        if (security is "" or "false" or "0")
            security = "none";
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

        // Xray 26 не принимает allowInsecure: без проверки сертификата — только с его отпечатком (pcs).
        var pin = NullIfEmpty(q.GetValueOrDefault("pcs")) ?? NullIfEmpty(q.GetValueOrDefault("pinSHA256"));
        if (pin != null && !pin.Split(',').All(p => IsSha256Hex(p.Trim())))
            throw new LinkParseException("pcs в ссылке — не SHA-256 в hex");
        if (security == "tls" && pin == null && q.GetValueOrDefault("allowInsecure") is "1" or "true" or "True")
            throw new LinkParseException("Ссылка с allowInsecure=1 без отпечатка сертификата (pcs): Xray 26 не подключается без проверки сертификата");

        var path = NullIfEmpty(q.GetValueOrDefault("path"));
        var host = NullIfEmpty(q.GetValueOrDefault("host"));
        var mode = NullIfEmpty(q.GetValueOrDefault("mode"));
        return new StreamFields
        {
            Network = network,
            Security = security,
            Sni = sni,
            Fingerprint = NullIfEmpty(q.GetValueOrDefault("fp")),
            Alpn = NullIfEmpty(q.GetValueOrDefault("alpn")),
            PinSha256 = security == "tls" ? pin : null,
            VerifyName = security == "tls" ? NullIfEmpty(q.GetValueOrDefault("vcn")) : null,
            PublicKey = NullIfEmpty(publicKey),
            ShortId = NullIfEmpty(q.GetValueOrDefault("sid")),
            SpiderX = NullIfEmpty(q.GetValueOrDefault("spx")),
            XhttpPath = network == "xhttp" ? path ?? "/" : null,
            XhttpHost = network == "xhttp" ? host : null,
            XhttpMode = network == "xhttp" ? mode ?? "auto" : null,
            XhttpExtra = network == "xhttp" ? NullIfEmpty(extra) : null,
            Path = network is "ws" or "httpupgrade" ? path ?? "/" : null,
            HostHeader = network is "ws" or "httpupgrade" ? host : null,
            ServiceName = network == "grpc" ? NullIfEmpty(q.GetValueOrDefault("serviceName")) ?? path ?? "" : null,
            GrpcMulti = network == "grpc" && mode == "multi",
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

    public static bool TryDecodeBase64(string text, out string decoded)
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
