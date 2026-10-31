namespace PVpn.Core.Config;

/// <summary>
/// Настройки обхода блокировок, которые присылает панель подписки заголовками: fragmentation-enable/-packets/-length/
/// -interval и noises-enable/-type/-packet/-delay. Применяются к серверам этой подписки поверх настроек пользователя
/// (заданное панелью заменяет, остальное остаётся как у пользователя). null в поле — панель его не задала.
/// </summary>
public sealed record PanelBypass
{
    public bool? Fragment { get; init; }
    public string? FragmentPackets { get; init; }
    public string? FragmentLength { get; init; }
    public string? FragmentInterval { get; init; }
    public bool? Noises { get; init; }
    public string? NoiseType { get; init; }
    public string? NoisePacket { get; init; }
    public string? NoiseDelay { get; init; }

    /// <summary>Из заголовков панели; null — ни одного такого заголовка нет.</summary>
    public static PanelBypass? FromHeaders(Func<string, string?> header)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 200 ? null : s.Trim();
        static bool? Flag(string? s) => Clean(s)?.ToLowerInvariant() switch
        {
            "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => null,
        };
        var packets = Clean(header("fragmentation-packets"))?.ToLowerInvariant();
        var result = new PanelBypass
        {
            Fragment = Flag(header("fragmentation-enable")),
            // «all» у панелей — все записи соединения; у xray это диапазон записей.
            FragmentPackets = packets == "all" ? "1-100" : packets,
            FragmentLength = Clean(header("fragmentation-length")),
            FragmentInterval = Clean(header("fragmentation-interval")),
            Noises = Flag(header("noises-enable")),
            NoiseType = Clean(header("noises-type"))?.ToLowerInvariant(),
            NoisePacket = Clean(header("noises-packet")),
            NoiseDelay = Clean(header("noises-delay")),
        };
        return result == new PanelBypass() ? null : result;
    }

    /// <summary>Настройки пользователя с заменой того, что задала панель (проверять — <see cref="BypassRules.Validate"/>).</summary>
    public BypassOptions ApplyTo(BypassOptions user) => user with
    {
        Fragment = Fragment ?? user.Fragment,
        FragmentPackets = FragmentPackets ?? user.FragmentPackets,
        FragmentLength = FragmentLength ?? user.FragmentLength,
        FragmentInterval = FragmentInterval ?? user.FragmentInterval,
        Noises = Noises ?? user.Noises,
        NoiseType = NoiseType ?? user.NoiseType,
        NoisePacket = NoisePacket ?? user.NoisePacket,
        NoiseDelay = NoiseDelay ?? user.NoiseDelay,
    };

    /// <summary>Коротко для интерфейса: «фрагментация tlshello 10-20 байт, 5-15 мс; шумы выкл.».</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Fragment == false)
            parts.Add("фрагментация выкл.");
        else if (Fragment == true || FragmentPackets != null || FragmentLength != null || FragmentInterval != null)
            parts.Add("фрагментация " + string.Join(" ", new[]
            {
                FragmentPackets, FragmentLength != null ? FragmentLength + " байт" : null, FragmentInterval != null ? FragmentInterval + " мс" : null,
            }.Where(s => s != null)).Trim());
        if (Noises == false)
            parts.Add("шумы выкл.");
        else if (Noises == true || NoiseType != null || NoisePacket != null || NoiseDelay != null)
            parts.Add("шумы " + string.Join(" ", new[] { NoiseType, NoisePacket, NoiseDelay != null ? NoiseDelay + " мс" : null }.Where(s => s != null)).Trim());
        return string.Join("; ", parts);
    }
}
