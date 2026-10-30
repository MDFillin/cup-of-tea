using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.Networking.WinSock;

namespace PVpn.Core.Net;

/// <summary>Маршрут к одному адресу (/32), который мы добавили и обязаны убрать.</summary>
public sealed record HostRoute(string Destination, ulong InterfaceLuid, int InterfaceIndex, string Gateway);

/// <summary>
/// Маршрут к серверу VPN через физический шлюз: соединения с сервером не попадают в TUN, петли нет.
/// Маршрут создаётся только в активном хранилище и исчезает после перезагрузки.
/// </summary>
public static class RouteManager
{
    /// <summary>Добавляет маршрут. Возвращает null, если такой маршрут уже был (его не трогаем и не удаляем потом).</summary>
    public static unsafe HostRoute? AddHostRoute(IPAddress destination, PhysicalInterface via)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Маршрут к серверу поддерживается только для IPv4");

        var row = new MIB_IPFORWARD_ROW2();
        PInvoke.InitializeIpForwardEntry(&row);
        row.InterfaceLuid = new NET_LUID_LH { Value = via.Luid };
        SetIpv4(&row.DestinationPrefix.Prefix, destination);
        row.DestinationPrefix.PrefixLength = 32;
        SetIpv4(&row.NextHop, via.Gateway);
        row.Metric = 0;
        row.Protocol = NL_ROUTE_PROTOCOL.MIB_IPPROTO_NETMGMT;

        var err = PInvoke.CreateIpForwardEntry2(&row);
        if (err == WIN32_ERROR.ERROR_OBJECT_ALREADY_EXISTS)
            return null;
        if (err != WIN32_ERROR.NO_ERROR)
            throw new Win32Exception((int)err, $"Не удалось добавить маршрут к {destination}");
        return new HostRoute(destination.ToString(), via.Luid, via.Index, via.Gateway.ToString());
    }

    /// <summary>Удаляет маршрут. Отсутствующий маршрут — не ошибка.</summary>
    public static unsafe void DeleteHostRoute(HostRoute route)
    {
        var row = new MIB_IPFORWARD_ROW2();
        PInvoke.InitializeIpForwardEntry(&row);
        row.InterfaceLuid = new NET_LUID_LH { Value = route.InterfaceLuid };
        SetIpv4(&row.DestinationPrefix.Prefix, IPAddress.Parse(route.Destination));
        row.DestinationPrefix.PrefixLength = 32;
        SetIpv4(&row.NextHop, IPAddress.Parse(route.Gateway));

        var err = PInvoke.DeleteIpForwardEntry2(&row);
        if (err is not (WIN32_ERROR.NO_ERROR or WIN32_ERROR.ERROR_NOT_FOUND or WIN32_ERROR.ERROR_FILE_NOT_FOUND))
            throw new Win32Exception((int)err, $"Не удалось удалить маршрут к {route.Destination}");
    }

    private static unsafe void SetIpv4(SOCKADDR_INET* sa, IPAddress address)
    {
        sa->si_family = ADDRESS_FAMILY.AF_INET;
        sa->Ipv4.sin_family = ADDRESS_FAMILY.AF_INET;
        var bytes = address.GetAddressBytes();
        fixed (byte* p = bytes)
            Buffer.MemoryCopy(p, &sa->Ipv4.sin_addr, 4, 4);
    }
}
