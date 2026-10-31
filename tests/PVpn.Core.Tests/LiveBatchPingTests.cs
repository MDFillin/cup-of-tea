using System.Diagnostics;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;
using PVpn.Core.Sessions;
using Xunit.Abstractions;

namespace PVpn.Core.Tests;

/// <summary>Живой пинг всех серверов одним xray: список читается (только чтение) из CUP_LIVE_STORE (profiles.dat).</summary>
public class LiveBatchPingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PingsAllServersInOneProcess()
    {
        if (Environment.GetEnvironmentVariable("CUP_LIVE_STORE") is not { Length: > 0 } path || !File.Exists(path))
            return;
        var data = new ProfileStore(path).Load();
        // При включённом kill switch наружу пускают только установленные ядра — их и берём, если задан CUP_LIVE_CORES.
        var cores = Environment.GetEnvironmentVariable("CUP_LIVE_CORES") is { Length: > 0 } dir
            ? new CorePaths(dir) : CorePaths.Resolve(allowDevFallback: true);
        // Для сравнения — старый способ (отдельный xray) на первых трёх серверах.
        var old = Stopwatch.StartNew();
        foreach (var p in data.Profiles.Take(3))
        {
            await using var session = await ProxySession.StartAsync(p, cores,
                new ProxySessionOptions { SocksPort = 0, HttpPort = -1 }, (_, _) => { });
            output.WriteLine($"  по одному: {Flags.Strip(p.Name),-30} {await Diagnostics.DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10))}");
        }
        output.WriteLine($"по одному 3 сервера за {old.ElapsedMilliseconds} мс");
        var sw = Stopwatch.StartNew();
        var results = await BatchPing.MeasureAsync(data.Profiles.Select(p => new PingTarget(p, BypassOptions.Default)).ToList(),
            cores);
        output.WriteLine($"серверов {data.Profiles.Count}, ответили {results.Values.Count(r => r.Ok)}, за {sw.ElapsedMilliseconds} мс");
        foreach (var p in data.Profiles)
            output.WriteLine($"  {Flags.Strip(p.Name),-40} {results.GetValueOrDefault(p.Id)}");
    }
    [Fact]
    public async Task AutoSelectOverLinkServersWorks()
    {
        if (Environment.GetEnvironmentVariable("CUP_LIVE_STORE") is not { Length: > 0 } path || !File.Exists(path))
            return;
        var cores = Environment.GetEnvironmentVariable("CUP_LIVE_CORES") is { Length: > 0 } dir
            ? new CorePaths(dir) : CorePaths.Resolve(allowDevFallback: true);
        var data = new ProfileStore(path).Load();
        // Серверы по ссылкам в подписке бывают не всегда — тогда тот же автовыбор из основных выходов полных конфигов.
        var groups = data.Profiles.Where(AutoSelect.CanJoin).GroupBy(p => p.SubscriptionId).Where(g => g.Count() >= 2)
            .Select(g => (Count: g.Count(), Build: (Func<XrayConfigProfile>)(() =>
                AutoSelect.Build("Автовыбор", AutoSelect.IdFor(g.Key), g.Key, g.Select(p => (p, BypassOptions.Default)).ToList())))).ToList();
        if (groups.Count == 0)
        {
            var servers = data.Profiles.OfType<XrayConfigProfile>().Where(p => p.ServerCount == 1).Take(5).ToList();
            groups.Add((servers.Count, () =>
            {
                var outbounds = new System.Text.Json.Nodes.JsonArray();
                for (var i = 0; i < servers.Count; i++)
                    foreach (var o in ServerConfigs.PingOutbounds(servers[i], $"{AutoSelect.ServerPrefix}{i}", null))
                        outbounds.Add(o);
                return AutoSelect.FromOutbounds("Автовыбор", Guid.NewGuid(), null, outbounds, servers.Count);
            }));
        }
        foreach (var g in groups)
        {
            var auto = g.Build();
            output.WriteLine($"выходы: {string.Join(", ", System.Text.Json.Nodes.JsonNode.Parse(auto.Config)!["outbounds"]!.AsArray().Select(o => (string?)o!["tag"]))}");
            await using var session = await ProxySession.StartAsync(auto, cores, new ProxySessionOptions { SocksPort = 0, HttpPort = -1, Routing = RoutingProfiles.RussiaDirectNoAds },
                (_, line) => output.WriteLine("xray: " + line));
            output.WriteLine($"автовыбор из {g.Count}: {await Diagnostics.DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10))}");
            // Через минуту наблюдатель xray успевает проверить серверы — балансировщик идёт через лучший.
            await Task.Delay(TimeSpan.FromSeconds(70));
            output.WriteLine($"  через 70 с: {await Diagnostics.DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10))}");
        }
    }
}
