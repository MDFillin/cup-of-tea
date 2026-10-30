using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.SystemProxy;

namespace PVpn.Core.Diagnostics;

/// <summary>
/// Архив для разбора проблем («Отправить диагностику»): журналы, сеть, настройки и список серверов — без ключей.
/// Из всех файлов вычищаются секреты серверов, UUID, ссылки vless:// и hy2:// и токены подписок.
/// </summary>
public static partial class DiagnosticsReport
{
    /// <summary>Сеть: физический адаптер, маршруты по умолчанию, другие VPN, системный прокси (как «tempest-cli netinfo»).</summary>
    public static string Network(string tunName)
    {
        var sb = new StringBuilder();
        var physical = NetworkInfo.GetDefaultPhysicalInterface();
        sb.AppendLine(physical == null
            ? "Физический адаптер с интернетом не найден"
            : $"Физический адаптер: «{physical.Name}» ({physical.Description}), индекс {physical.Index}, шлюз {physical.Gateway}, метрика {physical.Metric}");
        sb.AppendLine("Маршруты по умолчанию:");
        foreach (var r in NetworkInfo.GetDefaultRoutes().OrderBy(r => r.EffectiveMetric))
            sb.AppendLine($"  {r.Prefix,-12} через {r.NextHop,-15} «{r.Name}» метрика {r.RouteMetric}+{r.InterfaceMetric} {(r.Hardware ? "физический" : "виртуальный")} {(r.Up ? "" : "(выключен)")}");
        var conflicts = NetworkInfo.DetectConflicts(tunName);
        sb.AppendLine(conflicts.Count == 0 ? "Конфликтов нет" : "Конфликты (другой VPN забирает маршрут по умолчанию):");
        foreach (var c in conflicts)
            sb.AppendLine($"  «{c.Name}» ({c.Description}): {c.Route}");
        sb.AppendLine("Публичные подсети локальных адаптеров (пойдут мимо туннеля): " + string.Join(", ", NetworkInfo.GetPublicLocalSubnets(tunName)));
        sb.AppendLine("Сети (для доверенных): " + string.Join(", ", NetworkNames.GetConnected().Select(n => $"«{n}»")));
        var proxy = WinInetProxy.Query();
        sb.AppendLine($"Системный прокси: {(proxy.ProxyEnabled ? proxy.Server : "выключен")}" +
                      (WinInetProxy.HasPendingRestore ? $" (есть невосстановленная копия от {Product.Name})" : ""));
        var security = SecurityProducts();
        sb.AppendLine("Антивирусы и сетевые экраны: " + (security.Count == 0 ? "нет данных" : string.Join(", ", security)));
        return sb.ToString();
    }

    /// <summary>
    /// Антивирусы и сторонние сетевые экраны из Центра безопасности Windows. Сетевой экран антивируса заменяет брандмауэр
    /// Windows и может задерживать соединения на адаптере TUN (стек system/mixed). Спрашивается через PowerShell:
    /// без пакета WMI и только для диагностики.
    /// </summary>
    public static IReadOnlyList<string> SecurityProducts()
    {
        const string script = "foreach ($c in 'AntiVirusProduct','FirewallProduct') { Get-CimInstance -Namespace root/SecurityCenter2 -ClassName $c " +
                              "-ErrorAction SilentlyContinue | ForEach-Object { $c + '|' + $_.displayName } }";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.Environment["PSModulePath"] = "";
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000))
                p.Kill();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('|', 2))
                .Where(x => x.Length == 2)
                .Select(x => (x[0] == "FirewallProduct" ? "сетевой экран " : "антивирус ") + x[1])
                .Distinct()
                .ToList();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Серверы без ключей: имя, протокол, адрес, подписка.</summary>
    public static string Servers(ProfileData data)
    {
        var sb = new StringBuilder();
        foreach (var p in data.Profiles)
        {
            var sub = data.Subscriptions.FirstOrDefault(s => s.Id == p.SubscriptionId);
            sb.AppendLine($"{p.Name} — {p.ProtocolLabel}, {p.Host}:{p.Port}" + (sub != null ? $", подписка «{sub.Name}»" : ""));
        }
        foreach (var s in data.Subscriptions)
        {
            sb.AppendLine($"Подписка «{s.Name}»: {new Uri(s.Url).Host}, обновлена {s.LastUpdated:dd.MM.yyyy HH:mm}, " +
                          $"раз в {s.UpdateIntervalHours} ч{(s.LastError != null ? $", ошибка: {s.LastError}" : "")}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Секреты, которые нельзя выпускать: ключи серверов (и части «логин:пароль» у Hysteria2) и адреса подписок целиком.
    /// </summary>
    public static IEnumerable<string> Secrets(ProfileData data) =>
        data.Profiles.SelectMany(p => p.Secrets).SelectMany(s => s.Split(':').Append(s)).Concat(data.Subscriptions.SelectMany(s => new[] { s.Url, new Uri(s.Url).PathAndQuery }));

    /// <summary>Пишет zip: имя файла в архиве → текст. Каждый текст проходит через <see cref="Clean"/>.</summary>
    public static void Write(string zipPath, IReadOnlyDictionary<string, string> files, IEnumerable<string> secrets)
    {
        var sanitizer = new LogSanitizer(secrets);
        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                writer.Write(Clean(text, sanitizer));
            }
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    internal static string Clean(string text, LogSanitizer sanitizer)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(sanitizer.Clean);
        return LinkRegex().Replace(string.Join(Environment.NewLine, lines), m => m.Groups[1].Value + "://***");
    }

    /// <summary>Ссылки на серверы целиком (в журналах их быть не должно, но проверяем).</summary>
    [GeneratedRegex(@"\b(vless|hy2|hysteria2|vmess|trojan|ss)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}
