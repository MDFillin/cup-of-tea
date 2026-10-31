using System.Diagnostics;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;
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
}
