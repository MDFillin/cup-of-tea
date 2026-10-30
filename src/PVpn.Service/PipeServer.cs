using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PVpn.Core.Ipc;

namespace PVpn.Service;

/// <summary>
/// Канал \\.\pipe\tempestvpn. Подключаться могут только SYSTEM, администраторы и пользователи из service.json.
/// Служба не принимает от клиента ни путей, ни готовых конфигов — только ссылку сервера и настройки.
/// </summary>
internal sealed class PipeServer(SessionManager manager, CoreUpdateManager coreUpdates, ServiceLog log, ServiceConfig config) : BackgroundService
{
    private const int MaxRequestBytes = 64 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = BuildSecurity();
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(IpcConstants.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                    4096, 4096, security);
            }
            catch (UnauthorizedAccessException) when (first)
            {
                log.Info("Канал службы уже создан другим процессом — служба не может принимать команды");
                throw;
            }
            first = false;

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            _ = HandleAsync(pipe, stoppingToken);
        }
    }

    private PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        var users = config.AllowedUserSids.Select(s => new SecurityIdentifier(s)).ToList();
        if (users.Count == 0)
        {
            // Без настроек — только интерактивные пользователи (вошедшие в систему за этим компьютером).
            users.Add(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        }
        foreach (var user in users)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        log.Info($"Канал службы: доступ для {string.Join(", ", users.Select(u => u.Value))}");
        return security;
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await DispatchAsync(line).ConfigureAwait(false);
                // Долгие команды — в журнал: по ним видно, сколько интерфейс ждал (например, отключения).
                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                    log.Info($"Канал: команда «{CommandName(line)}» выполнена за {sw.ElapsedMilliseconds} мс");
                // Время на ответ отсчитывается заново: сама команда (отключение, новые правила) может идти дольше 30 с.
                using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
                write.CancelAfter(TimeSpan.FromSeconds(10));
                var json = JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse) + "\n";
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(json), write.Token).ConfigureAwait(false);
                await pipe.FlushAsync(write.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException)
            {
                log.Info($"Канал: запрос не обработан ({e.GetType().Name}: {e.Message})");
            }
        }
    }

    private async Task<IpcResponse> DispatchAsync(string line)
    {
        var request = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest)
                      ?? throw new InvalidDataException("Пустой запрос");
        switch (request.Command)
        {
            case IpcCommands.Status:
                return new IpcResponse { Ok = true, Status = manager.Status };
            case IpcCommands.Connect:
                return manager.BeginConnect(request.Link, request.Options);
            case IpcCommands.Disconnect:
                await manager.DisconnectAsync("Отключено").ConfigureAwait(false);
                return new IpcResponse { Ok = true, Status = manager.Status };
            case IpcCommands.Confirm:
                return manager.Confirm();
            case IpcCommands.Reconfigure:
                return await manager.ReconfigureAsync(request.Options).ConfigureAwait(false);
            case IpcCommands.Repair:
                await manager.RepairAsync().ConfigureAwait(false);
                return new IpcResponse { Ok = true, Status = manager.Status };
            case IpcCommands.Logs:
                return new IpcResponse { Ok = true, Logs = log.After(request.LogsAfter) };
            case IpcCommands.Cores:
                return await coreUpdates.HandleAsync(request.CoreAction).ConfigureAwait(false);
            default:
                return new IpcResponse { Ok = false, Error = $"Неизвестная команда: {request.Command}" };
        }
    }

    private static string CommandName(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest)?.Command ?? "?";
        }
        catch (JsonException)
        {
            return "?";
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n')
                break;
            buffer.WriteByte(one[0]);
            if (buffer.Length > MaxRequestBytes)
                throw new InvalidDataException("Слишком большой запрос");
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
