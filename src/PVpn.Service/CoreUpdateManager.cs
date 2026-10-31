using PVpn.Core.Cores;
using PVpn.Core.Ipc;

namespace PVpn.Service;

/// <summary>
/// Проверка, обновление и откат ядер по командам из канала. Скачивание долгое, а запрос в канале живёт 30 с,
/// поэтому check/update/rollback запускаются в фоне и сразу возвращают состояние; ход виден в status.
/// </summary>
internal sealed class CoreUpdateManager(CorePaths cores, SessionManager sessions, ServiceLog log)
{
    private readonly object sync = new();
    private CoreStatus status = new();
    private Dictionary<CoreKind, string> latest = [];

    public async Task<IpcResponse> HandleAsync(string? action, string? geoSource = null)
    {
        switch (action ?? CoreActions.Status)
        {
            case CoreActions.Status:
                return new IpcResponse { Ok = true, Cores = await SnapshotAsync().ConfigureAwait(false) };
            case CoreActions.Check:
            case CoreActions.Update:
            case CoreActions.Rollback:
            case CoreActions.GeoUpdate:
                lock (sync)
                {
                    if (status.Busy)
                        return new IpcResponse { Ok = false, Error = "Ядра уже обновляются — дождитесь окончания", Cores = status };
                    status = status with
                    {
                        Busy = true,
                        Error = null,
                        Message = action switch
                        {
                            CoreActions.Check => "Проверяю версии на GitHub…",
                            CoreActions.Update => "Скачиваю и проверяю новые версии…",
                            CoreActions.GeoUpdate => "Скачиваю и проверяю базы geoip/geosite…",
                            _ => "Возвращаю прежние версии…",
                        },
                    };
                }
                _ = Task.Run(() => action == CoreActions.GeoUpdate ? RunGeoAsync(geoSource) : RunAsync(action!));
                return new IpcResponse { Ok = true, Cores = status };
            default:
                return new IpcResponse { Ok = false, Error = $"Неизвестное действие с ядрами: {action}" };
        }
    }

    private async Task RunAsync(string action)
    {
        string? message = null, error = null;
        try
        {
            using var http = CoreUpdater.CreateClient();
            var updater = new CoreUpdater(cores, http, log.Info);
            var results = new List<string>();
            foreach (var kind in Enum.GetValues<CoreKind>())
            {
                try
                {
                    switch (action)
                    {
                        case CoreActions.Check:
                            latest[kind] = (await updater.GetLatestAsync(kind).ConfigureAwait(false)).Version;
                            break;
                        case CoreActions.Update:
                            var r = await updater.UpdateAsync(kind, force: false, sessions.RunExclusiveAsync).ConfigureAwait(false);
                            if (r.NewVersion != null)
                                latest[kind] = r.NewVersion;
                            results.Add(r.Message);
                            break;
                        default:
                            if (File.Exists(updater.PreviousPath(kind)))
                                results.Add((await updater.RollbackAsync(kind, sessions.RunExclusiveAsync).ConfigureAwait(false)).Message);
                            break;
                    }
                }
                catch (Exception e) when (e is CoreUpdateException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
                                               or System.Text.Json.JsonException or InvalidDataException)
                {
                    // Сбой одного ядра не мешает другому; текущая версия при этом остаётся на месте.
                    log.Info($"Ядро {CoreUpdater.Title(kind)}: {e.Message}");
                    error = (error == null ? "" : error + " ") + $"{CoreUpdater.Title(kind)}: {e.Message}";
                }
            }
            message = action switch
            {
                CoreActions.Check => error == null ? "Проверено" : null,
                CoreActions.Rollback when results.Count == 0 => "Прежних версий нет — откатываться не к чему",
                _ => string.Join(" ", results) + (results.Count > 0 && action != CoreActions.Check && sessions.Status.State != SessionState.Disconnected
                    ? " Новая версия подействует при следующем подключении." : ""),
            };
        }
        finally
        {
            var snapshot = await SnapshotAsync().ConfigureAwait(false);
            lock (sync)
                status = snapshot with
                {
                    Busy = false,
                    Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                    Error = error,
                    CheckedAt = action == CoreActions.Rollback ? snapshot.CheckedAt : DateTimeOffset.Now,
                };
        }
    }

    /// <summary>Базы geoip/geosite: только известные источники (ключ из GeoUpdater.Sources), адрес служба не принимает.</summary>
    private async Task RunGeoAsync(string? source)
    {
        string? message = null, error = null;
        try
        {
            using var http = CoreUpdater.CreateClient();
            message = await new GeoUpdater(cores, http, log.Info).UpdateAsync(GeoUpdater.SourceOf(source), sessions.RunExclusiveAsync).ConfigureAwait(false);
            if (sessions.Status.State != SessionState.Disconnected)
                message += ". Подействуют при следующем подключении.";
        }
        catch (Exception e) when (e is CoreUpdateException or HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            log.Info($"Базы geoip/geosite: {e.Message}");
            error = "Базы geoip/geosite: " + e.Message;
        }
        finally
        {
            var snapshot = await SnapshotAsync().ConfigureAwait(false);
            lock (sync)
                status = snapshot with { Busy = false, Message = message, Error = error };
        }
    }

    private async Task<CoreStatus> SnapshotAsync()
    {
        var list = new List<CoreInfo>();
        foreach (var kind in Enum.GetValues<CoreKind>())
        {
            var exe = CoreUpdater.ExeName(kind);
            list.Add(new CoreInfo(CoreUpdater.Title(kind),
                await CoreUpdater.GetVersionAsync(kind, Path.Combine(cores.Directory, exe)).ConfigureAwait(false),
                latest.GetValueOrDefault(kind),
                await CoreUpdater.GetVersionAsync(kind, Path.Combine(CoreUpdater.PreviousDirectoryOf(cores), exe)).ConfigureAwait(false)));
        }
        lock (sync)
            return status with { Cores = list, GeoUpdated = GeoUpdater.InstalledAt(cores), GeoSource = GeoUpdater.InstalledSource(cores) };
    }
}
