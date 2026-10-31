using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Routing;

namespace PVpn.Core.Tests;

public class DnsTests
{
    private static SingBoxOptions SingBox(DohServer dns, SplitTunnelOptions? split = null) => new()
    {
        SocksPort = 23456,
        SocksUser = "u",
        SocksPassword = "p4ssw0rd",
        ServerAddresses = [IPAddress.Parse("203.0.113.10")],
        Dns = dns,
        SplitTunnel = split,
    };

    [Theory]
    [InlineData("https://dns.example.com/dns-query", "dns.example.com", 443, "/dns-query")]
    [InlineData("dns.example.com", "dns.example.com", 443, "/dns-query")]
    [InlineData("https://DNS.Example.com:8443/q?x=1", "dns.example.com", 8443, "/q?x=1")]
    [InlineData("1.2.3.4", "1.2.3.4", 443, "/dns-query")]
    [InlineData("https://пример.рф/", "xn--e1afmkfd.xn--p1ai", 443, "/dns-query")]
    public void ParsesCustomDoh(string input, string host, int port, string path)
    {
        var doh = DnsRules.ParseCustom(input);
        Assert.Equal((host, port, path), (doh.Host, doh.Port, doh.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://dns.example.com/dns-query")]
    [InlineData("tls://1.1.1.1")]
    [InlineData("https://user:pw@dns.example.com/")]
    [InlineData("https://[2606:4700::1111]/dns-query")]
    [InlineData("https://dns example.com/")]
    public void RejectsBadCustomDoh(string input) =>
        Assert.Throws<ConfigException>(() => DnsRules.Validate(new DnsOptions { Provider = DnsProvider.Custom, CustomUrl = input }));

    [Fact]
    public void RejectsUnknownProvider() =>
        Assert.Throws<ConfigException>(() => DnsRules.Validate(new DnsOptions { Provider = (DnsProvider)42 }));

    [Fact]
    public void PresetUsesIpWithServerName()
    {
        var c = SingBoxConfigBuilder.Build(SingBox(DnsRules.Preset(DnsProvider.AdGuard)));
        var servers = c["dns"]!["servers"]!.AsArray();
        var remote = Assert.Single(servers)!;
        Assert.Equal("94.140.14.14", (string?)remote["server"]);
        Assert.Equal("dns.adguard-dns.com", (string?)remote["tls"]!["server_name"]);
        Assert.Equal("proxy", (string?)remote["detour"]);
        Assert.Null(remote["domain_resolver"]);
    }

    [Fact]
    public void CustomDomainGetsBootstrapThroughSameRoute()
    {
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = SplitTunnelMode.ExcludeSelected, Domains = ["gosuslugi.ru"] });
        var c = SingBoxConfigBuilder.Build(SingBox(DnsRules.ParseCustom("https://dns.example.com/dns-query"), split));
        var servers = c["dns"]!["servers"]!.AsArray().Select(s => s!).ToDictionary(s => (string)s["tag"]!);
        Assert.Equal("remote-bootstrap", (string?)servers["remote"]["domain_resolver"]);
        Assert.Equal("proxy", (string?)servers["remote-bootstrap"]["detour"]);
        Assert.Equal("direct-dns-bootstrap", (string?)servers["direct-dns"]["domain_resolver"]);
        Assert.Null(servers["direct-dns-bootstrap"]["detour"]);
        Assert.Equal("1.1.1.1", (string?)servers["direct-dns-bootstrap"]["server"]);
    }

    // Настоящий sing-box принимает все готовые DNS и свой адрес (домен с bootstrap) — с раздельным туннелированием и без.
    [Theory]
    [InlineData(DnsProvider.Cloudflare, null, false)]
    [InlineData(DnsProvider.Google, null, false)]
    [InlineData(DnsProvider.Quad9, null, true)]
    [InlineData(DnsProvider.AdGuard, null, true)]
    [InlineData(DnsProvider.Custom, "https://dns.example.com:8443/dns-query", false)]
    [InlineData(DnsProvider.Custom, "https://dns.example.com/dns-query", true)]
    [InlineData(DnsProvider.Custom, "9.9.9.9", true)]
    public async Task SingBoxAcceptsDnsConfigs(DnsProvider provider, string? url, bool split)
    {
        var options = DnsRules.Validate(new DnsOptions { Provider = provider, CustomUrl = url });
        var splitOptions = split
            ? SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = SplitTunnelMode.OnlySelected, Domains = ["youtube.com"] })
            : null;
        var config = SingBoxConfigBuilder.Build(SingBox(DnsRules.Resolve(options), splitOptions));
        var result = await CoreValidator.ValidateSingBoxAsync(CorePaths.Resolve(allowDevFallback: true), config.ToJsonString());
        Assert.True(result.Ok, result.Output);
    }

    [Fact]
    public void IpcCarriesDnsAndDefaultsWhenMissing()
    {
        var request = new IpcRequest
        {
            Command = IpcCommands.Connect,
            Options = new ConnectOptions { Dns = new DnsOptions { Provider = DnsProvider.Quad9 } },
        };
        var json = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);
        Assert.Contains("\"Quad9\"", json);
        Assert.Equal(DnsProvider.Quad9, JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest)!.Options!.Dns!.Provider);

        var partial = JsonSerializer.Deserialize("{\"customUrl\":\"x\"}", IpcJsonContext.Default.DnsOptions)!;
        Assert.Equal(DnsProvider.Cloudflare, partial.Provider);
        Assert.Equal("1.1.1.1", DnsRules.Resolve(null).Host);
    }

    private static BashWsEntry Entry(string ip, string org, string type) => new(ip, "nl", "Netherlands", org, org, type);

    [Fact]
    public void LeakVerdictFollowsExpectedProvider()
    {
        var entries = new[]
        {
            Entry("203.0.113.10", "AS1 Hoster", "ip"),
            Entry("172.70.1.1", "AS13335 CloudFlare Inc", "dns"),
            Entry("172.70.1.2", "AS13335 CloudFlare Inc", "dns"),
        };
        var ok = DnsLeakTest.Evaluate(entries, DnsRules.OrgKeywords(DnsProvider.Cloudflare));
        Assert.False(ok.Leak);
        Assert.Equal(2, ok.Resolvers.Count);
        Assert.Equal("203.0.113.10", Assert.Single(ok.Exit).Ip);

        var leak = DnsLeakTest.Evaluate([.. entries, Entry("95.1.1.1", "AS12389 Rostelecom", "dns")], DnsRules.OrgKeywords(DnsProvider.Cloudflare));
        Assert.True(leak.Leak);
        Assert.Contains("Rostelecom", leak.Verdict);

        Assert.Null(DnsLeakTest.Evaluate(entries, null).Leak);
        Assert.Null(DnsLeakTest.Evaluate([entries[0]], DnsRules.OrgKeywords(DnsProvider.Cloudflare)).Leak);
    }

    [Fact]
    public void LeakTestIdRejectsHtmlPage()
    {
        Assert.Equal("625fd927f73cf6e7", DnsLeakTest.ParseId("625fd927f73cf6e7\n"));
        Assert.Null(DnsLeakTest.ParseId("<!DOCTYPE HTML>\n<html lang=\"id\" class=\"fonts-loading\">"));
        Assert.Null(DnsLeakTest.ParseId(""));
        Assert.Null(DnsLeakTest.ParseId(new string('a', 65)));
    }
}
