using System.Text;
using System.Text.Json.Nodes;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

// Trojan, VMess, Shadowsocks и транспорты WS/gRPC/HTTPUpgrade. Ключи и пароли — фиктивные.
public class MoreProtocolTests
{
    private const string Uuid = LinkParserTests.Uuid;
    private const string Pbk = LinkParserTests.Pbk;

    internal const string TrojanWsTls = "trojan://tr0jan-pass@203.0.113.20:443?security=tls&sni=www.example.com&type=ws&path=%2Fws&host=cdn.example.com&fp=chrome#trojan-ws";
    internal const string TrojanXhttpReality = $"trojan://tr0jan-pass@203.0.113.20:443?type=xhttp&security=reality&sni=www.example.com&fp=firefox&pbk={Pbk}&sid=0123abcd&spx=%2F&host=www.example.com&path=%2Fframes&mode=packet-up#trojan-xhttp";
    internal const string VlessGrpcTls = $"vless://{Uuid}@203.0.113.21:443?type=grpc&security=tls&encryption=none&sni=grpc.example.com&fp=firefox&serviceName=notes.v1.Sync&mode=gun#vless-grpc";
    internal const string VlessHttpUpgrade = $"vless://{Uuid}@203.0.113.21:8080?type=httpupgrade&security=none&path=%2Fup&host=up.example.com#vless-hu";
    internal const string SsSip002 = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpzM2NyZXQ@203.0.113.22:8388#ss-sip002";
    internal const string Ss2022 = "ss://2022-blake3-aes-128-gcm:AAECAwQFBgcICQoLDA0ODw%3D%3D@203.0.113.22:8389#ss-2022";

