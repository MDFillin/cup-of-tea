using System.Management;
using System.Runtime.InteropServices;
using PVpn.Core.Diagnostics;

namespace PVpn.App;

/// <summary>Сторонние антивирусы из Центра безопасности Windows (подсказка — <see cref="AntivirusAdvice"/>).</summary>
internal static class Antivirus
{
    /// <summary>Включённые антивирусы, кроме встроенного в Windows.</summary>
    public static IReadOnlyList<string> ActiveThirdParty()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var name = (item["displayName"] as string)?.Trim();
                    var state = Convert.ToUInt32(item["productState"] ?? 0u);
                    if (string.IsNullOrEmpty(name) || AntivirusAdvice.IsBuiltIn(name) || !AntivirusAdvice.IsEnabled(state) || names.Contains(name))
                        continue;
                    names.Add(name);
                }
            }
        }
        // Центра безопасности нет (Windows Server) или WMI недоступен — подсказка просто не появится.
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or PlatformNotSupportedException)
        {
        }
        return names;
    }
}
