using PVpn.Core.Config;
using PVpn.Core.Diagnostics;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>
/// «Автовыбор» для серверов по ссылкам: в каждой группе (подписка или свои) с двумя и больше такими серверами
/// первым пунктом идёт автовыбор. В списке он не хранится — собирается заново при загрузке, а конфиг для xray —
/// при подключении, с текущими настройками обхода.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Id автовыбора → его группа (Id подписки; null — свои серверы).</summary>
    private readonly Dictionary<Guid, Guid?> autoGroups = [];

    private bool IsAuto(Profile profile) => autoGroups.ContainsKey(profile.Id);

    private List<Profile> AutoMembers(Guid? subscriptionId) =>
        data.Profiles.Where(p => p.SubscriptionId == subscriptionId && AutoSelect.CanJoin(p)).ToList();

    /// <summary>Пункты автовыбора для списка (по одному на подходящую группу).</summary>
    private List<ProfileItem> BuildAutoItems()
    {
        autoGroups.Clear();
        var groups = data.Profiles.Where(AutoSelect.CanJoin).GroupBy(p => p.SubscriptionId)
            .Where(g => g.Count() >= AutoSelect.MinServers).ToList();
        var items = new List<ProfileItem>();
        foreach (var g in groups)
        {
            var groupName = data.Subscriptions.FirstOrDefault(s => s.Id == g.Key)?.Name;
            var name = groups.Count == 1 ? "Автовыбор" : $"Автовыбор · {Flags.Strip(groupName ?? "свои")}";
            var id = AutoSelect.IdFor(g.Key);
            autoGroups[id] = g.Key;
            try
            {
                var profile = AutoSelect.Build(name, id, g.Key, g.Select(p => (p, BypassFor(p, settings.Bypass))).ToList());
                items.Add(new ProfileItem(profile, groupName, -1, isAuto: true));
            }
            catch (ConfigException e)
            {
                autoGroups.Remove(id);
                AppendLog("app", $"Автовыбор для «{groupName ?? "свои"}» не собрался: {e.Message}");
            }
        }
        return items;
    }

    /// <summary>
    /// Для подключения: автовыбор собирается заново из текущих серверов группы с текущими настройками обхода.
    /// null — не вышло (причина уже в <see cref="Notice"/>).
    /// </summary>
    private Profile? Materialize(Profile profile)
    {
        if (!autoGroups.TryGetValue(profile.Id, out var group))
            return profile;
        try
        {
            var user = Bypass.ToOptions();
            return AutoSelect.Build(profile.Name, profile.Id, group, AutoMembers(group).Select(p => (p, BypassFor(p, user))).ToList());
        }
        catch (ConfigException e)
        {
            Notice = "Автовыбор: " + e.Message;
            return null;
        }
    }

    /// <summary>Задержка автовыбора в списке — лучшая среди серверов его группы.</summary>
    private void ShowAutoDelays()
    {
        foreach (var item in Profiles.Where(i => i.IsAuto))
        {
            var group = autoGroups.GetValueOrDefault(item.Profile.Id);
            var delays = Profiles.Where(i => !i.IsAuto && i.Profile.SubscriptionId == group && AutoSelect.CanJoin(i.Profile))
                .Select(i => i.Delay).OfType<DelayResult>().ToList();
            item.Delay = delays.Where(d => d.Ok).MinBy(d => d.Milliseconds) ?? delays.FirstOrDefault();
            item.Checking = false;
        }
    }
}
