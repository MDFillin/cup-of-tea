using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Service;

internal sealed class SessionException(string message) : Exception(message);

/// <summary>
/// Одна TUN-сессия: маршрут к серверу через физический шлюз → xray (SOCKS с паролем) → sing-box (TUN).
/// Если любой шаг не удался, всё уже сделанное откатывается.
/// </summary>
internal sealed class TunSession
{
    public const string InterfaceName = IpcConstants.TunInterfaceName;

    private ConnectOptions options;
    private readonly CorePaths cores;
    private readonly ServiceLog log;
    private LogSanitizer sanitizer;
    private HostRoute? route;

    public Profile Profile { get; private set; }
    public bool KillSwitchActive { get; private set; }
    public IPAddress ServerIp { get; private set; } = IPAddress.None;
    public PhysicalInterface Physical { get; private set; } = null!;
    public CoreProcess Xray { get; private set; } = null!;
    public CoreProcess SingBox { get; private set; } = null!;
    public int SocksPort { get; private set; }

    /// <summary>Порт статистики xray на 127.0.0.1 (интерфейс показывает по ней скорость и трафик).</summary>
    public int MetricsPort { get; private set; }
    public NetworkCredential SocksCredential { get; }

    private TunSession(Profile profile, ConnectOptions options, CorePaths cores, ServiceLog log)
    {
        Profile = profile;
        this.options = options;
        this.cores = cores;
        this.log = log;
        SocksCredential = new NetworkCredential("tempest", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        sanitizer = new LogSanitizer([.. profile.Secrets, SocksCredential.Password]);
    }

    /// <summary>Задержка проверки связи при подключении (служба сразу показывает её как пинг).</summary>
    public int? DelayMs { get; private set; }

    /// <summary>Таймаут одной проверки связи сторожем.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private HttpClient? probe;

    /// <summary>
    /// Проверка связи через xray одним запросом. Клиент живёт, пока живёт xray: соединение через туннель
    /// переиспользуется, и проверка не нагружает канал рукопожатиями.
    /// </summary>
    public Task<DelayResult> ProbeAsync(CancellationToken ct)
    {
        probe ??= DelayTester.CreateSocksClient(SocksPort, ProbeTimeout, SocksCredential);
        return DelayTester.ProbeAsync(probe, ct);
    }

    private void ResetProbe()
    {
        probe?.Dispose();
        probe = null;
    }

    /// <summary>
    /// Настройки, от которых зависит sing-box (TUN, DNS, правила). Если у нового подключения они те же,
    /// сервер меняется перезапуском одного xray — без пересоздания адаптера TUN.
    /// </summary>
    public bool CanSwitchTo(Profile nextProfile, ConnectOptions next) => SingBoxKey(Profile, options) == SingBoxKey(nextProfile, next);

    private static string SingBoxKey(Profile p, ConnectOptions o) => System.Text.Json.JsonSerializer.Serialize(
        o with { Bypass = null, TestSeconds = 0, IgnoreConflicts = false, BlockQuic = BlocksQuic(p, o), UseServerRouting = DnsViaProxy(p, o) },
        IpcJsonContext.Default.ConnectOptions);

    /// <summary>У сервера из подписки свой DNS, и его маршруты включены — DNS решает xray (см. SingBoxOptions.DnsViaProxy).</summary>
    private static bool DnsViaProxy(Profile p, ConnectOptions o) => p is XrayConfigProfile server && o.UseServerRouting && ServerConfigs.HasDns(server);

    /// <summary>IP остальных серверов конфига из подписки (автовыбор, цепочки): их тоже мимо TUN.</summary>
    private IReadOnlyList<IPAddress> extraServerIps = [];

    /// <summary>Домены серверов конфига → IP (см. XrayOptions.ResolvedHosts).</summary>
    private IReadOnlyDictionary<string, string> resolvedHosts = new Dictionary<string, string>();

    private static bool BlocksQuic(Profile p, ConnectOptions o) => SingBoxConfigBuilder.BlocksQuic(p, o.BlockQuic);

    public static async Task<TunSession> StartAsync(Profile profile, ConnectOptions options, CorePaths cores, ServiceLog log,
        Action<string> progress, CancellationToken ct)
    {
        var session = new TunSession(profile, options, cores, log);
        try
        {
            await session.StartCoreAsync(progress, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            // Отмена или ошибка на старте: пользователь ждёт — ядрам меньше времени на штатную остановку.
            await session.StopAsync(quick: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartCoreAsync(Action<string> progress, CancellationToken ct)
    {
        progress("Проверяю сеть");
        Physical = NetworkInfo.GetDefaultPhysicalInterface()
                   ?? throw new SessionException("Нет подключения к интернету: не найден Wi-Fi или кабель с маршрутом по умолчанию");
        log.Info($"Физический адаптер: «{Physical.Name}», шлюз {Physical.Gateway}");

        if (!options.IgnoreConflicts)
        {
            var conflicts = NetworkInfo.DetectConflicts(InterfaceName);
            if (conflicts.Count > 0)
            {
                var names = string.Join(", ", conflicts.Select(c => $"«{c.Name}» ({c.Description})"));
                throw new SessionException($"Включён другой VPN: {names}. Выключите его — два VPN в режиме TUN мешают друг другу.");
            }
        }

        // После остановки sing-box Windows удаляет адаптер с задержкой (на некоторых ПК — до полуминуты).
        // Не дождались — не страшно: sing-box открывает существующий адаптер с тем же именем, а что туннель
        // заработал, проверяется DNS-запросом через него (WaitTunnelAnswersAsync).
        if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == InterfaceName) && !WaitAdapterGone(TimeSpan.FromSeconds(3)))
            log.Info($"Адаптер {InterfaceName} остался от прошлого подключения, sing-box использует его");

        ServerIp = await ResolveServerAsync(Profile.Host, ct).ConfigureAwait(false);
        (extraServerIps, resolvedHosts) = await ResolveExtraServersAsync(Profile, ServerIp, ct).ConfigureAwait(false);

        progress("Прокладываю маршрут к серверу");
        StateJournal.Update(j => j with { Profile = Profile.Name, StartedAt = DateTimeOffset.Now });
        AddRoute();

        if (options.KillSwitch)
        {
            // До запуска ядер: пока туннель поднимается, мимо него ничего не уходит.
            progress("Включаю kill switch");
            StateJournal.Update(j => j with { KillSwitch = true });
            ApplyKillSwitch(null);
        }

        progress("Запускаю xray");
        SocksPort = Ports.GetFreeLoopbackPort();
        MetricsPort = Ports.GetFreeLoopbackPort();
        await StartXrayAsync(ct).ConfigureAwait(false);
        await ProbeServerAsync(progress, ct).ConfigureAwait(false);

        progress("Поднимаю TUN");
        await StartSingBoxAsync(ct).ConfigureAwait(false);
        DnsCache.Flush();
    }

    private async Task ProbeServerAsync(Action<string> progress, CancellationToken ct)
    {
        progress("Проверяю связь с сервером");
        var sw = Stopwatch.StartNew();
        DelayResult probe = new(null, "нет попыток");
        for (var attempt = 1; attempt <= 3 && !probe.Ok; attempt++)
        {
            // Через тот же клиент, что и сторож: соединение остаётся прогретым, и первая проверка сторожа
            // не тратит время на рукопожатия (на нестабильном XHTTP это давало бы ложное «не отвечает»).
            probe = await ProbeAsync(ct).ConfigureAwait(false);
            if (probe.Ok)
            {
                // Первый запрос включает установку соединения; задержку показываем по второму.
                var warm = await ProbeAsync(ct).ConfigureAwait(false);
                if (warm.Ok)
                    probe = warm;
            }
            else
                log.Info($"Проверка связи, попытка {attempt}: {probe.Error}");
        }
        if (!probe.Ok)
            throw new SessionException($"Сервер не отвечает по протоколу {Profile.ProtocolLabel} ({probe.Error}). Попробуйте другой протокол этого сервера.");
        DelayMs = probe.Milliseconds;
        log.Info($"Сервер отвечает, задержка {probe.Milliseconds} мс (проверка {sw.ElapsedMilliseconds} мс)");
    }

    /// <summary>
    /// Другой сервер при тех же настройках TUN: новый маршрут (если у сервера другой IP) и новый xray на прежнем
    /// SOCKS-порту. sing-box и адаптер остаются, kill switch тоже (он пускает наружу ядра, а не адреса).
    /// </summary>
    public async Task SwitchAsync(Profile profile, ConnectOptions next, Action<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ip = await ResolveServerAsync(profile.Host, ct).ConfigureAwait(false);
        var (extra, hosts) = await ResolveExtraServersAsync(profile, ip, ct).ConfigureAwait(false);
        // sing-box уже исключил из TUN адреса прежних серверов; новые без перезапуска TUN не исключить.
        if (extra.Except(extraServerIps).Any())
            throw new SessionException("У нового сервера другие адреса автовыбора — нужен перезапуск TUN");
        Profile = profile;
        options = next;
        resolvedHosts = hosts;
        sanitizer = new LogSanitizer([.. profile.Secrets, SocksCredential.Password]);
        if (!ip.Equals(ServerIp))
        {
            RemoveRoute();
            ServerIp = ip;
            AddRoute();
        }
        progress("Перезапускаю xray");
        await Xray.DisposeAsync().ConfigureAwait(false);
        await StartXrayAsync(ct).ConfigureAwait(false);
        await ProbeServerAsync(progress, ct).ConfigureAwait(false);
        log.Info($"Сервер сменён без перезапуска TUN за {sw.ElapsedMilliseconds} мс");
    }

    /// <summary>DNS-серверы физического адаптера (обычно роутер) — для сайтов, идущих мимо VPN.</summary>
    private static IReadOnlyList<IPAddress> AdapterDns(int index) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index == index)
        .SelectMany(n => n.GetIPProperties().DnsAddresses)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
        .ToList();

    private async Task StartXrayAsync(CancellationToken ct)
    {
        // Соединения прежнего xray мертвы — проверки пойдут через новый.
        ResetProbe();
        var config = XrayConfigBuilder.Build(Profile, new XrayOptions
        {
            Mode = XrayMode.TunBackend,
            SocksPort = SocksPort,
            SocksUser = SocksCredential.UserName,
            SocksPassword = SocksCredential.Password,
            BindInterface = Physical.Name,
            ServerAddressOverride = ServerIp.ToString(),
            Bypass = options.Bypass ?? BypassOptions.Default,
            MetricsPort = MetricsPort,
            UseServerRouting = options.UseServerRouting,
            ResolvedHosts = resolvedHosts,
        });
        Xray = CoreProcess.Start("xray", cores.Xray, ["run", "-c", "stdin:"], config.ToJsonString(), sanitizer, log.Write);
        if (!await Ports.WaitListeningAsync(SocksPort, TimeSpan.FromSeconds(10), () => Xray.HasExited, ct).ConfigureAwait(false))
            throw new SessionException("xray не запустился: " + Xray.Tail(4));
    }

    private async Task StartSingBoxAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var config = SingBoxConfigBuilder.Build(new SingBoxOptions
        {
            SocksPort = SocksPort,
            SocksUser = SocksCredential.UserName,
            SocksPassword = SocksCredential.Password,
            ServerAddresses = [ServerIp, .. extraServerIps],
            InterfaceName = InterfaceName,
            Mtu = options.Mtu,
            Stack = options.Stack ?? "gvisor",
            // QUIC отклоняет sing-box (xray пакеты молча выбрасывал бы). Смена сервера без пересоздания TUN — внутри
            // одного вида транспорта (VLESS ↔ VLESS, hy2 ↔ hy2), см. CanSwitchTo.
            BlockQuic = BlocksQuic(Profile, options),
            DirectCidrs = NetworkInfo.GetPublicLocalSubnets(InterfaceName),
            DirectDns = AdapterDns(Physical.Index),
            SplitTunnel = options.SplitTunnel,
            Dns = DnsRules.Resolve(options.Dns),
            DnsViaProxy = DnsViaProxy(Profile, options),
        });
        // Список адаптеров Windows отдаёт не сразу, пока удаляет прежний адаптер TUN, — это видно здесь.
        if (sw.Elapsed > TimeSpan.FromSeconds(1))
            log.Info($"Конфиг sing-box собран за {sw.ElapsedMilliseconds} мс (Windows медленно отдаёт список адаптеров)");
        SingBox = CoreProcess.Start("sing-box", cores.SingBox, ["run", "-c", "stdin", "-D", ServicePaths.SingBoxDir, "--disable-color"],
            config.ToJsonString(), sanitizer, log.Write);

        var started = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (SingBox.HasExited)
                throw new SessionException("sing-box не запустился: " + SingBox.Tail(4));
            var tun = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == InterfaceName);
            if (tun is { OperationalStatus: OperationalStatus.Up } &&
                tun.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                log.Info($"TUN {InterfaceName} поднят за {started.ElapsedMilliseconds} мс");
                // После перезапуска sing-box у адаптера новый LUID: разрешение для TUN ставится заново.
                if (options.KillSwitch)
                    ApplyKillSwitch(NetworkInfo.GetInterfaceLuid(tun.GetIPProperties().GetIPv4Properties().Index));
                await WaitTunnelAnswersAsync(deadline, ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new SessionException("sing-box не поднял адаптер TUN за 20 секунд: " + SingBox.Tail(4));
    }

    /// <summary>
    /// Адаптер с адресом может остаться от прежнего sing-box, поэтому «поднят» ещё не значит «работает».
    /// Работает, когда DNS-запрос внутрь TUN получил ответ: его даёт только новый sing-box, и идёт он через туннель.
    /// </summary>
    private async Task WaitTunnelAnswersAsync(DateTime deadline, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var peer = IPAddress.Parse(SingBoxConfigBuilder.TunAddress4.Split('/')[0]).GetAddressBytes();
        peer[3]++;
        DelayResult result;
        do
        {
            if (SingBox.HasExited)
                throw new SessionException("sing-box не запустился: " + SingBox.Tail(4));
            result = await DelayTester.DnsProbeAsync(new IPAddress(peer), "www.gstatic.com", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        } while (!result.Ok && DateTime.UtcNow < deadline);
        log.Info(result.Ok
            ? $"Туннель отвечает: DNS через TUN за {result.Milliseconds} мс (ждал {sw.ElapsedMilliseconds} мс)"
            : $"Туннель не ответил на DNS-запрос за {sw.ElapsedMilliseconds} мс: {result.Error}");
    }

    /// <summary>Перезапуск xray (упал или не отвечает). SOCKS-порт и пароль прежние, sing-box не трогаем.</summary>
    public async Task RestartXrayAsync(CancellationToken ct)
    {
        await Xray.DisposeAsync().ConfigureAwait(false);
        await StartXrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Новые правила раздельного туннелирования (уже проверенные): перезапускается только sing-box.
    /// xray, маршрут к серверу и kill switch остаются — пока sing-box поднимается, трафик мимо туннеля закрыт.
    /// </summary>
    public async Task ReconfigureAsync(SplitTunnelOptions? split, CancellationToken ct)
    {
        options = options with { SplitTunnel = split };
        log.Info("Раздельное туннелирование: " + Describe(split) + ", перезапускаю sing-box");
        await RestartSingBoxAsync(ct).ConfigureAwait(false);
    }

    internal static string Describe(SplitTunnelOptions? split) => split is null or { IsEffective: false }
        ? "выключено"
        : (split.Mode == SplitTunnelMode.ExcludeSelected ? "всё через VPN, кроме" : "через VPN только") +
          $" программ: {split.Apps.Count}, сайтов: {split.Domains.Count}";

    public async Task RestartSingBoxAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await SingBox.DisposeAsync().ConfigureAwait(false);
        log.Info($"sing-box остановлен за {sw.ElapsedMilliseconds} мс");
        WaitAdapterGone(TimeSpan.FromSeconds(5));
        await StartSingBoxAsync(ct).ConfigureAwait(false);
        DnsCache.Flush();
    }

    /// <summary>Сменился физический адаптер или шлюз (другая сеть Wi-Fi, кабель): новый маршрут и xray на новом адаптере.</summary>
    public async Task RebindAsync(PhysicalInterface physical, CancellationToken ct)
    {
        log.Info($"Сеть сменилась: «{physical.Name}», шлюз {physical.Gateway}");
        RemoveRoute();
        Physical = physical;
        route = RouteManager.AddHostRoute(ServerIp, Physical);
        if (route != null)
        {
            var added = route;
            StateJournal.Update(j => j with { Routes = [.. j.Routes, added] });
        }
        await RestartXrayAsync(ct).ConfigureAwait(false);
    }

    /// <param name="quick">Подключение не состоялось: на штатную остановку ядер и уход адаптера — по секунде-две.</param>
    public async Task StopAsync(bool quick = false)
    {
        var sw = Stopwatch.StartNew();
        ResetProbe();
        var graceful = quick ? TimeSpan.FromSeconds(1.5) : (TimeSpan?)null;
        // Ядра останавливаются одновременно: каждому даётся время на штатную остановку.
        await Task.WhenAll(
            SingBox != null ? SingBox.StopAsync(graceful) : Task.CompletedTask,
            Xray != null ? Xray.StopAsync(graceful) : Task.CompletedTask).ConfigureAwait(false);
        if (SingBox != null)
            await SingBox.DisposeAsync().ConfigureAwait(false);
        if (Xray != null)
            await Xray.DisposeAsync().ConfigureAwait(false);
        log.Info($"Ядра остановлены за {sw.ElapsedMilliseconds} мс");
        WaitAdapterGone(TimeSpan.FromSeconds(quick ? 2 : 5));
        // Kill switch снимается последним, когда ядра уже остановлены.
        if (KillSwitchActive)
            DisableKillSwitch();
        RemoveRoute();
        DnsCache.Flush();
    }

    /// <param name="tunnelLuid">null — TUN ещё не поднят (наружу только ядра и локальная сеть).</param>
    private void ApplyKillSwitch(ulong? tunnelLuid)
    {
        KillSwitch.Enable([cores.Xray, cores.SingBox], [.. KillSwitch.DefaultLanCidrs, .. NetworkInfo.GetPublicLocalSubnets(InterfaceName)],
            tunnelLuid);
        KillSwitchActive = true;
        log.Info(tunnelLuid == null ? "Kill switch включён" : "Kill switch: трафик через TUN разрешён");
    }

    private void DisableKillSwitch()
    {
        try
        {
            var removed = KillSwitch.Disable();
            KillSwitchActive = false;
            StateJournal.Update(j => j with { KillSwitch = false });
            log.Info($"Kill switch выключен (снято фильтров: {removed})");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // Фильтры останутся до «Восстановить сеть» или перезагрузки — это безопаснее, чем утечка.
            log.Info($"Kill switch не выключился: {e.Message}");
        }
    }

    private void AddRoute()
    {
        route = RouteManager.AddHostRoute(ServerIp, Physical);
        if (route == null)
            return;
        var added = route;
        StateJournal.Update(j => j with { Routes = [.. j.Routes, added] });
        log.Info($"Маршрут {ServerIp}/32 через {Physical.Gateway} («{Physical.Name}»)");
    }

    /// <summary>Ждёт, пока Windows удалит адаптер TUN; если это долго — пишет в журнал, сколько ждали.</summary>
    private bool WaitAdapterGone(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var gone = Recovery.WaitAdapterGone(InterfaceName, timeout);
        if (sw.Elapsed > TimeSpan.FromSeconds(1))
            log.Info($"Удаление адаптера {InterfaceName}: {(gone ? "удалён" : "ещё есть")} через {sw.ElapsedMilliseconds} мс");
        return gone;
    }

    private void RemoveRoute()
    {
        if (route == null)
            return;
        try
        {
            RouteManager.DeleteHostRoute(route);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log.Info($"Маршрут к {route.Destination} не удалён: {e.Message}");
        }
        var removed = route;
        StateJournal.Update(j => j with { Routes = j.Routes.Where(r => r != removed).ToList() });
        route = null;
    }

    /// <summary>
    /// Адреса остальных серверов конфига из подписки. Не разрешился какой-то — не беда: xray и так привязан к
    /// физическому адаптеру, исключение из TUN — лишь дополнительная защита от петли.
    /// </summary>
    private static async Task<(IReadOnlyList<IPAddress> Ips, IReadOnlyDictionary<string, string> Hosts)> ResolveExtraServersAsync(
        Profile profile, IPAddress main, CancellationToken ct)
    {
        if (profile is not XrayConfigProfile server)
            return ([], new Dictionary<string, string>());
        var hosts = new Dictionary<string, string>(await ServerConfigs.ResolveHostsAsync(server, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        if (!IPAddress.TryParse(profile.Host, out _))
            hosts[profile.Host] = main.ToString();
        var ips = ServerConfigs.ServerHosts(server)
            .Select(h => IPAddress.TryParse(h, out var ip) ? ip : hosts.TryGetValue(h, out var r) ? IPAddress.Parse(r) : null)
            .OfType<IPAddress>().Where(ip => !ip.Equals(main)).Distinct()
            .Order(Comparer<IPAddress>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()))).ToList();
        return (ips, hosts);
    }

    private static async Task<IPAddress> ResolveServerAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                throw new SessionException("Серверы с IPv6-адресом пока не поддерживаются в режиме TUN");
            return ip;
        }
        // При смене сервера запрос идёт через туннель прежнего сервера — если тот не отвечает, ждать долго незачем:
        // вызывающий тогда поднимет всё заново, и адрес разрешится напрямую.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
            return addresses.FirstOrDefault() ?? throw new SessionException($"Не удалось узнать IP сервера {host}");
        }
        catch (SocketException e)
        {
            throw new SessionException($"Не удалось узнать IP сервера {host}: {e.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SessionException($"Не удалось узнать IP сервера {host}: DNS не ответил за 5 с");
        }
    }
}
