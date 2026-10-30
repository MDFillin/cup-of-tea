using System.Net;
using System.Net.Sockets;

namespace PVpn.Core.Net;

public static class Ports
{
    /// <summary>Свободный TCP-порт на 127.0.0.1.</summary>
    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static bool IsLoopbackPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Ждёт, пока на 127.0.0.1:port начнут принимать соединения.</summary>
    public static async Task<bool> WaitListeningAsync(int port, TimeSpan timeout, Func<bool> abort, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !abort())
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        return false;
    }
}
