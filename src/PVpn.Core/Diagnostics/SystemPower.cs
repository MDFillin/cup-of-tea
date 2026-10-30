using System.Runtime.InteropServices;

namespace PVpn.Core.Diagnostics;

public static partial class SystemPower
{
    private const int SmShuttingDown = 0x2000;

    /// <summary>Windows начала выключение или перезагрузку.</summary>
    public static bool IsShuttingDown => GetSystemMetrics(SmShuttingDown) != 0;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
