using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Service;

/// <summary>
/// Состояние службы и единственная активная сессия. Команды выполняются по одной;
/// подключение идёт в фоне, интерфейс следит за ним через Status.
/// </summary>
internal sealed class SessionManager(CorePaths cores, ServiceLog log) : IHostedService
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
    private const string NoNetworkMessage = "Нет сети, жду подключения";

    private readonly SemaphoreSlim gate = new(1, 1);
    private TunSession? session;
    private CancellationTokenSource? sessionCts;
    private Task? watchdog;
    private DateTimeOffset? testDeadline;
    private ServiceStatus status = new() { State = SessionState.Disconnected, ServiceVersion = Version };
    private Timer? networkDebounce;
    private readonly object networkLock = new();

    /// <summary>
    /// До этого момента «та же сеть вернулась» не проверяется: событие смены сети вызывает подъём нашего же TUN
    /// (подключение, новые правила), и лишняя проверка с перезапуском xray рвала только что поднятое подключение.
    /// </summary>
    private DateTimeOffset networkQuietUntil;

    /// <summary>Состояние для интерфейса; порт статистики берётся у текущего сеанса (после переподключения он новый).</summary>
    public ServiceStatus Status => Volatile.Read(ref status) is { State: SessionState.Connected } s && Volatile.Read(ref session) is { MetricsPort: > 0 } current
        ? s with { MetricsPort = current.MetricsPort }
        : Volatile.Read(ref status);

    private void SetStatus(Func<ServiceStatus, ServiceStatus> change)
    {
        var updated = change(Status) with { ServiceVersion = Version };
        Volatile.Write(ref status, updated);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ServicePaths.EnsureDirectories();
        log.Info($"Служба {Product.Name} {Version} запущена, ядра: {cores.Directory}");
        var interrupted = Recovery.Run(cores, log);
        SetStatus(s => s with
        {
            Message = interrupted ? "Подключение было прервано сбоем или перезагрузкой; сеть восстановлена" : "Отключено",
        });
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        await DisconnectAsync("Служба остановлена").ConfigureAwait(false);
        log.Info("Служба остановлена");
    }

    /// <summary>
    /// Выполнить действие, пока никакое подключение не стартует и не останавливается (замена файлов ядер).
    /// Уже работающее подключение не трогается.
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Начинает подключение и сразу возвращает управление.</summary>
    public IpcResponse BeginConnect(string? link, string? config, ConnectOptions? options)
    {
        if (string.IsNullOrWhiteSpace(link) && string.IsNullOrWhiteSpace(config))
            return new IpcResponse { Ok = false, Error = "Не передана ссылка сервера" };
        Profile profile;
        try
        {
            // Конфиг сервера из подписки служба очищает сама (ServerConfigs.Sanitize): клиенту не доверяем.
            profile = !string.IsNullOrWhiteSpace(config) ? ServerConfigs.ParseRuntime(config) : LinkParser.Parse(link!);
        }
        catch (Exception e) when (e is LinkParseException or ConfigException)
        {
            return new IpcResponse { Ok = false, Error = e.Message };
        }

        options ??= new ConnectOptions();
        if (options.Mtu is < 1280 or > 1500)
            return new IpcResponse { Ok = false, Error = "MTU должен быть от 1280 до 1500" };
        if (options.Stack is not (null or "gvisor" or "system" or "mixed"))
            return new IpcResponse { Ok = false, Error = "Стек TUN: gvisor, system или mixed" };
        try
        {
            options = options with
            {
                SplitTunnel = ValidateSplit(options.SplitTunnel),
                Bypass = BypassRules.Validate(options.Bypass ?? BypassOptions.Default),
                Dns = DnsRules.Validate(options.Dns ?? new DnsOptions()),
            };
        }
        catch (ConfigException e)
        {
            return new IpcResponse { Ok = false, Error = e.Message };
        }

        _ = Task.Run(() => ConnectAsync(profile, options));
        return new IpcResponse { Ok = true, Status = Status };
    }

    /// <summary>Пустой список «мимо VPN» равносилен выключенному раздельному туннелированию.</summary>
    private static SplitTunnelOptions? ValidateSplit(SplitTunnelOptions? split) =>
        split == null ? null : SplitTunnelRules.Validate(split) is { IsEffective: true } valid ? valid : null;

    /// <summary>Новые правила раздельного туннелирования для текущего подключения (перезапускается только sing-box).</summary>
    public async Task<IpcResponse> ReconfigureAsync(ConnectOptions? options)
    {
        SplitTunnelOptions? split;
        try
        {
            split = ValidateSplit(options?.SplitTunnel);
        }
        catch (ConfigException e)
        {
            return new IpcResponse { Ok = false, Error = e.Message };
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session is not { } current)
                return new IpcResponse { Ok = false, Error = "Нет активного подключения: правила применятся при следующем" };
            if (Status.State != SessionState.Connected)
                return new IpcResponse { Ok = false, Error = "Подключение сейчас восстанавливается — повторите через несколько секунд" };
            SetStatus(s => s with { State = SessionState.Reconnecting, Message = "Применяю правила раздельного туннелирования" });
            try
            {
                await current.ReconfigureAsync(split, sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SessionException or ConfigException or IOException or OperationCanceledException)
            {
                log.Info($"Правила не применились: {e.Message}");
                _ = Task.Run(() => DisconnectAsync($"Не удалось применить правила: {e.Message}", SessionState.Error));
                return new IpcResponse { Ok = false, Error = e.Message };
            }
            networkQuietUntil = DateTimeOffset.Now.AddSeconds(20);
            SetStatus(s => s with { State = SessionState.Connected, Message = "Подключено" });
            return new IpcResponse { Ok = true, Status = Status };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ConnectAsync(Profile profile, ConnectOptions options)
    {
        var sw = Stopwatch.StartNew();
        sessionCts?.Cancel();
        // Пока прежнее подключение останавливается, интерфейс уже видит, что началось новое.
        SetStatus(s => s with
        {
            State = SessionState.Connecting,
            ProfileName = profile.Name,
            Protocol = profile.ProtocolLabel,
            Server = $"{profile.Host}:{profile.Port}",
            Message = session != null ? "Переключаюсь на другой сервер" : "Подключаюсь",
        });
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = new CancellationTokenSource();
            log.Info($"Подключение: {profile.Name} ({profile.ProtocolLabel}, {profile.Host}:{profile.Port}); DNS {DnsRules.Resolve(options.Dns)}" +
                     (options.SplitTunnel != null ? $"; раздельное туннелирование: {TunSession.Describe(options.SplitTunnel)}" : "") +
                     (sw.ElapsedMilliseconds > 1000 ? $" (ждал предыдущую команду {sw.ElapsedMilliseconds} мс)" : ""));

            if (session is { } running && running.CanSwitchTo(profile, options))
            {
                bool switched;
                try
                {
                    switched = await SwitchAsync(running, profile, options, cts).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Отменили отключением или новым подключением — состояние выставит та команда.
                    log.Info("Смена сервера отменена");
                    return;
                }
                if (switched)
                {
                    OnConnected(running, options, cts, sw);
                    return;
                }
            }

            await StopSessionAsync().ConfigureAwait(false);
            sessionCts = cts;
            SetStatus(_ => new ServiceStatus
            {
                State = SessionState.Connecting,
                ProfileName = profile.Name,
                Protocol = profile.ProtocolLabel,
                Server = $"{profile.Host}:{profile.Port}",
                Message = "Подключаюсь",
            });

            try
            {
                session = await TunSession.StartAsync(profile, options, cores, log,
                    step => SetStatus(s => s with { Message = step }), cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Граница фоновой задачи: любая ошибка должна стать понятным статусом, а не зависшим «Подключаюсь».
                var message = e switch
                {
                    OperationCanceledException => "Подключение отменено",
                    SessionException => e.Message,
                    _ => $"Ошибка подключения: {e.Message}",
                };
                log.Info($"Подключение не удалось: {message}" + (e is SessionException or OperationCanceledException ? "" : $" ({e.GetType().Name})"));
                SetStatus(s => s with { State = SessionState.Error, Message = message });
                return;
            }

            OnConnected(session, options, cts, sw);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Смена сервера без перезапуска TUN (вызывается под gate). false — не вышло: прежнее подключение остановлено
    /// не будет, вызывающий поднимает всё заново.
    /// </summary>
    private async Task<bool> SwitchAsync(TunSession running, Profile profile, ConnectOptions options, CancellationTokenSource cts)
    {
        // Сторож прежнего сервера уже остановлен (sessionCts отменён до gate).
        if (watchdog != null)
        {
            await Task.WhenAny(watchdog, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            watchdog = null;
        }
        sessionCts = cts;
        testDeadline = null;
        try
        {
            await running.SwitchAsync(profile, options, step => SetStatus(s => s with { Message = step }), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SessionException or ConfigException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            log.Info($"Сменить сервер без перезапуска TUN не удалось: {e.Message}");
            if (e is OperationCanceledException)
                throw;
            return false;
        }
    }

    private void OnConnected(TunSession current, ConnectOptions options, CancellationTokenSource cts, Stopwatch sw)
    {
        testDeadline = options.TestSeconds > 0 ? DateTimeOffset.Now.AddSeconds(options.TestSeconds) : null;
        SetStatus(s => s with
        {
            State = SessionState.Connected,
            Message = testDeadline != null ? $"Тестовое подключение: отключусь через {options.TestSeconds} с, если не подтвердить" : "Подключено",
            ProfileName = current.Profile.Name,
            Protocol = current.Profile.ProtocolLabel,
            Server = $"{current.Profile.Host}:{current.Profile.Port}",
            Since = DateTimeOffset.Now,
            PhysicalInterface = current.Physical.Name,
            TestDeadline = testDeadline,
            KillSwitchActive = current.KillSwitchActive,
            DelayMs = current.DelayMs,
            PublicIp = null,
            PublicIpChecked = false,
            Warnings = [],
        });
        log.Info($"Подключено за {sw.ElapsedMilliseconds} мс" + (testDeadline != null ? $" (тестовый режим до {testDeadline:HH:mm:ss})" : ""));

        networkQuietUntil = DateTimeOffset.Now.AddSeconds(20);
        watchdog = Task.Run(() => WatchAsync(current, cts.Token));
        _ = Task.Run(() => PostConnectChecksAsync(cts.Token));
    }

    public async Task DisconnectAsync(string reason, SessionState finalState = SessionState.Disconnected)
    {
        sessionCts?.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session == null && Status.State is SessionState.Disconnected)
                return;
            var sw = Stopwatch.StartNew();
            SetStatus(s => s with { State = SessionState.Disconnecting, Message = "Отключаюсь" });
            await StopSessionAsync().ConfigureAwait(false);
            SetStatus(_ => new ServiceStatus { State = finalState, Message = reason });
            log.Info($"Отключено за {sw.ElapsedMilliseconds} мс: {reason}");
        }
        finally
        {
            gate.Release();
        }
    }

    public IpcResponse Confirm()
    {
        if (session == null || testDeadline == null)
            return new IpcResponse { Ok = false, Error = "Нет тестового подключения" };
        testDeadline = null;
        SetStatus(s => s with { TestDeadline = null, Message = "Подключено" });
        log.Info("Тестовое подключение подтверждено");
        return new IpcResponse { Ok = true, Status = Status };
    }

    public async Task RepairAsync()
    {
        await DisconnectAsync("Сеть восстановлена").ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Recovery.Run(cores, log);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Вызывается под gate.</summary>
    private async Task StopSessionAsync()
    {
        var old = session;
        session = null;
        testDeadline = null;
        if (old != null)
            await old.StopAsync().ConfigureAwait(false);
        if (watchdog != null)
        {
            await Task.WhenAny(watchdog, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            watchdog = null;
        }
    }

    private async Task PostConnectChecksAsync(CancellationToken ct)
    {
        try
        {
            var warnings = new List<string>();
            var ip = await DelayTester.GetPublicIpDirectAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (ip == null)
                warnings.Add("Не удалось проверить внешний IP через туннель");

            var addresses = await Dns.GetHostAddressesAsync("www.gstatic.com", ct).ConfigureAwait(false);
            if (addresses.Any(DnsCache.IsFakeAddress))
                warnings.Add($"DNS вернул ненастоящий адрес: {string.Join(", ", addresses.Where(DnsCache.IsFakeAddress))}");

            var delay = await DelayTester.HttpDelayDirectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            log.Info($"Проверка после подключения: IP {ip ?? "—"}, DNS {string.Join(", ", addresses.Select(a => a.ToString()))}, задержка {delay}");
            // Задержку из проверки при подключении не затираем, если этот запрос не прошёл.
            SetStatus(s => s with
            {
                PublicIp = ip,
                PublicIpChecked = true,
                DelayMs = delay.Milliseconds ?? s.DelayMs,
                Warnings = [.. s.Warnings, .. warnings],
            });
        }
        catch (Exception e) when (e is OperationCanceledException or System.Net.Sockets.SocketException)
        {
            log.Info($"Проверка после подключения не завершилась: {e.Message}");
            if (!ct.IsCancellationRequested)
                SetStatus(s => s with { PublicIpChecked = true });
        }
    }

    /// <summary>Проверка связи, пока всё хорошо.</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(10);

    /// <summary>После неудачной проверки — повтор почти сразу: одна неудача ещё не обрыв.</summary>
    private static readonly TimeSpan WatchRetry = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько неудач подряд считается обрывом. Обрыв замечается за ~10 + 8 + 2 + 8 ≈ 28 с в худшем случае
    /// (было ~90 с), а один медленный ответ не рвёт соединения пользователя перезапуском xray.
    /// </summary>
    private const int WatchFailuresToRecover = 2;

    /// <summary>Следит за ядрами, связью и тестовым таймером.</summary>
    private async Task WatchAsync(TunSession current, CancellationToken ct)
    {
        var failures = 0;
        var restarts = new Queue<DateTimeOffset>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wait = failures > 0 ? WatchRetry : WatchInterval;
                if (testDeadline is { } deadline)
                    wait = TimeSpan.FromTicks(Math.Clamp((deadline - DateTimeOffset.Now).Ticks, TimeSpan.FromSeconds(1).Ticks, wait.Ticks));

                var tick = Task.Delay(wait, ct);
                var done = await Task.WhenAny(current.Xray.Exited, current.SingBox.Exited, tick).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;

                if (testDeadline is { } d && DateTimeOffset.Now >= d)
                {
                    _ = Task.Run(() => DisconnectAsync("Тестовое подключение не подтверждено и отключено автоматически"));
                    return;
                }

                if (done != tick)
                {
                    // Ядро могли перезапустить намеренно (новые правила раздельного туннелирования): та команда держит
                    // gate, пока не поднимет новый процесс. Дожидаемся её и сверяем, тот ли это ещё процесс.
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    var replaced = !ReferenceEquals(done, current.Xray.Exited) && !ReferenceEquals(done, current.SingBox.Exited);
                    gate.Release();
                    if (replaced)
                        continue;

                    var core = done == current.Xray.Exited ? "xray" : "sing-box";
                    // При выключении Windows консольные процессы (наши ядра) закрываются раньше служб.
                    // Перезапускать их незачем: служба скоро получит остановку и всё уберёт, kill switch пока держит.
                    if (SystemPower.IsShuttingDown)
                    {
                        log.Info($"{core} завершился: Windows выключается");
                        return;
                    }
                    log.Info($"{core} неожиданно завершился (код {await (Task<int>)done})");
                    restarts.Enqueue(DateTimeOffset.Now);
                    while (restarts.Count > 0 && restarts.Peek() < DateTimeOffset.Now.AddMinutes(-10))
                        restarts.Dequeue();
                    if (restarts.Count > 3)
                    {
                        _ = Task.Run(() => DisconnectAsync($"{core} падает раз за разом, подключение остановлено. Подробности в журнале.", SessionState.Error));
                        return;
                    }
                    SetStatus(s => s with { State = SessionState.Reconnecting, Message = $"Перезапускаю {core}" });
                    var restarted = await UnderGateAsync(current, () => core == "xray" ? current.RestartXrayAsync(ct) : current.RestartSingBoxAsync(ct), ct)
                        .ConfigureAwait(false);
                    if (!restarted)
                        return;
                    SetStatus(s => s with { State = SessionState.Connected, Message = "Подключено" });
                    continue;
                }

                var delay = await current.ProbeAsync(ct).ConfigureAwait(false);
                if (delay.Ok)
                {
                    if (failures > 0)
                        log.Info($"Сервер снова отвечает ({delay.Milliseconds} мс)");
                    failures = 0;
                    // Сервер снова ответил после «не отвечает» — только теперь подключение действительно восстановлено.
                    SetStatus(s => s with
                    {
                        DelayMs = delay.Milliseconds,
                        State = SessionState.Connected,
                        Message = s.State == SessionState.Reconnecting ? "Подключено" : s.Message,
                    });
                    continue;
                }

                failures++;
                log.Info($"Сервер не ответил ({failures} раз подряд): {delay.Error}");
                SetStatus(s => s with { DelayMs = null });
                if (failures >= WatchFailuresToRecover)
                {
                    failures = 0;
                    // «Переподключение» держится, пока сервер не ответит (см. выше), а не до перезапуска xray: иначе
                    // в сети со страницей входа служба через секунду снова писала бы «Подключено», хотя интернета нет,
                    // и интерфейс не показал бы кнопку «Войти в сеть».
                    SetStatus(s => s with { State = SessionState.Reconnecting, Message = "Сервер не отвечает, переподключаюсь" });
                    if (!await UnderGateAsync(current, () => RecoverLinkAsync(current, ct), ct).ConfigureAwait(false))
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.Info($"Не удалось восстановить подключение: {e.Message} ({e.GetType().Name})");
            _ = Task.Run(() => DisconnectAsync($"Подключение потеряно: {e.Message}", SessionState.Error));
        }
    }

    /// <summary>
    /// Сервер перестал отвечать (под gate). Сначала смотрим, не сменилась ли сеть: если Wi-Fi сменился, пока служба
    /// была занята, xray привязан к старому адаптеру, и простой перезапуск ничего бы не дал. Нет сети — ждём её,
    /// xray не трогаем (вернётся сеть — сработает CheckNetworkAsync).
    /// </summary>
    private async Task RecoverLinkAsync(TunSession current, CancellationToken ct)
    {
        var physical = NetworkInfo.GetDefaultPhysicalInterface();
        if (physical == null)
        {
            SetStatus(s => s with { Message = NoNetworkMessage });
            return;
        }
        if (physical.Index != current.Physical.Index || !physical.Gateway.Equals(current.Physical.Gateway))
        {
            await current.RebindAsync(physical, ct).ConfigureAwait(false);
            SetStatus(s => s with { PhysicalInterface = physical.Name });
            return;
        }
        log.Info("Перезапускаю xray: сервер не отвечает");
        await current.RestartXrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Выполняет действие над сессией под общей блокировкой, если сессия всё ещё текущая.</summary>
    private async Task<bool> UnderGateAsync(TunSession current, Func<Task> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, current))
                return false;
            await action().ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // Подъём нашего же TUN тоже вызывает это событие, поэтому ждём, пока всё утихнет, и сравниваем адаптер.
    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleNetworkCheck(TimeSpan.FromSeconds(3));

    private void ScheduleNetworkCheck(TimeSpan delay)
    {
        lock (networkLock)
        {
            networkDebounce?.Dispose();
            networkDebounce = new Timer(_ => _ = Task.Run(CheckNetworkAsync), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task CheckNetworkAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            // Служба занята (подключение, переключение). Сеть могла смениться прямо сейчас — проверим, когда освободится,
            // иначе xray остался бы привязан к пропавшему адаптеру.
            ScheduleNetworkCheck(TimeSpan.FromSeconds(2));
            return;
        }
        try
        {
            var current = session;
            if (current == null)
                return;
            var physical = NetworkInfo.GetDefaultPhysicalInterface();
            if (physical == null)
            {
                SetStatus(s => s with { Message = NoNetworkMessage });
                return;
            }
            if (physical.Index == current.Physical.Index && physical.Gateway.Equals(current.Physical.Gateway))
            {
                // Та же сеть вернулась (Wi-Fi переподключился, ПК проснулся). Соединения xray с сервером могли
                // протухнуть: проверяем связь сразу, не дожидаясь сторожа, и при необходимости перезапускаем xray.
                var lost = Status.Message == NoNetworkMessage;
                if (lost)
                    SetStatus(s => s with { Message = "Подключено" });
                // Сразу после подъёма TUN событие вызвано нами самими, а не возвращением сети.
                if (!lost && DateTimeOffset.Now < networkQuietUntil)
                    return;
                var ct = sessionCts?.Token ?? CancellationToken.None;
                // Одна неудача — ещё не повод: первые запросы через туннель бывают медленными. Перезапуск xray рвёт все соединения.
                var probe = await current.ProbeAsync(ct).ConfigureAwait(false);
                if (!probe.Ok)
                    probe = await current.ProbeAsync(ct).ConfigureAwait(false);
                if (!probe.Ok)
                {
                    log.Info($"После смены сети сервер не отвечает ({probe.Error}), перезапускаю xray");
                    await current.RestartXrayAsync(ct).ConfigureAwait(false);
                }
                return;
            }
            SetStatus(s => s with { State = SessionState.Reconnecting, Message = "Сеть сменилась, переподключаюсь" });
            await current.RebindAsync(physical, sessionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            SetStatus(s => s with { State = SessionState.Connected, Message = "Подключено", PhysicalInterface = physical.Name });
        }
        catch (Exception ex) when (ex is SessionException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            log.Info($"Не удалось перейти на новую сеть: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }
}
