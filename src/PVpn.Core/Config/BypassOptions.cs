using System.Globalization;
using System.Text.RegularExpressions;

namespace PVpn.Core.Config;

/// <summary>
/// Настройки обхода блокировок. Значения по умолчанию проверены на домашней сети (фрагментация, mux, шумы).
/// Пустое значение (null) у отпечатка и режима XHTTP — взять из ссылки сервера.
/// Свойства с set, а не init: JSON с исходным генератором ставит отсутствующим init-свойствам default
/// (null, false, 0) вместо значений по умолчанию, и пропущенное поле молча выключало бы защиту.
/// </summary>
public sealed record BypassOptions
{
    /// <summary>Значения по умолчанию (каждый раз новый объект: свойства изменяемые).</summary>
    public static BypassOptions Default => new();

    /// <summary>Отпечаток TLS (uTLS) для VLESS; null — из ссылки, а если там нет — chrome.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>Режим XHTTP; null — из ссылки. Сервер в режиме auto принимает любой.</summary>
    public string? XhttpMode { get; set; }

    /// <summary>
    /// Резать начало TLS-соединения VLESS на куски: DPI провайдера не видит SNI и не рвёт REALITY.
    /// «tlshello» — только ClientHello, «1-3» — первые записи потока.
    /// </summary>
    public bool Fragment { get; set; } = true;
    public string FragmentPackets { get; set; } = "tlshello";
    public string FragmentLength { get; set; } = "100-200";
    public string FragmentInterval { get; set; } = "10-20";

    /// <summary>
    /// Мультиплексирование (mux.cool) для VLESS по TCP: меньше рукопожатий REALITY, которые на домашнем
    /// интернете иногда зависают. У Vision через mux идёт только UDP (xudp), у XHTTP своё — xmux.
    /// </summary>
    public bool Mux { get; set; } = true;
    public int MuxConcurrency { get; set; } = 8;
    public int XudpConcurrency { get; set; } = 16;

    /// <summary>Свои параметры xmux для XHTTP; null — из ссылки или значения Xray (3 соединения).</summary>
    public XmuxOptions? Xmux { get; set; }

    /// <summary>Шумы перед подключением Hysteria2 (UDP): случайные пакеты сбивают DPI с толку.</summary>
    public bool Noises { get; set; } = true;

    /// <summary>rand — случайные байты (длина — <see cref="NoisePacket"/>), str, hex или base64 — свои данные.</summary>
    public string NoiseType { get; set; } = "rand";
    public string NoisePacket { get; set; } = "50-100";
    public string NoiseDelay { get; set; } = "10-16";

    /// <summary>Как часто Hysteria2 меняет порт, если в ссылке диапазон портов.</summary>
    public int HopIntervalSeconds { get; set; } = 30;
}

/// <summary>Параметры xmux (XHTTP). Пустое поле — значение Xray по умолчанию.</summary>
public sealed record XmuxOptions
{
    public string? MaxConcurrency { get; set; }
    public string? MaxConnections { get; set; }
    public string? CMaxReuseTimes { get; set; }
    public string? HMaxRequestTimes { get; set; }
    public string? HMaxReusableSecs { get; set; }
    public int HKeepAlivePeriod { get; set; }
}

public static partial class BypassRules
{
    public static readonly string[] Fingerprints = ["chrome", "firefox", "safari", "ios", "android", "edge", "random", "randomized"];
    public static readonly string[] XhttpModes = ["auto", "packet-up", "stream-up", "stream-one"];
    public static readonly string[] NoiseTypes = ["rand", "str", "hex", "base64"];

    [GeneratedRegex(@"^(\d{1,6})(?:-(\d{1,6}))?$")]
    private static partial Regex RangeRegex();

