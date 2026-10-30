using System.Net;
using System.Text;
using PVpn.Core.Profiles;

namespace PVpn.Core.Tests;

public class SubscriptionTests
{
    private static readonly string Body = string.Join("\n", LinkParserTests.XhttpLink, LinkParserTests.VisionLink, LinkParserTests.Hy2Link,
        "vmess://eyJhZGQiOiIyMDMuMC4xMTMuMTAifQ==", "trojan://pass@203.0.113.10:443#t");

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
    [InlineData("vmess://eyJhZGQiOiIyMDMuMC4xMTMuMTAifQ==", "другие протоколы")]
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
        Assert.StartsWith("v2rayN", agent);
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
}
