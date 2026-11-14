using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>Подписка в списке настроек: трафик, срок и время обновления.</summary>
public sealed class SubscriptionItem(Subscription subscription, int servers)
{
    public Subscription Subscription { get; } = subscription;
    public string Name => Subscription.Name;

    public string Details
    {
        get
        {
            var s = Subscription;
            var used = Gb(s.Upload + s.Download);
            var parts = new List<string>
            {
                $"серверов {servers}",
                s.Total > 0 ? $"трафик {used} из {Gb(s.Total)} ГБ" : $"трафик {used} ГБ",
                s.Expire is { } e ? $"до {e.LocalDateTime:dd.MM.yyyy}" + (e < DateTimeOffset.Now ? " (истекла)" : "") : "бессрочно",
                s.LastUpdated is { } t ? $"обновлена {t.LocalDateTime:dd.MM HH:mm}" : "не обновлялась",
            };
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Коротко для главной: трафик, срок и когда обновлена (без числа серверов — оно рядом).</summary>
    public string Summary
    {
        get
        {
            var s = Subscription;
            var used = Gb(s.Upload + s.Download);
            var parts = new List<string>();
            if (s.Total > 0 || s.Upload + s.Download > 0)
                parts.Add(s.Total > 0 ? $"{used} из {Gb(s.Total)} ГБ" : $"{used} ГБ");
            if (s.Expire is { } e)
                parts.Add(e < DateTimeOffset.Now ? "срок истёк" : $"до {e.LocalDateTime:dd.MM.yyyy}");
            parts.Add(s.LastUpdated is { } t ? $"обновлена {t.LocalDateTime:dd.MM HH:mm}" : "не обновлялась");
            return string.Join(" · ", parts);
        }
    }

    public string? Error => Subscription.LastError is { } e ? $"Не удалось обновить: {e}" : null;

    public string? Description => Subscription.Description;
    public string? SupportUrl => Subscription.SupportUrl;
    public string? WebPageUrl => Subscription.WebPageUrl;
    public bool HasLinks => SupportUrl != null || WebPageUrl != null;

    /// <summary>Служебные строки панели: «Использовано 3.43/60 ГБ, доступно 56.57 ГБ» и т. п.</summary>
    public string? Notes => Subscription.Notes.Count > 0 ? string.Join(Environment.NewLine, Subscription.Notes) : null;

    /// <summary>Доля израсходованного трафика (0–100) для полоски; без лимита — 0.</summary>
    public double UsedPercent => Subscription.Total > 0 ? Math.Min(100, 100.0 * (Subscription.Upload + Subscription.Download) / Subscription.Total) : 0;
    public bool HasLimit => Subscription.Total > 0;

    private static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.##", CultureInfo.CurrentCulture);
}

public sealed partial class MainViewModel
{
    public ObservableCollection<SubscriptionItem> Subscriptions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSubscriptionCommand))]
    private string subscriptionUrl = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSubscriptionCommand), nameof(UpdateSubscriptionCommand), nameof(UpdateAllSubscriptionsCommand), nameof(UpdateCurrentSubscriptionCommand))]
    private bool subscriptionBusy;

    private void LoadSubscriptions()
    {
        Subscriptions.Clear();
        foreach (var s in data.Subscriptions)
            Subscriptions.Add(new SubscriptionItem(s, data.Profiles.Count(p => p.SubscriptionId == s.Id)));
        RefreshRoutingChoices();
    }

    private bool CanAddSubscription() => !SubscriptionBusy && !string.IsNullOrWhiteSpace(SubscriptionUrl);

    [RelayCommand(CanExecute = nameof(CanAddSubscription))]
    private Task AddSubscription() => AddSubscriptionAsync(SubscriptionUrl);

    private async Task AddSubscriptionAsync(string url)
    {
        SubscriptionBusy = true;
        try
        {
            using var http = SubscriptionClient.CreateClient(settings.SendHwid);
            var update = await SubscriptionService.AddAsync(data, http, url, null);
            if (TrySaveProfiles())
            {
                SubscriptionUrl = "";
                Notice = "Подписка добавлена. " + update;
                PingIfEnabled();
            }
        }
        catch (SubscriptionException e)
        {
            Notice = e.Message;
        }
        finally
        {
            SubscriptionBusy = false;
            LoadProfiles();
        }
    }

    private bool CanUpdateSubscription(SubscriptionItem? item) => !SubscriptionBusy && item != null;

    [RelayCommand(CanExecute = nameof(CanUpdateSubscription))]
    private Task UpdateSubscription(SubscriptionItem? item) => UpdateSubscriptionsAsync(item == null ? [] : [item.Subscription], quiet: false);

    private bool CanUpdateAll() => !SubscriptionBusy;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private Task UpdateAllSubscriptions() => UpdateSubscriptionsAsync(data.Subscriptions.ToList(), quiet: false);

    [RelayCommand]
    private void RemoveSubscription(SubscriptionItem? item)
    {
        if (item == null)
            return;
        if (MessageBox.Show($"Удалить подписку «{item.Name}» и все её серверы?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        data.RemoveSubscription(item.Subscription.Id);
        TrySaveProfiles();
        SelectedProfile = null;
        LoadProfiles();
    }

    /// <summary>Автообновление: раз в минуту смотрит, каким подпискам пора (молча; ошибка видна в настройках).</summary>
    private async Task AutoUpdateSubscriptionsAsync()
    {
        if (SubscriptionBusy)
            return;
        var due = data.Subscriptions.Where(s => s.IsDue(DateTimeOffset.Now)).ToList();
        if (due.Count > 0)
            await UpdateSubscriptionsAsync(due, quiet: true);
    }

    private async Task UpdateSubscriptionsAsync(IReadOnlyList<Subscription> subscriptions, bool quiet)
    {
        if (subscriptions.Count == 0)
            return;
        SubscriptionBusy = true;
        var messages = new List<string>();
        try
        {
            using var http = SubscriptionClient.CreateClient(settings.SendHwid);
            foreach (var s in subscriptions)
            {
                // Подписку могли удалить, пока шло обновление другой.
                if (data.Subscriptions.FirstOrDefault(x => x.Id == s.Id) is not { } current)
                    continue;
                try
                {
                    messages.Add((await SubscriptionService.UpdateAsync(data, http, current)).ToString());
                }
                catch (SubscriptionException e)
                {
                    messages.Add($"«{s.Name}»: {e.Message}");
                }
                catch (HttpRequestException e)
                {
                    messages.Add($"«{s.Name}»: {e.Message}");
                }
            }
            TrySaveProfiles();
        }
        finally
        {
            SubscriptionBusy = false;
            LoadProfiles();
        }
        if (!quiet)
            Notice = string.Join(" ", messages);
        PingIfEnabled();
    }
}
