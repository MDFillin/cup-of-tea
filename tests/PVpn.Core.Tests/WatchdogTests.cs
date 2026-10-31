using PVpn.Core.Sessions;

namespace PVpn.Core.Tests;

public class WatchdogTests
{
    [Fact]
    public void RestartsAfterTwoFailuresInARow()
    {
        var w = new ProxyWatchdog();
        Assert.Equal(WatchdogAction.None, w.Report(ok: false, networkAvailable: true));
        Assert.Equal(ProxyWatchdog.RecheckInterval, w.NextCheckIn);
        Assert.Equal(WatchdogAction.Restart, w.Report(ok: false, networkAvailable: true));
        Assert.Equal(1, w.Restarts);
    }

    [Fact]
    public void SuccessResetsCounters()
    {
        var w = new ProxyWatchdog();
        w.Report(false, true);
        Assert.Equal(WatchdogAction.None, w.Report(true, true));
        Assert.Equal(WatchdogAction.None, w.Report(false, true));
        Assert.Equal(ProxyWatchdog.CheckInterval, new ProxyWatchdog().NextCheckIn);
    }

    [Fact]
    public void NoRestartWithoutNetwork()
    {
        var w = new ProxyWatchdog();
        for (var i = 0; i < 5; i++)
            Assert.Equal(WatchdogAction.None, w.Report(false, networkAvailable: false));
        Assert.Equal(0, w.Restarts);
    }

    [Fact]
    public void GivesUpAfterMaxRestarts()
    {
        var w = new ProxyWatchdog();
        var restarts = 0;
        for (var i = 0; i < 20; i++)
            if (w.Report(false, true) == WatchdogAction.Restart)
                restarts++;
        Assert.Equal(ProxyWatchdog.MaxRestarts, restarts);
        Assert.True(w.GaveUp);
        Assert.Equal(WatchdogAction.None, w.CoreExited());
        w.Report(true, true);
        Assert.False(w.GaveUp);
        Assert.Equal(WatchdogAction.Restart, w.CoreExited());
    }
}
