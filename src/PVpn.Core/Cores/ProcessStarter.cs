using System.ComponentModel;
using System.Diagnostics;

namespace PVpn.Core.Cores;

internal static class ProcessStarter
{
    private const int ERROR_SHARING_VIOLATION = 32;

    /// <summary>
    /// Process.Start с повторами: сразу после скачивания или обновления exe его может держать антивирус,
    /// и CreateProcess возвращает ERROR_SHARING_VIOLATION.
    /// </summary>
    public static void Start(Process process)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                process.Start();
                return;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ERROR_SHARING_VIOLATION && attempt < 10)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}
