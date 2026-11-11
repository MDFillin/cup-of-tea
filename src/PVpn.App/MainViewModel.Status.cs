using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Sessions;
using PVpn.Core.SystemProxy;

namespace PVpn.App;

public sealed partial class MainViewModel
{
    // ───────────── Состояние ─────────────

    private async Task PollAsync()
    {
        if (polling)
            return;
        polling = true;
        try
        {
            tick++;
            if (tick % 60 == 5)
                _ = AutoUpdateSubscriptionsAsync();
            // Повтор автоподключения (служба ещё не запустилась после загрузки, сервер не отвечал).
            if (tick % 60 == 30)
                _ = EvaluateNetworkAsync();
            // Свой профиль маршрутизации по ссылке и базы geoip/geosite — проверка, не пора ли обновить.
            if (tick % 60 == 45)
                _ = RefreshCustomRoutingAsync();
            if (tick % 300 == 50)
                _ = MaybeUpdateGeoAsync();
            if (tick % 300 == 70)
                _ = MaybeCheckAppUpdateAsync();
            if (proxy != null || proxyStarting)
                await PollProxyAsync();
            else
                await PollServiceAsync();
            await MeasureTrafficAsync();
            UpdateSessionTime();
        }
        finally
        {
            polling = false;
            TrayChanged?.Invoke();
        }
    }

    private async Task MeasureTrafficAsync()
    {
        TrafficSample? sample = null;
        if (State == UiState.On)
        {
            // Счётчики адаптера wintun не годятся: Windows показывает в них загрузку и как отправленное.
            var port = proxy is { } session ? session.MetricsPort : serviceMetricsPort;
            if (port is > 0)
                sample = await TrafficSources.ReadXrayAsync(metricsHttp, port.Value, XrayConfigBuilder.ProxyTag);
        }
        if (sample is not { } s)
        {
            meter.Reset();
            SpeedText = TrafficText = null;
            ShowTraffic(null);
            return;
        }
        meter.Add(s, DateTime.UtcNow);
        ShowTraffic(meter);
        SpeedText = $"↓ {TrafficMeter.FormatSpeed(meter.DownPerSecond)}   ↑ {TrafficMeter.FormatSpeed(meter.UpPerSecond)}";
        TrafficText = $"↓ {TrafficMeter.FormatBytes(meter.SessionDown)}   ↑ {TrafficMeter.FormatBytes(meter.SessionUp)}";
    }

