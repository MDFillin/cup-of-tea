using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.App;

/// <summary>Пункт выбора профиля маршрутизации: ключ для настроек и подпись.</summary>
public sealed record RoutingChoice(string Key, string Title);

/// <summary>Пункт сортировки серверов.</summary>
public sealed record SortChoice(string Key, string Title);

/// <summary>
/// Профили маршрутизации (свои, готовые и от подписки), объявления панели, сортировка и автоматический пинг серверов.
/// </summary>
public sealed partial class MainViewModel
{
    private const string RoutingOff = "off", RoutingCustom = "custom", RoutingSubscriptionPrefix = "sub:";

    public ObservableCollection<RoutingChoice> RoutingChoices { get; } = [];

    /// <summary>Выбранный профиль (ключ <see cref="RoutingChoice.Key"/>).</summary>
    [ObservableProperty] private string routingChoice = RoutingOff;

    /// <summary>Что в выбранном профиле — для подписи под выбором.</summary>
    [ObservableProperty] private string? routingSummary;

    public IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new("none", "Как в подписке"),
        new("ping", "По пингу"),
        new("name", "По имени"),
    ];

    [ObservableProperty] private string serverSort = "none";

    /// <summary>Пинг всех серверов при запуске и после обновления подписки.</summary>
    [ObservableProperty] private bool autoPing;

    /// <summary>Сообщать панели подписки идентификатор устройства (лимит устройств).</summary>
    [ObservableProperty] private bool sendHwid;

    /// <summary>Объявление панели (заголовок announce) на главной; null — нет или скрыто.</summary>
    [ObservableProperty] private string? announceText;
    [ObservableProperty] private string? announceUrl;
    private string? dismissedAnnounce;

    private void LoadRoutingSettings()
    {
        RoutingChoice = settings.RoutingChoice;
        ServerSort = SortChoices.Any(c => c.Key == settings.ServerSort) ? settings.ServerSort : "none";
        AutoPing = settings.AutoPing;
        SendHwid = settings.SendHwid;
        ApplySort();
    }

    /// <summary>Список профилей: выключено, готовые, от подписок и свой (импортированный).</summary>
    private bool refreshingRouting;

    private void RefreshRoutingChoices()
    {
        // Пока список пересобирается, выпадающий список сбрасывает выбор в null — это не выбор пользователя.
        refreshingRouting = true;
        var keep = settings.RoutingChoice;
        RoutingChoices.Clear();
        RoutingChoices.Add(new RoutingChoice(RoutingOff, "Выключен — всё через VPN"));
        foreach (var preset in RoutingProfiles.Presets)
            RoutingChoices.Add(new RoutingChoice(preset.Name, preset.Name));
        foreach (var s in data.Subscriptions.Where(s => s.Routing != null))
            RoutingChoices.Add(new RoutingChoice(RoutingSubscriptionPrefix + s.Id, $"От подписки «{Flags.Strip(s.Name)}»: {s.Routing!.Name}"));
        if (settings.CustomRouting is { } custom)
            RoutingChoices.Add(new RoutingChoice(RoutingCustom, $"Свой: {custom.Name}"));

        // Подписка впервые прислала профиль, а пользователь ничего не выбирал — включаем его (как «onadd» у панелей).
        if (keep == RoutingOff && !settings.RoutingChosenByUser
            && data.Subscriptions.FirstOrDefault(s => s.Routing != null) is { } withRouting)
            keep = RoutingSubscriptionPrefix + withRouting.Id;
        RoutingChoice = RoutingChoices.Any(c => c.Key == keep) ? keep : RoutingOff;
        refreshingRouting = false;
        if (settings.RoutingChoice != RoutingChoice)
        {
            settings.RoutingChoice = RoutingChoice;
            SaveSettings();
        }
        UpdateRoutingSummary();
        UpdateAnnounce();
    }

    partial void OnRoutingChoiceChanged(string value)
    {
        if (value == null)
        {
            if (!refreshingRouting)
                RoutingChoice = settings.RoutingChoice;
            return;
        }
        UpdateRoutingSummary();
        if (loadingSettings || refreshingRouting || settings.RoutingChoice == value)
            return;
        settings.RoutingChoice = value;
        settings.RoutingChosenByUser = true;
        SaveSettings();
        if (IsActive)
            Notice = "Профиль маршрутизации подействует при следующем подключении.";
    }

    /// <summary>Профиль для подключения (серверам с полным конфигом из подписки — их собственные маршруты).</summary>
    private RoutingProfile? EffectiveRouting() => RoutingChoice switch
    {
        RoutingOff => null,
        RoutingCustom => settings.CustomRouting,
        var key when key.StartsWith(RoutingSubscriptionPrefix, StringComparison.Ordinal) =>
            data.Subscriptions.FirstOrDefault(s => RoutingSubscriptionPrefix + s.Id == key)?.Routing,
        var name => RoutingProfiles.Presets.FirstOrDefault(p => p.Name == name),
    };

    private void UpdateRoutingSummary()
    {
        if (EffectiveRouting() is not { } p)
        {
            RoutingSummary = "Весь трафик идёт через VPN (кроме правил раздельного туннелирования).";
            return;
        }
        static string Part(string what, int sites, int ips) => sites + ips == 0 ? "" : $"{what}: сайтов {sites}, адресов {ips}";
        var parts = new[]
        {
            Part("напрямую", p.DirectSites.Count, p.DirectIp.Count),
            Part("через VPN", p.ProxySites.Count, p.ProxyIp.Count),
            Part("блокировать", p.BlockSites.Count, p.BlockIp.Count),
        }.Where(s => s.Length > 0);
        RoutingSummary = string.Join("; ", parts) + (p.GlobalProxy ? ". Остальное — через VPN." : ". Остальное — напрямую.")
                         + " Для серверов с полным конфигом из подписки действуют их маршруты.";
    }

    /// <summary>Профиль из буфера обмена: JSON, base64 или ссылка «…://routing/onadd/…».</summary>
    [RelayCommand]
    private void ImportRouting()
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            text = "";
        }
        if (RoutingProfiles.TryParse(text) is not { } profile)
        {
            Notice = "В буфере обмена нет профиля маршрутизации (JSON с DirectSites/ProxySites/BlockSites, base64 или ссылка …://routing/onadd/…).";
            return;
        }
        ApplyImportedRouting(profile);
    }

    private void ApplyImportedRouting(RoutingProfile profile)
    {
        settings.CustomRouting = profile;
        settings.RoutingChoice = RoutingCustom;
        settings.RoutingChosenByUser = true;
        SaveSettings();
        RefreshRoutingChoices();
        RoutingChoice = RoutingCustom;
        Notice = $"Профиль маршрутизации «{profile.Name}» добавлен и выбран ({profile.RuleCount} правил).";
    }

    // ───────────── Объявления панели ─────────────

    /// <summary>Объявление подписки текущего сервера, иначе — первой подписки, где оно есть.</summary>
    private void UpdateAnnounce()
    {
        var current = CurrentServer?.Profile.SubscriptionId;
        var sub = data.Subscriptions.FirstOrDefault(s => s.Id == current && s.Announce != null)
                  ?? data.Subscriptions.FirstOrDefault(s => s.Announce != null);
        AnnounceText = sub?.Announce is { } text && text != dismissedAnnounce ? text : null;
        AnnounceUrl = AnnounceText != null ? sub?.AnnounceUrl : null;
    }

    [RelayCommand]
    private void DismissAnnounce()
    {
        dismissedAnnounce = AnnounceText;
        AnnounceText = null;
    }

    /// <summary>Открыть ссылку панели (объявление, поддержка, сайт) в браузере.</summary>
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" or "tg" or "mailto")
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    // ───────────── Сортировка и автопинг ─────────────

    partial void OnServerSortChanged(string value)
    {
        ApplySort();
        if (loadingSettings)
            return;
        settings.ServerSort = value;
        SaveSettings();
    }

    partial void OnAutoPingChanged(bool value)
    {
        if (loadingSettings)
            return;
        settings.AutoPing = value;
        SaveSettings();
    }

    partial void OnSendHwidChanged(bool value)
    {
        if (loadingSettings)
            return;
        settings.SendHwid = value;
        SaveSettings();
    }

    /// <summary>Сортировка внутри групп (подписок); по пингу — живая: строки переставляются по мере проверки.</summary>
    private void ApplySort()
    {
        var view = ProfilesView;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.Group), ListSortDirection.Ascending));
            switch (ServerSort)
            {
                case "ping":
                    view.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.SortDelay), ListSortDirection.Ascending));
                    break;
                case "name":
                    view.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.Name), ListSortDirection.Ascending));
                    break;
                default:
                    view.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.Order), ListSortDirection.Ascending));
                    break;
            }
        }
        if (view is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            live.LiveSortingProperties.Clear();
            live.LiveSortingProperties.Add(nameof(ProfileItem.SortDelay));
            live.IsLiveSorting = ServerSort == "ping";
        }
    }

    /// <summary>Пинг после запуска и после обновления подписки, если включено (как «автоматический пинг»).</summary>
    private void PingIfEnabled()
    {
        if (AutoPing && Profiles.Count > 0)
            _ = CheckDelaysAsync();
    }
}
