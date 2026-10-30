using System.Net;

namespace PVpn.Core.Net;

public enum InternetState
{
    /// <summary>Интернет есть.</summary>
    Online,

    /// <summary>Сеть подменяет ответы: нужна страница входа (гостиница, кафе, вокзал).</summary>
    Portal,

    /// <summary>Нет ответа: интернета нет или он закрыт (например, kill switch).</summary>
    Offline,
}

/// <summary>
/// Проверка «есть ли интернет или сеть просит войти» — та же, что у Windows (NCSI): по HTTP без шифрования
/// запрашивается файл с известным содержимым. Страница входа отвечает перенаправлением или своей страницей.
/// </summary>
public static class CaptivePortal
{
    public const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string Expected = "Microsoft Connect Test";

    /// <summary>Адрес, который Windows открывает для входа: сеть с порталом перенаправит с него на свою страницу.</summary>
    public const string LoginUrl = "http://www.msftconnecttest.com/redirect";

    /// <summary>Клиент без прокси и без перехода по перенаправлениям (перенаправление и есть признак портала).</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<InternetState> CheckAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(ProbeUrl, ct).ConfigureAwait(false);
            var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
            return Classify(response.StatusCode, body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return InternetState.Offline;
        }
    }

    internal static InternetState Classify(HttpStatusCode code, string? body) =>
        code == HttpStatusCode.OK && body?.Trim() == Expected ? InternetState.Online
        : (int)code is >= 200 and < 400 ? InternetState.Portal
        // 511 Network Authentication Required — так портал и должен отвечать по стандарту.
        : code == HttpStatusCode.NetworkAuthenticationRequired ? InternetState.Portal
        : InternetState.Offline;
}

public enum AutoConnectAction
{
    None,
    Connect,
    Disconnect,
}

/// <summary>
/// Автоподключение и доверенные сети. Решение принимается при запуске и при каждой смене сети:
/// в доверенной сети (дом, работа) VPN не нужен, в любой другой — подключаемся сами.
/// </summary>
public static class AutoConnectPolicy
{
    /// <param name="networks">Подключённые сети сейчас (<see cref="NetworkNames.GetConnected"/>).</param>
    /// <param name="vpnActive">VPN включён или подключается.</param>
    /// <param name="userOverride">Пользователь сам включил или выключил VPN в этих же сетях — не спорим с ним.</param>
    public static AutoConnectAction Decide(IReadOnlyList<string> networks, IReadOnlyCollection<string> trusted, bool autoConnect,
        bool disconnectOnTrusted, bool vpnActive, bool userOverride)
    {
        if (userOverride || networks.Count == 0)
            return AutoConnectAction.None;
        var onTrusted = networks.Any(n => trusted.Contains(n, StringComparer.OrdinalIgnoreCase));
        if (onTrusted)
            return disconnectOnTrusted && vpnActive ? AutoConnectAction.Disconnect : AutoConnectAction.None;
        return autoConnect && !vpnActive ? AutoConnectAction.Connect : AutoConnectAction.None;
    }

    /// <summary>Ключ набора сетей: «та же сеть» для отмены автоматики после ручного действия.</summary>
    public static string Key(IReadOnlyList<string> networks) =>
        string.Join("|", networks.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal));
}
