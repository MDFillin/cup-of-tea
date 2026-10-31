using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Tests;

public class AutoSelectTests
{
    private static readonly Guid Sub = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static XrayConfigProfile Auto(BypassOptions? bypass = null) => AutoSelect.Build("Автовыбор", AutoSelect.IdFor(Sub), Sub,
        new[] { LinkParserTests.XhttpLink, LinkParserTests.VisionLink, LinkParserTests.Hy2Link }
            .Select(l => (LinkParser.Parse(l), BypassRules.Validate(bypass ?? BypassOptions.Default))).ToList());

    private static readonly XrayOptions Tun = new()
    {
        Mode = XrayMode.TunBackend, SocksPort = 23456, SocksUser = "u", SocksPassword = "p4ssw0rd", BindInterface = "Wi-Fi",
    };

    [Fact]
    public void BuildsBalancerOverAllServers()
    {
        var auto = Auto();
        Assert.Equal(AutoSelect.IdFor(Sub), auto.Id);
        Assert.Equal(Sub, auto.SubscriptionId);
        Assert.Equal(3, auto.ServerCount);
        Assert.Equal("Автовыбор", auto.Name);
        var config = JsonNode.Parse(auto.Config)!;
        var servers = config["outbounds"]!.AsArray().Where(o => ((string)o!["tag"]!).StartsWith(AutoSelect.ServerPrefix)).ToList();
        Assert.Equal(3, servers.Count);
        // Фрагментация — свои выходы с другим префиксом: балансировщик не выберет их как «сервер».
        Assert.All(config["outbounds"]!.AsArray().Where(o => (string?)o!["protocol"] == "freedom"),
            o => Assert.False(((string)o!["tag"]!).StartsWith(AutoSelect.ServerPrefix)));
        var balancer = config["routing"]!["balancers"]![0]!;
        Assert.Equal("leastPing", (string?)balancer["strategy"]!["type"]);
        Assert.Equal(AutoSelect.BalancerTag, (string?)config["routing"]!["rules"]!.AsArray().Last()!["balancerTag"]);
        Assert.NotEqual(AutoSelect.IdFor(null), AutoSelect.IdFor(Sub));
    }

    [Fact]
    public void ServiceAcceptsItAndAppliesOwnRouting()
    {
        // Служба получает конфиг по каналу и разбирает его заново.
        var runtime = ServerConfigs.ParseRuntime(Auto().Config);
        Assert.Equal("Автовыбор", runtime.Name);
        var routing = RoutingProfiles.Presets.First(p => p.DirectSites.Count > 0);
        var c = XrayConfigBuilder.Build(runtime, Tun with { Routing = routing, UseServerRouting = false });
        var rules = c["routing"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(rules, r => (string?)r["outboundTag"] == RoutingProfiles.DirectTag && r["domain"] != null);
        Assert.Contains(rules, r => r.ContainsKey("ip") && (string?)r["outboundTag"] == "auto-direct");
        Assert.Equal(AutoSelect.BalancerTag, (string?)rules[^1]["balancerTag"]);
        Assert.DoesNotContain(rules, r => (string?)r["outboundTag"] == XrayConfigBuilder.ProxyTag);
        // Каждый сервер привязан к адаптеру сам или ходит через привязанный выход фрагментации.
        var outbounds = c["outbounds"]!.AsArray().OfType<JsonObject>().ToDictionary(o => (string)o["tag"]!);
        foreach (var server in outbounds.Values.Where(o => ((string)o["tag"]!).StartsWith(AutoSelect.ServerPrefix)))
        {
            var sockopt = server["streamSettings"]!["sockopt"]!;
            var bound = (string?)sockopt["dialerProxy"] is { } via ? outbounds[via]["streamSettings"]!["sockopt"]! : sockopt;
            Assert.Equal("Wi-Fi", (string?)bound["interface"]);
        }
    }

    [Fact]
    public async Task XrayAcceptsAutoConfigs()
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        var runtime = ServerConfigs.ParseRuntime(Auto(new BypassOptions { Fragment = true }).Config);
        foreach (var options in new[]
                 {
                     Tun with { Routing = RoutingProfiles.RussiaDirectNoAds, MetricsPort = 23457 },
                     new XrayOptions { SocksPort = 23458, HttpPort = 23459 },
                 })
        {
            var r = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(runtime, options).ToJsonString());
            Assert.True(r.Ok, r.Output);
        }
    }

    [Fact]
    public void NeedsTwoServers()
    {
        Assert.Throws<ConfigException>(() => AutoSelect.Build("x", Guid.NewGuid(), null,
            [(LinkParser.Parse(LinkParserTests.XhttpLink), BypassOptions.Default)]));
        Assert.False(AutoSelect.CanJoin(Auto()));
    }
}
