using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PVpn.Core.Cores;

namespace PVpn.Core.Tests;

public class AppUpdateTests
{
    private const string Repo = "example/cup-of-tea";

    private static string Release(string version, string? digest, string? url = null, bool prerelease = false, string? name = null) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        prerelease,
        html_url = $"https://github.com/{Repo}/releases/tag/v{version}",
        body = "Что нового",
        assets = new object[]
        {
            new { name = $"CupOfTea-Portable-{version}.zip", browser_download_url = "https://github.com/x/p.zip", size = 10, digest = "sha256:" + new string('0', 64) },
            new
            {
                name = name ?? $"CupOfTea-Setup-{version}.exe",
                browser_download_url = url ?? $"https://github.com/{Repo}/releases/download/v{version}/CupOfTea-Setup-{version}.exe",
                size = 4,
                digest,
            },
        },
    });

    [Fact]
    public void PicksSetupWithDigest()
    {
        var sha = new string('a', 64);
        using var doc = JsonDocument.Parse(Release("0.6.0", "sha256:" + sha));
        var r = AppUpdater.ParseRelease(doc.RootElement, Repo);
        Assert.Equal((new Version(0, 6, 0), sha), (r.Version, r.Sha256));
        Assert.EndsWith("/CupOfTea-Setup-0.6.0.exe", r.Setup.AbsoluteUri);
        Assert.Equal("Что нового", r.Notes);
    }

    [Theory]
    [InlineData("0.6.0", null, null, false, null)]
    [InlineData("0.6.0", "ok", "https://evil.example/CupOfTea-Setup-0.6.0.exe", false, null)]
    [InlineData("0.6.0", "ok", null, true, null)]
    [InlineData("0.6.0", "ok", null, false, "CupOfTea-Setup-0.5.9.exe")]
    [InlineData("latest", "ok", null, false, null)]
    public void RejectsBadReleases(string version, string? digest, string? url, bool prerelease, string? name)
    {
        using var doc = JsonDocument.Parse(Release(version, digest == "ok" ? "sha256:" + new string('a', 64) : digest, url, prerelease, name));
        Assert.Throws<AppUpdateException>(() => AppUpdater.ParseRelease(doc.RootElement, Repo));
    }

    private sealed class FakeGitHub(string releaseJson, byte[] setup) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup), RequestMessage = request });
    }

    [Fact]
    public async Task ChecksVersionAndVerifiesDownload()
    {
        var setup = "MZ!!"u8.ToArray();
        var good = Convert.ToHexStringLower(SHA256.HashData(setup));
        using var http = new HttpClient(new FakeGitHub(Release("0.6.0", "sha256:" + good), setup));
        Assert.Null(await AppUpdater.CheckAsync(http, Repo, new Version(0, 6, 0)));
        var release = await AppUpdater.CheckAsync(http, Repo, new Version(0, 5, 0));
        Assert.NotNull(release);

        var dir = Path.Combine(Path.GetTempPath(), $"cup-upd-{Guid.NewGuid():N}");
        try
        {
            var path = await AppUpdater.DownloadAsync(http, release, dir);
            Assert.Equal(setup, await File.ReadAllBytesAsync(path));

            // Подменённый файл: SHA256 не сходится — установщика на диске не остаётся.
            await Assert.ThrowsAsync<AppUpdateException>(() => AppUpdater.DownloadAsync(http, release with { Sha256 = new string('b', 64) }, dir));
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task NotConfiguredWithoutRepository()
    {
        using var http = new HttpClient(new FakeGitHub("{}", []));
        await Assert.ThrowsAsync<AppUpdateException>(() => AppUpdater.CheckAsync(http, "", new Version(0, 5, 0)));
    }
}
