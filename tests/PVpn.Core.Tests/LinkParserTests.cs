using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

// Все ключи и пароли ниже — фиктивные.
public class LinkParserTests
{
    internal const string Uuid = "11111111-2222-4333-8444-555555555555";
    internal const string Pbk = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";
    internal const string Pin = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    internal const string XhttpLink =
        $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=reality&sni=www.example.nl&fp=chrome&pbk={Pbk}&sid=0123abcd&type=xhttp&path=%2Fdeadbeef&mode=auto#my-xhttp";

    internal const string VisionLink =
        $"vless://{Uuid}@203.0.113.10:8443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.nl&fp=chrome&pbk={Pbk}&sid=0123abcd&type=tcp&headerType=none#my-vision";

    internal const string Hy2Link = $"hy2://my:s3cret-pass@203.0.113.10:443?sni=www.example.nl&insecure=1&pinSHA256={Pin}#my-hy2";

    [Fact]
    public void ParsesXhttpReality()
    {
        var p = Assert.IsType<VlessProfile>(LinkParser.Parse(XhttpLink));
        Assert.Equal("my-xhttp", p.Name);
        Assert.Equal("203.0.113.10", p.Host);
        Assert.Equal(443, p.Port);
        Assert.Equal(Uuid, p.Uuid);
        Assert.Equal("xhttp", p.Network);
        Assert.Equal("reality", p.Security);
        Assert.Equal("www.example.nl", p.Sni);
        Assert.Equal(Pbk, p.PublicKey);
        Assert.Equal("0123abcd", p.ShortId);
        Assert.Equal("/deadbeef", p.XhttpPath);
        Assert.Equal("auto", p.XhttpMode);
        Assert.Null(p.Flow);
        Assert.Equal("VLESS XHTTP REALITY", p.ProtocolLabel);
        Assert.Equal(ServerTransport.Tcp, p.Transport);
    }

    [Fact]
    public void ParsesVisionReality()
    {
        var p = Assert.IsType<VlessProfile>(LinkParser.Parse(VisionLink));
        Assert.Equal(8443, p.Port);
        Assert.Equal("raw", p.Network);
        Assert.Equal("xtls-rprx-vision", p.Flow);
        Assert.Null(p.XhttpPath);
        Assert.Equal("VLESS Vision REALITY", p.ProtocolLabel);
    }

