using System.Text.Json.Serialization;

namespace PVpn.Core.Profiles;

/// <summary>Сервер, импортированный из ссылки. Хранится зашифрованным (см. ProfileStore).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(VlessProfile), "vless")]
[JsonDerivedType(typeof(Hysteria2Profile), "hysteria2")]
[JsonDerivedType(typeof(XrayConfigProfile), "xray-config")]
[JsonDerivedType(typeof(TrojanProfile), "trojan")]
[JsonDerivedType(typeof(VmessProfile), "vmess")]
[JsonDerivedType(typeof(ShadowsocksProfile), "shadowsocks")]
public abstract record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>Исходная ссылка: по ней профиль можно переимпортировать или экспортировать.</summary>
    public required string SourceLink { get; init; }

    /// <summary>Из какой подписки сервер; null — добавлен вручную.</summary>
    public Guid? SubscriptionId { get; init; }

    /// <summary>Короткое описание протокола для интерфейса, например «VLESS XHTTP REALITY».</summary>
    [JsonIgnore]
    public abstract string ProtocolLabel { get; }

    /// <summary>Транспорт до сервера: от него зависят пинг и правила kill switch.</summary>
    [JsonIgnore]
    public abstract ServerTransport Transport { get; }

    /// <summary>Строки, которые нельзя показывать в логах.</summary>
    [JsonIgnore]
    public abstract IReadOnlyList<string> Secrets { get; }
}

public enum ServerTransport { Tcp, Udp }

/// <summary>
/// Сервер поверх TCP-транспорта xray (VLESS, Trojan, VMess): сеть, защита (TLS/REALITY) и параметры транспорта.
/// Поля общие, чтобы соединение строилось одним кодом; имена полей XHTTP сохранены ради уже сохранённых профилей.
/// </summary>
public abstract record StreamProfile : Profile
{
    /// <summary>raw (он же tcp), xhttp, ws, grpc или httpupgrade.</summary>
    public string Network { get; init; } = "raw";

    /// <summary>reality, tls или none.</summary>
    public string Security { get; init; } = "none";

    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? Alpn { get; init; }

    /// <summary>SHA-256 сертификата сервера (pcs в ссылке): вместо проверки цепочки — сверка с ним (самоподписанные сертификаты).</summary>
    public string? PinSha256 { get; init; }

    /// <summary>Имя, по которому проверять сертификат, если оно не совпадает с SNI (vcn в ссылке).</summary>
    public string? VerifyName { get; init; }

    // REALITY
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? SpiderX { get; init; }

    // XHTTP
    public string? XhttpPath { get; init; }
    public string? XhttpHost { get; init; }
    public string? XhttpMode { get; init; }

    /// <summary>Параметр extra из ссылки (JSON с дополнительными настройками XHTTP).</summary>
    public string? XhttpExtra { get; init; }

    // WebSocket и HTTPUpgrade
    public string? Path { get; init; }
    public string? HostHeader { get; init; }

    // gRPC
    public string? ServiceName { get; init; }

    /// <summary>gRPC в режиме multi (mode=multi в ссылке).</summary>
    public bool GrpcMulti { get; init; }

    public override ServerTransport Transport => ServerTransport.Tcp;

    /// <summary>«XHTTP REALITY», «WS TLS», «gRPC TLS» — вторая половина подписи протокола.</summary>
    protected string StreamLabel(string? overrideTransport = null)
    {
        var transport = overrideTransport ?? Network switch
        {
            "xhttp" => "XHTTP",
            "ws" => "WS",
            "grpc" => "gRPC",
            "httpupgrade" => "HTTPUpgrade",
            _ => "TCP",
        };
        var security = Security switch { "reality" => "REALITY", "tls" => "TLS", _ => "" };
        return $"{transport} {security}".TrimEnd();
    }

    /// <summary>Копия с полями транспорта (тип записи сохраняется).</summary>
    internal StreamProfile WithStream(StreamFields s) => this with
    {
        Network = s.Network, Security = s.Security, Sni = s.Sni, Fingerprint = s.Fingerprint, Alpn = s.Alpn,
        PinSha256 = s.PinSha256, VerifyName = s.VerifyName, PublicKey = s.PublicKey, ShortId = s.ShortId, SpiderX = s.SpiderX,
        XhttpPath = s.XhttpPath, XhttpHost = s.XhttpHost, XhttpMode = s.XhttpMode, XhttpExtra = s.XhttpExtra,
        Path = s.Path, HostHeader = s.HostHeader, ServiceName = s.ServiceName, GrpcMulti = s.GrpcMulti,
    };
}

/// <summary>Поля транспорта и защиты из ссылки (общая часть VLESS, Trojan и VMess).</summary>
internal sealed record StreamFields
{
    public string Network { get; init; } = "raw";
    public string Security { get; init; } = "none";
    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? Alpn { get; init; }
    public string? PinSha256 { get; init; }
    public string? VerifyName { get; init; }
    public string? PublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? SpiderX { get; init; }
    public string? XhttpPath { get; init; }
    public string? XhttpHost { get; init; }
    public string? XhttpMode { get; init; }
    public string? XhttpExtra { get; init; }
    public string? Path { get; init; }
    public string? HostHeader { get; init; }
    public string? ServiceName { get; init; }
    public bool GrpcMulti { get; init; }
}

