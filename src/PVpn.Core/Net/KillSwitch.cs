using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace PVpn.Core.Net;

/// <summary>
/// Kill switch на WFP. Пока он включён, наружу выходит только: трафик через TUN, сами ядра
/// (xray — к серверу, sing-box — в локальную сеть), loopback, DHCP (и как клиент, и как сервер общего доступа), локальные сети
/// и адрес самого VPN-сервера (любым программам — например, панель управления на том же сервере). DNS (порт 53) мимо TUN закрыт.
/// Сессия WFP не динамическая: фильтры переживают падение ядер и самой службы (пока служба перезапускается,
/// утечки нет). Снимаются явно (<see cref="Disable"/>) или перезагрузкой — постоянными (persistent) не делаются.
/// </summary>
public static unsafe class KillSwitch
{
    // Свои ключи поставщика и подслоя: по ним Disable находит фильтры без журнала.
    private static readonly Guid ProviderKey = new("6f0b4a3e-7d2c-4f51-9a39-3c1f5e8b2a71");
    private static readonly Guid SubLayerKey = new("a4e1c7d2-5b38-4e9a-8f06-2d71b9c3e554");

    private const uint RpcCAuthnDefault = 0xFFFFFFFF;
    private const byte IpProtoUdp = 17;

    // Вес внутри подслоя: срабатывает подходящий фильтр с наибольшим весом.
    private const byte WeightLoopback = 15, WeightApps = 14, WeightDhcp = 13, WeightTunnel = 12,
        WeightBlockDns = 11, WeightLan = 10, WeightBlockAll = 0;

