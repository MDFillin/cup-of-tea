using System.Net;
using System.Text;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class SubscriptionTests
{
    private static readonly string Body = string.Join("\n", LinkParserTests.XhttpLink, LinkParserTests.VisionLink, LinkParserTests.Hy2Link,
        "tuic://uuid:pass@203.0.113.10:443#t", "ssr://c2VydmVyOjQ0Mw==");

    private static string Rename(string link, string name) => link[..link.IndexOf('#')] + "#" + Uri.EscapeDataString(name);

    private static SubscriptionFetch Fetch(params string[] links) =>
        SubscriptionClient.Parse(string.Join("\n", links), _ => null);

    [Fact]
    public void ParsesUserInfo()
    {
        var (up, down, total, expire) = SubscriptionClient.ParseUserInfo("upload=1024; download=2048;total=10737418240; expire=1767225600");
        Assert.Equal((1024L, 2048L, 10737418240L), (up, down, total));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), expire);

        var none = SubscriptionClient.ParseUserInfo("upload=x; total=0; expire=0; junk");
        Assert.Equal((0L, 0L, 0L, (DateTimeOffset?)null), none);
        Assert.Equal((0L, 0L, 0L, (DateTimeOffset?)null), SubscriptionClient.ParseUserInfo(null));
    }

    [Theory]
    [InlineData("My VPN", "My VPN")]
    [InlineData("base64:0JzQvtC5IFZQTg==", "Мой VPN")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void DecodesTitle(string? header, string? expected) => Assert.Equal(expected, SubscriptionClient.DecodeTitle(header));

    [Fact]
    public void ParsesPlainAndBase64Bodies()
    {
        var headers = new Dictionary<string, string>
        {
            ["subscription-userinfo"] = "upload=1; download=2; total=3",
            ["profile-update-interval"] = "6",
            ["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Панель")),
        };
        foreach (var body in new[] { Body, Convert.ToBase64String(Encoding.UTF8.GetBytes(Body)) })
        {
            var f = SubscriptionClient.Parse(body, h => headers.GetValueOrDefault(h));
            Assert.Equal(3, f.Result.Profiles.Count);
            Assert.Equal(2, f.Unsupported);
            Assert.Equal(6, f.UpdateIntervalHours);
            Assert.Equal("Панель", f.Title);
            Assert.Equal(3, f.Total);
        }
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html>…</html>", "веб-страница")]
    [InlineData("tuic://uuid:pass@203.0.113.10:443#t", "другие протоколы")]
    [InlineData("", "не нашлось")]
    public void RejectsBodiesWithoutServers(string body, string message)
    {
        var e = Assert.Throws<SubscriptionException>(() => SubscriptionClient.Parse(body, _ => null));
        Assert.Contains(message, e.Message);
    }

    [Theory]
    [InlineData("ftp://panel.example/sub")]
    [InlineData("panel.example/sub")]
    [InlineData("https://user:pw@panel.example/sub")]
    public void RejectsBadUrls(string url) => Assert.Throws<SubscriptionException>(() => SubscriptionClient.ValidateUrl(url));

    [Fact]
    public void MergeKeepsIdsRemovesMissingAndLeavesManualServers()
    {
        var data = new ProfileData();
        var manual = LinkParser.Parse(Rename(LinkParserTests.Hy2Link, "manual"));
        data.Profiles.Add(manual);
        var sub = new Subscription { Name = "panel", Url = "https://panel.example/sub/abc123" };

        var first = data.Apply(sub, Fetch(Rename(LinkParserTests.XhttpLink, "a"), Rename(LinkParserTests.VisionLink, "b"),
            Rename(LinkParserTests.Hy2Link, "b")), DateTimeOffset.Now);
        Assert.Equal((3, 0, 0), (first.Added, first.Updated, first.Removed));
        Assert.Equal(["manual", "a", "b", "b (2)"], data.Profiles.Select(p => p.Name));
        var idA = data.Profiles.Single(p => p.Name == "a").Id;
        data.SelectedId = data.Profiles.Single(p => p.Name == "b (2)").Id;

        var second = data.Apply(first.Subscription, Fetch(Rename(LinkParserTests.Hy2Link, "c"), Rename(LinkParserTests.VisionLink, "a")), DateTimeOffset.Now);
        Assert.Equal((1, 1, 2), (second.Added, second.Updated, second.Removed));
        Assert.Equal(["manual", "c", "a"], data.Profiles.Select(p => p.Name));
        Assert.Equal(idA, data.Profiles.Single(p => p.Name == "a").Id);
        // Тот же сервер «a» теперь из другой ссылки (Vision вместо XHTTP).
        Assert.NotNull(Assert.IsType<VlessProfile>(data.Profiles.Single(p => p.Name == "a")).Flow);
        Assert.Null(data.SelectedId);
        Assert.Equal(manual.Id, data.Profiles[0].Id);
        Assert.Null(data.Profiles[0].SubscriptionId);
        Assert.Single(data.Subscriptions);

        data.RemoveSubscription(sub.Id);
        Assert.Equal(["manual"], data.Profiles.Select(p => p.Name));
        Assert.Empty(data.Subscriptions);
    }

    [Fact]
    public void DueAfterIntervalAndRetriesErrorsLessOften()
    {
        var now = DateTimeOffset.Now;
        var sub = new Subscription { Name = "p", Url = "https://p/s", UpdateIntervalHours = 12, LastAttempt = now.AddHours(-1) };
        Assert.False(sub.IsDue(now));
        Assert.True(sub.IsDue(now.AddHours(12)));
        Assert.True((sub with { LastError = "x" }).IsDue(now.AddMinutes(31)));
        Assert.False((sub with { AutoUpdate = false }).IsDue(now.AddDays(1)));
        Assert.True(new Subscription { Name = "n", Url = "https://p/s" }.IsDue(now));
    }

    private sealed class FakeHandler(HttpStatusCode code, string body, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            inspect?.Invoke(request);
            var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
            r.Headers.TryAddWithoutValidation("subscription-userinfo", "upload=10; download=20; total=100; expire=1767225600");
            r.Headers.TryAddWithoutValidation("profile-title", "Панель");
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task AddAndUpdateThroughHttp()
    {
        string? agent = null;
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, Convert.ToBase64String(Encoding.UTF8.GetBytes(Body)),
            r => agent = r.Headers.UserAgent.ToString()));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.10");
        var data = new ProfileData();

        var added = await SubscriptionService.AddAsync(data, http, "https://panel.example/sub/abc123", null);
        Assert.Equal("Панель", added.Subscription.Name);
        Assert.Equal(3, added.Added);
        Assert.Equal(100, added.Subscription.Total);
        // Панели отдают полные конфиги (с маршрутами и DNS) только по такому User-Agent.
        Assert.StartsWith(SubscriptionClient.FullConfigUserAgent, agent);
        await Assert.ThrowsAsync<SubscriptionException>(() => SubscriptionService.AddAsync(data, http, "https://panel.example/sub/abc123", null));

        using var broken = new HttpClient(new FakeHandler(HttpStatusCode.Forbidden, ""));
        var e = await Assert.ThrowsAsync<SubscriptionException>(() => SubscriptionService.UpdateAsync(data, broken, data.Subscriptions[0]));
        Assert.Contains("403", e.Message);
        Assert.Equal(3, data.Profiles.Count);
        Assert.Contains("403", data.Subscriptions[0].LastError);
        Assert.NotNull(data.Subscriptions[0].LastUpdated);
    }

    [Fact]
    public void StoreKeepsSubscriptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pvpn-test-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new ProfileStore(path);
            var data = new ProfileData();
            data.Apply(new Subscription { Name = "panel", Url = "https://panel.example/sub/abc123" }, Fetch(LinkParserTests.Hy2Link), DateTimeOffset.Now);
            store.Save(data);
            var back = store.Load();
            Assert.Equal("https://panel.example/sub/abc123", Assert.Single(back.Subscriptions).Url);
            Assert.Equal(back.Subscriptions[0].Id, Assert.Single(back.Profiles).SubscriptionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsPanelMetaFromHeadersAndBody()
    {
        var routing = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"Name":"RU","DirectSites":["geosite:category-ru"],"DirectIp":["geoip:ru"]}"""));
        var headers = new Dictionary<string, string>
        {
            ["announce"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Плановые работы в 3:00")),
            ["support-url"] = "https://t.me/support_bot",
            ["profile-description"] = "Лучший чай",
            ["sort-order"] = "ping",
            ["hide-url"] = "1",
            ["routing"] = "anyapp://routing/onadd/" + routing,
            ["new-url"] = "https://panel2.example/sub/abc",
            ["subscription-userinfo"] = "upload=1; download=2; total=3; expire=1767225600000",
        };
        var body = string.Join("\n", "#announce: из тела (заголовок главнее)", "#profile-web-page-url: https://tea.example", LinkParserTests.Hy2Link);
        var f = SubscriptionClient.Parse(body, h => headers.GetValueOrDefault(h));
        Assert.Equal("Плановые работы в 3:00", f.Meta.Announce);
        Assert.Equal("https://tea.example/", f.Meta.WebPageUrl);
        Assert.Equal("https://t.me/support_bot", f.Meta.SupportUrl);
        Assert.Equal(("Лучший чай", "ping", true), (f.Meta.Description, f.Meta.SortOrder, f.Meta.HideUrl));
        Assert.Equal(["geosite:category-ru"], f.Meta.Routing!.DirectSites);
        // Срок в миллисекундах.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), f.Expire);
        Assert.Single(f.Result.Profiles);

        var data = new ProfileData();
        var sub = data.Apply(new Subscription { Name = "p", Url = "https://panel.example/sub/abc" }, f, DateTimeOffset.Now).Subscription;
        Assert.Equal("https://panel2.example/sub/abc", sub.Url);
        Assert.Equal("Плановые работы в 3:00", sub.Announce);
        Assert.NotNull(sub.Routing);
    }

    [Fact]
    public void RoutingLineInBase64BodyIsMetaNotServer()
    {
        var routing = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"BlockSites":["geosite:category-ads-all"]}"""));
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", "anyapp://routing/onadd/" + routing, LinkParserTests.XhttpLink)));
        var f = SubscriptionClient.Parse(body, _ => null);
        Assert.Equal(["geosite:category-ads-all"], f.Meta.Routing!.BlockSites);
        Assert.Single(f.Result.Profiles);
        Assert.Empty(f.Result.Errors);
    }

    [Fact]
    public void NewUrlMustBeHttpsAndNewDomainKeepsPath()
    {
        Assert.Null(SubscriptionClient.Parse(LinkParserTests.Hy2Link, h => h == "new-url" ? "http://evil.example/x" : null).Meta.NewUrl);
        var f = SubscriptionClient.Parse(LinkParserTests.Hy2Link, h => h == "new-domain" ? "panel3.example" : null);
        var data = new ProfileData();
        var sub = data.Apply(new Subscription { Name = "p", Url = "https://panel.example:2096/sub/abc?x=1" }, f, DateTimeOffset.Now).Subscription;
        Assert.Equal("https://panel3.example:2096/sub/abc?x=1", sub.Url);
    }

    [Fact]
    public void SendsStableHwid()
    {
        Assert.Matches("^[0-9a-f]{32}$", DeviceInfo.Hwid);
        Assert.Equal(DeviceInfo.Hwid, DeviceInfo.Hwid);
        using var http = SubscriptionClient.CreateClient();
        Assert.True(http.DefaultRequestHeaders.TryGetValues("x-hwid", out var v) && v.Single() == DeviceInfo.Hwid);
        using var quiet = SubscriptionClient.CreateClient(sendHwid: false);
        Assert.False(quiet.DefaultRequestHeaders.Contains("x-hwid"));
    }
}
