using System.Text.Json.Serialization;

namespace PVpn.Core.Profiles;

/// <summary>Сервер, импортированный из ссылки. Хранится зашифрованным (см. ProfileStore).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(VlessProfile), "vless")]
[JsonDerivedType(typeof(Hysteria2Profile), "hysteria2")]
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

public sealed record VlessProfile : Profile
{
    public required string Uuid { get; init; }
    public string Encryption { get; init; } = "none";
    public string? Flow { get; init; }

    /// <summary>"raw" (он же tcp) или "xhttp".</summary>
    public string Network { get; init; } = "raw";

    /// <summary>"reality", "tls" или "none".</summary>
    public string Security { get; init; } = "reality";

    public string? Sni { get; init; }
    public string? Fingerprint { get; init; }
    public string? Alpn { get; init; }

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

    public override string ProtocolLabel
    {
        get
        {
            var transport = Network == "xhttp" ? "XHTTP" : Flow == "xtls-rprx-vision" ? "Vision" : "TCP";
            var security = Security switch { "reality" => "REALITY", "tls" => "TLS", _ => "" };
            return $"VLESS {transport} {security}".TrimEnd();
        }
    }

    public override ServerTransport Transport => ServerTransport.Tcp;

    public override IReadOnlyList<string> Secrets => [Uuid];
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
