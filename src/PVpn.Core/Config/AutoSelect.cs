using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Config;

/// <summary>
/// «Автовыбор» для серверов по ссылкам: из группы серверов (одна подписка или свои) собирается конфиг xray, как
/// у серверов из подписки с балансировщиком: у каждого сервера свой выход, xray сам проверяет их и ведёт трафик
/// через самый быстрый (leastPing), а если проверок ещё нет — через первый. Дальше конфиг идёт тем же путём,
/// что и конфиги из подписки (служба его очищает, привязывает к адаптеру, разрешает адреса серверов заранее).
/// </summary>
public static class AutoSelect
{
    public const string BalancerTag = "auto";

    /// <summary>Префикс выходов-серверов: по нему их выбирают балансировщик и проверки (выходы фрагментации — другие).</summary>
    public const string ServerPrefix = "srv-";
    private const string DirectTag = "auto-direct";

    /// <summary>Пометка в meta: конфиг собран клиентом, к нему применяется свой профиль маршрутизации.</summary>
    internal const string MetaKey = "clientAuto";

    /// <summary>Сколько серверов нужно для автовыбора.</summary>
    public const int MinServers = 2;

    /// <summary>Серверы, которые могут войти в автовыбор: по ссылкам (у конфигов из подписки свои балансировщики).</summary>
    public static bool CanJoin(Profile p) => p is not XrayConfigProfile;

    /// <summary>Постоянный Id автовыбора группы — чтобы выбор в списке переживал перезапуск.</summary>
    public static Guid IdFor(Guid? subscriptionId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("cupoftea-auto:" + (subscriptionId?.ToString() ?? "own")));
        return new Guid(hash);
    }

    /// <param name="servers">Серверы группы и их настройки обхода (свои или с поправками панели подписки).</param>
    public static XrayConfigProfile Build(string name, Guid id, Guid? subscriptionId, IReadOnlyList<(Profile Profile, BypassOptions Bypass)> servers)
    {
        if (servers.Count < MinServers)
            throw new ConfigException($"для автовыбора нужно хотя бы {MinServers} сервера");
        var outbounds = new JsonArray();
        var index = 0;
        foreach (var (profile, bypass) in servers)
        {
            foreach (var o in ServerOutbounds(profile, $"{ServerPrefix}{index}", $"auto-fragment-{index}", null, bypass))
                outbounds.Add(o);
            index++;
        }
        return FromOutbounds(name, id, subscriptionId, outbounds, servers.Count);
    }

    /// <summary>Конфиг автовыбора из готовых выходов (серверы — с тегами <see cref="ServerPrefix"/>N).</summary>
    internal static XrayConfigProfile FromOutbounds(string name, Guid id, Guid? subscriptionId, JsonArray outbounds, int count)
    {
        outbounds.Add(new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" });

        var config = new JsonObject
        {
            ["remarks"] = name,
            ["meta"] = new JsonObject { [MetaKey] = true },
            ["outbounds"] = outbounds,
            ["burstObservatory"] = new JsonObject
            {
                ["subjectSelector"] = new JsonArray(ServerPrefix),
                ["pingConfig"] = new JsonObject
                {
                    ["destination"] = "http://www.gstatic.com/generate_204",
                    ["interval"] = "1m",
                    ["sampling"] = 2,
                    ["timeout"] = "5s",
                },
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["balancers"] = new JsonArray(new JsonObject
                {
                    ["tag"] = BalancerTag,
                    ["selector"] = new JsonArray(ServerPrefix),
                    ["strategy"] = new JsonObject { ["type"] = "leastPing" },
                    ["fallbackTag"] = ServerPrefix + "0",
                }),
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("localhost"), ["outboundTag"] = DirectTag },
                    new JsonObject { ["type"] = "field", ["ip"] = XrayConfigBuilder.ToJsonArray(XrayConfigBuilder.PrivateCidrs), ["outboundTag"] = DirectTag },
                    new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["balancerTag"] = BalancerTag },
                },
            },
        };
        var (built, _) = ServerConfigs.FromConfig(config);
        return built with
        {
            Id = id,
            SubscriptionId = subscriptionId,
            Description = $"лучший по пингу из {count}",
        };
    }

    /// <summary>Конфиг собран клиентом (см. <see cref="Build"/>).</summary>
    internal static bool IsClientAuto(JsonObject config) => config["meta"]?[MetaKey] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// Выходы одного сервера по ссылке с заданными тегами: сам сервер и, если включена фрагментация, его
    /// собственный выход фрагментации (у серверов разных подписок её настройки могут отличаться).
    /// </summary>
    internal static IEnumerable<JsonObject> ServerOutbounds(Profile profile, string tag, string fragmentTag, string? bind, BypassOptions bypass)
    {
        bypass = BypassRules.Validate(bypass);
        var fragment = bypass.Fragment && profile is StreamProfile { Security: "tls" or "reality" };
        var main = XrayConfigBuilder.BuildOutbound(profile, tag, bind, null, fragment, bypass);
        if (!fragment)
            return [main];
        main["streamSettings"]!["sockopt"]!["dialerProxy"] = fragmentTag;
        var frag = XrayConfigBuilder.BuildFragmentOutbound(bind, bypass);
        frag["tag"] = fragmentTag;
        return [main, frag];
    }
}
