using System.Runtime.InteropServices;

namespace PVpn.Core.Net;

/// <summary>
/// Имена подключённых сетей из Network List Manager — те же, что в «Параметрах» Windows: у Wi-Fi это имя сети
/// (SSID), у кабеля обычно «Сеть», «Сеть 2». WLAN API для SSID не годится: в Windows 11 24H2 он отдаёт SSID
/// только программам с доступом к геолокации.
/// </summary>
public static class NetworkNames
{
    private const int NLM_ENUM_NETWORK_CONNECTED = 1;

    /// <summary>
    /// Подключённые сети физических адаптеров. Сети TUN и VPN (PVpn, Radmin и другие) отбрасываются: иначе
    /// «сеть» менялась бы при каждом подключении VPN.
    /// </summary>
    public static IReadOnlyList<string> GetConnected()
    {
        var names = new List<string>();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: true)!;
            var manager = (INetworkListManager)Activator.CreateInstance(type)!;
            try
            {
                var networks = manager.GetNetworks(NLM_ENUM_NETWORK_CONNECTED);
                var buffer = new INetwork[1];
                while (networks.Next(1, buffer, out var fetched) == 0 && fetched == 1)
                {
                    var name = buffer[0].GetName();
                    if (!string.IsNullOrWhiteSpace(name) && AdapterIds(buffer[0]).Any(NetworkInfo.IsHardwareAdapter))
                        names.Add(name.Trim());
                    Marshal.ReleaseComObject(buffer[0]);
                }
                Marshal.ReleaseComObject(networks);
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (COMException)
        {
            // Служба списка сетей недоступна: считаем, что известных сетей нет.
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<Guid> AdapterIds(INetwork network)
    {
        var ids = new List<Guid>();
        var connections = network.GetNetworkConnections();
        var buffer = new INetworkConnection[1];
        while (connections.Next(1, buffer, out var fetched) == 0 && fetched == 1)
        {
            ids.Add(buffer[0].GetAdapterId());
            Marshal.ReleaseComObject(buffer[0]);
        }
        Marshal.ReleaseComObject(connections);
        return ids;
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        IEnumNetworks GetNetworks(int flags);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetNewEnum();

        [PreserveSig]
        int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetwork[] elements, out int fetched);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetNewEnum();

        [PreserveSig]
        int Next(int celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] INetworkConnection[] elements, out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool IsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool IsConnected();

        int GetConnectivity();

        Guid GetConnectionId();

        Guid GetAdapterId();
    }
}
