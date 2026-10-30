using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PVpn.Core.Config;

namespace PVpn.Core.Routing;

[JsonConverter(typeof(JsonStringEnumConverter<SplitTunnelMode>))]
public enum SplitTunnelMode
{
    /// <summary>Весь трафик через VPN, выбранные программы и сайты — напрямую.</summary>
    ExcludeSelected,

    /// <summary>Весь трафик напрямую, через VPN — только выбранные программы и сайты.</summary>
    OnlySelected,
}

/// <summary>Программа (полный путь к exe) или папка: под правило попадает любой exe внутри неё, в том числе во вложенных папках.</summary>
public sealed record SplitApp(string Path, bool IsFolder);

/// <summary>Правила раздельного туннелирования для одного подключения (список выбранного режима).</summary>
public sealed record SplitTunnelOptions
{
    public SplitTunnelMode Mode { get; set; }
    public IReadOnlyList<SplitApp> Apps { get; set; } = [];

    /// <summary>Домены: правило действует и на все поддомены.</summary>
    public IReadOnlyList<string> Domains { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Apps.Count == 0 && Domains.Count == 0;

    /// <summary>
    /// Меняет ли что-то в маршрутизации: пустой список «мимо VPN» — это обычный VPN, а пустой список
    /// «только через VPN» — весь трафик напрямую.
    /// </summary>
    [JsonIgnore]
    public bool IsEffective => Mode == SplitTunnelMode.OnlySelected || !IsEmpty;
}

public static partial class SplitTunnelRules
{
    public const int MaxApps = 300;
    public const int MaxDomains = 2000;

    /// <summary>
    /// «https://www.Example.com/путь», «*.example.com», «.example.com.», «пример.рф» → «example.com» / «xn--e1afmkfd.xn--p1ai».
    /// Одна метка («ru», «рф») тоже допустима: правило на всю доменную зону.
    /// </summary>
    public static string NormalizeDomain(string input)
    {
        var s = input.Trim();
        if (s.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out var uri))
            s = uri.Host;
        else
            s = s.Split('/', '?', '#')[0];
        s = s.TrimStart('*').Trim('.').ToLowerInvariant();
        if (s.Length == 0)
            throw new FormatException("Пустой адрес сайта");
        try
        {
            s = new IdnMapping().GetAscii(s);
        }
        catch (ArgumentException)
        {
            throw new FormatException($"«{input.Trim()}» — не похоже на адрес сайта");
        }
        if (s.Length > 253 || !DomainPattern().IsMatch(s))
            throw new FormatException($"«{input.Trim()}» — не похоже на адрес сайта");
        // www.youtube.com → youtube.com: иначе m.youtube.com и сам youtube.com прошли бы мимо правила.
        if (s.StartsWith("www.", StringComparison.Ordinal) && s.Count(c => c == '.') >= 2)
            s = s[4..];
        return s;
    }

    /// <summary>Полный путь без завершающего разделителя (для папки) — так он хранится и сравнивается.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*') || path.Contains('?'))
            throw new FormatException($"Неверный путь: {path}");
        if (!Path.IsPathFullyQualified(path))
            throw new FormatException($"Нужен полный путь: {path}");
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd('\\', '/');
        // «C:\» → «C:» превращать нельзя: это уже другой (относительный) путь. Оставляем корень как есть.
        return trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>
    /// Папка для правила «по папке» для exe: обычно папка самого exe, но если она похожа на папку версии
    /// (Discord: …\Discord\app-1.0.9187\Discord.exe, Chromium: …\Application\130.0.1\), берётся папка выше —
    /// после обновления программы правило не слетит.
    /// </summary>
    public static string SuggestFolder(string exePath)
    {
        var dir = Path.GetDirectoryName(NormalizePath(exePath)) ?? throw new FormatException($"Неверный путь: {exePath}");
        while (VersionFolderPattern().IsMatch(Path.GetFileName(dir)) && Path.GetDirectoryName(dir) is { } parent && Path.GetFileName(parent).Length > 0)
            dir = parent;
        return dir;
    }

    /// <summary>
    /// Проверяет и нормализует правила (служба не доверяет тому, что пришло по каналу):
    /// полные пути, корректные домены, ограничения на количество, без повторов.
    /// </summary>
    public static SplitTunnelOptions Validate(SplitTunnelOptions options)
    {
        if (!Enum.IsDefined(options.Mode))
            throw new ConfigException("Неизвестный режим раздельного туннелирования");
        // Явный null в JSON от клиента — пустой список.
        options = options with { Apps = options.Apps ?? [], Domains = options.Domains ?? [] };
        if (options.Apps.Count > MaxApps)
            throw new ConfigException($"Слишком много программ в раздельном туннелировании (больше {MaxApps})");
        if (options.Domains.Count > MaxDomains)
            throw new ConfigException($"Слишком много сайтов в раздельном туннелировании (больше {MaxDomains})");
        try
        {
            var apps = options.Apps
                .Select(a => new SplitApp(NormalizePath(a.Path), a.IsFolder))
                .DistinctBy(a => (a.Path.ToUpperInvariant(), a.IsFolder))
                .ToList();
            var domains = options.Domains.Select(NormalizeDomain).Distinct(StringComparer.Ordinal).ToList();
            return options with { Apps = apps, Domains = domains };
        }
        catch (FormatException e)
        {
            throw new ConfigException($"Раздельное туннелирование: {e.Message}");
        }
    }

    /// <summary>
    /// Регулярное выражение для sing-box (RE2) по пути программы: точное совпадение для exe и
    /// «начинается с папки\» для папки. Без учёта регистра — как сравнивает пути Windows.
    /// </summary>
    public static string PathRegex(SplitApp app)
    {
        var escaped = EscapeRe2(app.Path);
        if (!app.IsFolder)
            return $"(?i)^{escaped}$";
        return escaped.EndsWith(@"\\", StringComparison.Ordinal) ? $"(?i)^{escaped}" : $"(?i)^{escaped}\\\\";
    }

    /// <summary>
    /// Экранирование для RE2 (Go). Regex.Escape из .NET не подходит: он экранирует пробел («\ »), а RE2 такую
    /// последовательность отвергает — путь «C:\Program Files\…» сломал бы весь конфиг sing-box.
    /// </summary>
    internal static string EscapeRe2(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (@"\.+*?()|[]{}^$".Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^(app-|v)?\d+(\.\d+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionFolderPattern();
}
