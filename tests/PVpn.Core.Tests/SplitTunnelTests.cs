using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Ipc;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Tests;

public class SplitTunnelTests
{
    private static readonly SplitTunnelOptions Sample = new()
    {
        Mode = SplitTunnelMode.ExcludeSelected,
        Apps =
        [
            new SplitApp(@"C:\Users\fedor\AppData\Local\Discord", IsFolder: true),
            new SplitApp(@"C:\Program Files (x86)\Steam\steam.exe", IsFolder: false),
        ],
        Domains = ["gosuslugi.ru", "xn--p1ai"],
    };

    private static SingBoxOptions SingBox(SplitTunnelOptions? split, bool blockQuic = true) => new()
    {
        SocksPort = 23456,
        SocksUser = "u",
        SocksPassword = "p4ssw0rd",
        ServerAddresses = [IPAddress.Parse("203.0.113.10")],
        BlockQuic = blockQuic,
        SplitTunnel = split,
    };

    [Theory]
    [InlineData("https://www.Gosuslugi.ru/help?x=1", "gosuslugi.ru")]
    [InlineData("*.example.com", "example.com")]
    [InlineData(".example.com.", "example.com")]
    [InlineData("www.youtube.com", "youtube.com")]
    [InlineData("www.com", "www.com")]
    [InlineData("ru", "ru")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("discord.gg/abc", "discord.gg")]
    public void NormalizesDomains(string input, string expected) =>
        Assert.Equal(expected, SplitTunnelRules.NormalizeDomain(input));

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.com")]
    [InlineData("a..b")]
    public void RejectsBadDomains(string input) =>
        Assert.Throws<FormatException>(() => SplitTunnelRules.NormalizeDomain(input));

