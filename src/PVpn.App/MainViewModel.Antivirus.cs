using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PVpn.App;

/// <summary>Подсказка про сторонний антивирус: после первого подключения, пока её не скрыли для этого антивируса.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private string? antivirusHint;
    private IReadOnlyList<string> antivirusNames = [];
    private bool antivirusChecked;

    /// <summary>Папка ядер, которую нужно добавить в исключения (установленная программа — рядом с ней).</summary>
    private static string CoresFolder => Path.Combine(AppContext.BaseDirectory, "cores");

    /// <summary>Один раз за запуск, после подключения: антивирус мешает только трафику VPN.</summary>
    private async Task CheckAntivirusAsync()
    {
        if (antivirusChecked || SnapshotMode)
            return;
        antivirusChecked = true;
        var found = await Task.Run(Antivirus.ActiveThirdParty);
        var fresh = found.Where(n => !settings.AntivirusHintDismissed.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0)
            return;
        antivirusNames = fresh;
        AntivirusHint = PVpn.Core.Diagnostics.AntivirusAdvice.For(fresh, CoresFolder);
        AppendLog("app", $"Найден антивирус: {string.Join(", ", fresh)} — показана подсказка про исключения для ядер");
    }

    [RelayCommand]
    private void DismissAntivirusHint()
    {
        settings.AntivirusHintDismissed = [.. settings.AntivirusHintDismissed.Union(antivirusNames, StringComparer.OrdinalIgnoreCase)];
        SaveSettings();
        AntivirusHint = null;
    }

    [RelayCommand]
    private void OpenCoresFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CoresFolder}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Notice = e.Message;
        }
    }
}
