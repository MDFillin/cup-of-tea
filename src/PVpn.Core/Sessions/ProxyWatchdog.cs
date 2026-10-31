namespace PVpn.Core.Sessions;

public enum WatchdogAction
{
    None,
    Restart,
}

/// <summary>
/// Сторож режима прокси: по результатам проверок через туннель решает, когда перезапускать xray. Две неудачи подряд
/// (при живой сети) — перезапуск; после <see cref="MaxRestarts"/> перезапусков без единой удачной проверки сторож
/// только наблюдает — дело, скорее всего, в сервере, а не в процессе. Удачная проверка сбрасывает счётчики.
/// </summary>
public sealed class ProxyWatchdog
{
    public const int FailuresToRestart = 2;
    public const int MaxRestarts = 3;
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(10);

    private int failures;

    public int Restarts { get; private set; }

    /// <summary>Перезапуски исчерпаны, а сервер так и не ответил.</summary>
    public bool GaveUp => Restarts >= MaxRestarts && failures > 0;

    /// <summary>После неудачи проверяем чаще, чтобы не ждать перезапуска минуту.</summary>
    public TimeSpan NextCheckIn => failures > 0 ? RecheckInterval : CheckInterval;

    public WatchdogAction Report(bool ok, bool networkAvailable)
    {
        if (ok)
        {
            failures = 0;
            Restarts = 0;
            return WatchdogAction.None;
        }
        // Сети нет совсем — перезапуск не поможет; ждём, пока она появится.
        if (!networkAvailable)
        {
            failures = 0;
            return WatchdogAction.None;
        }
        failures++;
        if (failures < FailuresToRestart || Restarts >= MaxRestarts)
            return WatchdogAction.None;
        failures = 0;
        Restarts++;
        return WatchdogAction.Restart;
    }

    /// <summary>xray завершился сам: перезапускаем, пока не исчерпан лимит.</summary>
    public WatchdogAction CoreExited()
    {
        if (Restarts >= MaxRestarts)
            return WatchdogAction.None;
        Restarts++;
        return WatchdogAction.Restart;
    }

    public void Reset()
    {
        failures = 0;
        Restarts = 0;
    }
}
