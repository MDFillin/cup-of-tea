using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PVpn.App;

/// <summary>
/// Программа ушла в трей и окно освобождено: собрать мусор и отдать Windows лишнюю память процесса.
/// Страницы, которые понадобятся снова, Windows вернёт сама; в трее программа почти ничего не делает.
/// </summary>
internal static class MemoryTrim
{
    public static void Run()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        EmptyWorkingSet(self.Handle);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
