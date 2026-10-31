using System.Text.Json;
using PVpn.Core.Config;
using PVpn.Core.Routing;

namespace PVpn.App;

/// <summary>Раздельное туннелирование: у каждого режима свой список, при смене режима списки не теряются.</summary>
public sealed class SplitTunnelSettings
{
    public bool Enabled { get; set; }
    public SplitTunnelMode Mode { get; set; } = SplitTunnelMode.ExcludeSelected;

    /// <summary>Режим «всё через VPN»: что идёт напрямую.</summary>
    public SplitList Exclude { get; set; } = new();

    /// <summary>Режим «всё напрямую»: что идёт через VPN.</summary>
    public SplitList Only { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public SplitList Current => Mode == SplitTunnelMode.OnlySelected ? Only : Exclude;
}

public sealed class SplitList
{
    public List<SplitApp> Apps { get; set; } = [];
    public List<string> Domains { get; set; } = [];
}

/// <summary>Настройки интерфейса (без секретов) в %APPDATA%\CupOfTea\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath =>
        Path.Combine(Product.DataDir(Environment.SpecialFolder.ApplicationData), "settings.json");

    /// <summary>true — весь трафик через службу (TUN), false — только прокси из интерфейса.</summary>
    public bool TunMode { get; set; } = true;
    public bool KillSwitch { get; set; } = true;
    public bool BlockQuic { get; set; } = true;

    /// <summary>У серверов с конфигом из подписки — их маршруты и DNS (российские сайты напрямую и т. п.).</summary>
    public bool UseServerRouting { get; set; } = true;
    public int Mtu { get; set; } = 1500;

    /// <summary>Сетевой стек TUN (gvisor, system, mixed), см. ConnectOptions.Stack.</summary>
    public string TunStack { get; set; } = "gvisor";

    /// <summary>Уведомления о подключении, обрыве и восстановлении (когда окно не на переднем плане).</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>Глобальное сочетание «подключить / отключить»; пусто — выключено.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+P";

    /// <summary>Подключаться самому: при запуске и при переходе в сеть, которой нет в доверенных.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Доверенные сети (имена из Windows: у Wi-Fi — имя сети): в них VPN не нужен.</summary>
    public List<string> TrustedNetworks { get; set; } = [];

    /// <summary>Отключать VPN, когда компьютер попадает в доверенную сеть.</summary>
    public bool DisconnectOnTrusted { get; set; }

    /// <summary>Обход блокировок (в обоих режимах). Проверяется перед подключением.</summary>
    public BypassOptions Bypass { get; set; } = BypassOptions.Default;

    /// <summary>DNS в режиме TUN.</summary>
    public DnsOptions Dns { get; set; } = new();

    /// <summary>Профиль маршрутизации: off, имя готового, sub:&lt;id подписки&gt; или custom.</summary>
    public string RoutingChoice { get; set; } = "off";

    /// <summary>Пользователь сам выбирал профиль — профиль от подписки не включается сам.</summary>
    public bool RoutingChosenByUser { get; set; }

    /// <summary>Свой профиль, импортированный из буфера или ссылкой.</summary>
    public PVpn.Core.Routing.RoutingProfile? CustomRouting { get; set; }

    /// <summary>Сортировка серверов: none, ping, name.</summary>
    public string ServerSort { get; set; } = "none";

    /// <summary>Пинг серверов при запуске и после обновления подписки.</summary>
    public bool AutoPing { get; set; } = true;

    /// <summary>Сообщать панели подписки идентификатор устройства (x-hwid) — для лимита устройств.</summary>
    public bool SendHwid { get; set; } = true;

    /// <summary>Настройки обхода от панели подписки (fragmentation-*, noises-*) — для её серверов поверх своих.</summary>
    public bool UseSubscriptionBypass { get; set; } = true;
    public SplitTunnelSettings SplitTunnel { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
