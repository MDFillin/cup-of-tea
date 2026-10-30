using System.Net;
using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class ConfigBuilderTests
{
    private static readonly Profile Xhttp = LinkParser.Parse(LinkParserTests.XhttpLink);
    private static readonly Profile Vision = LinkParser.Parse(LinkParserTests.VisionLink);
    private static readonly Profile Hy2 = LinkParser.Parse(LinkParserTests.Hy2Link);

    internal static XrayOptions TunOptions(int port = 23456) => new()
    {
        Mode = XrayMode.TunBackend,
        SocksPort = port,
        SocksUser = "u",
        SocksPassword = "p4ssw0rd",
        BindInterface = "Беспроводная сеть",
    };

    private static SingBoxOptions SingBox(bool blockQuic = true) => new()
    {
        SocksPort = 23456,
        SocksUser = "u",
        SocksPassword = "p4ssw0rd",
        ServerAddresses = [IPAddress.Parse("203.0.113.10")],
        DirectCidrs = ["26.0.0.0/8"],
        BlockQuic = blockQuic,
    };

    [Fact]
    public void XhttpOutboundHasRealityAndBinding()
    {
        var o = XrayConfigBuilder.BuildOutbound(Xhttp, "proxy", "Беспроводная сеть");
        Assert.Equal("vless", (string?)o["protocol"]);
        Assert.Equal("203.0.113.10", (string?)o["settings"]!["address"]);
        Assert.Equal(443, (int?)o["settings"]!["port"]);
        Assert.Null(o["settings"]!["flow"]);
        var stream = o["streamSettings"]!;
        Assert.Equal("xhttp", (string?)stream["network"]);
        Assert.Equal("/deadbeef", (string?)stream["xhttpSettings"]!["path"]);
        Assert.Equal("www.example.nl", (string?)stream["realitySettings"]!["serverName"]);
        Assert.Equal(LinkParserTests.Pbk, (string?)stream["realitySettings"]!["publicKey"]);
        Assert.Equal("Беспроводная сеть", (string?)stream["sockopt"]!["interface"]);
    }

    [Fact]
    public void VisionOutboundHasFlowAndRawNetwork()
    {
        var o = XrayConfigBuilder.BuildOutbound(Vision, "proxy", null);
        Assert.Equal("xtls-rprx-vision", (string?)o["settings"]!["flow"]);
        Assert.Equal("raw", (string?)o["streamSettings"]!["network"]);
        Assert.Null(o["streamSettings"]!["sockopt"]);
    }

    [Fact]
    public void Hysteria2OutboundPinsCertificate()
    {
        var o = XrayConfigBuilder.BuildOutbound(Hy2, "proxy", null, addressOverride: "198.51.100.7");
        Assert.Equal("hysteria", (string?)o["protocol"]);
        Assert.Equal(2, (int?)o["settings"]!["version"]);
        Assert.Equal("198.51.100.7", (string?)o["settings"]!["address"]);
        var stream = o["streamSettings"]!;
        Assert.Equal("hysteria", (string?)stream["network"]);
        Assert.Equal("my:s3cret-pass", (string?)stream["hysteriaSettings"]!["auth"]);
        Assert.Equal(LinkParserTests.Pin, (string?)stream["tlsSettings"]!["pinnedPeerCertSha256"]);
        Assert.Equal("h3", (string?)stream["tlsSettings"]!["alpn"]![0]);
        Assert.Null(stream["tlsSettings"]!["allowInsecure"]);
    }

    [Fact]
    public void ProxyModeHasSocksHttpAndDirectPrivate()
    {
        var c = XrayConfigBuilder.Build(Xhttp, new XrayOptions { SocksPort = 10808, HttpPort = 10809 });
        var inbounds = c["inbounds"]!.AsArray();
        Assert.Equal(["socks", "http"], inbounds.Select(i => (string?)i!["protocol"]));
        Assert.All(inbounds, i => Assert.Equal("127.0.0.1", (string?)i!["listen"]));
        Assert.Equal("proxy", (string?)c["outbounds"]![0]!["tag"]);
        Assert.Contains("192.168.0.0/16", c["routing"]!["rules"]![1]!["ip"]!.AsArray().Select(x => (string?)x));
    }

    [Fact]
    public void VlessDialsThroughFragmentOutbound()
    {
        var c = XrayConfigBuilder.Build(Xhttp, TunOptions());
        var outbounds = c["outbounds"]!.AsArray();
        Assert.Equal("fragment", (string?)outbounds[0]!["streamSettings"]!["sockopt"]!["dialerProxy"]);
        var fragment = outbounds.Single(o => (string?)o!["tag"] == "fragment")!;
        Assert.Equal("tlshello", (string?)fragment["settings"]!["fragment"]!["packets"]);
        Assert.Equal("Беспроводная сеть", (string?)fragment["streamSettings"]!["sockopt"]!["interface"]);

        var plain = XrayConfigBuilder.Build(Xhttp, TunOptions() with { Bypass = new BypassOptions { Fragment = false } });
        Assert.DoesNotContain(plain["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "fragment");
    }

    [Fact]
    public void Hysteria2NeverUsesFragment()
    {
        var c = XrayConfigBuilder.Build(Hy2, TunOptions());
        Assert.DoesNotContain(c["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "fragment");
        Assert.Null(c["outbounds"]![0]!["streamSettings"]!["sockopt"]!["dialerProxy"]);
    }

    [Fact]
    public void TunBackendRequiresPasswordAndHasNoDirect()
    {
        Assert.Throws<ConfigException>(() => XrayConfigBuilder.Build(Xhttp, TunOptions() with { SocksPassword = null }));
        var c = XrayConfigBuilder.Build(Xhttp, TunOptions());
        var socks = c["inbounds"]![0]!;
        Assert.Equal("password", (string?)socks["settings"]!["auth"]);
        Assert.Equal("127.0.0.1", (string?)socks["listen"]);
        // Прямого выхода нет: «fragment» — тоже freedom, но он только устанавливает соединение с сервером.
        Assert.DoesNotContain(c["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "direct");
        Assert.Null(c["routing"]);
    }

    [Fact]
    public void QuicIsRejectedBySingBoxNotDroppedByXray()
    {
        // sing-box отвечает отказом — браузер сразу уходит на TCP. Молча выброшенный пакет (blackhole xray)
        // заставлял ждать секунды при первом заходе на сайт.
        var rules = SingBox().Let(SingBoxConfigBuilder.Build)["route"]!["rules"]!.AsArray();
        Assert.Contains(rules, r => (string?)r!["network"] == "udp" && (int?)r["port"] == 443 && (string?)r["action"] == "reject");
        Assert.Null(XrayConfigBuilder.Build(Xhttp, TunOptions())["routing"]);
    }

    [Fact]
    public void SingBoxUsesRealDnsAndExcludesServer()
    {
        var c = SingBox().Let(SingBoxConfigBuilder.Build);
        var json = c.ToJsonString();
        Assert.DoesNotContain("fakeip", json);
        Assert.Equal("ipv4_only", (string?)c["dns"]!["strategy"]);
        Assert.Equal("proxy", (string?)c["dns"]!["servers"]![0]!["detour"]);
        var tun = c["inbounds"]![0]!;
        Assert.True((bool?)tun["auto_route"]);
        Assert.True((bool?)tun["strict_route"]);
        Assert.Equal("203.0.113.10/32", (string?)tun["route_exclude_address"]![0]);
        Assert.Equal("proxy", (string?)c["route"]!["final"]);
        Assert.Contains("\"26.0.0.0/8\"", json);
    }

    [Fact]
    public void SingBoxQuicRuleFollowsOption()
    {
        static bool HasQuicRule(JsonObject c) =>
            c["route"]!["rules"]!.AsArray().Any(r => (int?)r!["port"] == 443 && (string?)r["action"] == "reject");
        Assert.True(HasQuicRule(SingBoxConfigBuilder.Build(SingBox(blockQuic: true))));
        Assert.False(HasQuicRule(SingBoxConfigBuilder.Build(SingBox(blockQuic: false))));
    }

    // Проверка настоящими ядрами из D:\vpn\client\cores (scripts\fetch-cores.ps1).
    [Theory]
    [InlineData("xhttp")]
    [InlineData("vision")]
    [InlineData("hy2")]
    public async Task CoresAcceptGeneratedConfigs(string which)
    {
        var profile = which switch { "xhttp" => Xhttp, "vision" => Vision, _ => Hy2 };
        var cores = CorePaths.Resolve(allowDevFallback: true);

        var proxy = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(profile, new XrayOptions { SocksPort = 10808, HttpPort = 10809, BindInterface = "Wi-Fi" }).ToJsonString());
        Assert.True(proxy.Ok, proxy.Output);

        var tun = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(profile, TunOptions()).ToJsonString());
        Assert.True(tun.Ok, tun.Output);

        var singBox = await CoreValidator.ValidateSingBoxAsync(cores, SingBoxConfigBuilder.Build(SingBox()).ToJsonString());
        Assert.True(singBox.Ok, singBox.Output);
    }
}

internal static class FunctionalExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
