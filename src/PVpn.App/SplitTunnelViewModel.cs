using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PVpn.Core.Routing;

namespace PVpn.App;

/// <summary>Программа или папка в списке раздельного туннелирования.</summary>
public sealed class SplitAppItem(SplitApp app)
{
    public SplitApp App { get; } = app;
    public string Name => App.IsFolder ? Path.GetFileName(App.Path) is { Length: > 0 } n ? n : App.Path : Path.GetFileNameWithoutExtension(App.Path);
    public string Details => App.IsFolder ? $"Папка · {App.Path}" : App.Path;
    public bool IsFolder => App.IsFolder;
}

/// <summary>
/// Вкладка «Раздельное туннелирование». Списки двух режимов хранятся отдельно (<see cref="SplitTunnelSettings"/>);
/// на экране — список текущего режима.
/// </summary>
public sealed partial class SplitTunnelViewModel : ObservableObject
{
    private readonly SplitTunnelSettings settings;
    private readonly Action save;
    private readonly Action<string> notify;

    public ObservableCollection<SplitAppItem> Apps { get; } = [];
    public ObservableCollection<string> Domains { get; } = [];

    [ObservableProperty] private bool enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnlyMode), nameof(ListTitle), nameof(ListHint))]
    private bool isExcludeMode = true;

    [ObservableProperty] private string newDomain = "";

    /// <summary>Правила изменились (вкл/выкл, режим, списки) — для «Применить сейчас».</summary>
    public event Action? Changed;

    private readonly bool loading;

    public SplitTunnelViewModel(SplitTunnelSettings settings, Action save, Action<string> notify)
    {
        this.settings = settings;
        this.save = save;
        this.notify = notify;
        loading = true;
        Enabled = settings.Enabled;
        IsExcludeMode = settings.Mode == SplitTunnelMode.ExcludeSelected;
        loading = false;
        Reload();
    }

    public bool IsOnlyMode
    {
        get => !IsExcludeMode;
        set => IsExcludeMode = !value;
    }

    public string ListTitle => IsExcludeMode ? "идут напрямую, мимо VPN" : "идут через VPN";

    public string ListHint => IsExcludeMode
        ? "Весь остальной трафик — через VPN."
        : "Весь остальной трафик — напрямую, без VPN.";

    /// <summary>Коротко для карточки статуса; null — не действует.</summary>
    public string? Summary => ToOptions() is null
        ? null
        : Apps.Count == 0 && Domains.Count == 0
            ? "через VPN ничего — весь трафик напрямую"
            : (IsExcludeMode ? "мимо VPN: " : "только через VPN: ") + $"программ {Apps.Count}, сайтов {Domains.Count}";

    /// <summary>Режим «только выбранное через VPN» с пустым списком: VPN включён, но весь трафик идёт напрямую.</summary>
    public bool AllDirectWarning => Enabled && IsOnlyMode && Apps.Count == 0 && Domains.Count == 0;

    /// <summary>Правила для подключения: null — выключено или пустой список «мимо VPN» (это обычный VPN).</summary>
    public SplitTunnelOptions? ToOptions()
    {
        var list = settings.Current;
        var options = new SplitTunnelOptions { Mode = settings.Mode, Apps = [.. list.Apps], Domains = [.. list.Domains] };
        return Enabled && options.IsEffective ? options : null;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (loading)
            return;
        settings.Enabled = value;
        Commit();
    }

    partial void OnIsExcludeModeChanged(bool value)
    {
        if (loading)
            return;
        settings.Mode = value ? SplitTunnelMode.ExcludeSelected : SplitTunnelMode.OnlySelected;
        Reload();
        Commit();
    }

    private void Reload()
    {
        Apps.Clear();
        foreach (var a in settings.Current.Apps)
            Apps.Add(new SplitAppItem(a));
        Domains.Clear();
        foreach (var d in settings.Current.Domains)
            Domains.Add(d);
    }

    private void Commit()
    {
        save();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AllDirectWarning));
        Changed?.Invoke();
    }

    // ───────────── Программы ─────────────

    [RelayCommand]
    private void AddExe()
    {
        var dialog = new OpenFileDialog { Title = "Программа", Filter = "Программы (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true)
            return;
        var exe = dialog.FileName;
        // Discord и подобные лежат в папке версии (app-1.0.9187): после обновления путь сменится. Предложить папку выше.
        var folder = SplitTunnelRules.SuggestFolder(exe);
        if (!string.Equals(folder, Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(
                $"{Path.GetFileName(exe)} лежит в папке версии ({Path.GetFileName(Path.GetDirectoryName(exe))}). " +
                $"После обновления программы путь изменится и правило перестанет действовать.\n\n" +
                $"Добавить папку «{folder}» целиком?",
                Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            AddApps([new SplitApp(folder, IsFolder: true)]);
            return;
        }
        AddApps([new SplitApp(exe, IsFolder: false)]);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Папка с программой (под правило попадут все программы внутри)" };
        if (dialog.ShowDialog() == true)
            AddApps([new SplitApp(dialog.FolderName, IsFolder: true)]);
    }

    [RelayCommand]
    private void AddRunning()
    {
        var picker = new RunningAppsWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true)
            AddApps(picker.Selected);
    }

    public void AddApps(IEnumerable<SplitApp> apps)
    {
        var list = settings.Current.Apps;
        var added = 0;
        foreach (var app in apps)
        {
            SplitApp normalized;
            try
            {
                normalized = new SplitApp(SplitTunnelRules.NormalizePath(app.Path), app.IsFolder);
            }
            catch (FormatException e)
            {
                notify(e.Message);
                continue;
            }
            if (list.Any(a => a.IsFolder == normalized.IsFolder && string.Equals(a.Path, normalized.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(normalized);
            added++;
        }
        if (added == 0)
            return;
        Reload();
        Commit();
    }

    [RelayCommand]
    private void RemoveApp(SplitAppItem? item)
    {
        if (item == null)
            return;
        settings.Current.Apps.Remove(item.App);
        Reload();
        Commit();
    }

    // ───────────── Сайты ─────────────

    [RelayCommand]
    private void AddDomain()
    {
        if (string.IsNullOrWhiteSpace(NewDomain))
            return;
        string domain;
        try
        {
            domain = SplitTunnelRules.NormalizeDomain(NewDomain);
        }
        catch (FormatException e)
        {
            notify(e.Message);
            return;
        }
        NewDomain = "";
        if (settings.Current.Domains.Contains(domain))
            return;
        settings.Current.Domains.Add(domain);
        Reload();
        Commit();
    }

    [RelayCommand]
    private void RemoveDomain(string? domain)
    {
        if (domain == null)
            return;
        settings.Current.Domains.Remove(domain);
        Reload();
        Commit();
    }
}
