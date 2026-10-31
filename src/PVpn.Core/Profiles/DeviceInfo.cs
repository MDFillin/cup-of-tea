using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace PVpn.Core.Profiles;

/// <summary>
/// Сведения об устройстве для панели подписки (заголовки x-hwid, x-device-os, x-ver-os, x-device-model) — по ним
/// панели вроде Remnawave считают лимит устройств. HWID — хэш MachineGuid Windows с солью программы: стабилен
/// для этого ПК, но сам MachineGuid не раскрывает.
/// </summary>
public static class DeviceInfo
{
    private static readonly Lazy<string> Id = new(ComputeHwid);

    public static string Hwid => Id.Value;

    public static IEnumerable<(string Name, string Value)> Headers()
    {
        yield return ("x-hwid", Hwid);
        yield return ("x-device-os", "Windows");
        yield return ("x-ver-os", Environment.OSVersion.Version.ToString());
        if (Model() is { } model)
            yield return ("x-device-model", model);
        yield return ("x-client", Product.Id);
    }

    private static string ComputeHwid()
    {
        var machine = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string
                      ?? Environment.MachineName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Product.Id}:{machine}"));
        return Convert.ToHexStringLower(hash, 0, 16);
    }

    /// <summary>Модель ПК из BIOS (как её показывает «Сведения о системе»); только печатные ASCII-символы.</summary>
    private static string? Model()
    {
        var bios = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        var maker = Registry.GetValue(bios, "SystemManufacturer", null) as string;
        var product = Registry.GetValue(bios, "SystemProductName", null) as string;
        var model = string.Join(" ", new[] { maker, product }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        model = new string(model.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        return model.Length == 0 ? null : model.Length > 64 ? model[..64] : model;
    }
}