public sealed record VlessProfile : StreamProfile
{
    public required string Uuid { get; init; }
    public string Encryption { get; init; } = "none";
    public string? Flow { get; init; }

    public override string ProtocolLabel => "VLESS " + StreamLabel(Network == "raw" && Flow == "xtls-rprx-vision" ? "Vision" : null);

    public override IReadOnlyList<string> Secrets => [Uuid];
}

public sealed record TrojanProfile : StreamProfile
{
    public required string Password { get; init; }

    public override string ProtocolLabel => "Trojan " + StreamLabel();

    public override IReadOnlyList<string> Secrets => [Password];
}

public sealed record VmessProfile : StreamProfile
{
    public required string Uuid { get; init; }

    /// <summary>Шифрование VMess (security в xray): auto, aes-128-gcm, chacha20-poly1305, none, zero.</summary>
    public string Cipher { get; init; } = "auto";

    public override string ProtocolLabel => "VMess " + StreamLabel();

    public override IReadOnlyList<string> Secrets => [Uuid];
}

public sealed record ShadowsocksProfile : Profile
{
    /// <summary>Метод шифрования, например 2022-blake3-aes-128-gcm или chacha20-ietf-poly1305.</summary>
    public required string Method { get; init; }
    public required string Password { get; init; }

    public override string ProtocolLabel => "Shadowsocks";

    public override ServerTransport Transport => ServerTransport.Tcp;

    public override IReadOnlyList<string> Secrets => [Password];
}

public sealed record Hysteria2Profile : Profile
{
    /// <summary>Строка авторизации: пароль или «логин:пароль» для userpass на сервере.</summary>
    public required string Auth { get; init; }
    public string? Sni { get; init; }

    /// <summary>SHA-256 сертификата сервера (hex, двоеточия допустимы).</summary>
    public string? PinSha256 { get; init; }

    /// <summary>insecure=1 из ссылки. Сам по себе не используется: Xray требует pin.</summary>
    public bool Insecure { get; init; }
    public string? Alpn { get; init; }

    /// <summary>
    /// Порты для смены (port hopping), например «20000-30000» или «443,20000-30000»; null — один порт.
    /// Берутся из адреса (host:20000-30000) или параметра mport. Сервер должен слушать весь диапазон.
    /// </summary>
    public string? HopPorts { get; init; }

    /// <summary>
    /// Пароль обфускации salamander (obfs=salamander&amp;obfs-password=… в ссылке); null — без обфускации.
    /// Пакеты QUIC шифруются этим паролем, и фильтр провайдера не узнаёт в них Hysteria2.
    /// </summary>
    public string? ObfsPassword { get; init; }

    public override string ProtocolLabel => (HopPorts, ObfsPassword) switch
    {
        (null, null) => "Hysteria2",
        (null, _) => "Hysteria2 (salamander)",
        (_, null) => "Hysteria2 (смена портов)",
        _ => "Hysteria2 (salamander, смена портов)",
    };

    public override ServerTransport Transport => ServerTransport.Udp;

    public override IReadOnlyList<string> Secrets => ObfsPassword == null ? [Auth] : [Auth, ObfsPassword];
}

/// <summary>
/// Сервер из подписки в виде полного конфига xray: со своими маршрутами, DNS, фрагментацией, автовыбором и цепочками.
/// Хранится уже очищенным (<see cref="PVpn.Core.Config.ServerConfigs.Sanitize"/>); служба очищает его ещё раз сама.
/// SourceLink пустой: ссылки у такого сервера нет.
/// </summary>
public sealed record XrayConfigProfile : Profile
{
    /// <summary>Конфиг xray (JSON): outbounds, routing, dns, observatory; входы и журнал подставляет клиент.</summary>
    public required string Config { get; init; }

    /// <summary>Протокол основного сервера, например «VLESS XHTTP REALITY».</summary>
    public required string MainProtocol { get; init; }

    /// <summary>Сколько серверов в автовыборе или цепочке (1 — один сервер).</summary>
    public int ServerCount { get; init; } = 1;

    /// <summary>Основной сервер работает по UDP (Hysteria2).</summary>
    public bool Udp { get; init; }

    /// <summary>Описание от панели (meta.serverDescription), например «без списания пакета».</summary>
    public string? Description { get; init; }

    public override string ProtocolLabel => ServerCount > 1 ? $"{MainProtocol} · автовыбор из {ServerCount}" : MainProtocol;

    public override ServerTransport Transport => Udp ? ServerTransport.Udp : ServerTransport.Tcp;

    public override IReadOnlyList<string> Secrets => PVpn.Core.Config.ServerConfigs.SecretsOf(Config);
}
