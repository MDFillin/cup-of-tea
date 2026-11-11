using System.Text.Json.Serialization;
using PVpn.Core.Config;
using PVpn.Core.Routing;

namespace PVpn.Core.Ipc;

/// <summary>
/// Протокол «интерфейс ↔ служба»: по одному JSON на строку, запрос → ответ, затем соединение закрывается.
/// </summary>
public static class IpcConstants
{
    public const string PipeName = "cupoftea";
    public const string ServiceName = Product.Id;

    /// <summary>Имя адаптера TUN.</summary>
    public const string TunInterfaceName = Product.Id;
}

public static class IpcCommands
{
    public const string Status = "status";
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";

    /// <summary>Подтвердить тестовое подключение (отменить автоматический откат).</summary>
    public const string Confirm = "confirm";

    /// <summary>Применить новые правила раздельного туннелирования к текущему подключению (без переподключения).</summary>
    public const string Reconfigure = "reconfigure";
    public const string Repair = "repair";
    public const string Logs = "logs";

    /// <summary>
    /// Защита до входа в Windows: <see cref="IpcRequest.Remember"/> true — служба запоминает текущее подключение и
    /// поднимает его сама при загрузке; false — забывает.
    /// </summary>
    public const string Resume = "resume";

    /// <summary>Ядра: <see cref="IpcRequest.CoreAction"/> — status, check, update или rollback.</summary>
    public const string Cores = "cores";
}

public static class CoreActions
{
    /// <summary>Что стоит сейчас и что известно о новых версиях (без сети).</summary>
    public const string Status = "status";

    /// <summary>Узнать последние версии на GitHub.</summary>
    public const string Check = "check";

    /// <summary>Скачать, проверить и поставить новые версии (в фоне; ход — в status).</summary>
    public const string Update = "update";

    /// <summary>Вернуть прежние версии.</summary>
    public const string Rollback = "rollback";

    /// <summary>Скачать и поставить свежие базы geoip/geosite (источник — <see cref="IpcRequest.GeoSource"/>).</summary>
    public const string GeoUpdate = "geo-update";
}

/// <summary>Одно ядро: версия сейчас, последняя на GitHub (null — не проверяли) и сохранённая для отката.</summary>
public sealed record CoreInfo(string Name, string? Current, string? Latest, string? Previous)
{
    public bool UpdateAvailable => Latest != null && Current != Latest;
}

public sealed record CoreStatus
{
    /// <summary>Идёт проверка, обновление или откат.</summary>
    public bool Busy { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<CoreInfo> Cores { get; init; } = [];
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Когда базы geoip/geosite последний раз заменялись и откуда (null — из комплекта xray).</summary>
    public DateTimeOffset? GeoUpdated { get; init; }
    public string? GeoSource { get; init; }
}

/// <remarks>
/// Свойства с set: для init исходный генератор JSON ставит пропущенным полям default (KillSwitch = false),
/// а не значения по умолчанию.
/// </remarks>
public sealed record ConnectOptions
{
    public bool KillSwitch { get; set; } = true;

    /// <summary>Блокировать QUIC (UDP 443) у трафика через VPN: браузер сразу уходит на TCP.</summary>
    public bool BlockQuic { get; set; } = true;
    /// <summary>Обход блокировок; null — по умолчанию. Служба проверяет настройки сама.</summary>
    public BypassOptions? Bypass { get; set; }

    /// <summary>DNS в TUN; null — Cloudflare. Служба проверяет настройки сама.</summary>
    public DnsOptions? Dns { get; set; }
    public int Mtu { get; set; } = 1500;

    /// <summary>
    /// Сетевой стек TUN в sing-box: gvisor — TCP целиком внутри sing-box; system и mixed — TCP через стек Windows
    /// (быстрее, но sing-box принимает входящие на адаптере TUN, и сторонний сетевой экран может их задерживать).
    /// null — gvisor.
    /// </summary>
    public string? Stack { get; set; }

    /// <summary>Больше 0 — тестовое подключение: служба сама отключится через столько секунд, если его не подтвердить.</summary>
    public int TestSeconds { get; set; }

    /// <summary>Подключаться, даже если другой VPN держит маршрут по умолчанию.</summary>
    public bool IgnoreConflicts { get; set; }

    /// <summary>Раздельное туннелирование; null — выключено. Служба проверяет правила сама.</summary>
    public SplitTunnelOptions? SplitTunnel { get; set; }

    /// <summary>У серверов с конфигом из подписки — их маршруты и DNS (false — всё через сервер).</summary>
    public bool UseServerRouting { get; set; } = true;

    /// <summary>Профиль маршрутизации для серверов по ссылкам; null — всё через VPN. Служба проверяет его сама.</summary>
    public RoutingProfile? Routing { get; set; }
}

public sealed record IpcRequest
{
    public required string Command { get; init; }

    /// <summary>Ссылка сервера (vless:// или hy2://). Служба разбирает её сама и строит конфиги сама.</summary>
    public string? Link { get; init; }

    /// <summary>Вместо ссылки — полный конфиг сервера из подписки (JSON). Служба очищает его сама.</summary>
    public string? Config { get; init; }
    public ConnectOptions? Options { get; init; }

    /// <summary>
    /// Для connect и resume: служба запоминает подключение (файл читает только SYSTEM) и сама поднимает его после
    /// перезагрузки, ещё до входа пользователя. Тестовые подключения не запоминаются.
    /// </summary>
    public bool Remember { get; init; }

    /// <summary>Для logs: вернуть строки с номером больше этого.</summary>
    public long LogsAfter { get; init; }

    /// <summary>Для cores: см. <see cref="CoreActions"/>.</summary>
    public string? CoreAction { get; init; }

    /// <summary>Для geo-update: источник баз (ключ из GeoUpdater.Sources).</summary>
    public string? GeoSource { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Error,
}

public sealed record ServiceStatus
{
    public SessionState State { get; init; }
    public string? ProfileName { get; init; }
    public string? Protocol { get; init; }
    public string? Server { get; init; }

    /// <summary>Понятное пользователю описание текущего состояния или ошибки.</summary>
    public string? Message { get; init; }
    public DateTimeOffset? Since { get; init; }
    public string? PhysicalInterface { get; init; }
    public string? PublicIp { get; init; }

    /// <summary>Проверка внешнего IP после подключения закончилась (PublicIp null — не удалось узнать).</summary>
    public bool PublicIpChecked { get; init; }
    public int? DelayMs { get; init; }
    public bool KillSwitchActive { get; init; }

    /// <summary>Порт статистики xray службы на 127.0.0.1 (трафик через VPN); null — не подключено.</summary>
    public int? MetricsPort { get; init; }
    public DateTimeOffset? TestDeadline { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string ServiceVersion { get; init; } = "";
}

public sealed record LogLine(long Seq, DateTimeOffset Time, string Source, string Text);

public sealed record IpcResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public ServiceStatus? Status { get; init; }
    public IReadOnlyList<LogLine>? Logs { get; init; }
    public CoreStatus? Cores { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(SplitTunnelOptions))]
[JsonSerializable(typeof(BypassOptions))]
[JsonSerializable(typeof(DnsOptions))]
public partial class IpcJsonContext : JsonSerializerContext;
