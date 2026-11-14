using Microsoft.Win32;

namespace PVpn.Core.Net;

/// <summary>
/// Правило NRPT «все имена — в DNS туннеля», пока поднят TUN. Без него Windows на компьютере с политикой
/// DisableSmartNameResolution (её оставляют другие VPN-программы) после пустого ответа на запрос AAAA идёт
/// спрашивать DNS остальных адаптеров; туда запросы закрыты kill switch, и каждое новое имя ждёт тайм-аута
/// около 11 секунд — если домашняя сеть выдаёт IPv6-DNS. С правилом Windows спрашивает только DNS туннеля.
/// Служба DNS-клиента следит за этим разделом реестра сама: правило действует сразу, без перезапуска.
/// </summary>
public static class DnsPolicy
{
    /// <summary>Свой раздел правила: по нему оно находится и снимается без журнала.</summary>
    public const string RuleId = "{5F0C3A52-9B7E-4E4B-8C1D-7A1E6D2F9C41}";

    private const string RulesKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig";

    /// <summary>Правило «.» (любое имя) с обычными DNS-серверами.</summary>
    private const int GenericDnsServers = 8;

    /// <summary>Ставит или заменяет правило.</summary>
    /// <exception cref="UnauthorizedAccessException" />
    /// <exception cref="IOException" />
    public static void Set(IReadOnlyList<string> servers)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{RulesKey}\{RuleId}");
        key.SetValue("Version", 2, RegistryValueKind.DWord);
        key.SetValue("Name", new[] { "." }, RegistryValueKind.MultiString);
        key.SetValue("GenericDNSServers", string.Join(";", servers), RegistryValueKind.String);
        key.SetValue("ConfigOptions", GenericDnsServers, RegistryValueKind.DWord);
        key.SetValue("Comment", Product.Name, RegistryValueKind.String);
    }

    /// <summary>Снимает правило. false — его не было.</summary>
    /// <exception cref="UnauthorizedAccessException" />
    /// <exception cref="IOException" />
    public static bool Clear()
    {
        using var rules = Registry.LocalMachine.OpenSubKey(RulesKey, writable: true);
        if (rules?.OpenSubKey(RuleId) is not { } rule)
            return false;
        rule.Dispose();
        rules.DeleteSubKeyTree(RuleId, throwOnMissingSubKey: false);
        return true;
    }

    public static bool IsSet
    {
        get
        {
            using var rule = Registry.LocalMachine.OpenSubKey($@"{RulesKey}\{RuleId}");
            return rule != null;
        }
    }
}
