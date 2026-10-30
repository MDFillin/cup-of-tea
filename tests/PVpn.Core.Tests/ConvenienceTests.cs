using System.IO.Compression;
using System.Text.Json;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class ConvenienceTests
{
    [Fact]
    public void MeterCountsSpeedAndSessionAcrossRestarts()
    {
        var m = new TrafficMeter();
        var t = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        m.Add(new TrafficSample(1000, 5000), t);
        Assert.Equal((0L, 0L), (m.SessionUp, m.SessionDown));
        m.Add(new TrafficSample(3000, 15000), t.AddSeconds(2));
        Assert.Equal(1000, m.UpPerSecond);
        Assert.Equal(5000, m.DownPerSecond);
        Assert.Equal((2000L, 10000L), (m.SessionUp, m.SessionDown));

        // xray перезапустился (переподключение): счётчики с нуля, сеанс продолжается.
        m.Add(new TrafficSample(100, 200), t.AddSeconds(3));
        Assert.Equal((2000L, 10000L), (m.SessionUp, m.SessionDown));
        m.Add(new TrafficSample(600, 1200), t.AddSeconds(4));
        Assert.Equal((2500L, 11000L), (m.SessionUp, m.SessionDown));
        Assert.Equal(1000, m.DownPerSecond);

        m.Reset();
        Assert.Equal((0L, 0L), (m.SessionUp, m.SessionDown));
    }

    [Theory]
    [InlineData(100, "800 бит/с")]
    [InlineData(12_500, "100 Кбит/с")]
    [InlineData(1_550_000, "12.4 Мбит/с")]
    public void FormatsSpeedInBits(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, TrafficMeter.FormatSpeed(bytesPerSecond).Replace(',', '.'));

    [Fact]
    public void ParsesXrayMetrics()
    {
        using var doc = JsonDocument.Parse("""{"cmdline":["x"],"stats":{"inbound":{},"outbound":{"proxy":{"downlink":3628,"uplink":679},"direct":{"downlink":1,"uplink":2}}}}""");
        Assert.Equal(new TrafficSample(679, 3628), TrafficSources.ParseXray(doc.RootElement, "proxy"));
        Assert.Equal(new TrafficSample(0, 0), TrafficSources.ParseXray(doc.RootElement, "nothing-yet"));
        using var none = JsonDocument.Parse("""{"cmdline":["x"]}""");
        Assert.Null(TrafficSources.ParseXray(none.RootElement, "proxy"));
    }

    [Fact]
    public async Task XrayAcceptsMetricsConfig()
    {
        var config = XrayConfigBuilder.Build(LinkParser.Parse(LinkParserTests.XhttpLink),
            new XrayOptions { SocksPort = 10808, HttpPort = 10809, MetricsPort = 10810 });
        Assert.Equal("127.0.0.1:10810", (string?)config["metrics"]!["listen"]);
        Assert.True((bool?)config["policy"]!["system"]!["statsOutboundDownlink"]);
        var result = await CoreValidator.ValidateXrayAsync(CorePaths.Resolve(allowDevFallback: true), config.ToJsonString());
        Assert.True(result.Ok, result.Output);

        var plain = XrayConfigBuilder.Build(LinkParser.Parse(LinkParserTests.XhttpLink), new XrayOptions { SocksPort = 10808 });
        Assert.Null(plain["metrics"]);
    }

    [Fact]
    public void DiagnosticsHaveNoSecrets()
    {
        var data = new ProfileData();
        var sub = new Subscription { Name = "panel", Url = "<subscription-url-removed>" };
        data.Apply(sub, SubscriptionClient.Parse(string.Join("\n", LinkParserTests.XhttpLink, LinkParserTests.Hy2Link), _ => null), DateTimeOffset.Now);

        var path = Path.Combine(Path.GetTempPath(), $"pvpn-diag-{Guid.NewGuid():N}.zip");
        try
        {
            DiagnosticsReport.Write(path, new Dictionary<string, string>
            {
                ["servers.txt"] = DiagnosticsReport.Servers(data),
                ["log.txt"] = $"ошибка: {LinkParserTests.Hy2Link}\nпароль s3cret-pass, uuid {LinkParserTests.Uuid}\nподписка {sub.Url}\nпуть /sub/t0kenSECRET",
            }, DiagnosticsReport.Secrets(data));

            using var zip = ZipFile.OpenRead(path);
            var all = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
            Assert.Contains("my-hy2", all);
            Assert.Contains("panel.example", all);
            Assert.Contains("<server-link-removed>", all);
            foreach (var secret in new[] { "s3cret-pass", LinkParserTests.Uuid, "t0kenSECRET", LinkParserTests.Pin })
                Assert.DoesNotContain(secret, all);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
