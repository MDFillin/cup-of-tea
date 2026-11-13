using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using PVpn.Core.Cores;
using PVpn.Core.Net;

namespace PVpn.Service;

/// <summary>
/// Возвращает сеть в исходное состояние после падения или по кнопке «Восстановить сеть».
/// Идемпотентно: можно вызывать сколько угодно раз. Вызывается при старте службы и командой --repair.
/// </summary>
internal static class Recovery
{
    /// <returns>true — было что убирать (подключение прервалось сбоем службы или ПК).</returns>
    /// <param name="keepKillSwitch">
    /// Служба перезапустилась после сбоя и сейчас сама поднимет прерванное подключение: kill switch не снимается,
    /// а остаётся в виде «без туннеля», чтобы до переподключения ничего не ушло напрямую.
    /// </param>
    public static bool Run(CorePaths cores, ServiceLog log, bool keepKillSwitch = false)
    {
        var restored = KillOrphanCores(cores, log);

        var journal = StateJournal.Load();
        restored |= journal.Routes.Count > 0 || journal.KillSwitch;
        foreach (var route in journal.Routes)
        {
            try
            {
                RouteManager.DeleteHostRoute(route);
                log.Info($"Восстановление: удалён маршрут к {route.Destination}");
            }
            catch (Win32Exception e)
            {
                log.Info($"Восстановление: маршрут к {route.Destination} не удалён: {e.Message}");
            }
        }

        // Kill switch ищется по своему поставщику WFP, а не по журналу: снимется, даже если журнал потерян.
        var held = false;
        if (keepKillSwitch)
        {
            try
            {
                TunSession.Block(cores);
                held = true;
                restored = true;
                log.Info("Восстановление: kill switch оставлен — интернет закрыт, пока подключение не восстановится");
            }
            catch (Win32Exception e)
            {
                log.Info($"Восстановление: kill switch не переставлен: {e.Message}");
            }
        }
        if (!held)
        {
            try
            {
                var removed = KillSwitch.Disable();
                if (removed > 0)
                {
                    restored = true;
                    log.Info($"Восстановление: снят kill switch ({removed} фильтров)");
                }
            }
            catch (Win32Exception e)
            {
                log.Info($"Восстановление: kill switch не снят: {e.Message}");
            }
        }

        if (!WaitAdapterGone(TunSession.InterfaceName, TimeSpan.FromSeconds(5)))
            log.Info($"Восстановление: адаптер {TunSession.InterfaceName} всё ещё есть в системе");

        DnsCache.Flush();
        StateJournal.Clear();
        if (held)
            StateJournal.Update(j => j with { KillSwitch = true });
        return restored;
    }

    private static bool KillOrphanCores(CorePaths cores, ServiceLog log)
    {
        var killed = false;
        foreach (var (name, path) in new[] { ("xray", cores.Xray), ("sing-box", cores.SingBox) })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (!string.Equals(p.MainModule?.FileName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                            continue;
                        p.Kill(entireProcessTree: true);
                        killed = true;
                        log.Info($"Восстановление: завершён оставшийся процесс {name} ({p.Id})");
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                    {
                    }
                }
            }
        }
        return killed;
    }

    internal static bool WaitAdapterGone(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var exists = NetworkInterface.GetAllNetworkInterfaces().Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!exists)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(250);
        }
    }
}
