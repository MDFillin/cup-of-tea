using System.Net;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class MiscTests
{
    [Fact]
    public void SanitizerMasksSecretsAndUuids()
    {
        var s = new LogSanitizer(["s3cret-pass"]);
        var line = s.Clean($"auth my:s3cret-pass user {LinkParserTests.Uuid} ok");
        Assert.DoesNotContain("s3cret-pass", line);
        Assert.DoesNotContain(LinkParserTests.Uuid, line);
    }

    [Fact]
    public void UpsertReplacesByNameAndKeepsId()
    {
        var data = new ProfileData();
        var first = LinkParser.Parse(LinkParserTests.XhttpLink);
        Assert.True(data.Upsert(first));
        var updated = LinkParser.Parse(LinkParserTests.XhttpLink.Replace(":443?", ":2053?"));
        Assert.False(data.Upsert(updated));
        var stored = Assert.Single(data.Profiles);
        Assert.Equal(first.Id, stored.Id);
        Assert.Equal(2053, stored.Port);
    }

    [Fact]
    public void UpsertMatchesNameWithoutFlagOnlyWhenUnique()
    {
        var data = new ProfileData();
        var plain = LinkParser.Parse(LinkParserTests.Hy2Link) with { Name = "my-hy2" };
        data.Profiles.Add(plain);
        Assert.False(data.Upsert(LinkParser.Parse(LinkParserTests.Hy2Link) with { Name = "🇳🇱 my-hy2" }));
        Assert.Equal("🇳🇱 my-hy2", Assert.Single(data.Profiles).Name);
        Assert.Equal(plain.Id, data.Profiles[0].Id);

        data.Profiles.Add(LinkParser.Parse(LinkParserTests.Hy2Link) with { Name = "🇩🇪 my-hy2" });
        Assert.True(data.Upsert(LinkParser.Parse(LinkParserTests.Hy2Link) with { Name = "my-hy2" }));
        Assert.Equal(3, data.Profiles.Count);
    }

    [Fact]
    public void ProfileStoreRoundTripsEncrypted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pvpn-test-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new ProfileStore(path);
            var data = new ProfileData();
            data.Upsert(LinkParser.Parse(LinkParserTests.XhttpLink));
            data.Upsert(LinkParser.Parse(LinkParserTests.Hy2Link));
            store.Save(data);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("s3cret", raw);
            Assert.DoesNotContain(LinkParserTests.Uuid, raw);

            var loaded = store.Load();
            Assert.Equal(["my-xhttp", "my-hy2"], loaded.Profiles.Select(p => p.Name));
            Assert.IsType<Hysteria2Profile>(loaded.Profiles[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("26.12.34.56", 8, "26.0.0.0")]
    [InlineData("192.168.56.1", 24, "192.168.56.0")]
    [InlineData("10.1.2.3", 30, "10.1.2.0")]
    public void ComputesNetworkAddress(string address, int prefix, string expected) =>
        Assert.Equal(expected, NetworkInfo.NetworkAddress(IPAddress.Parse(address), prefix).ToString());

    [Theory]
    [InlineData("26.0.0.0", false)]
    [InlineData("192.168.1.0", true)]
    [InlineData("172.20.0.0", true)]
    [InlineData("172.32.0.0", false)]
    [InlineData("100.100.0.0", true)]
    public void ClassifiesPrivateNetworks(string address, bool expected) =>
        Assert.Equal(expected, NetworkInfo.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0", 8)]
    [InlineData("255.255.255.255/32", "255.255.255.255", 32)]
    [InlineData("fe80::/10", "fe80::", 10)]
    [InlineData("26.0.0.0", "26.0.0.0", 32)]
    public void ParsesCidr(string cidr, string address, int prefix)
    {
        var parsed = NetworkInfo.ParseCidr(cidr);
        Assert.Equal(IPAddress.Parse(address), parsed.Address);
        Assert.Equal(prefix, parsed.PrefixLength);
    }

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("fe80::/129")]
    [InlineData("10.0.0.0/x")]
    public void RejectsBadCidr(string cidr) =>
        Assert.ThrowsAny<FormatException>(() => NetworkInfo.ParseCidr(cidr));

    [Fact]
    public void KillSwitchLanListParses() =>
        Assert.All(KillSwitch.DefaultLanCidrs, c => NetworkInfo.ParseCidr(c));

    [Fact]
    public void KillSwitchServerAddressesAreSingleIpv4Hosts()
    {
        var cidrs = KillSwitch.ServerCidrs(
        [
            IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.20"),
            IPAddress.None, IPAddress.Any, IPAddress.Loopback, IPAddress.Parse("2001:db8::1"),
        ]);
        Assert.Equal(["203.0.113.10/32", "203.0.113.20/32"], cidrs);
        Assert.All(cidrs, c => NetworkInfo.ParseCidr(c));
        // Пустой список — фильтра нет вовсе (фильтр WFP без условий пропускал бы всё).
        Assert.Empty(KillSwitch.ServerCidrs([IPAddress.None]));
    }

    [Fact]
    public void AntivirusAdviceSkipsDisabledAndBuiltIn()
    {
        // Значения productState из Центра безопасности: 0x061000/0x041100 — включён, 0x060100/0x040000 — выключен.
        Assert.True(AntivirusAdvice.IsEnabled(0x061000));
        Assert.True(AntivirusAdvice.IsEnabled(0x041100));
        Assert.False(AntivirusAdvice.IsEnabled(0x060100));
        Assert.False(AntivirusAdvice.IsEnabled(0x040000));
        Assert.True(AntivirusAdvice.IsBuiltIn("Windows Defender"));
        Assert.True(AntivirusAdvice.IsBuiltIn("Microsoft Defender Antivirus"));
        Assert.False(AntivirusAdvice.IsBuiltIn("Kaspersky Standard"));

        var kaspersky = AntivirusAdvice.For(["Kaspersky Standard"], @"C:\Program Files\The Cup of Tea\cores");
        Assert.Contains(@"C:\Program Files\The Cup of Tea\cores", kaspersky);
        Assert.Contains("Не проверять весь трафик", kaspersky);
        Assert.DoesNotContain("\n", AntivirusAdvice.For(["Some Antivirus"], "cores"));
    }

    [Fact]
    public void ResumeRequestRoundTripsRemember()
    {
        var request = new IpcRequest { Command = IpcCommands.Connect, Link = "vless://x@h:443", Remember = true, Options = new ConnectOptions { KillSwitch = true } };
        var json = System.Text.Json.JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, IpcJsonContext.Default.IpcRequest)!;
        Assert.True(back.Remember);
        Assert.True(back.Options!.KillSwitch);
        // Старый интерфейс поле не передаёт — служба ничего не запоминает.
        Assert.False(System.Text.Json.JsonSerializer.Deserialize("""{"command":"connect","link":"vless://x@h:443"}""", IpcJsonContext.Default.IpcRequest)!.Remember);
    }
}
