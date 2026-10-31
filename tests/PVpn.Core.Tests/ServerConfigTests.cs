using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Tests;

// Конфиги серверов, как их отдают панели (полный конфиг xray вместо ссылки). Все ключи — фиктивные.
public class ServerConfigTests
{
    private static JsonObject Sample() => (JsonObject)JsonNode.Parse(CoreSelfTest.ServerConfigSample)!;

    private static XrayConfigProfile Server(JsonObject? config = null) => ServerConfigs.FromConfig(config ?? Sample()).Profile;

    private static readonly XrayOptions Tun = new()
    {
        Mode = XrayMode.TunBackend, SocksPort = 23456, SocksUser = "u", SocksPassword = "p4ssw0rd", BindInterface = "Беспроводная сеть",
    };

    private static JsonArray Rules(JsonObject config) => config["routing"]!["rules"]!.AsArray();

    [Fact]
    public void ParsesPanelResponseWithServersAndNotes()
    {
        var info = Sample();
        info["remarks"] = "Использовано 3.43/60.00 ГБ";
        foreach (var o in info["outbounds"]!.AsArray())
        {
            if (o!["settings"]?["vnext"] is JsonArray vnext)
                vnext[0]!["address"] = "127.0.0.1";
            if (o["settings"]?["address"] != null)
                o["settings"]!["address"] = "127.0.0.1";
        }
        var broken = new JsonObject { ["remarks"] = "сломанный", ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "blackhole" }) };
        var result = ServerConfigs.ParseMany(new JsonArray(Sample(), info, broken).ToJsonString());

        var server = Assert.IsType<XrayConfigProfile>(Assert.Single(result.Profiles));
        Assert.Equal("self-config", server.Name);
        Assert.Equal(("a.example.com", 443), (server.Host, server.Port));
        Assert.Equal("VLESS Vision REALITY", server.MainProtocol);
        Assert.Equal(2, server.ServerCount);
        Assert.Equal("VLESS Vision REALITY · автовыбор из 2", server.ProtocolLabel);
        Assert.Equal(ServerTransport.Tcp, server.Transport);
        Assert.Equal("", server.SourceLink);
        Assert.Equal(["Использовано 3.43/60.00 ГБ"], result.Notes);
        Assert.Contains("сломанный", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void SanitizeKeepsOnlySafeParts()
    {
        var raw = Sample();
        raw["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "0.0.0.0", ["port"] = 1080, ["protocol"] = "socks" });
        raw["log"] = new JsonObject { ["access"] = @"C:\Windows\System32\evil.txt" };
        raw["api"] = new JsonObject { ["tag"] = "api" };
        var proxy = raw["outbounds"]![0]!.AsObject();
        proxy["sendThrough"] = "10.0.0.1";
        proxy["streamSettings"]!["sockopt"]!["interface"] = "Чужой адаптер";
        proxy["streamSettings"]!["tlsSettings"] = new JsonObject { ["certificates"] = new JsonArray(new JsonObject { ["certificateFile"] = @"C:\key.pem" }) };

        var clean = ServerConfigs.Sanitize(raw);
        Assert.Null(clean["inbounds"]);
        Assert.Null(clean["log"]);
        Assert.Null(clean["api"]);
        var p = clean["outbounds"]![0]!;
        Assert.Null(p["sendThrough"]);
        Assert.Null(p["streamSettings"]!["sockopt"]!["interface"]);
        Assert.Null(p["streamSettings"]!["tlsSettings"]!["certificates"]);
        // Фрагментация, маршруты, DNS и автовыбор сервера остаются как есть.
        Assert.Equal("fragment", (string?)p["streamSettings"]!["finalmask"]!["tcp"]![0]!["type"]);
        Assert.NotNull(clean["dns"]);
        Assert.NotNull(clean["burstObservatory"]);
        Assert.NotNull(clean["routing"]!["balancers"]);
    }

    [Theory]
    [InlineData("ext:custom.dat:ru", "файлы")]
    [InlineData("file:C:/x.dat", "файлы")]
    public void RejectsFileReferences(string domain, string message)
    {
        var raw = Sample();
        Rules(raw).Insert(0, new JsonObject { ["type"] = "field", ["domain"] = new JsonArray(domain), ["outboundTag"] = "direct" });
        Assert.Contains(message, Assert.Throws<ConfigException>(() => ServerConfigs.Sanitize(raw)).Message);
    }

    [Fact]
    public void RejectsSocketAddressesAndUnknownOutbounds()
    {
        var socket = Sample();
        socket["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"] = @"\\.\pipe\something";
        Assert.Throws<ConfigException>(() => ServerConfigs.Sanitize(socket));

        var unknown = Sample();
        unknown["outbounds"]!.AsArray().Add(new JsonObject { ["protocol"] = "reverse-something" });
        Assert.Throws<ConfigException>(() => ServerConfigs.Sanitize(unknown));
    }

    [Fact]
    public void TunRuntimeAddsOwnSocksAndBindsOutboundsToAdapter()
    {
        var c = XrayConfigBuilder.Build(Server(), Tun with { Bypass = BypassOptions.Default });
        var inbound = Assert.Single(c["inbounds"]!.AsArray())!;
        // Тег «socks» — на него ссылаются правила сервера (например, порт 53 → DNS).
        Assert.Equal(ServerConfigs.SocksInbound, (string?)inbound["tag"]);
        Assert.Equal("password", (string?)inbound["settings"]!["auth"]);
        Assert.True((bool?)inbound["sniffing"]!["routeOnly"]);

        foreach (var o in c["outbounds"]!.AsArray())
        {
            var bound = (string?)o!["streamSettings"]?["sockopt"]?["interface"];
            if ((string?)o["protocol"] is "blackhole" or "dns")
                Assert.Null(bound);
            else
                Assert.Equal("Беспроводная сеть", bound);
        }
        // Настройки «Обход блокировок» к серверу из подписки не применяются: своего fragment-выхода нет.
        Assert.DoesNotContain(c["outbounds"]!.AsArray(), o => (string?)o!["tag"] == XrayConfigBuilder.FragmentTag);
        Assert.Null(c["remarks"]);
        Assert.Equal("none", (string?)c["log"]!["access"]);
    }

    [Fact]
    public void ServerRoutingCanBeTurnedOff()
    {
        var on = Rules(XrayConfigBuilder.Build(Server(), Tun));
        var off = Rules(XrayConfigBuilder.Build(Server(), Tun with { UseServerRouting = false }));
        Assert.Contains(on, r => r!["domain"] != null);
        // Остаются служебные правила: DNS, inboundTag, локальные сети и последнее «всё остальное».
        Assert.DoesNotContain(off, r => r!["domain"] != null || r["protocol"] != null);
        Assert.Contains(off, r => (string?)r!["port"] == "53");
        Assert.Contains(off, r => (string?)r!["ip"]?[0] == "geoip:private");
        Assert.Equal("auto", (string?)off[^1]!["balancerTag"]);
    }

    [Theory]
    [InlineData(SplitTunnelMode.ExcludeSelected)]
    [InlineData(SplitTunnelMode.OnlySelected)]
    public void ProxySplitRulesGoAboveServerRules(SplitTunnelMode mode)
    {
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = mode, Domains = ["example.org"] });
        var c = XrayConfigBuilder.Build(Server(), new XrayOptions { SocksPort = 23458, HttpPort = 23459, SplitTunnel = split });
        var rules = Rules(c);
        var mine = rules.Select((r, i) => (r, i)).First(x => x.r!["domain"]?[0]?.ToString() == "domain:example.org");
        // После служебных (DNS, inboundTag), но раньше маршрутов сервера.
        Assert.True(rules.Take(mine.i).All(r => r!["inboundTag"] != null || (string?)r["port"] == "53"));
        if (mode == SplitTunnelMode.ExcludeSelected)
            Assert.Equal("tempest-direct", (string?)mine.r!["outboundTag"]);
        else
        {
            Assert.Equal("auto", (string?)mine.r!["balancerTag"]);
            Assert.Equal("tempest-direct", (string?)rules[mine.i + 1]!["outboundTag"]);
        }
        Assert.Equal([ServerConfigs.SocksInbound, ServerConfigs.HttpInbound], c["inbounds"]!.AsArray().Select(i => (string?)i!["tag"]));
    }

    [Fact]
    public async Task XrayAcceptsRuntimeConfigs()
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = SplitTunnelMode.OnlySelected, Domains = ["example.org"] });
        foreach (var options in new[]
                 {
                     Tun with { MetricsPort = 23457 },
                     Tun with { UseServerRouting = false },
                     new XrayOptions { SocksPort = 23458, HttpPort = 23459, SplitTunnel = split },
                 })
        {
            var r = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(Server(), options).ToJsonString());
            Assert.True(r.Ok, r.Output);
        }
    }

    [Fact]
    public async Task SingBoxSendsDnsToServerWhenItHasOwnDns()
    {
        var options = new SingBoxOptions
        {
            SocksPort = 23456, SocksUser = "u", SocksPassword = "p4ssw0rd",
            ServerAddresses = [System.Net.IPAddress.Parse("203.0.113.10")], DnsViaProxy = true,
        };
        var c = SingBoxConfigBuilder.Build(options);
        var dns = c["route"]!["rules"]!.AsArray().Single(r => (string?)r!["protocol"] == "dns")!;
        Assert.Equal(("route", "proxy"), ((string?)dns["action"], (string?)dns["outbound"]));
        Assert.True(ServerConfigs.HasDns(Server()));
        var r = await CoreValidator.ValidateSingBoxAsync(CorePaths.Resolve(allowDevFallback: true), c.ToJsonString());
        Assert.True(r.Ok, r.Output);
    }

    [Fact]
    public void SecretsAndHostsComeFromConfig()
    {
        var server = Server();
        Assert.Contains(LinkParserTests.Uuid, server.Secrets);
        Assert.Contains("self-test", server.Secrets);
        Assert.Contains("0123abcd", server.Secrets);
        Assert.Equal(["a.example.com", "203.0.113.11"], ServerConfigs.ServerHosts(server));
    }

    [Fact]
    public void ProfileSurvivesStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pvpn-test-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new ProfileStore(path);
            var data = new ProfileData();
            data.Profiles.Add(Server());
            store.Save(data);
            var back = Assert.IsType<XrayConfigProfile>(Assert.Single(store.Load().Profiles));
            Assert.Equal(Server().Config, back.Config);
            Assert.Equal(2, back.ServerCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Финляндия🇫🇮🇫🇮 xhttp", "Финляндия xhttp", "FI", 4)]
    [InlineData("🇮🇹[Ру мост]-Италия", "[Ру мост]-Италия", "IT", 2)]
    [InlineData("Автовыбор Wi-FI", "Автовыбор Wi-FI", null, 1)]
    [InlineData("by @kpenkuu4au🇬🇷", "by @kpenkuu4au", "GR", 2)]
    public void FlagsAreSplitOut(string name, string plain, string? country, int segments)
    {
        Assert.Equal(plain, Flags.Strip(name));
        Assert.Equal(country, Flags.FirstCountry(name));
        Assert.Equal(segments, Flags.Split(name).Count);
    }
}
