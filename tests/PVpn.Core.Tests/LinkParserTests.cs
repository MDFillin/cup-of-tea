using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

// Все ключи и пароли ниже — фиктивные.
public class LinkParserTests
{
    internal const string Uuid = "11111111-2222-4333-8444-555555555555";
    internal const string Pbk = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";
    internal const string Pin = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    internal const string XhttpLink =
        $"<server-link-removed>";

    internal const string VisionLink =
        $"<server-link-removed>";

    internal const string Hy2Link = $"<server-link-removed>";

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
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse($"<server-link-removed>"));
        Assert.Equal("pass", p.Auth);
        Assert.False(p.Insecure);
        Assert.Equal(pin, p.PinSha256);
    }

    [Fact]
    public void DecodesCyrillicNameAndIpv6Host()
    {
        var link = $"<server-link-removed>";
        var p = LinkParser.Parse(link);
        Assert.Equal("Я-дом", p.Name);
        Assert.Equal("2001:db8::1", p.Host);
    }

    [Fact]
    public void DefaultsNameToHostAndPort()
    {
        var p = LinkParser.Parse($"<server-link-removed>");
        Assert.Equal("203.0.113.10:443", p.Name);
    }

    [Theory]
    [InlineData($"<server-link-removed>", "ws")]
    [InlineData($"<server-link-removed>", "pbk")]
    [InlineData($"<server-link-removed>", "sni")]
    [InlineData($"<server-link-removed>", "порт")]
    [InlineData($"<server-link-removed>", "UUID")]
    [InlineData("<server-link-removed>", "pinSHA256")]
    [InlineData("<server-link-removed>", "Обфускация")]
    [InlineData("<server-link-removed>", "mport")]
    [InlineData("<server-link-removed>", "порт")]
    [InlineData($"<server-link-removed>", "только у hy2")]
    [InlineData("<server-link-removed>", "SHA-256")]
    [InlineData("<server-link-removed>", "Неизвестный")]
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

            сломанная: <server-link-removed>
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
    [InlineData("<server-link-removed>", 20000, "20000-30000")]
    [InlineData("<server-link-removed>", 443, "443,20000-30000")]
    [InlineData("<server-link-removed>", 443, "20000-30000")]
    [InlineData("<server-link-removed>", 443, "20000-30000")]
    [InlineData("<server-link-removed>", 5000, "5000-5010")]
    [InlineData("<server-link-removed>", 443, null)]
    public void ParsesHysteria2PortHopping(string link, int port, string? hop)
    {
        var p = Assert.IsType<Hysteria2Profile>(LinkParser.Parse(link));
        Assert.Equal(port, p.Port);
        Assert.Equal(hop, p.HopPorts);
        Assert.Equal(hop == null ? "Hysteria2" : "Hysteria2 (смена портов)", p.ProtocolLabel);
    }

    [Fact]
    public void RejectsInvalidExtraJson()
    {
        var link = XhttpLink.Replace("#my-xhttp", "&extra=%7Bbroken#my-xhttp");
        Assert.Throws<LinkParseException>(() => LinkParser.Parse(link));
    }
}