    private async Task PollProxyAsync()
    {
        if (proxy is not { } session)
            return;
        ConnectedServer = ProfileData.PlainName(session.Profile.Name);
        KillSwitchText = null;
        sessionSince = proxySince;
        if (session.Xray.HasExited)
        {
            if (watchdog.CoreExited() == WatchdogAction.Restart)
            {
                await RestartProxyAsync("xray неожиданно завершился");
                return;
            }
            AppendLog("app", "xray неожиданно завершился, прокси выключен");
            await StopProxyAsync();
            SetState(UiState.Error, "Ошибка", "xray завершился. Подробности — в «Журнале».");
            return;
        }
        SetState(UiState.On, "Подключено", $"Режим прокси · SOCKS 127.0.0.1:{session.SocksPort} · HTTP 127.0.0.1:{session.HttpPort}");
        if (DateTime.UtcNow < proxyNextCheck)
            return;
        proxyNextCheck = DateTime.UtcNow.Add(ProxyWatchdog.CheckInterval);
        var delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10));
        var ip = delay.Ok ? await DelayTester.GetPublicIpViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10)) : null;
        if (proxy != session)
            return;
        PublicIp = ip ?? "—";
        DelayText = delay.Ok ? $"{delay.Milliseconds} мс" : "нет ответа";
        var gaveUp = watchdog.GaveUp;
        var action = watchdog.Report(delay.Ok, NetworkInterface.GetIsNetworkAvailable());
        proxyNextCheck = DateTime.UtcNow.Add(watchdog.NextCheckIn);
        if (action == WatchdogAction.Restart)
            await RestartProxyAsync($"сервер не отвечает ({delay.Error})");
        else if (watchdog.GaveUp && !gaveUp)
        {
            AppendLog("app", $"Сторож: после {ProxyWatchdog.MaxRestarts} перезапусков сервер так и не ответил — дальше только наблюдаю");
            Notification?.Invoke("Сервер не отвечает", $"Перезапуск xray не помог. Попробуйте другой сервер в {Brand.Name}.");
        }
    }

    /// <summary>
    /// Сторож: перезапуск xray с тем же сервером, не трогая состояние «выключено» (системный прокси вернётся к
    /// новому порту сразу после запуска).
    /// </summary>
    private async Task RestartProxyAsync(string reason)
    {
        if (proxy is not { } old)
            return;
        AppendLog("app", $"Сторож: {reason} — перезапускаю xray (попытка {watchdog.Restarts} из {ProxyWatchdog.MaxRestarts})");
        Notification?.Invoke("Переподключение", $"{ProfileData.PlainName(old.Profile.Name)}: {reason}. Перезапускаю соединение.");
        proxy = null;
        proxyStarting = true;
        SetState(UiState.Busy, "Переподключение…", reason);
        await old.DisposeAsync();
        proxyStarting = false;
        await ConnectProxyAsync(old.Profile, restart: true);
        if (proxy == null && State == UiState.Busy)
            SetState(UiState.Off, "Отключено", null);
    }

    /// <summary>Сменился физический адаптер (Wi-Fi → кабель): xray привязан к старому — перезапускаем.</summary>
    private async Task CheckProxyAdapterAsync()
    {
        if (proxy is not { BoundInterface: { } bound } || proxyStarting)
            return;
        var current = await Task.Run(() => NetworkInfo.GetDefaultPhysicalInterface()?.Name);
        if (current != null && current != bound)
            await RestartProxyAsync($"сеть сменилась: «{bound}» → «{current}»");
    }

    private async Task PollServiceAsync()
    {
        var installed = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{IpcConstants.ServiceName}") != null;
        ServiceStatus status;
        try
        {
            status = (await service.StatusAsync()).Status ?? new ServiceStatus();
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            ServiceVersion = "—";
            CanInstallService = !installed && File.Exists(InstallScript);
            ServiceNotice = installed
                ? $"Служба {Brand.Name} не отвечает ({e.Message}). Режим TUN недоступен, режим прокси работает."
                : $"Служба {Brand.Name} не установлена: режим TUN недоступен. Установите её (один раз нужны права администратора) или пользуйтесь режимом прокси.";
            if (IsTunMode && State != UiState.Error)
                SetState(UiState.Off, "Отключено", null);
            return;
        }

        ServiceNotice = null;
        CanInstallService = false;
        ServiceVersion = status.ServiceVersion;
        // Служба подключена (например, интерфейс запустили заново) — показываем её режим.
        if (status.State != SessionState.Disconnected && !IsTunMode)
            IsTunMode = true;
        ApplyServiceStatus(status);

        if (tick % 2 == 0)
            await PollLogsAsync();
    }

    private void ApplyServiceStatus(ServiceStatus s)
    {
        serviceMetricsPort = s.MetricsPort;
        sessionSince = s.State == SessionState.Connected ? s.Since : null;
        ConnectedServer = s.ProfileName != null ? ProfileData.PlainName(s.ProfileName) : null;
        PublicIp = s.State == SessionState.Connected ? s.PublicIp ?? (s.PublicIpChecked ? "—" : "проверяю…") : null;
        DelayText = s.State == SessionState.Connected ? (s.DelayMs is { } d ? $"{d} мс" : "—") : null;
        KillSwitchText = s.State == SessionState.Connected ? (s.KillSwitchActive ? "включён" : "выключен") : null;
        var server = ConnectedServer != null ? $"{ConnectedServer} · {s.Protocol}" : null;
        switch (s.State)
        {
            case SessionState.Connected:
                SetState(UiState.On, "Подключено", s.Message is null or "Подключено" ? server : $"{server}. {s.Message}");
                break;
            case SessionState.Connecting:
                SetState(UiState.Busy, "Подключение…", s.Message);
                break;
            case SessionState.Reconnecting:
                SetState(UiState.Busy, "Переподключение…", s.Message);
                break;
            case SessionState.Disconnecting:
                SetState(UiState.Busy, "Отключение…", s.Message);
                break;
            case SessionState.Error:
                SetState(UiState.Error, "Ошибка", s.Message);
                break;
            default:
                SetState(UiState.Off, "Отключено", s.Message is null or "Отключено" ? null : s.Message);
                break;
        }
        // Скрытые крестиком предупреждения не возвращаются, пока служба их не сняла (тогда скрытие забывается).
        dismissedWarnings.IntersectWith(s.Warnings);
        var visible = s.Warnings.Where(w => !dismissedWarnings.Contains(w)).ToList();
        if (!visible.SequenceEqual(Warnings))
        {
            Warnings.Clear();
            foreach (var w in visible)
                Warnings.Add(w);
        }
    }

    private readonly HashSet<string> dismissedWarnings = [];

    [RelayCommand]
    private void DismissWarning(string? text)
    {
        if (text != null && Warnings.Remove(text))
            dismissedWarnings.Add(text);
    }

    partial void OnStateChanged(UiState value)
    {
        UpdateSessionTime();
        OnHomeChanged();
        TrackConnected(value);
        if (value != UiState.On)
            SplitPending = false;
    }

    /// <summary>Уведомление для трея: заголовок и текст.</summary>
    public event Action<string, string>? Notification;

    private void SetState(UiState newState, string title, string? detail)
    {
        if (settings.Notifications && (newState != State || title != StatusTitle) && Describe(State, StatusTitle, newState, title, detail) is { } note)
            Notification?.Invoke(note.Title, note.Text);
        State = newState;
        StatusTitle = title;
        StatusDetail = detail;
    }

    /// <summary>О каких переходах сообщать: подключение, обрыв и восстановление, ошибка, отключение не по кнопке.</summary>
    private (string Title, string Text)? Describe(UiState oldState, string oldTitle, UiState newState, string newTitle, string? detail)
    {
        var server = ConnectedServer ?? "сервер";
        return (oldState, newState) switch
        {
            (UiState.Busy, UiState.On) when oldTitle.StartsWith("Переподключение", StringComparison.Ordinal) => ("Связь восстановлена", server),
            (UiState.Busy, UiState.On) => ("VPN подключён", server),
            (UiState.On, UiState.Busy) when newTitle.StartsWith("Переподключение", StringComparison.Ordinal) =>
                ("Связь прервалась", "Переподключаюсь… Kill switch не пускает трафик мимо VPN."),
            (UiState.On or UiState.Busy, UiState.Error) => ("Ошибка VPN", detail ?? $"Подробности в окне {Brand.Name}"),
            (UiState.On, UiState.Off) => ("VPN отключён", detail ?? server),
            _ => null,
        };
    }

    /// <summary>Архив для разбора проблем на рабочем столе (без ключей серверов и токенов подписок).</summary>
    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        var files = new Dictionary<string, string>
        {
            ["info.txt"] = string.Join(Environment.NewLine,
                $"{Brand.Name} {AppVersion}, служба {ServiceVersion}, {Environment.OSVersion}, {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
                $"Режим: {(IsTunMode ? "TUN" : "прокси")}, состояние: {StatusTitle}. {StatusDetail}",
                $"Сервер: {ConnectedServer ?? "—"}, IP {PublicIp ?? "—"}, задержка {DelayText ?? "—"}, kill switch {KillSwitchText ?? "—"}",
                $"Раздельное туннелирование: {SplitSummary ?? "выключено"}",
                "Предупреждения: " + string.Join("; ", Warnings)),
            ["network.txt"] = DiagnosticsReport.Network(IpcConstants.TunInterfaceName),
            ["servers.txt"] = DiagnosticsReport.Servers(data),
            ["app-log.txt"] = FullLog(),
        };
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Logs }, TimeSpan.FromSeconds(10));
            files["service-log.txt"] = string.Join(Environment.NewLine, (r.Logs ?? []).Select(l => $"{l.Time.ToLocalTime():dd.MM HH:mm:ss} [{l.Source}] {l.Text}"));
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            files["service-log.txt"] = $"Служба недоступна: {e.Message}";
        }
        foreach (var (name, path) in new[] { ("settings.json", AppSettings.FilePath),
                     ("app-errors.log", Path.Combine(Product.DataDir(Environment.SpecialFolder.LocalApplicationData), "app-errors.log")) })
        {
            if (File.Exists(path))
                files[name] = await File.ReadAllTextAsync(path);
        }

        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Brand.Name.Replace(' ', '-')}-диагностика-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            DiagnosticsReport.Write(zip, files, DiagnosticsReport.Secrets(data));
            Notice = $"Диагностика сохранена: {zip}. Ключей серверов и ссылок подписок в ней нет.";
            Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = $"Не удалось сохранить диагностику: {e.Message}";
        }
    }
}
