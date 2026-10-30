using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PVpn.Core.Diagnostics;

public static unsafe partial class ProcessPaths
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Полный путь к exe процесса или null, если доступа нет (процесс SYSTEM или запущен от администратора).</summary>
    public static string? TryGet(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
            return null;
        var buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref uint size);
}
