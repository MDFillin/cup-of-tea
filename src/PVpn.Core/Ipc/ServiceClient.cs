using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PVpn.Core.Diagnostics;

namespace PVpn.Core.Ipc;

public sealed class ServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Клиент службы (для интерфейса и tempest-cli).</summary>
public sealed partial class ServiceClient
{
    /// <summary>
    /// Проверять, что на том конце канала служба (канал принадлежит SYSTEM/Администраторам,
    /// процесс — из Program Files), а не чужой процесс, который успел занять имя канала.
    /// Отключать только при разработке.
    /// </summary>
    public bool VerifyServer { get; init; } = true;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new ServiceUnavailableException($"Служба {Product.Name} не запущена", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new ServiceUnavailableException($"Нет доступа к службе {Product.Name}: служба установлена для другого пользователя", e);
        }

        if (VerifyServer)
            EnsureTrustedServer(pipe);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var line = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest) + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), cts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
                           ?? throw new ServiceUnavailableException("Служба закрыла соединение без ответа");
        return JsonSerializer.Deserialize(responseLine, IpcJsonContext.Default.IpcResponse)
               ?? throw new ServiceUnavailableException("Пустой ответ службы");
    }

    public Task<IpcResponse> StatusAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Command = IpcCommands.Status }, TimeSpan.FromSeconds(5), ct);

    private static void EnsureTrustedServer(NamedPipeClientStream pipe)
    {
        // Главная проверка — владелец канала. Обычный пользователь не может создать канал, владелец
        // которого SYSTEM или Администраторы, так что занять имя канала вместо службы он не сможет.
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new ServiceUnavailableException($"Канал {Product.Id} открыт не службой {Product.Name} (владелец канала {owner?.Value ?? "неизвестен"})");

        // Путь к процессу службы (LocalSystem) виден только администратору. Если виден — сверяем.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetNamedPipeServerProcessId");
        var path = ProcessPaths.TryGet((int)pid);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (path != null && !path.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ServiceUnavailableException($"Канал {Product.Id} открыт не службой {Product.Name} (процесс {pid}: {path})");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