    [Theory]
    [InlineData(@"C:\Users\f\AppData\Local\Discord\app-1.0.9187\Discord.exe", @"C:\Users\f\AppData\Local\Discord")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files\Google\Chrome\Application")]
    [InlineData(@"D:\Games\Tool\1.2.3\v4.5\tool.exe", @"D:\Games\Tool")]
    [InlineData(@"C:\Telegram Desktop\Telegram.exe", @"C:\Telegram Desktop")]
    public void SuggestsFolderAboveVersionFolders(string exe, string expected) =>
        Assert.Equal(expected, SplitTunnelRules.SuggestFolder(exe));

    [Fact]
    public void PathRegexMatchesWindowsPathsCaseInsensitively()
    {
        // .NET и RE2 одинаково понимают используемое подмножество: (?i), ^, $, \\, \( и т. п.
        var folder = new Regex(SplitTunnelRules.PathRegex(Sample.Apps[0]));
        Assert.Matches(folder, @"c:\users\FEDOR\appdata\local\discord\app-1.0.9200\Discord.exe");
        Assert.DoesNotMatch(folder, @"C:\Users\fedor\AppData\Local\DiscordPTB\app-1\Discord.exe");

        var exe = new Regex(SplitTunnelRules.PathRegex(Sample.Apps[1]));
        Assert.Matches(exe, @"C:\Program Files (x86)\Steam\STEAM.EXE");
        Assert.DoesNotMatch(exe, @"C:\Program Files (x86)\Steam\steam.exe.bak");
        Assert.DoesNotMatch(exe, @"C:\Program Files (x86)\Steam\steamwebhelper.exe");

        Assert.DoesNotContain(@"\ ", SplitTunnelRules.PathRegex(Sample.Apps[1]));
    }

    [Fact]
    public void ValidateNormalizesAndRejectsRelativePaths()
    {
        var v = SplitTunnelRules.Validate(Sample with
        {
            Apps = [new SplitApp(@"C:\Apps\Discord\", true), new SplitApp(@"c:\apps\discord", true)],
            Domains = ["WWW.Example.com", "example.com"],
        });
        Assert.Single(v.Apps);
        Assert.Equal(@"C:\Apps\Discord", v.Apps[0].Path);
        Assert.Equal(["example.com"], v.Domains);

        Assert.Throws<ConfigException>(() => SplitTunnelRules.Validate(Sample with { Apps = [new SplitApp(@"Discord\app.exe", false)] }));
        Assert.Throws<ConfigException>(() => SplitTunnelRules.Validate(Sample with { Domains = ["bad domain"] }));
    }

    [Fact]
    public void ExcludeModeSendsSelectedDirectAndKeepsVpnFinal()
    {
        var c = SingBoxConfigBuilder.Build(SingBox(Sample));
        var rules = c["route"]!["rules"]!.AsArray();
        var apps = rules.Single(r => r!["process_path_regex"] != null)!;
        var sites = rules.Single(r => r!["domain_suffix"] != null)!;
        Assert.Equal("direct", (string?)apps["outbound"]);
        Assert.Equal("direct", (string?)sites["outbound"]);
        Assert.Null(apps["domain_suffix"]);
        Assert.Equal("proxy", (string?)c["route"]!["final"]);
        Assert.Equal("remote", (string?)c["dns"]!["final"]);
        var dnsRule = c["dns"]!["rules"]![0]!;
        Assert.Equal("direct-dns", (string?)dnsRule["server"]);
        // QUIC режется у всего, что идёт через VPN, после правил раздельного туннелирования.
        Assert.True(rules.IndexOf(rules.Single(IsQuicReject)) > rules.IndexOf(sites));
    }

    [Fact]
    public void OnlySelectedModeSendsSelectedToVpnAndRestDirect()
    {
        var c = SingBoxConfigBuilder.Build(SingBox(Sample with { Mode = SplitTunnelMode.OnlySelected }));
        var rules = c["route"]!["rules"]!.AsArray();
        Assert.Equal("direct", (string?)c["route"]!["final"]);
        // DNS — весь через туннель: ответ провайдера для выбранных программ мог бы быть подменён.
        Assert.Equal("remote", (string?)c["dns"]!["final"]);
        Assert.Empty(c["dns"]!["rules"]!.AsArray());
        Assert.Equal(2, rules.Count(r => (string?)r!["outbound"] == "proxy"));
        // QUIC режется только у выбранного (у прямого трафика он не мешает).
        var quic = rules.Where(IsQuicReject).ToList();
        Assert.Equal(2, quic.Count);
        Assert.All(quic, q => Assert.True(q!["process_path_regex"] != null || q["domain_suffix"] != null));
    }

    [Fact]
    public void EmptyExcludeListIsPlainVpnButEmptyOnlyListIsAllDirect()
    {
        var plain = SingBoxConfigBuilder.Build(SingBox(null)).ToJsonString();
        Assert.Equal(plain, SingBoxConfigBuilder.Build(SingBox(new SplitTunnelOptions { Mode = SplitTunnelMode.ExcludeSelected })).ToJsonString());

        var allDirect = SingBoxConfigBuilder.Build(SingBox(new SplitTunnelOptions { Mode = SplitTunnelMode.OnlySelected }));
        Assert.Equal("direct", (string?)allDirect["route"]!["final"]);
        Assert.Equal("remote", (string?)allDirect["dns"]!["final"]);
    }

    [Fact]
    public void DirectSitesResolveViaAdapterDns()
    {
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions { Mode = SplitTunnelMode.ExcludeSelected, Domains = ["gosuslugi.ru"] });
        var c = SingBoxConfigBuilder.Build(SingBox(split) with { DirectDns = [System.Net.IPAddress.Parse("192.168.1.1")] });
        var direct = c["dns"]!["servers"]!.AsArray().Single(s => (string?)s!["tag"] == "direct-dns")!;
        Assert.Equal("udp", (string?)direct["type"]);
        Assert.Equal("192.168.1.1", (string?)direct["server"]);
        Assert.Null(direct["detour"]);
    }

    [Fact]
    public void ProxyModeUsesOnlyDomains()
    {
        var hy2 = LinkParser.Parse(LinkParserTests.Hy2Link);
        var exclude = XrayConfigBuilder.Build(hy2, new XrayOptions { SocksPort = 10808, SplitTunnel = Sample });
        var rules = exclude["routing"]!["rules"]!.AsArray();
        Assert.Equal("direct", (string?)rules[2]!["outboundTag"]);
        Assert.Equal("domain:gosuslugi.ru", (string?)rules[2]!["domain"]![0]);
        Assert.DoesNotContain("Discord", exclude.ToJsonString());

        var only = XrayConfigBuilder.Build(hy2, new XrayOptions { SocksPort = 10808, SplitTunnel = Sample with { Mode = SplitTunnelMode.OnlySelected } });
        var onlyRules = only["routing"]!["rules"]!.AsArray();
        Assert.Equal("proxy", (string?)onlyRules[2]!["outboundTag"]);
        Assert.Equal("direct", (string?)onlyRules[3]!["outboundTag"]);
    }

    [Fact]
    public void IpcCarriesSplitOptions()
    {
        var request = new IpcRequest { Command = IpcCommands.Reconfigure, Options = new ConnectOptions { SplitTunnel = Sample } };
        var json = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);
        Assert.Contains("\"ExcludeSelected\"", json);
        var back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest)!;
        Assert.Equal(Sample.Apps, back.Options!.SplitTunnel!.Apps);
        Assert.Equal(Sample.Domains, back.Options.SplitTunnel.Domains);
    }

    // Настоящий sing-box проверяет конфиги с правилами по путям (RE2) и DNS-правилами обоих режимов.
    [Theory]
    [InlineData(SplitTunnelMode.ExcludeSelected)]
    [InlineData(SplitTunnelMode.OnlySelected)]
    public async Task SingBoxAcceptsSplitConfigs(SplitTunnelMode mode)
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        var config = SingBoxConfigBuilder.Build(SingBox(SplitTunnelRules.Validate(Sample with { Mode = mode })));
        var result = await CoreValidator.ValidateSingBoxAsync(cores, config.ToJsonString());
        Assert.True(result.Ok, result.Output);

        var hy2 = LinkParser.Parse(LinkParserTests.Hy2Link);
        var xray = await CoreValidator.ValidateXrayAsync(cores,
            XrayConfigBuilder.Build(hy2, new XrayOptions { SocksPort = 10808, HttpPort = 10809, SplitTunnel = Sample with { Mode = mode } }).ToJsonString());
        Assert.True(xray.Ok, xray.Output);
    }

    private static bool IsQuicReject(JsonNode? r) => (int?)r!["port"] == 443 && (string?)r["action"] == "reject";
}
