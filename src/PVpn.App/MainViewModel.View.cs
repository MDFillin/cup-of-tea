using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Diagnostics;

namespace PVpn.App;

/// <summary>Страницы окна и то, что показывает главная: таймер сеанса, скорость, текущий сервер.</summary>
public sealed partial class MainViewModel
{
    public const int HomePage = 0, ServersPage = 1, RoutesPage = 2, SettingsPageIndex = 3, LogPage = 4;

    /// <summary>Открытая страница бокового меню (0 — главная … 4 — журнал).</summary>
    [ObservableProperty] private int page;

    /// <summary>Открытая страница внутри настроек (dns, bypass, tun, trusted, subs, cores); null — список настроек.</summary>
    [ObservableProperty] private string? settingsPage;

    [RelayCommand]
    private void GoTo(string index) => Page = int.Parse(index);

    [RelayCommand]
    private void OpenSettings(string? name) => SettingsPage = name;

    // ───────────── Главная ─────────────

    private DateTimeOffset? sessionSince;

    /// <summary>Время с подключения («00:42:13»); null — не подключено.</summary>
    [ObservableProperty] private string? sessionTime;
    [ObservableProperty] private string? downSpeed;
    [ObservableProperty] private string? upSpeed;
    [ObservableProperty] private string? sessionTraffic;

    /// <summary>Подпись на плашке над таймером.</summary>
    public string StateLabel => State switch
    {
        UiState.On => "Защищено",
        UiState.Busy => StatusTitle.TrimEnd('…'),
        UiState.Error => "Ошибка",
        _ => "Не защищено",
    };

    /// <summary>Крупная строка: время сеанса, пока подключено, иначе состояние.</summary>
    public string BigStatus => State == UiState.On && SessionTime != null ? SessionTime : StatusTitle;

    /// <summary>Сервер на главной: подключённый, а если VPN выключен — выбранный.</summary>
    public ProfileItem? CurrentServer =>
        (IsActive && ConnectedServer != null ? Profiles.FirstOrDefault(p => p.Name == ConnectedServer) : null) ?? SelectedProfile;

    /// <summary>Строка под цифрами: kill switch и раздельное туннелирование.</summary>
    public string? FooterInfo
    {
        get
        {
            var parts = new List<string>();
            if (KillSwitchText != null)
                parts.Add($"Kill switch {KillSwitchText}");
            if (SplitSummary != null)
                parts.Add($"Раздельное туннелирование: {SplitSummary}");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    partial void OnStatusTitleChanged(string value) => OnHomeChanged();
    partial void OnSessionTimeChanged(string? value) => OnPropertyChanged(nameof(BigStatus));
    partial void OnConnectedServerChanged(string? value) => OnHomeChanged();
    partial void OnKillSwitchTextChanged(string? value) => OnPropertyChanged(nameof(FooterInfo));

    private void OnHomeChanged()
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(BigStatus));
        OnPropertyChanged(nameof(CurrentServer));
        RefreshHomeGroup();
        OnPropertyChanged(nameof(FooterInfo));
    }

    private void UpdateSessionTime()
    {
        if (State != UiState.On || sessionSince is not { } since)
        {
            SessionTime = null;
            return;
        }
        var t = DateTimeOffset.Now - since;
        SessionTime = t < TimeSpan.Zero ? "00:00:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void ShowTraffic(TrafficMeter? m)
    {
        DownSpeed = m == null ? null : TrafficMeter.FormatSpeed(m.DownPerSecond);
        UpSpeed = m == null ? null : TrafficMeter.FormatSpeed(m.UpPerSecond);
        SessionTraffic = m == null ? null : TrafficMeter.FormatBytes(m.SessionDown + m.SessionUp);
    }

    // ───────────── Серверы: группы по подпискам и поиск ─────────────

    private ICollectionView? profilesView;

    public ICollectionView ProfilesView
    {
        get
        {
            if (profilesView == null)
            {
                profilesView = CollectionViewSource.GetDefaultView(Profiles);
                profilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileItem.Group)));
                profilesView.Filter = o => string.IsNullOrWhiteSpace(ServerFilter) || o is ProfileItem p
                    && (p.Name.Contains(ServerFilter.Trim(), StringComparison.CurrentCultureIgnoreCase)
                        || p.Details.Contains(ServerFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));
            }
            return profilesView;
        }
    }

    [ObservableProperty] private string serverFilter = "";

    partial void OnServerFilterChanged(string value)
    {
        ProfilesView.Refresh();
        GroupsVersion++;
        OnPropertyChanged(nameof(SearchEmptyText));
    }

    /// <summary>Меняется при сворачивании групп и поиске: по нему заголовки групп перечитывают своё состояние.</summary>
    [ObservableProperty] private int groupsVersion;

    /// <summary>Группа развёрнута: при поиске — всегда (найденное не прячется), иначе — как оставил пользователь.</summary>
    public bool IsGroupExpanded(string group) =>
        !string.IsNullOrWhiteSpace(ServerFilter) || !settings.CollapsedGroups.Contains(group, StringComparer.Ordinal);

    /// <summary>Пользователь свернул или развернул группу. При поиске не запоминается: там все группы открыты.</summary>
    public void SetGroupExpanded(string group, bool expanded)
    {
        if (!string.IsNullOrWhiteSpace(ServerFilter) || IsGroupExpanded(group) == expanded)
            return;
        if (expanded)
            settings.CollapsedGroups.RemoveAll(g => g == group);
        else
            settings.CollapsedGroups.Add(group);
        SaveSettings();
        GroupsVersion++;
    }

    [RelayCommand]
    private void CollapseAllGroups()
    {
        settings.CollapsedGroups = [.. Profiles.Select(p => p.Group).Distinct()];
        SaveSettings();
        GroupsVersion++;
    }

    [RelayCommand]
    private void ExpandAllGroups()
    {
        settings.CollapsedGroups.Clear();
        SaveSettings();
        GroupsVersion++;
    }
}
