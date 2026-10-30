using System.Net;
using System.Runtime.InteropServices;

namespace PVpn.Core.Net;

public static partial class DnsCache
{
    /// <summary>Сбрасывает кэш DNS Windows (как ipconfig /flushdns).</summary>
    public static bool Flush() => DnsFlushResolverCache();

    /// <summary>
    /// Адреса, которых не бывает у настоящих сайтов: пулы fakeip (198.18.0.0/15) и 240.0.0.0/4.
    /// Такой ответ, оставшийся в кэше после другого VPN-клиента, оставляет Windows без интернета.
    /// </summary>
    public static bool IsFakeAddress(IPAddress a)
    {
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = a.GetAddressBytes();
        return (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || b[0] >= 240;
    }

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
