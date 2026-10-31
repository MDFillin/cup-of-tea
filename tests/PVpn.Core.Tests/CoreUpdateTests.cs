using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PVpn.Core.Cores;

namespace PVpn.Core.Tests;

public class CoreUpdateTests
{
    private const string Download = "https://github.com/XTLS/Xray-core/releases/download/v{0}/Xray-windows-64.zip";

    private static string Release(string version, string digest, string url, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        prerelease,
        assets = new object[]
        {
            new { name = "Xray-windows-arm64-v8a.zip", browser_download_url = "https://github.com/x/arm.zip", size = 10, digest = "sha256:" + new string('0', 64) },
            new { name = "Xray-windows-64.zip", browser_download_url = url, size = 1000, digest },
        },
    });

    [Theory]
    [InlineData(CoreKind.Xray, "Xray 26.10.21 (Xray, Penetrates Everything.) 1b1647c (go1.27.3 windows/amd64)\nA unified platform", "26.10.21")]
    [InlineData(CoreKind.SingBox, "sing-box version 1.14.4\n\nEnvironment: go1.25", "1.14.4")]
    [InlineData(CoreKind.Xray, "something else", null)]
    public void ParsesVersion(CoreKind kind, string output, string? expected) => Assert.Equal(expected, CoreUpdater.ParseVersion(kind, output));

    [Fact]
    public void PicksWindowsX64AssetWithDigest()
    {
        var sha = new string('a', 64);
        using var doc = JsonDocument.Parse(Release("26.11.1", "sha256:" + sha, string.Format(Download, "26.11.1")));
        var r = CoreUpdater.ParseRelease(CoreKind.Xray, doc.RootElement);
        Assert.Equal(("26.11.1", sha), (r.Version, r.Sha256));
        Assert.EndsWith("/Xray-windows-64.zip", r.Download.AbsoluteUri);
    }

    [Theory]
    [InlineData("26.11.1", null, false)]
    [InlineData("26.11.1", "md5:abc", false)]
    [InlineData("26.11.1", "ok", true)]
    [InlineData("latest", "ok", false)]
    public void RejectsBadReleases(string version, string? digest, bool prerelease)
    {
        using var doc = JsonDocument.Parse(Release(version, digest == "ok" ? "sha256:" + new string('a', 64) : digest!, string.Format(Download, version), prerelease));
        Assert.Throws<CoreUpdateException>(() => CoreUpdater.ParseRelease(CoreKind.Xray, doc.RootElement));
    }

    [Fact]
    public void RejectsDownloadsOutsideGitHub()
    {
        using var doc = JsonDocument.Parse(Release("26.11.1", "sha256:" + new string('a', 64), "https://evil.example/Xray-windows-64.zip"));
        Assert.Throws<CoreUpdateException>(() => CoreUpdater.ParseRelease(CoreKind.Xray, doc.RootElement));
    }

    /// <summary>Отдаёт ответ GitHub API и архив по их адресам.</summary>
    private sealed class FakeGitHub(string releaseJson, byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip), RequestMessage = request });
    }

    /// <summary>Папка с cores (копия настоящего xray) и архив релиза с тем же xray.</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"pvpn-upd-{Guid.NewGuid():N}");
        public CorePaths Cores { get; }
        public byte[] Zip { get; }
        public string ZipSha { get; }
        public string Version { get; } = "26.10.21";

        public Sandbox()
        {
            var real = CorePaths.Resolve(allowDevFallback: true);
            Cores = new CorePaths(Path.Combine(Root, "cores"));
            Directory.CreateDirectory(Cores.Directory);
            File.Copy(real.Xray, Cores.Xray);
            // Как в настоящей установке и в релизе xray: рядом с ядром базы geoip/geosite.
            foreach (var geo in CoreUpdater.GeoFiles)
                File.Copy(Path.Combine(real.Directory, geo), Path.Combine(Cores.Directory, geo));
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntryFromFile(real.Xray, "xray.exe");
                zip.CreateEntry("LICENSE");
                foreach (var geo in CoreUpdater.GeoFiles)
                    zip.CreateEntryFromFile(Path.Combine(real.Directory, geo), geo);
            }
            Zip = ms.ToArray();
            ZipSha = Convert.ToHexStringLower(SHA256.HashData(Zip));
        }

        public string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task UpdatesKeepsPreviousAndRollsBack()
    {
        using var box = new Sandbox();
        var before = box.Hash(box.Cores.Xray);
        using var http = new HttpClient(new FakeGitHub(Release(box.Version, "sha256:" + box.ZipSha, string.Format(Download, box.Version)), box.Zip));
        var updater = new CoreUpdater(box.Cores, http);

        var same = await updater.UpdateAsync(CoreKind.Xray, force: false);
        Assert.False(same.Changed);

        var exclusiveCalls = 0;
        var r = await updater.UpdateAsync(CoreKind.Xray, force: true, async swap => { exclusiveCalls++; await swap(); });
        Assert.True(r.Changed, r.Message);
        Assert.Equal(1, exclusiveCalls);
        Assert.True(File.Exists(updater.PreviousPath(CoreKind.Xray)));
        Assert.Equal(box.Version, await CoreUpdater.GetVersionAsync(CoreKind.Xray, box.Cores.Xray));
        Assert.Contains("xray 26.10.21", await File.ReadAllTextAsync(Path.Combine(box.Cores.Directory, "VERSIONS.txt")));
        Assert.Empty(Directory.GetFiles(box.Root, "core.zip", SearchOption.AllDirectories));

        var back = await updater.RollbackAsync(CoreKind.Xray);
        Assert.True(back.Changed);
        Assert.Equal(before, box.Hash(box.Cores.Xray));
        Assert.True(File.Exists(updater.PreviousPath(CoreKind.Xray)));
    }

    [Fact]
    public async Task WrongDigestLeavesCurrentCore()
    {
        using var box = new Sandbox();
        var before = box.Hash(box.Cores.Xray);
        using var http = new HttpClient(new FakeGitHub(Release("26.11.1", "sha256:" + new string('b', 64), string.Format(Download, "26.11.1")), box.Zip));
        var e = await Assert.ThrowsAsync<CoreUpdateException>(() => new CoreUpdater(box.Cores, http).UpdateAsync(CoreKind.Xray, force: false));
        Assert.Contains("SHA256", e.Message);
        Assert.Equal(before, box.Hash(box.Cores.Xray));
        Assert.False(File.Exists(Path.Combine(box.Root, "cores-previous", "xray.exe")));
    }

    [Fact]
    public async Task VersionMismatchLeavesCurrentCore()
    {
        using var box = new Sandbox();
        var before = box.Hash(box.Cores.Xray);
        // Релиз «99.0.0», а в архиве xray 26.10.21: подмена или ошибка сборки — не ставим.
        using var http = new HttpClient(new FakeGitHub(Release("99.0.0", "sha256:" + box.ZipSha, string.Format(Download, "99.0.0")), box.Zip));
        var e = await Assert.ThrowsAsync<CoreUpdateException>(() => new CoreUpdater(box.Cores, http).UpdateAsync(CoreKind.Xray, force: false));
        Assert.Contains("99.0.0", e.Message);
        Assert.Equal(before, box.Hash(box.Cores.Xray));
    }

    [Fact]
    public async Task RollbackWithoutPreviousFails()
    {
        using var box = new Sandbox();
        using var http = new HttpClient(new FakeGitHub("{}", []));
        await Assert.ThrowsAsync<CoreUpdateException>(() => new CoreUpdater(box.Cores, http).RollbackAsync(CoreKind.Xray));
    }

    [Theory]
    [InlineData(CoreKind.Xray)]
    [InlineData(CoreKind.SingBox)]
    public async Task CurrentCoresPassSelfTest(CoreKind kind)
    {
        var cores = CorePaths.Resolve(allowDevFallback: true);
        Assert.Null(await CoreSelfTest.RunAsync(kind, kind == CoreKind.Xray ? cores.Xray : cores.SingBox));
    }
}
