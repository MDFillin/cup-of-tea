using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Net;
using PVpn.Core.Profiles;

namespace PVpn.Core.Sessions;

/// <summary>Сервер для пинга и его настройки обхода (свои или с поправками панели подписки).</summary>
public sealed record PingTarget(Profile Profile, BypassOptions Bypass);

/// <summary>
/// Пинг многих серверов одним xray: у каждого сервера свой SOCKS-вход на 127.0.0.1 и свой выход, проверки идут
/// параллельно. Раньше на каждый сервер запускался отдельный xray — на подписку из 30 серверов это 30 процессов.
/// Серверы с полным конфигом из подписки пингуются по основному серверу конфига (с цепочкой, если она есть).
/// </summary>
public static class BatchPing
{
    public const int MaxParallel = 8;

    /// <summary>Результат по Id сервера. Сервер, который не удалось описать для xray, получает ошибку, остальные проверяются.</summary>
    public static async Task<IReadOnlyDictionary<Guid, DelayResult>> MeasureAsync(IReadOnlyList<PingTarget> targets, CorePaths cores,
        Action<Guid, DelayResult>? progress = null, CancellationToken ct = default, Action<string>? log = null)
    {
        var results = new Dictionary<Guid, DelayResult>();
        if (targets.Count == 0)
            return results;
        var bind = NetworkInfo.GetDefaultPhysicalInterface()?.Name;

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        var ports = new Dictionary<Guid, int>();
        var secrets = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var (profile, bypass) = (targets[i].Profile, targets[i].Bypass);
            try
            {
                var tag = $"out-{i}";
                foreach (var o in Outbounds(profile, tag, bind, bypass))
                    outbounds.Add(o);
                var port = Ports.GetFreeLoopbackPort();
                inbounds.Add(new JsonObject
                {
                    ["tag"] = $"in-{i}",
                    ["listen"] = "127.0.0.1",
                    ["port"] = port,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
                });
                rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray($"in-{i}"), ["outboundTag"] = tag });
                ports[profile.Id] = port;
                secrets.AddRange(profile.Secrets);
            }
            catch (ConfigException e)
            {
                results[profile.Id] = new DelayResult(null, e.Message);
                progress?.Invoke(profile.Id, results[profile.Id]);
            }
        }
        if (ports.Count == 0)
            return results;

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = log != null ? "warning" : "error", ["access"] = "none" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules },
        };
        var lastPort = ports.Values.Max();
        await using var xray = CoreProcess.Start("xray-ping", cores.Xray, ["run", "-c", "stdin:"], config.ToJsonString(),
            new LogSanitizer(secrets), (_, line) => log?.Invoke(line));
        if (!await Ports.WaitListeningAsync(lastPort, TimeSpan.FromSeconds(15), () => xray.HasExited, ct).ConfigureAwait(false))
            throw new InvalidOperationException("xray для пинга не запустился: " + xray.Tail(3));

        using var limit = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(ports.Select(async kv =>
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var delay = await DelayTester.HttpDelayViaSocksAsync(kv.Value, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                lock (results)
                    results[kv.Key] = delay;
                progress?.Invoke(kv.Key, delay);
            }
            finally
            {
                limit.Release();
            }
        })).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Выходы одного сервера с уникальными тегами: сам сервер (<paramref name="tag"/>) и его вспомогательный выход
    /// фрагментации или цепочки.
    /// </summary>
    private static IEnumerable<JsonObject> Outbounds(Profile profile, string tag, string? bind, BypassOptions bypass)
    {
        if (profile is XrayConfigProfile server)
            return ServerConfigs.PingOutbounds(server, tag, bind);
        return AutoSelect.ServerOutbounds(profile, tag, tag + "-fragment", bind, bypass);
    }
}
