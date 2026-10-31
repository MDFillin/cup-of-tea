using PVpn.Core.Cores;
using Xunit.Abstractions;

namespace PVpn.Core.Tests;

/// <summary>Живая проверка обновления программы с настоящего релиза на GitHub (CUP_LIVE_UPDATE=1).</summary>
public class LiveAppUpdateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FindsAndDownloadsLatestRelease()
    {
        if (Environment.GetEnvironmentVariable("CUP_LIVE_UPDATE") != "1")
            return;
        using var http = CoreUpdater.CreateClient();
        var release = await AppUpdater.CheckAsync(http, new Version(0, 5, 0));
        Assert.NotNull(release);
        output.WriteLine($"{release.Version} {release.Setup} {release.Size} байт, страница {release.Page}");
        var dir = Path.Combine(Path.GetTempPath(), $"cup-live-upd-{Guid.NewGuid():N}");
        try
        {
            var path = await AppUpdater.DownloadAsync(http, release, dir);
            Assert.Equal(release.Size, new FileInfo(path).Length);
            output.WriteLine("скачан и сверен: " + Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Assert.Null(await AppUpdater.CheckAsync(http, release.Version));
    }
}
