using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core;

namespace PVpn.App;

/// <summary>Главная: серверы текущей подписки в выдвижной панели и зимнее оформление.</summary>
public sealed partial class MainViewModel
{
    // ───────────── Серверы текущей подписки ─────────────

    /// <summary>Панель с серверами той же подписки, что и текущий сервер, открыта (не запоминается: открывают на минуту).</summary>
    [ObservableProperty] private bool homeServersOpen;

    private ListCollectionView? groupServers;
    private string? shownGroup;

    /// <summary>Серверы группы текущего сервера — в том же порядке, что на странице «Серверы».</summary>
    public ICollectionView GroupServers
    {
        get
        {
            if (groupServers == null)
            {
                groupServers = new ListCollectionView(Profiles) { Filter = o => o is ProfileItem p && p.Group == shownGroup };
                SortLikeServerList(groupServers);
            }
            return groupServers;
        }
    }

    /// <summary>Имя группы: подписка или «Свои».</summary>
    public string? CurrentGroup => CurrentServer?.Group;

    public int CurrentGroupCount => CurrentGroup is { } g ? Profiles.Count(p => p.Group == g) : 0;

    /// <summary>Подписка текущего сервера (у своих серверов её нет — нечего обновлять).</summary>
    public SubscriptionItem? CurrentSubscription =>
        CurrentServer?.Profile.SubscriptionId is { } id ? Subscriptions.FirstOrDefault(s => s.Subscription.Id == id)
        : CurrentGroup is { } g ? Subscriptions.FirstOrDefault(s => s.Name == g) : null;

    /// <summary>Идёт проверка задержки (кнопка «Пинг» неактивна).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCurrentGroupCommand))]
    private bool isPinging;

    /// <summary>Сменился текущий сервер или список: панель показывает серверы его группы.</summary>
    private void RefreshHomeGroup()
    {
        var group = CurrentGroup;
        if (group != shownGroup)
        {
            shownGroup = group;
            groupServers?.Refresh();
        }
        OnPropertyChanged(nameof(CurrentGroup));
        OnPropertyChanged(nameof(CurrentGroupCount));
        OnPropertyChanged(nameof(CurrentSubscription));
        if (group == null)
            HomeServersOpen = false;
    }

    [RelayCommand]
    private void ToggleHomeServers() => HomeServersOpen = !HomeServersOpen && CurrentServer != null;

    [RelayCommand]
    private void CloseHomeServers() => HomeServersOpen = false;

    /// <summary>Щелчок по серверу в панели: выбрать его, а если VPN включён — переключиться (как в меню трея).</summary>
    [RelayCommand]
    private async Task PickServerAsync(ProfileItem? item)
    {
        if (item == null)
            return;
        if (item == CurrentServer)
        {
            HomeServersOpen = false;
            return;
        }
        await SwitchServerAsync(item.Profile.Id);
        HomeServersOpen = false;
    }

    private bool CanPingCurrentGroup() => !IsPinging;

    /// <summary>Пинг только серверов этой группы — быстрее, чем всех.</summary>
    [RelayCommand(CanExecute = nameof(CanPingCurrentGroup))]
    private Task PingCurrentGroupAsync() =>
        CurrentGroup is { } g ? CheckDelaysForAsync(Profiles.Where(p => p.Group == g).ToList()) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private Task UpdateCurrentSubscriptionAsync() =>
        CurrentSubscription is { } s ? UpdateSubscriptionsAsync([s.Subscription], quiet: false) : Task.CompletedTask;

    /// <summary>Тот же порядок внутри группы, что на странице «Серверы» (см. ApplySort).</summary>
    private void SortLikeServerList(ListCollectionView view)
    {
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.SortTop), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(ServerSort switch
            {
                "ping" => nameof(ProfileItem.SortDelay),
                "name" => nameof(ProfileItem.Name),
                _ => nameof(ProfileItem.Order),
            }, ListSortDirection.Ascending));
        }
        view.LiveSortingProperties.Clear();
        view.LiveSortingProperties.Add(nameof(ProfileItem.SortDelay));
        view.IsLiveSorting = ServerSort == "ping";
    }

    // ───────────── Зимнее оформление ─────────────

    public IReadOnlyList<SortChoice> WinterChoices { get; } =
    [
        new(Season.Auto, "Само: 1 декабря – 28 февраля"),
        new(Season.Always, "Всегда"),
        new(Season.Never, "Выключено"),
    ];

    [ObservableProperty] private string winterMode = Season.Auto;

    /// <summary>Сейчас загружено зимнее оформление (снег на главной, значок у названия).</summary>
    [ObservableProperty] private bool isWinter;

    /// <summary>(Разработка) снимок окна в зимнем оформлении: <c>--snapshot … --winter</c>.</summary>
    public bool? SeasonOverride { get; set; }

    /// <summary>Какое оформление нужно сейчас (по настройке и дате).</summary>
    public bool WantsWinter => SeasonOverride ?? Season.WinterThemeFor(WinterMode, DateTime.Now);

    /// <summary>Выбор в настройках сменился: окно откроется заново в новом оформлении.</summary>
    public event Action? WinterModeChanged;

    partial void OnWinterModeChanged(string value)
    {
        if (loadingSettings)
            return;
        settings.WinterTheme = value;
        SaveSettings();
        WinterModeChanged?.Invoke();
    }
}
