using System.Text.Json;
using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Ipc;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class BypassTests
{
    private static readonly Profile Xhttp = LinkParser.Parse(LinkParserTests.XhttpLink);
    private static readonly Profile Vision = LinkParser.Parse(LinkParserTests.VisionLink);
    private static readonly Profile Hy2 = LinkParser.Parse(LinkParserTests.Hy2Link);

    // VLESS REALITY по TCP без Vision — через mux идут и TCP, и UDP.
    private static readonly Profile PlainReality = LinkParser.Parse(LinkParserTests.VisionLink.Replace("flow=xtls-rprx-vision&", ""));

    private static readonly Profile XhttpWithExtra = LinkParser.Parse(LinkParserTests.XhttpLink.Replace("#my-xhttp",
        "&extra=" + Uri.EscapeDataString("""{"xPaddingBytes":"100-1000","xmux":{"maxConnections":"2"}}""") + "#my-xhttp"));

    private static readonly Profile Hy2Hop = LinkParser.Parse(LinkParserTests.Hy2Link.Replace(":443?", ":443,20000-30000?"));

    private static readonly Profile Hy2Obfs = LinkParser.Parse(LinkParserTests.Hy2Link.Replace(":443?", ":8443?obfs=salamander&obfs-password=0bf5&mport=30000-39999&"));

    private static JsonObject Proxy(Profile profile, BypassOptions bypass) =>
        (JsonObject)XrayConfigBuilder.Build(profile, ConfigBuilderTests.TunOptions() with { Bypass = BypassRules.Validate(bypass) })["outbounds"]![0]!;

    [Fact]
    public void DefaultsAreTunedValues()
    {
        var d = BypassRules.Validate(BypassOptions.Default);
        Assert.Equal(BypassOptions.Default, d);
        Assert.True(d.Fragment);
        Assert.Equal(("tlshello", "100-200", "10-20"), (d.FragmentPackets, d.FragmentLength, d.FragmentInterval));
        Assert.True(d.Mux);
        Assert.Equal((8, 16), (d.MuxConcurrency, d.XudpConcurrency));
        Assert.True(d.Noises);
        Assert.Equal(("rand", "50-100", "10-16"), (d.NoiseType, d.NoisePacket, d.NoiseDelay));
        Assert.Null(d.Fingerprint);
        Assert.Null(d.XhttpMode);
        Assert.Null(d.Xmux);
    }

    [Fact]
    public void ValidateNormalizes()
    {
        var v = BypassRules.Validate(new BypassOptions
        {
            Fingerprint = " Firefox ",
            XhttpMode = "",
            FragmentPackets = "TLSHello",
            FragmentLength = " 50 - 80 ",
            FragmentInterval = "5-5",
            NoiseType = "hex",
            NoisePacket = "0xDE AD be ef",
            Xmux = new XmuxOptions { MaxConnections = " 4 ", HMaxRequestTimes = "" },
        });
        Assert.Equal("firefox", v.Fingerprint);
        Assert.Null(v.XhttpMode);
        Assert.Equal(("tlshello", "50-80", "5"), (v.FragmentPackets, v.FragmentLength, v.FragmentInterval));
        Assert.Equal("deadbeef", v.NoisePacket);
        Assert.Equal("4", v.Xmux!.MaxConnections);
        Assert.Null(v.Xmux.HMaxRequestTimes);
    }

    public static TheoryData<BypassOptions, string> Invalid => new()
    {
        { new BypassOptions { Fingerprint = "netscape" }, "Отпечаток" },
        { new BypassOptions { XhttpMode = "grpc" }, "XHTTP" },
        { new BypassOptions { FragmentPackets = "all" }, "пакеты" },
        { new BypassOptions { FragmentLength = "0-10" }, "длина" },
        { new BypassOptions { FragmentLength = "200-100" }, "длина" },
        { new BypassOptions { FragmentInterval = "10-5000" }, "пауза" },
        { new BypassOptions { MuxConcurrency = 0 }, "Mux" },
        { new BypassOptions { XudpConcurrency = 5000 }, "xudp" },
        { new BypassOptions { NoiseType = "exp" }, "тип" },
        { new BypassOptions { NoisePacket = "9000" }, "случайного" },
        { new BypassOptions { NoiseType = "hex", NoisePacket = "abc" }, "hex" },
        { new BypassOptions { NoiseType = "base64", NoisePacket = "not base64!" }, "base64" },
        { new BypassOptions { NoiseType = "str", NoisePacket = "" }, "текст" },
        { new BypassOptions { HopIntervalSeconds = 2 }, "интервал" },
        { new BypassOptions { Xmux = new XmuxOptions { MaxConcurrency = "16", MaxConnections = "3" } }, "вместе" },
        { new BypassOptions { Xmux = new XmuxOptions { HMaxReusableSecs = "-5" } }, "hMaxReusableSecs" },
        { new BypassOptions { Xmux = new XmuxOptions { HKeepAlivePeriod = -1 } }, "keep-alive" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void ValidateRejects(BypassOptions options, string expectedInMessage)
    {
        var e = Assert.Throws<ConfigException>(() => BypassRules.Validate(options));
        Assert.Contains(expectedInMessage, e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintAndXhttpModeOverrideLink()
    {
        var o = Proxy(Xhttp, new BypassOptions { Fingerprint = "safari", XhttpMode = "packet-up" });
        Assert.Equal("safari", (string?)o["streamSettings"]!["realitySettings"]!["fingerprint"]);
        Assert.Equal("packet-up", (string?)o["streamSettings"]!["xhttpSettings"]!["mode"]);

        var fromLink = Proxy(Xhttp, BypassOptions.Default);
        Assert.Equal("chrome", (string?)fromLink["streamSettings"]!["realitySettings"]!["fingerprint"]);
        Assert.Equal("auto", (string?)fromLink["streamSettings"]!["xhttpSettings"]!["mode"]);
        Assert.Null(fromLink["streamSettings"]!["xhttpSettings"]!["extra"]);
    }

    [Fact]
    public void FragmentUsesConfiguredValues()
    {
        var c = XrayConfigBuilder.Build(Vision, ConfigBuilderTests.TunOptions() with
        {
            Bypass = BypassRules.Validate(new BypassOptions { FragmentPackets = "1-3", FragmentLength = "3-5", FragmentInterval = "0" }),
        });
        var fragment = c["outbounds"]!.AsArray().Single(o => (string?)o!["tag"] == "fragment")!["settings"]!["fragment"]!;
        Assert.Equal(("1-3", "3-5", "0"), ((string?)fragment["packets"], (string?)fragment["length"], (string?)fragment["interval"]));
    }

    [Fact]
    public void MuxDependsOnTransport()
    {
        var vision = Proxy(Vision, BypassOptions.Default)["mux"]!;
        Assert.True((bool?)vision["enabled"]);
        Assert.Equal(-1, (int?)vision["concurrency"]);
        Assert.Equal(16, (int?)vision["xudpConcurrency"]);
        Assert.Equal("skip", (string?)vision["xudpProxyUDP443"]);

        var plain = Proxy(PlainReality, new BypassOptions { MuxConcurrency = 4, XudpConcurrency = 32 })["mux"]!;
        Assert.Equal((4, 32), ((int?)plain["concurrency"], (int?)plain["xudpConcurrency"]));

        Assert.Null(Proxy(Xhttp, BypassOptions.Default)["mux"]);
        Assert.Null(Proxy(Hy2, BypassOptions.Default)["mux"]);
        Assert.Null(Proxy(Vision, new BypassOptions { Mux = false })["mux"]);
    }

    [Fact]
    public void XmuxGoesIntoExtraAndKeepsLinkExtra()
    {
        var xmux = new XmuxOptions { MaxConcurrency = "16-32", HMaxReusableSecs = "600", HKeepAlivePeriod = 30 };
        var extra = Proxy(XhttpWithExtra, new BypassOptions { Xmux = xmux })["streamSettings"]!["xhttpSettings"]!["extra"]!;
        Assert.Equal("100-1000", (string?)extra["xPaddingBytes"]);
        Assert.Equal("16-32", (string?)extra["xmux"]!["maxConcurrency"]);
        Assert.Null(extra["xmux"]!["maxConnections"]);
        Assert.Equal("600", (string?)extra["xmux"]!["hMaxReusableSecs"]);
        Assert.Equal(30, (int?)extra["xmux"]!["hKeepAlivePeriod"]);

        // Без своих параметров xmux из ссылки остаётся как есть.
        var linkOnly = Proxy(XhttpWithExtra, BypassOptions.Default)["streamSettings"]!["xhttpSettings"]!["extra"]!;
        Assert.Equal("2", (string?)linkOnly["xmux"]!["maxConnections"]);

        var noLinkExtra = Proxy(Xhttp, new BypassOptions { Xmux = new XmuxOptions { MaxConnections = "1" } });
        Assert.Equal("1", (string?)noLinkExtra["streamSettings"]!["xhttpSettings"]!["extra"]!["xmux"]!["maxConnections"]);
    }

    [Fact]
    public void Hysteria2GetsNoiseAndPortHoppingMasks()
    {
        var masks = Proxy(Hy2Hop, BypassOptions.Default)["streamSettings"]!["finalmask"]!["udp"]!.AsArray();
        Assert.Equal(["noise", "udphop"], masks.Select(m => (string?)m!["type"]));
        var noise = masks[0]!["settings"]!["noise"]![0]!;
        Assert.Equal(("50-100", "10-16"), ((string?)noise["rand"], (string?)noise["delay"]));
        var hop = masks[1]!["settings"]!;
        Assert.Equal("443,20000-30000", (string?)hop["remotePorts"]);
        Assert.Equal("30", (string?)hop["interval"]);
        Assert.Equal("intervallocal,intervalremote", (string?)hop["mode"]);

        var str = Proxy(Hy2, new BypassOptions { NoiseType = "str", NoisePacket = "GET / HTTP/1.1" })["streamSettings"]!["finalmask"]!["udp"]![0]!["settings"]!["noise"]![0]!;
        Assert.Equal(("str", "GET / HTTP/1.1"), ((string?)str["type"], (string?)str["packet"]));
        Assert.Null(str["rand"]);

        Assert.Null(Proxy(Hy2, new BypassOptions { Noises = false })["streamSettings"]!["finalmask"]);
        // У VLESS шумов нет: они только для UDP-транспорта.
        Assert.Null(Proxy(Vision, BypassOptions.Default)["streamSettings"]!["finalmask"]);
    }

    [Fact]
    public void SalamanderIsInnermostMask()
    {
        var masks = Proxy(Hy2Obfs, BypassOptions.Default)["streamSettings"]!["finalmask"]!["udp"]!.AsArray();
        Assert.Equal(["salamander", "noise", "udphop"], masks.Select(m => (string?)m!["type"]));
        Assert.Equal("0bf5", (string?)masks[0]!["settings"]!["password"]);
        Assert.Equal("30000-39999", (string?)masks[2]!["settings"]!["remotePorts"]);
        // Без обфускации маски salamander нет.
        Assert.DoesNotContain(Proxy(Hy2Hop, BypassOptions.Default)["streamSettings"]!["finalmask"]!["udp"]!.AsArray(),
            m => (string?)m!["type"] == "salamander");
    }

    [Fact]
    public void BypassSurvivesIpcRoundTrip()
    {
        var options = new ConnectOptions
        {
            Bypass = new BypassOptions { Fingerprint = "ios", Mux = false, Xmux = new XmuxOptions { MaxConnections = "2" }, HopIntervalSeconds = 45 },
        };
        var json = JsonSerializer.Serialize(new IpcRequest { Command = IpcCommands.Connect, Options = options }, IpcJsonContext.Default.IpcRequest);
        var back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest)!.Options!.Bypass;
        Assert.Equal(options.Bypass, back);
    }

    [Fact]
    public void PartialJsonKeepsDefaults()
    {
        var partial = JsonSerializer.Deserialize("""{"mux":false,"xmux":{"maxConnections":"2"}}""", IpcJsonContext.Default.BypassOptions)!;
        var valid = BypassRules.Validate(partial);
        Assert.False(valid.Mux);
        Assert.Equal(BypassOptions.Default with { Mux = false, Xmux = new XmuxOptions { MaxConnections = "2" } }, valid);
    }

    [Fact]
    public void PartialConnectOptionsKeepKillSwitchOn()
    {
        var o = JsonSerializer.Deserialize("""{"command":"connect","options":{"mtu":1400,"splitTunnel":{"apps":null}}}""", IpcJsonContext.Default.IpcRequest)!.Options!;
        Assert.True(o.KillSwitch);
        Assert.True(o.BlockQuic);
        Assert.Equal(1400, o.Mtu);
        Assert.Empty(Routing.SplitTunnelRules.Validate(o.SplitTunnel!).Apps);
    }

    // Настоящий xray принимает все варианты (и отвергает неизвестный тип маски, то есть finalmask действительно читается).
    public static TheoryData<string, BypassOptions> CoreCases => new()
    {
        { "xhttp", new BypassOptions { Fingerprint = "firefox", XhttpMode = "stream-one", Xmux = new XmuxOptions { MaxConcurrency = "16-32", HKeepAlivePeriod = 30 } } },
        { "xhttp-extra", new BypassOptions { XhttpMode = "packet-up", Xmux = new XmuxOptions { MaxConnections = "4", CMaxReuseTimes = "64-128" } } },
        { "vision", new BypassOptions { Fingerprint = "randomized", FragmentPackets = "1-3", FragmentLength = "3-5", FragmentInterval = "1" } },
        { "plain", new BypassOptions { MuxConcurrency = 4, XudpConcurrency = 8 } },
        { "hy2", BypassOptions.Default },
        { "hy2-hop", new BypassOptions { NoiseType = "base64", NoisePacket = "AAECAwQ=", NoiseDelay = "0", HopIntervalSeconds = 10 } },
        { "hy2-hop", new BypassOptions { NoiseType = "hex", NoisePacket = "deadbeef" } },
        { "hy2-hop", new BypassOptions { NoiseType = "str", NoisePacket = "hello" } },
        { "hy2-obfs", BypassOptions.Default },
        { "hy2-obfs", new BypassOptions { Noises = false } },
    };

    [Theory]
    [MemberData(nameof(CoreCases))]
    public async Task XrayAcceptsBypassConfigs(string which, BypassOptions bypass)
    {
        var profile = which switch
        {
            "xhttp" => Xhttp,
            "xhttp-extra" => XhttpWithExtra,
            "vision" => Vision,
            "plain" => PlainReality,
            "hy2" => Hy2,
            "hy2-obfs" => Hy2Obfs,
            _ => Hy2Hop,
        };
        var cores = CorePaths.Resolve(allowDevFallback: true);
        var valid = BypassRules.Validate(bypass);
        foreach (var options in new[] { ConfigBuilderTests.TunOptions(), new XrayOptions { SocksPort = 10808, HttpPort = 10809 } })
        {
            var result = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(profile, options with { Bypass = valid }).ToJsonString());
            Assert.True(result.Ok, result.Output);
        }
    }

    [Fact]
    public async Task XrayRejectsUnknownMaskType()
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        var config = XrayConfigBuilder.Build(Hy2, ConfigBuilderTests.TunOptions());
        config["outbounds"]![0]!["streamSettings"]!["finalmask"]!["udp"]![0]!["type"] = "no-such-mask";
        var result = await CoreValidator.ValidateXrayAsync(cores, config.ToJsonString());
        Assert.False(result.Ok);
    }
}
