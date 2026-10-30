using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace PVpn.Core.Profiles;

/// <summary>
/// Подписка: ссылка, по которой панель (Remnawave, 3x-ui, Marzban…) отдаёт список серверов. Хранится вместе
/// с профилями в зашифрованном profiles.dat — в ссылке личный токен.
/// </summary>
public sealed record Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Url { get; init; }

    /// <summary>Обновлять автоматически (раз в <see cref="UpdateIntervalHours"/>).</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>Как часто обновлять; панель может подсказать заголовком profile-update-interval.</summary>
    public int UpdateIntervalHours { get; init; } = 12;

    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>Последняя попытка (удачная или нет) — чтобы при ошибке не стучаться каждую минуту.</summary>
    public DateTimeOffset? LastAttempt { get; init; }
    public string? LastError { get; init; }

    /// <summary>Трафик и срок из заголовка subscription-userinfo (байты; 0 — без ограничения).</summary>
    public long Upload { get; init; }
    public long Download { get; init; }
    public long Total { get; init; }
    public DateTimeOffset? Expire { get; init; }

    public bool IsDue(DateTimeOffset now) =>
        AutoUpdate && (LastAttempt ?? DateTimeOffset.MinValue) < now - (LastError == null ? TimeSpan.FromHours(UpdateIntervalHours) : TimeSpan.FromMinutes(30));
}

/// <summary>Что вернула панель.</summary>
public sealed record SubscriptionFetch(
    ImportResult Result,
    int Unsupported,
    string? Title,
    int? UpdateIntervalHours,
    long Upload,
    long Download,
    long Total,
    DateTimeOffset? Expire);

/// <summary>Итог обновления подписки для сообщения пользователю.</summary>
public sealed record SubscriptionUpdate(Subscription Subscription, int Added, int Updated, int Removed, int Unsupported, IReadOnlyList<ImportError> Errors)
{
    public override string ToString()
    {
        var sb = new StringBuilder($"«{Subscription.Name}»: серверов новых {Added}, обновлено {Updated}, удалено {Removed}");
        if (Unsupported > 0)
            sb.Append($"; пропущено ссылок других протоколов (vmess, trojan, ss…): {Unsupported}");
        if (Errors.Count > 0)
            sb.Append($"; ошибок в ссылках: {Errors.Count} (первая: {Errors[0].Message})");
        return sb.Append('.').ToString();
    }
}

public sealed class SubscriptionException(string message) : Exception(message);

public static partial class SubscriptionClient
{
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Проверяет адрес подписки: только http(s), без логина в адресе. Возвращает нормализованный адрес.
    /// </summary>
    public static string ValidateUrl(string? url)
    {
        var s = url?.Trim() ?? "";
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new SubscriptionException("Нужна ссылка на подписку вида https://…");
        if (uri.UserInfo.Length > 0)
            throw new SubscriptionException("Логин и пароль в адресе подписки не поддерживаются");
        return uri.AbsoluteUri;
    }

