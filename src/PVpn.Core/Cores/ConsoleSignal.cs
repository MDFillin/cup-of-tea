using System.Runtime.InteropServices;

namespace PVpn.Core.Cores;

/// <summary>
/// Штатная остановка ядра: Ctrl+C в его скрытую консоль. И xray, и sing-box на это закрываются сами;
/// sing-box при этом удаляет адаптер TUN и возвращает настройки интерфейсов — после Kill этого не будет.
/// Вызывать только из отдельного короткоживущего процесса-помощника: он сам подключается к консоли ядра
/// и получает тот же Ctrl+C (служба, сделав это у себя, завершилась бы).
/// </summary>
public static unsafe partial class ConsoleSignal
{
    private const uint CtrlCEvent = 0;

    public static bool SendCtrlC(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
            return false;
        // Флаг «игнорировать Ctrl+C» для самого помощника, иначе он завершится раньше, чем разошлёт сигнал.
        SetConsoleCtrlHandler(null, true);
        return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}
