using PVpn.Core.Cores;
using Xunit.Abstractions;

namespace PVpn.Core.Tests;

/// <summary>Живое обновление баз geoip/geosite во временной папке (нужна сеть): только при CUP_LIVE_GEO=1.</summary>
public class LiveGeoTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("v2fly")]
    [InlineData("runetfreedom")]
    public async Task UpdatesFromGitHub(string source)
    {
        if (Environment.GetEnvironmentVariable("CUP_LIVE_GEO") != "1")
            return;
        var real = CorePaths.Resolve(allowDevFallback: true);
        var root = Path.Combine(Path.GetTempPath(), $"cup-geo-{Guid.NewGuid():N}");
        var cores = new CorePaths(Path.Combine(root, "cores"));
        Directory.CreateDirectory(cores.Directory);
        try
        {
            File.Copy(real.Xray, cores.Xray);
            foreach (var geo in CoreUpdater.GeoFiles)
                File.Copy(Path.Combine(real.Directory, geo), Path.Combine(cores.Directory, geo));
            using var http = CoreUpdater.CreateClient();
            var message = await new GeoUpdater(cores, http, output.WriteLine).UpdateAsync(GeoUpdater.SourceOf(source));
            output.WriteLine(message);
            Assert.Equal(source, GeoUpdater.InstalledSource(cores));
            Assert.True(File.Exists(Path.Combine(root, "cores-previous", "geosite.dat")));
            output.WriteLine($"geosite {new FileInfo(Path.Combine(cores.Directory, "geosite.dat")).Length / 1024} КБ, geoip {new FileInfo(Path.Combine(cores.Directory, "geoip.dat")).Length / 1024} КБ");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