    /// <summary>Клиент для подписок. Панели отдают обычный список ссылок VPN-клиентам, а браузеру — страницу.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.10");
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({Product.Id})"));
        return http;
    }

    public static async Task<SubscriptionFetch> FetchAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        url = ValidateUrl(url);
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new SubscriptionException($"Не удалось получить подписку: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SubscriptionException("Панель подписки не ответила за 30 секунд");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new SubscriptionException($"Панель подписки ответила {(int)response.StatusCode} {response.ReasonPhrase}");
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new SubscriptionException($"Подписка больше {MaxBytes / 1024 / 1024} МБ — это не похоже на список серверов");
            }
            return Parse(body, name => response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v) ? v.FirstOrDefault() : null);
        }
    }

    /// <summary>Разбирает ответ панели; <paramref name="header"/> — значение заголовка по имени или null.</summary>
    public static SubscriptionFetch Parse(string body, Func<string, string?> header)
    {
        if (body.TrimStart().StartsWith('<'))
            throw new SubscriptionException("По ссылке открылась веб-страница, а не список серверов. Скопируйте в панели именно ссылку подписки.");
        // Обычно подписка — base64 от списка ссылок, реже — сам список.
        var text = !body.Contains("://", StringComparison.Ordinal) && LinkParser.TryDecodeBase64(body.Trim(), out var d) ? d : body;
        var result = LinkParser.ParseMany(text);
        var unsupported = OtherLinkRegex().Matches(text).Count;
        if (result.Profiles.Count == 0 && result.Errors.Count == 0)
        {
            throw new SubscriptionException(unsupported > 0
                ? $"В подписке нет серверов VLESS и Hysteria2 (есть только другие протоколы: {unsupported})"
                : "В подписке не нашлось ссылок на серверы");
        }

        var info = ParseUserInfo(header("subscription-userinfo"));
        var interval = int.TryParse(header("profile-update-interval")?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 1 and <= 168 ? h : (int?)null;
        return new SubscriptionFetch(result, unsupported, DecodeTitle(header("profile-title")), interval, info.Upload, info.Download, info.Total, info.Expire);
    }

    /// <summary>«upload=1; download=2; total=3; expire=1700000000» (expire=0 или нет — бессрочно).</summary>
    public static (long Upload, long Download, long Total, DateTimeOffset? Expire) ParseUserInfo(string? value)
    {
        long upload = 0, download = 0, total = 0;
        DateTimeOffset? expire = null;
        foreach (var part in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0 || n > long.MaxValue)
                continue;
            switch (kv[0].ToLowerInvariant())
            {
                case "upload": upload = (long)n; break;
                case "download": download = (long)n; break;
                case "total": total = (long)n; break;
                case "expire" when n is > 0 and < 253402300800: expire = DateTimeOffset.FromUnixTimeSeconds((long)n); break;
            }
        }
        return (upload, download, total, expire);
    }

    /// <summary>profile-title: обычный текст или «base64:…».</summary>
    internal static string? DecodeTitle(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        if (s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            s = LinkParser.TryDecodeBase64(s[7..], out var decoded) ? decoded.Trim() : null;
        return string.IsNullOrEmpty(s) ? null : s.Length > 60 ? s[..60] : s;
    }

    /// <summary>Ссылки протоколов, которые PVpn не поддерживает.</summary>
    [GeneratedRegex(@"(?<![a-z0-9])(?:vmess|trojan|ss|ssr|tuic|wireguard|wg|socks)://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLinkRegex();
}

public static class SubscriptionMerge
{
    /// <summary>
    /// Заменяет серверы подписки новым списком. Сервер с тем же именем сохраняет Id (выбор в списке не теряется);
    /// пропавшие из подписки удаляются, ручные серверы не трогаются. Повторы имён внутри подписки получают « (2)».
    /// </summary>
    public static SubscriptionUpdate Apply(this ProfileData data, Subscription subscription, SubscriptionFetch fetch, DateTimeOffset now)
    {
        var old = data.Profiles.Where(p => p.SubscriptionId == subscription.Id).ToList();
        var byName = old.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<Profile>();
        int added = 0, updated = 0;
        foreach (var p in fetch.Result.Profiles)
        {
            var name = p.Name;
            for (var i = 2; !seen.Add(name); i++)
                name = $"{p.Name} ({i})";
            if (byName.TryGetValue(name, out var existing))
            {
                fresh.Add(p with { Id = existing.Id, Name = name, SubscriptionId = subscription.Id });
                updated++;
            }
            else
            {
                fresh.Add(p with { Name = name, SubscriptionId = subscription.Id });
                added++;
            }
        }

        // Новые серверы встают на место старых (порядок списка у пользователя не прыгает).
        var index = data.Profiles.FindIndex(p => p.SubscriptionId == subscription.Id);
        data.Profiles.RemoveAll(p => p.SubscriptionId == subscription.Id);
        data.Profiles.InsertRange(index < 0 ? data.Profiles.Count : index, fresh);
        if (data.SelectedId is { } selected && old.Any(p => p.Id == selected) && fresh.All(p => p.Id != selected))
            data.SelectedId = null;

        var updatedSub = subscription with
        {
            UpdateIntervalHours = fetch.UpdateIntervalHours ?? subscription.UpdateIntervalHours,
            LastUpdated = now,
            LastAttempt = now,
            LastError = null,
            Upload = fetch.Upload,
            Download = fetch.Download,
            Total = fetch.Total,
            Expire = fetch.Expire,
        };
        data.SetSubscription(updatedSub);
        var removed = old.Count(o => fresh.All(f => f.Id != o.Id));
        return new SubscriptionUpdate(updatedSub, added, updated, removed, fetch.Unsupported, fetch.Result.Errors);
    }

    public static void SetSubscription(this ProfileData data, Subscription subscription)
    {
        var i = data.Subscriptions.FindIndex(s => s.Id == subscription.Id);
        if (i < 0)
            data.Subscriptions.Add(subscription);
        else
            data.Subscriptions[i] = subscription;
    }

    /// <summary>Удаляет подписку вместе с её серверами.</summary>
    public static void RemoveSubscription(this ProfileData data, Guid id)
    {
        var ids = data.Profiles.Where(p => p.SubscriptionId == id).Select(p => p.Id).ToHashSet();
        data.Profiles.RemoveAll(p => ids.Contains(p.Id));
        data.Subscriptions.RemoveAll(s => s.Id == id);
        if (data.SelectedId is { } s && ids.Contains(s))
            data.SelectedId = null;
    }

    public static Subscription? FindSubscription(this ProfileData data, string nameOrId) =>
        data.Subscriptions.FirstOrDefault(s => s.Id.ToString() == nameOrId)
        ?? data.Subscriptions.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Название новой подписки: из profile-title или по адресу панели.</summary>
    public static string DefaultName(ProfileData data, string url, string? title)
    {
        var name = title ?? new Uri(url).Host;
        var result = name;
        for (var i = 2; data.Subscriptions.Any(s => string.Equals(s.Name, result, StringComparison.OrdinalIgnoreCase)); i++)
            result = $"{name} ({i})";
        return result;
    }
}

/// <summary>Добавление и обновление подписок поверх хранилища (общая часть CLI и интерфейса).</summary>
public static class SubscriptionService
{
    public static async Task<SubscriptionUpdate> AddAsync(ProfileData data, HttpClient http, string url, string? name, CancellationToken ct = default)
    {
        url = SubscriptionClient.ValidateUrl(url);
        if (data.Subscriptions.Any(s => s.Url == url))
            throw new SubscriptionException("Эта подписка уже добавлена");
        var fetch = await SubscriptionClient.FetchAsync(http, url, ct).ConfigureAwait(false);
        var sub = new Subscription { Name = string.IsNullOrWhiteSpace(name) ? SubscriptionMerge.DefaultName(data, url, fetch.Title) : name.Trim(), Url = url };
        return data.Apply(sub, fetch, DateTimeOffset.Now);
    }

    /// <summary>Обновляет подписку; при ошибке запоминает её в подписке (серверы остаются прежними) и бросает исключение.</summary>
    public static async Task<SubscriptionUpdate> UpdateAsync(ProfileData data, HttpClient http, Subscription sub, CancellationToken ct = default)
    {
        try
        {
            var fetch = await SubscriptionClient.FetchAsync(http, sub.Url, ct).ConfigureAwait(false);
            return data.Apply(sub, fetch, DateTimeOffset.Now);
        }
        catch (SubscriptionException e)
        {
            data.SetSubscription(sub with { LastAttempt = DateTimeOffset.Now, LastError = e.Message });
            throw;
        }
    }
}
