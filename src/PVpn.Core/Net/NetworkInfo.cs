using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.Networking.WinSock;

namespace PVpn.Core.Net;

/// <summary>Физический адаптер, через который идёт интернет (Wi-Fi или кабель).</summary>
public sealed record PhysicalInterface(int Index, ulong Luid, string Name, string Description, IPAddress Gateway, uint Metric);

/// <summary>Другой VPN-адаптер, который перехватывает маршрут по умолчанию.</summary>
public sealed record RouteConflict(int Index, string Name, string Description, string Route);

public sealed record DefaultRoute(int InterfaceIndex, ulong Luid, string Prefix, IPAddress NextHop, uint RouteMetric, uint InterfaceMetric, bool Hardware, bool Up, string Name, string Description)
{
    public uint EffectiveMetric => RouteMetric + InterfaceMetric;
}

public static class NetworkInfo
{
    /// <summary>Все IPv4-маршруты 0.0.0.0/0, 0.0.0.0/1 и 128.0.0.0/1 с метриками и сведениями об адаптерах.</summary>
    public static unsafe IReadOnlyList<DefaultRoute> GetDefaultRoutes()
    {
        var result = new List<DefaultRoute>();
        MIB_IPFORWARD_TABLE2* table = null;
        var err = PInvoke.GetIpForwardTable2(ADDRESS_FAMILY.AF_INET, &table);
        if (err != WIN32_ERROR.NO_ERROR)
            throw new System.ComponentModel.Win32Exception((int)err, "GetIpForwardTable2");
        try
        {
            var rows = table->Table.AsSpan((int)table->NumEntries);
            foreach (ref readonly var row in rows)
            {
                var prefixLength = row.DestinationPrefix.PrefixLength;
                var dest = ToAddress(row.DestinationPrefix.Prefix);
                var prefix = $"{dest}/{prefixLength}";
                if (prefix is not ("0.0.0.0/0" or "0.0.0.0/1" or "128.0.0.0/1"))
                    continue;

                var ifRow = new MIB_IF_ROW2 { InterfaceLuid = row.InterfaceLuid };
                if (PInvoke.GetIfEntry2(&ifRow) != WIN32_ERROR.NO_ERROR)
                    continue;

                var ipIf = new MIB_IPINTERFACE_ROW();
                PInvoke.InitializeIpInterfaceEntry(&ipIf);
                ipIf.Family = ADDRESS_FAMILY.AF_INET;
                ipIf.InterfaceLuid = row.InterfaceLuid;
                var ifMetric = PInvoke.GetIpInterfaceEntry(&ipIf) == WIN32_ERROR.NO_ERROR ? ipIf.Metric : 0u;

                result.Add(new DefaultRoute(
                    (int)row.InterfaceIndex,
                    row.InterfaceLuid.Value,
                    prefix,
                    ToAddress(row.NextHop),
                    row.Metric,
                    ifMetric,
                    Hardware: (ifRow.InterfaceAndOperStatusFlags._bitfield & 0x1) != 0,
                    Up: ifRow.OperStatus == IF_OPER_STATUS.IfOperStatusUp,
                    Name: ifRow.Alias.ToString(),
                    Description: ifRow.Description.ToString()));
            }
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
        return result;
    }

    /// <summary>
    /// Физический адаптер с наименьшей метрикой маршрута по умолчанию.
    /// Виртуальные адаптеры (TUN, TAP, Radmin, VirtualBox, Hyper-V) отсеиваются по флагу HardwareInterface.
    /// </summary>
    public static PhysicalInterface? GetDefaultPhysicalInterface()
    {
        var best = GetDefaultRoutes()
            .Where(r => r.Hardware && r.Up && r.Prefix == "0.0.0.0/0" && !r.NextHop.Equals(IPAddress.Any))
            .OrderBy(r => r.EffectiveMetric)
            .FirstOrDefault();
        return best == null
            ? null
            : new PhysicalInterface(best.InterfaceIndex, best.Luid, best.Name, best.Description, best.NextHop, best.EffectiveMetric);
    }

    /// <summary>
    /// Виртуальные адаптеры, которые забирают трафик раньше физического: маршруты /1 или /0 с метрикой не хуже.
    /// Radmin VPN (маршрут /0 с метрикой ~9000) сюда не попадает.
    /// </summary>
    public static IReadOnlyList<RouteConflict> DetectConflicts(string ownInterfaceName)
    {
        var routes = GetDefaultRoutes();
        var physical = routes.Where(r => r.Hardware && r.Up && r.Prefix == "0.0.0.0/0").Select(r => r.EffectiveMetric).DefaultIfEmpty(uint.MaxValue).Min();
        return routes
            .Where(r => !r.Hardware && r.Up && !string.Equals(r.Name, ownInterfaceName, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Prefix != "0.0.0.0/0" || r.EffectiveMetric <= physical)
            .GroupBy(r => r.InterfaceIndex)
            .Select(g => new RouteConflict(g.Key, g.First().Name, g.First().Description, string.Join(", ", g.Select(r => r.Prefix))))
            .ToList();
    }

    /// <summary>
    /// IPv4-подсети поднятых адаптеров, кроме частных диапазонов и нашего TUN (например, 26.0.0.0/8 у Radmin VPN).
    /// Их трафик должен идти мимо туннеля.
    /// </summary>
    public static IReadOnlyList<string> GetPublicLocalSubnets(string ownInterfaceName)
    {
        var result = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            if (string.Equals(nic.Name, ownInterfaceName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength is <= 0 or >= 32)
                    continue;
                var network = NetworkAddress(ua.Address, ua.PrefixLength);
                if (!IsPrivate(network))
                    result.Add($"{network}/{ua.PrefixLength}");
            }
        }
        return result.Order().ToList();
    }

    /// <summary>
    /// Физический ли адаптер (флаг HardwareInterface): Wi-Fi и сетевая карта — да; TUN, TAP, Radmin, VirtualBox — нет.
    /// </summary>
    public static unsafe bool IsHardwareAdapter(Guid adapterId)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => Guid.TryParse(n.Id, out var id) && id == adapterId);
        if (nic?.GetIPProperties().GetIPv4Properties() is not { } v4)
            return false;
        var row = new MIB_IF_ROW2 { InterfaceIndex = (uint)v4.Index };
        return PInvoke.GetIfEntry2(&row) == WIN32_ERROR.NO_ERROR && (row.InterfaceAndOperStatusFlags._bitfield & 0x1) != 0;
    }

    /// <summary>LUID адаптера по индексу интерфейса (для фильтров WFP).</summary>
    public static unsafe ulong GetInterfaceLuid(int interfaceIndex)
    {
        var row = new MIB_IF_ROW2 { InterfaceIndex = (uint)interfaceIndex };
        var err = PInvoke.GetIfEntry2(&row);
        if (err != WIN32_ERROR.NO_ERROR)
            throw new System.ComponentModel.Win32Exception((int)err, $"GetIfEntry2 (интерфейс {interfaceIndex})");
        return row.InterfaceLuid.Value;
    }

    /// <summary>«10.0.0.0/8», «fe80::/10» → адрес и длина префикса.</summary>
    public static (IPAddress Address, int PrefixLength) ParseCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var address = IPAddress.Parse(slash < 0 ? cidr : cidr[..slash]);
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = slash < 0 ? max : int.Parse(cidr[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (prefix < 0 || prefix > max)
            throw new FormatException($"Неверная длина префикса в {cidr}");
        return (address, prefix);
    }

    internal static IPAddress NetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var value = ((uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3]) & mask;
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    internal static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
    }

    private static unsafe IPAddress ToAddress(SOCKADDR_INET sa)
    {
        if (sa.si_family == ADDRESS_FAMILY.AF_INET6)
            return new IPAddress(new ReadOnlySpan<byte>(&sa.Ipv6.sin6_addr, 16));
        return new IPAddress(new ReadOnlySpan<byte>(&sa.Ipv4.sin_addr, 4));
    }
}