    private static string VmessJson(string net, string tls, string extra = "") =>
        "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"v":"2","ps":"vmess-{{net}}","add":"203.0.113.23","port":"443","id":"{{Uuid}}","aid":"0","scy":"auto","net":"{{net}}","type":"none","host":"v.example.com","path":"/vm","tls":"{{tls}}","sni":"v.example.com"{{extra}}}"""));

    [Fact]
    public void ParsesTrojan()
    {
        var t = Assert.IsType<TrojanProfile>(LinkParser.Parse(TrojanWsTls));
        Assert.Equal(("trojan-ws", "203.0.113.20", 443, "tr0jan-pass"), (t.Name, t.Host, t.Port, t.Password));
        Assert.Equal(("ws", "tls", "/ws", "cdn.example.com", "www.example.com"), (t.Network, t.Security, t.Path, t.HostHeader, t.Sni));
        Assert.Equal("Trojan WS TLS", t.ProtocolLabel);
        Assert.Contains("tr0jan-pass", t.Secrets);

        var r = Assert.IsType<TrojanProfile>(LinkParser.Parse(TrojanXhttpReality));
        Assert.Equal(("xhttp", "reality", "/frames", "packet-up"), (r.Network, r.Security, r.XhttpPath, r.XhttpMode));
        Assert.Equal("Trojan XHTTP REALITY", r.ProtocolLabel);

        // Без security у trojan по умолчанию TLS.
        Assert.Equal("tls", ((TrojanProfile)LinkParser.Parse("trojan://p@203.0.113.20:443#x")).Security);
    }

    [Fact]
    public void ParsesVmessJsonAndPlainLinks()
    {
        var ws = Assert.IsType<VmessProfile>(LinkParser.Parse(VmessJson("ws", "tls")));
        Assert.Equal(("vmess-ws", "203.0.113.23", 443, Uuid, "auto"), (ws.Name, ws.Host, ws.Port, ws.Uuid, ws.Cipher));
        Assert.Equal(("ws", "tls", "/vm", "v.example.com"), (ws.Network, ws.Security, ws.Path, ws.HostHeader));
        Assert.Equal("VMess WS TLS", ws.ProtocolLabel);

        var grpc = Assert.IsType<VmessProfile>(LinkParser.Parse(VmessJson("grpc", "")));
        Assert.Equal(("grpc", "none", "/vm"), (grpc.Network, grpc.Security, grpc.ServiceName));

        var plain = Assert.IsType<VmessProfile>(LinkParser.Parse($"vmess://{Uuid}@203.0.113.23:8443?type=tcp&security=tls&sni=v.example.com&encryption=aes-128-gcm#plain"));
        Assert.Equal(("plain", "aes-128-gcm", "tls", "raw"), (plain.Name, plain.Cipher, plain.Security, plain.Network));

        Assert.Throws<LinkParseException>(() => LinkParser.Parse(VmessJson("kcp", "")));
    }

    [Fact]
    public void ParsesShadowsocksForms()
    {
        var sip = Assert.IsType<ShadowsocksProfile>(LinkParser.Parse(SsSip002));
        Assert.Equal(("ss-sip002", "chacha20-ietf-poly1305", "s3cret", 8388), (sip.Name, sip.Method, sip.Password, sip.Port));

        var ss2022 = Assert.IsType<ShadowsocksProfile>(LinkParser.Parse(Ss2022));
        Assert.Equal(("2022-blake3-aes-128-gcm", "AAECAwQFBgcICQoLDA0ODw=="), (ss2022.Method, ss2022.Password));

        // Старый вид: всё в base64.
        var legacy = "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:p4ss@203.0.113.22:8390")) + "#legacy";
        var old = Assert.IsType<ShadowsocksProfile>(LinkParser.Parse(legacy));
        Assert.Equal(("legacy", "aes-256-gcm", "p4ss", "203.0.113.22", 8390), (old.Name, old.Method, old.Password, old.Host, old.Port));

        Assert.Contains("Плагин", Assert.Throws<LinkParseException>(() => LinkParser.Parse(SsSip002.Replace("#", "/?plugin=obfs-local%3Bobfs%3Dhttp#"))).Message);
        Assert.Contains("Метод", Assert.Throws<LinkParseException>(() => LinkParser.Parse("ss://rc4-md5:p@203.0.113.22:1#x")).Message);
    }

    [Fact]
    public void InsecureTlsNeedsCertificatePin()
    {
        Assert.Contains("pcs", Assert.Throws<LinkParseException>(() => LinkParser.Parse("trojan://p@203.0.113.20:443?security=tls&allowInsecure=1#x")).Message);
        var pinned = Assert.IsType<TrojanProfile>(LinkParser.Parse("trojan://p@203.0.113.20:443?security=tls&allowInsecure=1&pcs=AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99&vcn=real.example.com#x"));
        var tls = XrayConfigBuilder.Build(pinned, ConfigBuilderTests.TunOptions())["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!;
        Assert.Equal("AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99", (string?)tls["pinnedPeerCertSha256"]);
        Assert.Equal("real.example.com", (string?)tls["verifyPeerCertByName"]);
        Assert.Null(tls["allowInsecure"]);
    }

    [Fact]
    public void ParsesVlessTransports()
    {
        var grpc = Assert.IsType<VlessProfile>(LinkParser.Parse(VlessGrpcTls));
        Assert.Equal(("grpc", "tls", "notes.v1.Sync", false), (grpc.Network, grpc.Security, grpc.ServiceName, grpc.GrpcMulti));
        Assert.Equal("VLESS gRPC TLS", grpc.ProtocolLabel);
        var hu = Assert.IsType<VlessProfile>(LinkParser.Parse(VlessHttpUpgrade));
        Assert.Equal(("httpupgrade", "/up", "up.example.com"), (hu.Network, hu.Path, hu.HostHeader));
    }

    [Fact]
    public void LinksAreFoundInTextButSsInsideVlessIsNot()
    {
        var text = string.Join("\n", LinkParserTests.VisionLink, TrojanWsTls, SsSip002, VmessJson("ws", "tls"));
        var result = LinkParser.ParseMany(text);
        Assert.Equal(["my-vision", "trojan-ws", "ss-sip002", "vmess-ws"], result.Profiles.Select(p => p.Name));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void BuildsStreamSettingsPerTransport()
    {
        JsonObject Out(string link) => (JsonObject)XrayConfigBuilder.Build(LinkParser.Parse(link), ConfigBuilderTests.TunOptions())["outbounds"]![0]!;

        var trojan = Out(TrojanWsTls);
        Assert.Equal("trojan", (string?)trojan["protocol"]);
        Assert.Equal("tr0jan-pass", (string?)trojan["settings"]!["servers"]![0]!["password"]);
        Assert.Equal("/ws", (string?)trojan["streamSettings"]!["wsSettings"]!["path"]);
        Assert.Equal("www.example.com", (string?)trojan["streamSettings"]!["tlsSettings"]!["serverName"]);
        // Фрагментация ClientHello — и для Trojan поверх TLS.
        Assert.Equal(XrayConfigBuilder.FragmentTag, (string?)trojan["streamSettings"]!["sockopt"]!["dialerProxy"]);

        var vmess = Out(VmessJson("ws", "tls"));
        Assert.Equal("auto", (string?)vmess["settings"]!["vnext"]![0]!["users"]![0]!["security"]);

        var grpc = Out(VlessGrpcTls);
        Assert.Equal("notes.v1.Sync", (string?)grpc["streamSettings"]!["grpcSettings"]!["serviceName"]);
        Assert.Null(grpc["mux"]);

        var ss = Out(SsSip002);
        Assert.Equal("shadowsocks", (string?)ss["protocol"]);
        Assert.Equal("chacha20-ietf-poly1305", (string?)ss["settings"]!["servers"]![0]!["method"]);
    }

    [Theory]
    [InlineData(TrojanWsTls)]
    [InlineData(TrojanXhttpReality)]
    [InlineData(VlessGrpcTls)]
    [InlineData(VlessHttpUpgrade)]
    [InlineData(SsSip002)]
    [InlineData(Ss2022)]
    [InlineData("vmess-ws")]
    [InlineData("vmess-grpc")]
    [InlineData("trojan://p@203.0.113.20:443?security=tls&sni=self.example.com&allowInsecure=1&pcs=AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99#pinned")]
    public async Task XrayAcceptsConfigs(string link)
    {
        var profile = LinkParser.Parse(link switch
        {
            "vmess-ws" => VmessJson("ws", "tls"),
            "vmess-grpc" => VmessJson("grpc", "tls"),
            _ => link,
        });
        var cores = CorePaths.Resolve(allowDevFallback: true);
        foreach (var options in new[] { ConfigBuilderTests.TunOptions(), new XrayOptions { SocksPort = 10808, HttpPort = 10809 } })
        {
            var r = await CoreValidator.ValidateXrayAsync(cores, XrayConfigBuilder.Build(profile, options).ToJsonString());
            Assert.True(r.Ok, r.Output);
        }
    }
}
