using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Profiles;
using PVpn.Core.Sessions;
using Xunit.Abstractions;

namespace PVpn.Core.Tests;

/// <summary>
/// Живая проверка ссылок (режим прокси, сеть ПК не трогается): файл со ссылками — в переменной CUP_LIVE_LINKS.
/// Без неё тест ничего не делает. Ключи из файла никуда не пишутся.
/// </summary>
public class LiveLinkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ServersFromLinksAnswer()
    {
        if (Environment.GetEnvironmentVariable("CUP_LIVE_LINKS") is not { Length: > 0 } file || !File.Exists(file))
            return;
        var result = LinkParser.ParseMany(await File.ReadAllTextAsync(file));
        foreach (var e in result.Errors)
            output.WriteLine($"  разбор, строка {e.Line}: {e.Message}");
        var cores = CorePaths.Resolve(allowDevFallback: true);
        foreach (var p in result.Profiles)
        {
            try
            {
                await using var session = await ProxySession.StartAsync(p, cores, new ProxySessionOptions { SocksPort = 0, HttpPort = -1 }, (_, _) => { });
                var ip = await DelayTester.GetPublicIpViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(15));
                var delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10));
                output.WriteLine($"  {Flags.Strip(p.Name),-36} {p.ProtocolLabel,-24} IP {ip ?? "—"}, {delay}");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                output.WriteLine($"  {Flags.Strip(p.Name),-36} {p.ProtocolLabel,-24} ошибка: {e.Message.Split('\n')[0]}");
            }
        }
    }
}