    /// <summary>Локальные сети IPv4, куда можно напрямую (кроме DNS): частные, link-local, CGNAT, multicast, broadcast.</summary>
    public static readonly IReadOnlyList<string> DefaultLanCidrs =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "224.0.0.0/4", "255.255.255.255/32"];

    private static readonly string[] LanCidrs6 = ["fe80::/10", "ff00::/8", "fc00::/7"];

    /// <summary>Адреса сервера для фильтра: только настоящие IPv4 (без 0.0.0.0, 255.255.255.255 и loopback), каждый — /32.</summary>
    public static IReadOnlyList<string> ServerCidrs(IEnumerable<IPAddress> addresses) => addresses
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.None) && !IPAddress.IsLoopback(a))
        .Distinct()
        .Select(a => $"{a}/32")
        .ToList();

    private static Guid[] AllLayers =>
    [
        PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
        PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
    ];

    /// <summary>
    /// Ставит фильтры, заменяя прежние фильтры PVpn в одной транзакции (окна без защиты нет).
    /// </summary>
    /// <param name="allowedApps">Полные пути к ядрам: им можно наружу мимо TUN.</param>
    /// <param name="lanCidrs">Локальные сети IPv4 (см. <see cref="DefaultLanCidrs"/>) и, например, 26.0.0.0/8 у Radmin.</param>
    /// <param name="tunnelLuid">LUID адаптера TUN; null — TUN ещё не поднят, наружу только ядра и локальная сеть.</param>
    /// <param name="serverAddresses">
    /// IP текущего VPN-сервера: к ним можно любым программам. Маршрут к серверу и так идёт мимо туннеля (иначе петля),
    /// а сам адрес сервер и провайдер уже знают — без этого не открывалась панель управления, стоящая на том же сервере.
    /// </param>
    public static void Enable(IReadOnlyList<string> allowedApps, IReadOnlyList<string> lanCidrs, ulong? tunnelLuid,
        IReadOnlyList<IPAddress>? serverAddresses = null)
    {
        var servers = ServerCidrs(serverAddresses ?? []);
        using var arena = new Arena();
        var appIds = new List<nint>();
        try
        {
            foreach (var app in allowedApps)
            {
                FWP_BYTE_BLOB* blob;
                Check(PInvoke.FwpmGetAppIdFromFileName0(app, out blob), $"FwpmGetAppIdFromFileName0({app})");
                appIds.Add((nint)blob);
            }

            InTransaction(h =>
            {
                DeleteOurFilters(h);
                AddProviderAndSubLayer(h, arena);
                var c = new Conditions(arena);

                foreach (var layer in AllLayers)
                    AddFilter(h, arena, layer, WeightLoopback, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: loopback", c.Loopback());

                foreach (var appId in appIds)
                    foreach (var layer in AllLayers)
                        AddFilter(h, arena, layer, WeightApps, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: ядра VPN", c.App((FWP_BYTE_BLOB*)appId));

                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCP",
                    c.Protocol(IpProtoUdp), c.RemotePort(67));
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCP",
                    c.Protocol(IpProtoUdp), c.LocalPort(68));
                // Компьютер сам раздаёт адреса (общий доступ: Hyper-V Default Switch, WSL, мобильный хот-спот). Запрос
                // приходит с 0.0.0.0 — под «локальные сети» он не попадает, и без этого виртуалки остаются без адреса.
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCP-сервер",
                    c.Protocol(IpProtoUdp), c.LocalPort(67));
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCP-сервер",
                    c.Protocol(IpProtoUdp), c.RemotePort(68));
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCPv6",
                    c.Protocol(IpProtoUdp), c.RemotePort(547));
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, WeightDhcp, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: DHCPv6",
                    c.Protocol(IpProtoUdp), c.LocalPort(546));

                if (tunnelLuid is { } luid)
                    foreach (var layer in AllLayers)
                        AddFilter(h, arena, layer, WeightTunnel, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: туннель", c.LocalInterface(luid));

                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WeightBlockDns, FWP_ACTION_TYPE.FWP_ACTION_BLOCK, "The Cup of Tea: DNS мимо туннеля", c.RemotePort(53));
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, WeightBlockDns, FWP_ACTION_TYPE.FWP_ACTION_BLOCK, "The Cup of Tea: DNS мимо туннеля", c.RemotePort(53));

                // Несколько условий с одним полем объединяются через ИЛИ: один фильтр на все локальные сети.
                var lan4 = lanCidrs.Select(c.RemoteCidr).ToArray();
                var lan6 = LanCidrs6.Select(c.RemoteCidr).ToArray();
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WeightLan, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: локальная сеть", lan4);
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, WeightLan, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: локальная сеть", lan4);
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, WeightLan, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: локальная сеть", lan6);
                AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, WeightLan, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: локальная сеть", lan6);

                // Фильтр без условий пропускал бы всё, поэтому без адресов сервера его нет вовсе.
                if (servers.Count > 0)
                    AddFilter(h, arena, PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, WeightLan, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, "The Cup of Tea: VPN-сервер",
                        servers.Select(c.RemoteCidr).ToArray());

                foreach (var layer in AllLayers)
                    AddFilter(h, arena, layer, WeightBlockAll, FWP_ACTION_TYPE.FWP_ACTION_BLOCK, "The Cup of Tea: всё остальное");
            });
        }
        finally
        {
            foreach (var appId in appIds)
            {
                var p = (void*)appId;
                PInvoke.FwpmFreeMemory0(&p);
            }
        }
    }

    /// <summary>Снимает все фильтры PVpn (если есть). Возвращает число снятых фильтров.</summary>
    public static int Disable()
    {
        var removed = 0;
        InTransaction(h =>
        {
            removed = DeleteOurFilters(h);
            var subLayer = SubLayerKey;
            IgnoreNotFound(PInvoke.FwpmSubLayerDeleteByKey0(h, &subLayer), "FwpmSubLayerDeleteByKey0");
            var provider = ProviderKey;
            IgnoreNotFound(PInvoke.FwpmProviderDeleteByKey0(h, &provider), "FwpmProviderDeleteByKey0");
        });
        return removed;
    }

    /// <summary>Сколько фильтров PVpn сейчас стоит (для диагностики и тестов).</summary>
    public static int CountFilters()
    {
        var count = 0;
        WithEngine(h => count = EnumerateOurFilters(h).Count);
        return count;
    }

    /// <summary>Сессия WFP по умолчанию (не динамическая): фильтры остаются после закрытия дескриптора.</summary>
    private static void WithEngine(Action<FWPM_ENGINE_HANDLE> body)
    {
        var session = new FWPM_SESSION0 { txnWaitTimeoutInMSec = 10_000 };
        FWPM_ENGINE_HANDLE h;
        Check(PInvoke.FwpmEngineOpen0(default, RpcCAuthnDefault, null, &session, &h), "FwpmEngineOpen0");
        try
        {
            body(h);
        }
        finally
        {
            PInvoke.FwpmEngineClose0(h);
        }
    }

    private static void InTransaction(Action<FWPM_ENGINE_HANDLE> body) => WithEngine(h =>
    {
        Check(PInvoke.FwpmTransactionBegin0(h, 0), "FwpmTransactionBegin0");
        try
        {
            body(h);
            Check(PInvoke.FwpmTransactionCommit0(h), "FwpmTransactionCommit0");
        }
        catch
        {
            PInvoke.FwpmTransactionAbort0(h);
            throw;
        }
    });

    private static void AddProviderAndSubLayer(FWPM_ENGINE_HANDLE h, Arena arena)
    {
        var provider = new FWPM_PROVIDER0 { providerKey = ProviderKey };
        provider.displayData.name = arena.String(Product.Name);
        IgnoreAlreadyExists(PInvoke.FwpmProviderAdd0(h, &provider, default), "FwpmProviderAdd0");

        var subLayer = new FWPM_SUBLAYER0 { subLayerKey = SubLayerKey, providerKey = arena.Guid(ProviderKey), weight = ushort.MaxValue };
        subLayer.displayData.name = arena.String(Product.Name + " kill switch");
        IgnoreAlreadyExists(PInvoke.FwpmSubLayerAdd0(h, &subLayer, default), "FwpmSubLayerAdd0");
    }

    private static void AddFilter(FWPM_ENGINE_HANDLE h, Arena arena, Guid layer, byte weight, FWP_ACTION_TYPE action, string name,
        params FWPM_FILTER_CONDITION0[] conditions)
    {
        var filter = new FWPM_FILTER0
        {
            providerKey = arena.Guid(ProviderKey),
            layerKey = layer,
            subLayerKey = SubLayerKey,
            numFilterConditions = (uint)conditions.Length,
        };
        filter.displayData.name = arena.String(name);
        filter.weight.type = FWP_DATA_TYPE.FWP_UINT8;
        filter.weight.Anonymous.uint8 = weight;
        filter.action.type = action;
        fixed (FWPM_FILTER_CONDITION0* c = conditions)
        {
            filter.filterCondition = conditions.Length > 0 ? c : null;
            Check(PInvoke.FwpmFilterAdd0(h, &filter, default, null), $"FwpmFilterAdd0 ({name})");
        }
    }

    private static int DeleteOurFilters(FWPM_ENGINE_HANDLE h)
    {
        var ids = EnumerateOurFilters(h);
        foreach (var id in ids)
            IgnoreNotFound(PInvoke.FwpmFilterDeleteById0(h, id), "FwpmFilterDeleteById0");
        return ids.Count;
    }

    private static List<ulong> EnumerateOurFilters(FWPM_ENGINE_HANDLE h)
    {
        var ids = new List<ulong>();
        var provider = ProviderKey;
        foreach (var layer in AllLayers)
        {
            var template = new FWPM_FILTER_ENUM_TEMPLATE0
            {
                providerKey = &provider,
                layerKey = layer,
                enumType = FWP_FILTER_ENUM_TYPE.FWP_FILTER_ENUM_OVERLAPPING,
                actionMask = 0xFFFFFFFF,
            };
            FWPM_FILTER_ENUM_HANDLE enumHandle;
            Check(PInvoke.FwpmFilterCreateEnumHandle0(h, &template, &enumHandle), "FwpmFilterCreateEnumHandle0");
            try
            {
                while (true)
                {
                    FWPM_FILTER0** entries;
                    uint count;
                    Check(PInvoke.FwpmFilterEnum0(h, enumHandle, 64, &entries, &count), "FwpmFilterEnum0");
                    for (var i = 0; i < count; i++)
                        ids.Add(entries[i]->filterId);
                    var p = (void*)entries;
                    PInvoke.FwpmFreeMemory0(&p);
                    if (count < 64)
                        break;
                }
            }
            finally
            {
                PInvoke.FwpmFilterDestroyEnumHandle0(h, enumHandle);
            }
        }
        return ids;
    }

    private static void Check(uint result, string what)
    {
        if (result != 0)
            throw new Win32Exception(unchecked((int)result), $"{what}: 0x{result:X8}");
    }

    private static void IgnoreNotFound(uint result, string what)
    {
        if (result != 0 && !IsOneOf(result, HRESULT.FWP_E_FILTER_NOT_FOUND, HRESULT.FWP_E_SUBLAYER_NOT_FOUND, HRESULT.FWP_E_PROVIDER_NOT_FOUND))
            Check(result, what);
    }

    private static void IgnoreAlreadyExists(uint result, string what)
    {
        if (result != 0 && !IsOneOf(result, HRESULT.FWP_E_ALREADY_EXISTS))
            Check(result, what);
    }

    private static bool IsOneOf(uint result, params HRESULT[] codes) =>
        codes.Any(c => unchecked((uint)c.Value) == result);

    /// <summary>Значения условий фильтров; память под указатели — в арене, живёт до конца транзакции.</summary>
    private readonly struct Conditions(Arena arena)
    {
        public FWPM_FILTER_CONDITION0 Loopback()
        {
            var c = New(PInvoke.FWPM_CONDITION_FLAGS, FWP_MATCH_TYPE.FWP_MATCH_FLAGS_ALL_SET, FWP_DATA_TYPE.FWP_UINT32);
            c.conditionValue.Anonymous.uint32 = PInvoke.FWP_CONDITION_FLAG_IS_LOOPBACK;
            return c;
        }

        public FWPM_FILTER_CONDITION0 App(FWP_BYTE_BLOB* appId)
        {
            var c = New(PInvoke.FWPM_CONDITION_ALE_APP_ID, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE);
            c.conditionValue.Anonymous.byteBlob = appId;
            return c;
        }

        public FWPM_FILTER_CONDITION0 LocalInterface(ulong luid)
        {
            var c = New(PInvoke.FWPM_CONDITION_IP_LOCAL_INTERFACE, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_UINT64);
            var value = arena.Alloc<ulong>();
            *value = luid;
            c.conditionValue.Anonymous.uint64 = value;
            return c;
        }

        public FWPM_FILTER_CONDITION0 Protocol(byte protocol)
        {
            var c = New(PInvoke.FWPM_CONDITION_IP_PROTOCOL, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_UINT8);
            c.conditionValue.Anonymous.uint8 = protocol;
            return c;
        }

        public FWPM_FILTER_CONDITION0 RemotePort(ushort port) => Port(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, port);

        public FWPM_FILTER_CONDITION0 LocalPort(ushort port) => Port(PInvoke.FWPM_CONDITION_IP_LOCAL_PORT, port);

        public FWPM_FILTER_CONDITION0 RemoteCidr(string cidr)
        {
            var (address, prefix) = NetworkInfo.ParseCidr(cidr);
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var c = New(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_V4_ADDR_MASK);
                var v4 = arena.Alloc<FWP_V4_ADDR_AND_MASK>();
                // Адрес и маска — в порядке байтов хоста.
                v4->addr = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
                v4->mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                c.conditionValue.Anonymous.v4AddrMask = v4;
                return c;
            }
            else
            {
                var c = New(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_V6_ADDR_MASK);
                var v6 = arena.Alloc<FWP_V6_ADDR_AND_MASK>();
                for (var i = 0; i < 16; i++)
                    v6->addr.Value[i] = bytes[i];
                v6->prefixLength = (byte)prefix;
                c.conditionValue.Anonymous.v6AddrMask = v6;
                return c;
            }
        }

        private static FWPM_FILTER_CONDITION0 Port(Guid field, ushort port)
        {
            var c = New(field, FWP_MATCH_TYPE.FWP_MATCH_EQUAL, FWP_DATA_TYPE.FWP_UINT16);
            c.conditionValue.Anonymous.uint16 = port;
            return c;
        }

        private static FWPM_FILTER_CONDITION0 New(Guid field, FWP_MATCH_TYPE match, FWP_DATA_TYPE type)
        {
            var c = new FWPM_FILTER_CONDITION0 { fieldKey = field, matchType = match };
            c.conditionValue.type = type;
            return c;
        }
    }

    /// <summary>Неуправляемая память для одного вызова WFP; освобождается целиком.</summary>
    private sealed class Arena : IDisposable
    {
        private readonly List<nint> blocks = [];

        public T* Alloc<T>() where T : unmanaged
        {
            var p = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
            blocks.Add((nint)p);
            return p;
        }

        public Guid* Guid(Guid value)
        {
            var p = Alloc<Guid>();
            *p = value;
            return p;
        }

        public PWSTR String(string value)
        {
            var p = (char*)NativeMemory.AllocZeroed((nuint)((value.Length + 1) * sizeof(char)));
            blocks.Add((nint)p);
            value.AsSpan().CopyTo(new Span<char>(p, value.Length));
            return p;
        }

        public void Dispose()
        {
            foreach (var p in blocks)
                NativeMemory.Free((void*)p);
            blocks.Clear();
        }
    }
}