    /// <summary>
    /// Проверяет и нормализует настройки. Служба вызывает это сама: настройки приходят от интерфейса
    /// без прав администратора и попадают в конфиг процесса с правами системы.
    /// </summary>
    public static BypassOptions Validate(BypassOptions o)
    {
        var fingerprint = Choice(o.Fingerprint, Fingerprints, "Отпечаток TLS");
        var mode = Choice(o.XhttpMode, XhttpModes, "Режим XHTTP");

        var packets = (o.FragmentPackets ?? "").Trim().ToLowerInvariant();
        if (packets != "tlshello")
            packets = Range(packets, 1, 100, "Фрагментация: пакеты (tlshello или диапазон записей, например 1-3)");
        var length = Range(o.FragmentLength, 1, 16384, "Фрагментация: длина куска, байт");
        var interval = Range(o.FragmentInterval, 0, 1000, "Фрагментация: пауза, мс");

        if (o.MuxConcurrency is < 1 or > 1024)
            throw new ConfigException("Mux: число потоков на соединение — от 1 до 1024");
        if (o.XudpConcurrency is < 1 or > 1024)
            throw new ConfigException("Mux: число UDP-потоков (xudp) на соединение — от 1 до 1024");

        var noiseType = Choice(o.NoiseType, NoiseTypes, "Шумы: тип") ?? "rand";
        var noisePacket = ValidateNoisePacket(noiseType, o.NoisePacket);
        var noiseDelay = Range(o.NoiseDelay, 0, 1000, "Шумы: пауза, мс");

        if (o.HopIntervalSeconds is < 5 or > 3600)
            throw new ConfigException("Смена портов Hysteria2: интервал — от 5 до 3600 секунд");

        return o with
        {
            Fingerprint = fingerprint,
            XhttpMode = mode,
            FragmentPackets = packets,
            FragmentLength = length,
            FragmentInterval = interval,
            Xmux = o.Xmux is { } x ? ValidateXmux(x) : null,
            NoiseType = noiseType,
            NoisePacket = noisePacket,
            NoiseDelay = noiseDelay,
        };
    }

    private static XmuxOptions ValidateXmux(XmuxOptions x)
    {
        var result = new XmuxOptions
        {
            MaxConcurrency = OptionalRange(x.MaxConcurrency, 1, 1024, "XMUX: потоков на соединение (maxConcurrency)"),
            MaxConnections = OptionalRange(x.MaxConnections, 1, 1024, "XMUX: соединений (maxConnections)"),
            CMaxReuseTimes = OptionalRange(x.CMaxReuseTimes, 1, 100000, "XMUX: повторных использований соединения (cMaxReuseTimes)"),
            HMaxRequestTimes = OptionalRange(x.HMaxRequestTimes, 1, 100000, "XMUX: запросов на соединение (hMaxRequestTimes)"),
            HMaxReusableSecs = OptionalRange(x.HMaxReusableSecs, 1, 86400, "XMUX: время жизни соединения, с (hMaxReusableSecs)"),
            HKeepAlivePeriod = x.HKeepAlivePeriod is >= 0 and <= 600
                ? x.HKeepAlivePeriod
                : throw new ConfigException("XMUX: keep-alive, с (hKeepAlivePeriod) — от 0 до 600"),
        };
        if (result.MaxConcurrency != null && result.MaxConnections != null)
            throw new ConfigException("XMUX: maxConcurrency и maxConnections нельзя задать вместе — оставьте одно из полей пустым");
        return result;
    }

    private static string ValidateNoisePacket(string type, string? packet)
    {
        packet = packet?.Trim() ?? "";
        switch (type)
        {
            case "rand":
                return Range(packet, 1, 8192, "Шумы: длина случайного пакета, байт");
            case "str":
                return packet.Length is >= 1 and <= 1024 ? packet : throw new ConfigException("Шумы: текст пакета — от 1 до 1024 символов");
            case "hex":
                var hex = packet.Replace(" ", "");
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex[2..];
                if (hex.Length is 0 or > 2048 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
                    throw new ConfigException("Шумы: пакет в hex — чётное число шестнадцатеричных цифр, до 1024 байт");
                return hex.ToLowerInvariant();
            default:
                try
                {
                    var bytes = Convert.FromBase64String(packet);
                    if (bytes.Length is >= 1 and <= 1024)
                        return packet;
                }
                catch (FormatException)
                {
                }
                throw new ConfigException("Шумы: пакет в base64 — от 1 до 1024 байт");
        }
    }

    private static string? Choice(string? value, string[] allowed, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().ToLowerInvariant();
        return allowed.Contains(v) ? v : throw new ConfigException($"{what}: «{value}» не поддерживается (есть {string.Join(", ", allowed)})");
    }

    private static string? OptionalRange(string? value, int min, int max, string what) =>
        string.IsNullOrWhiteSpace(value) ? null : Range(value, min, max, what);

    /// <summary>Число или диапазон «от-до» в формате Xray, например «100-200».</summary>
    internal static string Range(string? value, int min, int max, string what)
    {
        var m = RangeRegex().Match(value?.Replace(" ", "") ?? "");
        if (m.Success)
        {
            var from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : from;
            if (from >= min && to <= max && from <= to)
                return from == to ? from.ToString(CultureInfo.InvariantCulture) : $"{from}-{to}";
        }
        throw new ConfigException($"{what}: нужно число или диапазон «от-до» в пределах {min}–{max}, а не «{value}»");
    }
}
