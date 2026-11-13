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
        // Служба упала при живом подключении: поднимаем его сама, а kill switch (если он ещё стоит) не снимаем.
        var crashed = LoadInterruptedSession();
        var keep = false;
        if (crashed != null && (crashed.Options?.KillSwitch ?? true))
        {
            try
            {
                keep = KillSwitch.CountFilters() > 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        var interrupted = Recovery.Run(cores, log, keep);
        hold = keep;
        var resume = crashed ?? ResumeStore.Load();
        SetStatus(s => s with
        {
            Message = crashed != null ? "Служба перезапустилась после сбоя, восстанавливаю подключение"
                : resume != null ? "Восстанавливаю подключение после перезагрузки"
                : interrupted ? "Подключение было прервано сбоем или перезагрузкой; сеть восстановлена" : "Отключено",
        });
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        if (resume != null)
            StartResume(resume, crashed != null ? "После сбоя службы восстанавливаю подключение к" : "Защита до входа в Windows: восстанавливаю подключение к");
        return Task.CompletedTask;
    }

    // ───────────── Kill switch держится, пока пользователь сам не отключит VPN ─────────────

    private const string BlockedNote = "Интернет закрыт kill switch — «Отключить» вернёт обычный доступ.";

    /// <summary>
    /// Подключение с kill switch состоялось: если оно оборвётся не по воле пользователя (сервер перестал отвечать,
    /// новый сервер не поднялся, упала служба), фильтры остаются и служба сама пробует подключиться снова.
    /// Снимается отключением, «Восстановить сеть» и остановкой службы.
    /// </summary>
    private bool hold;

    /// <summary>Запрос подключения, которое сейчас поднято (на него возвращаемся, если новый сервер не ответил).</summary>
    private IpcRequest? connectedRequest;

    /// <summary>Чем закончилась последняя попытка, пока интернет закрыт (для статуса между повторами).</summary>
    private string? blockedError;

    /// <summary>Номер команды пользователя: повтор, начатый до неё, не стартует.</summary>
    private int commandSeq;
    private readonly object resumeLock = new();

    /// <summary>Подключение, прерванное падением службы (файл записан после загрузки Windows); после перезагрузки — null.</summary>
    private static IpcRequest? LoadInterruptedSession()
    {
        try
        {
            var file = ServicePaths.SessionFile;
            if (!File.Exists(file))
                return null;
            var written = File.GetLastWriteTimeUtc(file);
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var request = ResumeStore.Load(file);
            if (request != null && written > boot)
                return request;
            ResumeStore.Clear(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private void SaveSession(IpcRequest request)
    {
        try
        {
            ResumeStore.Save(request, ServicePaths.SessionFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            log.Info($"Не удалось записать текущее подключение (после сбоя службы оно не восстановится): {e.Message}");
        }
    }

    private void ClearSession()
    {
        try
        {
            ResumeStore.Clear(ServicePaths.SessionFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Info($"Не удалось удалить запись о подключении: {e.Message}");
        }
    }

    /// <summary>Снимает kill switch, оставленный без сессии (под gate).</summary>
    private void ReleaseBlock()
    {
        try
        {
            if (KillSwitch.CountFilters() == 0)
                return;
            var removed = KillSwitch.Disable();
            StateJournal.Update(j => j with { KillSwitch = false });
            log.Info($"Kill switch выключен (снято фильтров: {removed})");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log.Info($"Kill switch не выключился: {e.Message}");
        }
    }

    /// <summary>Подключения нет, интернет закрыт kill switch; для интерфейса это «переподключение» — кнопка остаётся «Отключить».</summary>
    private void SetBlocked(string error, string? next = null)
    {
        blockedError = error.TrimEnd('.');
        SetStatus(s => s with
        {
            State = SessionState.Reconnecting,
            Message = $"{blockedError}. {BlockedNote}" + (next != null ? $" {next}" : ""),
            KillSwitchActive = true,
        });
    }

    /// <summary>Повторять подключение, пока интернет закрыт (если после <paramref name="seq"/> не было команды пользователя).</summary>
    private void StartRetry(int seq)
    {
        if (Volatile.Read(ref currentRequest) is not { } request)
            return;
        lock (resumeLock)
        {
            if (seq != commandSeq)
                return;
            StartResume(request, "Интернет закрыт kill switch, восстанавливаю подключение к", ResumeRetries[0]);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Выключение Windows или обновление программы: запомненное подключение остаётся — поднимется при следующем запуске.
        CancelResume();
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
    public IpcResponse BeginConnect(IpcRequest request)
    {
        Profile profile;
        ConnectOptions options;
        try
        {
            (profile, options) = Prepare(request);
        }
        catch (Exception e) when (e is LinkParseException or ConfigException)
        {
            return new IpcResponse { Ok = false, Error = e.Message };
        }

        // Команда пользователя важнее восстановления после перезагрузки.
        int seq;
        lock (resumeLock)
        {
            seq = ++commandSeq;
            CancelResume();
        }
        Volatile.Write(ref currentRequest, request);
        RememberOrForget(request);
        _ = Task.Run(async () =>
        {
            // Не вышло, а интернет закрыт kill switch — служба пробует снова сама.
            if (await ConnectAsync(profile, options).ConfigureAwait(false))
                StartRetry(seq);
        });
        return new IpcResponse { Ok = true, Status = Status };
    }

    /// <summary>Разбирает сервер и проверяет настройки (клиенту служба не доверяет).</summary>
    /// <exception cref="LinkParseException" />
    /// <exception cref="ConfigException" />
    private static (Profile Profile, ConnectOptions Options) Prepare(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Link) && string.IsNullOrWhiteSpace(request.Config))
            throw new ConfigException("Не передана ссылка сервера");
        // Конфиг сервера из подписки служба очищает сама (ServerConfigs.Sanitize): клиенту не доверяем.
        var profile = !string.IsNullOrWhiteSpace(request.Config) ? ServerConfigs.ParseRuntime(request.Config) : LinkParser.Parse(request.Link!);

        var options = request.Options ?? new ConnectOptions();
        if (options.Mtu is < 1280 or > 1500)
            throw new ConfigException("MTU должен быть от 1280 до 1500");
        if (options.Stack is not (null or "gvisor" or "system" or "mixed"))
            throw new ConfigException("Стек TUN: gvisor, system или mixed");
        options = options with
        {
            SplitTunnel = ValidateSplit(options.SplitTunnel),
            Bypass = BypassRules.Validate(options.Bypass ?? BypassOptions.Default),
            Dns = DnsRules.Validate(options.Dns ?? new DnsOptions()),
            Routing = options.Routing is { } routing ? RoutingProfiles.Validate(routing) : null,
        };
        return (profile, options);
    }

    // ───────────── Защита до входа в Windows ─────────────

    /// <summary>Запрос текущего подключения: его запоминают, если защиту включили, когда VPN уже подключён.</summary>
    private IpcRequest? currentRequest;
    private CancellationTokenSource? resumeCts;

    /// <summary>Повторы, если после загрузки подключиться не вышло (сервер не ответил, сеть ещё не готова); дальше — раз в минуту.</summary>
    private static readonly TimeSpan[] ResumeRetries =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    /// <summary>Пользователь отключил VPN: после перезагрузки подключение больше не восстанавливается.</summary>
    public async Task UserDisconnectAsync(string reason = "Отключено")
    {
        lock (resumeLock)
        {
            commandSeq++;
            CancelResume();
        }
        Volatile.Write(ref currentRequest, null);
        Forget();
        await DisconnectAsync(reason).ConfigureAwait(false);
    }

    /// <summary>Переключатель «Защита до входа в Windows» изменили в настройках.</summary>
    public IpcResponse SetRemember(bool remember)
    {
        // Запросы текущего подключения помнят выбор: при возврате на прежний сервер он не теряется.
        if (Volatile.Read(ref currentRequest) is { } current)
            Volatile.Write(ref currentRequest, current with { Remember = remember });
        if (connectedRequest is { } connected)
            connectedRequest = connected with { Remember = remember };
        if (!remember)
            Forget();
        else if (Volatile.Read(ref currentRequest) is { } request && Status.State != SessionState.Disconnected)
            RememberOrForget(request);
        // Не подключено — запомнится при следующем подключении (интерфейс передаст Remember).
        return new IpcResponse { Ok = true, Status = Status };
    }

    private void RememberOrForget(IpcRequest request)
    {
        // Тестовое подключение отключается само, если его не подтвердить, — после перезагрузки его не поднимаем.
        if (!request.Remember || request.Options is { TestSeconds: > 0 })
        {
            Forget();
            return;
        }
        try
        {
            ResumeStore.Save(request);
            log.Info("Защита до входа в Windows: подключение запомнено, после перезагрузки служба поднимет его сама");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            log.Info($"Не удалось запомнить подключение для восстановления после перезагрузки: {e.Message}");
        }
    }

    private void Forget()
    {
        try
        {
            if (!ResumeStore.Exists)
                return;
            ResumeStore.Clear();
            log.Info("Защита до входа в Windows: запомненное подключение удалено");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Info($"Не удалось удалить запомненное подключение: {e.Message}");
        }
    }

    private void CancelResume() => Interlocked.Exchange(ref resumeCts, null)?.Cancel();

    /// <param name="what">Начало строки журнала (дальше — имя сервера).</param>
    /// <param name="firstDelay">Пауза перед первой попыткой (повтор после неудачи).</param>
    private void StartResume(IpcRequest request, string what, TimeSpan? firstDelay = null)
    {
        Profile profile;
        ConnectOptions options;
        try
        {
            (profile, options) = Prepare(request);
        }
        catch (Exception e) when (e is LinkParseException or ConfigException)
        {
            log.Info($"Запомненное подключение не восстановить: {e.Message}");
            Forget();
            ClearSession();
            if (hold)
            {
                hold = false;
                ReleaseBlock();
            }
            SetStatus(s => s with { State = SessionState.Disconnected, Message = "Отключено", KillSwitchActive = false });
            return;
        }
        Volatile.Write(ref currentRequest, request);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref resumeCts, cts)?.Cancel();
        log.Info($"{what} {profile.Name}");
        _ = Task.Run(() => ResumeAsync(profile, options, firstDelay, cts.Token));
    }

    /// <summary>Поднимает запомненное подключение после загрузки и повторяет, пока не выйдет или пользователь не вмешается.</summary>
    private async Task ResumeAsync(Profile profile, ConnectOptions options, TimeSpan? firstDelay, CancellationToken ct)
    {
        try
        {
            if (firstDelay is { } pause)
            {
                if (hold && blockedError is { } failed)
                    SetBlocked(failed, $"Повтор через {pause.TotalSeconds:0} с.");
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            for (var attempt = firstDelay != null ? 1 : 0; ; attempt++)
            {
                // После загрузки сеть появляется не сразу: ждём адаптер с маршрутом по умолчанию.
                var waiting = false;
                while (NetworkInfo.GetDefaultPhysicalInterface() == null)
                {
                    if (!waiting)
                    {
                        waiting = true;
                        SetStatus(s => s with
                        {
                            State = SessionState.Connecting,
                            ProfileName = profile.Name,
                            Protocol = profile.ProtocolLabel,
                            Message = "Жду сеть, чтобы восстановить подключение",
                        });
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }

                await ConnectAsync(profile, options, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || Status.State == SessionState.Connected)
                    return;

                var delay = ResumeRetries[Math.Min(attempt, ResumeRetries.Length - 1)];
                var error = (hold ? blockedError : null) ?? Status.Message?.TrimEnd('.');
                log.Info($"Подключение не восстановилось ({error}), повтор через {delay.TotalSeconds:0} с");
                if (hold)
                    SetBlocked(error ?? "Подключение не восстановилось", $"Повтор через {delay.TotalSeconds:0} с.");
                else
                    SetStatus(s => s with { State = SessionState.Error, Message = $"{error}. Повтор через {delay.TotalSeconds:0} с" });
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    /// <param name="abandon">Восстановление после перезагрузки: отменено командой пользователя — не начинать.</param>
    /// <returns>true — подключиться не вышло, а интернет остался закрыт kill switch: нужен повтор.</returns>
    private async Task<bool> ConnectAsync(Profile profile, ConnectOptions options, CancellationToken abandon = default)
    {
        if (abandon.IsCancellationRequested)
            return false;
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
            if (abandon.IsCancellationRequested)
                return false;
            var cts = new CancellationTokenSource();
            // Куда вернуться, если новый сервер не ответит (запоминаем до смены: она меняет сессию на месте).
            (Profile Profile, ConnectOptions Options, IpcRequest Request)? previous =
                hold && session is { } up && connectedRequest is { } upRequest ? (up.Profile, up.Options, upRequest) : null;
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
                    return false;
                }
                if (switched)
                {
                    OnConnected(running, options, cts, sw);
                    return false;
                }
            }

            // Kill switch уже держит интернет закрытым и нужен новому подключению — между ними он не снимается.
            var keep = hold && options.KillSwitch;
            await StopSessionAsync(keep).ConfigureAwait(false);
            if (hold && !keep)
            {
                hold = false;
                ReleaseBlock();
            }
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
                    step => SetStatus(s => s with { Message = step }), cts.Token, keep).ConfigureAwait(false);
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
                if (!keep || e is OperationCanceledException)
                {
                    // Отмена — это отключение или новая команда: состояние и kill switch выставит она.
                    if (!keep)
                    {
                        connectedRequest = null;
                        ClearSession();
                    }
                    SetStatus(s => s with { State = SessionState.Error, Message = message });
                    return false;
                }

                if (previous is { } back)
                {
                    // Прежний сервер работал — возвращаемся на него, а не оставляем пользователя без интернета.
                    log.Info($"Возвращаюсь на прежний сервер: {back.Profile.Name}");
                    Volatile.Write(ref currentRequest, back.Request);
                    RememberOrForget(back.Request);
                    SetStatus(_ => new ServiceStatus
                    {
                        State = SessionState.Connecting,
                        ProfileName = back.Profile.Name,
                        Protocol = back.Profile.ProtocolLabel,
                        Server = $"{back.Profile.Host}:{back.Profile.Port}",
                        Message = "Сервер не ответил, возвращаюсь на прежний",
                        KillSwitchActive = true,
                    });
                    try
                    {
                        session = await TunSession.StartAsync(back.Profile, back.Options, cores, log,
                            step => SetStatus(s => s with { Message = step }), cts.Token, holdKillSwitch: true).ConfigureAwait(false);
                        OnConnected(session, back.Options, cts, sw);
                        var note = $"Сервер «{profile.Name}» не ответил — подключение возвращено на прежний сервер";
                        SetStatus(s => s with { Warnings = [.. s.Warnings, note] });
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        SetStatus(s => s with { State = SessionState.Error, Message = "Подключение отменено" });
                        return false;
                    }
                    catch (Exception again)
                    {
                        log.Info($"Вернуться на прежний сервер не удалось: {again.Message}" + (again is SessionException ? "" : $" ({again.GetType().Name})"));
                    }
                }

                log.Info("Интернет закрыт kill switch до восстановления подключения");
                SetBlocked(message);
                return true;
            }

            OnConnected(session, options, cts, sw);
            return false;
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
        // Тестовое подключение должно уметь отключиться само и вернуть сеть — его kill switch не удерживается.
        hold = current.KillSwitchActive && testDeadline == null;
        blockedError = null;
        connectedRequest = testDeadline == null ? Volatile.Read(ref currentRequest) : null;
        if (connectedRequest is { } request)
            SaveSession(request);
        else
            ClearSession();
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
        int seq;
        lock (resumeLock)
            seq = commandSeq;
        sessionCts?.Cancel();
        var blocked = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session == null && Status.State is SessionState.Disconnected && !hold)
                return;
            var sw = Stopwatch.StartNew();
            // Обрыв (не команда пользователя) при включённом kill switch: интернет остаётся закрытым, служба пробует снова.
            blocked = finalState == SessionState.Error && hold;
            if (!blocked)
                SetStatus(s => s with { State = SessionState.Disconnecting, Message = "Отключаюсь" });
            await StopSessionAsync(blocked).ConfigureAwait(false);
            if (blocked)
            {
                SetBlocked(reason);
                log.Info($"Подключение остановлено за {sw.ElapsedMilliseconds} мс, интернет закрыт kill switch: {reason}");
            }
            else
            {
                if (hold)
                    ReleaseBlock();
                hold = false;
                blockedError = null;
                connectedRequest = null;
                ClearSession();
                SetStatus(_ => new ServiceStatus { State = finalState, Message = reason });
                log.Info($"Отключено за {sw.ElapsedMilliseconds} мс: {reason}");
            }
        }
        finally
        {
            gate.Release();
        }
        if (blocked)
            StartRetry(seq);
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
        await UserDisconnectAsync("Сеть восстановлена").ConfigureAwait(false);
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
    /// <param name="keepKillSwitch">Kill switch остаётся (без туннеля): подключение оборвалось или его сейчас заменит другое.</param>
    private async Task StopSessionAsync(bool keepKillSwitch = false)
    {
        var old = session;
        session = null;
        testDeadline = null;
        if (old != null)
            await old.StopAsync(keepKillSwitch: keepKillSwitch).ConfigureAwait(false);
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
