using System.Text;
using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Tests;

public class RoutingProfileTests
{
    // Как его отдают панели: значения строками, лишние поля (DNS, geo-ссылки) не мешают.
    private const string PanelProfile = """
        {
          "Name": "ExampleProfile", "GlobalProxy": "true", "LastUpdated": "1767225600",
          "RemoteDNSType": "DoH", "RemoteDNSDomain": "https://cloudflare-dns.com/dns-query",
          "DirectSites": ["geosite:category-ru", "gosuslugi.ru"], "DirectIp": ["geoip:ru", "192.168.0.0/16"],
          "ProxySites": ["youtube.com"], "ProxyIp": [],
          "BlockSites": ["geosite:category-ads-all"], "BlockIp": [],
          "DomainStrategy": "IPIfNonMatch"
        }
        """;

    private static readonly Profile Xhttp = LinkParser.Parse(LinkParserTests.XhttpLink);

    [Fact]
    public void ParsesJsonBase64AndLinks()
    {
        var p = RoutingProfiles.TryParse(PanelProfile)!;
        Assert.Equal("ExampleProfile", p.Name);
        Assert.True(p.GlobalProxy);
        Assert.Equal(["geosite:category-ru", "domain:gosuslugi.ru"], p.DirectSites);
        Assert.Equal(["geoip:ru", "192.168.0.0/16"], p.DirectIp);
        Assert.Equal(["domain:youtube.com"], p.ProxySites);
        Assert.Equal("IPIfNonMatch", p.DomainStrategy);
        Assert.Equal(1767225600, p.LastUpdated);

        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(PanelProfile));
        Assert.Equal(p.DirectSites, RoutingProfiles.TryParse(b64)!.DirectSites);
        Assert.Equal(p.DirectSites, RoutingProfiles.TryParse("anyapp://routing/onadd/" + b64)!.DirectSites);
        Assert.Null(RoutingProfiles.TryParse("off"));
        Assert.Null(RoutingProfiles.TryParse("vless://not-a-profile"));
    }

    [Theory]
    [InlineData("ext:custom.dat:ru")]
    [InlineData("C:\\Windows\\file")]
    [InlineData("geosite:../../x")]
    public void RejectsFilesAndJunk(string site)
    {
        Assert.Throws<ConfigException>(() => RoutingProfiles.Validate(new RoutingProfile { DirectSites = [site] }));
        Assert.Null(RoutingProfiles.TryParse($$"""{"DirectSites": ["{{site.Replace("\\", "\\\\")}}"]}"""));
    }

    [Fact]
    public void TunConfigGetsSniffingAndRulesInOrder()
    {
        var profile = RoutingProfiles.TryParse(PanelProfile)!;
        var c = XrayConfigBuilder.Build(Xhttp, ConfigBuilderTests.TunOptions() with { Routing = profile, BindInterface = "Wi-Fi" });
        Assert.True((bool?)c["inbounds"]![0]!["sniffing"]!["routeOnly"]);
        var rules = c["routing"]!["rules"]!.AsArray();
        // Блокировка → через VPN → напрямую.
        Assert.Equal([RoutingProfiles.BlockTag, XrayConfigBuilder.ProxyTag, RoutingProfiles.DirectTag, RoutingProfiles.DirectTag],
            rules.Select(r => (string?)r!["outboundTag"]));
        Assert.Equal("IPIfNonMatch", (string?)c["routing"]!["domainStrategy"]);
        var direct = c["outbounds"]!.AsArray().Single(o => (string?)o!["tag"] == RoutingProfiles.DirectTag)!;
        Assert.Equal("Wi-Fi", (string?)direct["streamSettings"]!["sockopt"]!["interface"]);

        // Без профиля в TUN маршрутов у xray нет вовсе (всё решает sing-box).
        Assert.Null(XrayConfigBuilder.Build(Xhttp, ConfigBuilderTests.TunOptions())["routing"]);
    }

    [Fact]
    public void GlobalProxyOffSendsTheRestDirect()
    {
        var c = XrayConfigBuilder.Build(Xhttp, ConfigBuilderTests.TunOptions() with
        {
            Routing = new RoutingProfile { GlobalProxy = false, ProxySites = ["domain:youtube.com"] },
        });
        var last = c["routing"]!["rules"]!.AsArray()[^1]!;
        Assert.Equal(("tcp,udp", RoutingProfiles.DirectTag), ((string?)last["network"], (string?)last["outboundTag"]));
    }

    [Fact]
    public void ProxyModeRulesGoAfterLocalAndSplitRules()
    {
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = SplitTunnelMode.OnlySelected, Domains = ["example.org"] });
        var c = XrayConfigBuilder.Build(Xhttp, new XrayOptions
        {
            SocksPort = 10808, HttpPort = 10809, SplitTunnel = split, Routing = RoutingProfiles.RussiaDirectNoAds,
        });
        var rules = c["routing"]!["rules"]!.AsArray();
        // «Остальное напрямую» режима «только выбранное» остаётся последним.
        Assert.Equal("direct", (string?)rules[^1]!["outboundTag"]);
        Assert.Equal("tcp,udp", (string?)rules[^1]!["network"]);
        Assert.Contains(rules, r => (string?)r!["outboundTag"] == RoutingProfiles.BlockTag);
        Assert.All(c["inbounds"]!.AsArray(), i => Assert.True((bool?)i!["sniffing"]!["enabled"]));
    }

    [Fact]
    public async Task XrayAcceptsPresetsWithGeoData()
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        foreach (var preset in RoutingProfiles.Presets.Append(RoutingProfiles.TryParse(PanelProfile)!))
        {
            foreach (var options in new[] { ConfigBuilderTests.TunOptions(), new XrayOptions { SocksPort = 10808, HttpPort = 10809 } })
            {
                var r = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(Xhttp, options with { Routing = preset }).ToJsonString());
                Assert.True(r.Ok, $"{preset.Name}: {r.Output}");
            }
        }
    }

    [Fact]
    public void ProfileSurvivesIpc()
    {
        var options = new Ipc.ConnectOptions { Routing = RoutingProfiles.RussiaDirect };
        var json = System.Text.Json.JsonSerializer.Serialize(options, Ipc.IpcJsonContext.Default.ConnectOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Ipc.IpcJsonContext.Default.ConnectOptions)!;
        Assert.Equal(RoutingProfiles.RussiaDirect.DirectSites, back.Routing!.DirectSites);
        _ = JsonNode.Parse(RoutingProfiles.ToJson(back.Routing));
    }
}