    [Fact]
    public void ParsesHysteria2WithPin()
    {
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse(Hy2Link));
        Assert.Equal("my:s3cret-pass", p.Auth);
        Assert.Equal("www.example.nl", p.Sni);
        Assert.Equal(Pin, p.PinSha256);
        Assert.True(p.Insecure);
        Assert.Equal(ServerTransport.Udp, p.Transport);
    }

    [Fact]
    public void AcceptsHysteria2SchemeAndPinWithoutColons()
    {
        var pin = Pin.Replace(":", "").ToLowerInvariant();
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse($"hysteria2://pass@203.0.113.10:443?pinSHA256={pin}#x"));
        Assert.Equal("pass", p.Auth);
        Assert.False(p.Insecure);
        Assert.Equal(pin, p.PinSha256);
    }

    [Fact]
    public void DecodesCyrillicNameAndIpv6Host()
    {
        var link = $"vless://{Uuid}@[2001:db8::1]:443?security=none#%D0%AF-%D0%B4%D0%BE%D0%BC";
        var p = LinkParser.Parse(link);
        Assert.Equal("Я-дом", p.Name);
        Assert.Equal("2001:db8::1", p.Host);
    }

    [Fact]
    public void DefaultsNameToHostAndPort()
    {
        var p = LinkParser.Parse($"vless://{Uuid}@203.0.113.10:443?security=none");
        Assert.Equal("203.0.113.10:443", p.Name);
    }

    [Theory]
    [InlineData($"vless://{Uuid}@203.0.113.10:443?type=kcp&security=none#x", "kcp")]
    [InlineData($"vless://{Uuid}@203.0.113.10:443?security=reality&sni=www.example.nl#x", "pbk")]
    [InlineData($"vless://{Uuid}@203.0.113.10:443?security=reality&pbk={Pbk}#x", "sni")]
    [InlineData($"vless://{Uuid}@203.0.113.10?security=none#x", "порт")]
    [InlineData("vless://203.0.113.10:443?security=none#x", "UUID")]
    [InlineData("hy2://pass@203.0.113.10:443?insecure=1#x", "pinSHA256")]
    [InlineData("hy2://pass@203.0.113.10:443?obfs=salamander#x", "Обфускация")]
    [InlineData("hy2://pass@203.0.113.10:443?mport=abc#x", "mport")]
    [InlineData("hy2://pass@203.0.113.10:abc#x", "порт")]
    [InlineData($"vless://{Uuid}@203.0.113.10:443,8443?security=none#x", "только у hy2")]
    [InlineData("hy2://pass@203.0.113.10:443?pinSHA256=1234#x", "SHA-256")]
    [InlineData("tuic://pass@203.0.113.10:443#x", "Неизвестный")]
    public void RejectsUnsupportedLinks(string link, string expectedInMessage)
    {
        var e = Assert.Throws<LinkParseException>(() => LinkParser.Parse(link));
        Assert.Contains(expectedInMessage, e.Message);
    }

    [Fact]
    public void ParseManyFindsLinksInLabelledTextAndReportsErrors()
    {
        var text = $"""
            Мои ключи:
            my-xhttp: {XhttpLink}
            {VisionLink}   {Hy2Link}

            сломанная: vless://broken
            """;
        var result = LinkParser.ParseMany(text);
        Assert.Equal(["my-xhttp", "my-vision", "my-hy2"], result.Profiles.Select(p => p.Name));
        var error = Assert.Single(result.Errors);
        Assert.Equal(5, error.Line);
    }

    [Fact]
    public void ParseManyDecodesBase64Subscription()
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(XhttpLink + "\n" + Hy2Link));
        var result = LinkParser.ParseMany(base64);
        Assert.Equal(2, result.Profiles.Count);
    }

    [Theory]
    [InlineData("hy2://pass@203.0.113.10:20000-30000?sni=www.example.nl#x", 20000, "20000-30000")]
    [InlineData("hy2://pass@203.0.113.10:443,20000-30000#x", 443, "443,20000-30000")]
    [InlineData("hy2://pass@203.0.113.10:443?mport=20000-30000#x", 443, "20000-30000")]
    [InlineData("hy2://pass@203.0.113.10:443?mport=20000:30000#x", 443, "20000-30000")]
    [InlineData("hysteria2://pass@203.0.113.10:5000-5010#x", 5000, "5000-5010")]
    [InlineData("hy2://pass@203.0.113.10:443?mport=443#x", 443, null)]
    public void ParsesHysteria2PortHopping(string link, int port, string? hop)
    {
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse(link));
        Assert.Equal(port, p.Port);
        Assert.Equal(hop, p.HopPorts);
        Assert.Equal(hop == null ? "Hysteria2" : "Hysteria2 (смена портов)", p.ProtocolLabel);
    }

    [Fact]
    public void ParsesHysteria2Salamander()
    {
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse(
            $"hy2://pass@203.0.113.10:8443?sni=www.example.nl&pinSHA256={Pin}&obfs=salamander&obfs-password=0bf5-s3cret&mport=30000-39999#obfs"));
        Assert.Equal("0bf5-s3cret", p.ObfsPassword);
        Assert.Contains("0bf5-s3cret", p.Secrets);
        Assert.Equal("Hysteria2 (salamander, смена портов)", p.ProtocolLabel);

        var e = Assert.Throws<LinkParseException>(() => LinkParser.Parse("hy2://pass@203.0.113.10:443?obfs=salamander#x"));
        Assert.Contains("obfs-password", e.Message);
    }

    [Fact]
    public void RejectsInvalidExtraJson()
    {
        var link = XhttpLink.Replace("#my-xhttp", "&extra=%7Bbroken#my-xhttp");
        Assert.Throws<LinkParseException>(() => LinkParser.Parse(link));
    }
}
