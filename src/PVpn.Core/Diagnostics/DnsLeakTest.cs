using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PVpn.Core.Diagnostics;

public sealed record DnsLeakEntry(string Ip, string Country, string Org);

/// <param name="Leak">true — есть посторонние DNS, false — только ожидаемые, null — сравнить не с чем (смотреть список).</param>
public sealed record DnsLeakResult(IReadOnlyList<DnsLeakEntry> Exit, IReadOnlyList<DnsLeakEntry> Resolvers, bool? Leak, string Verdict);

/// <summary>
/// Проверка утечки DNS через bash.ws (как dnsleaktest.com): программа обращается к случайным поддоменам,
/// их DNS-сервер записывает, какие резолверы спрашивали адрес, и отдаёт список с сетью и страной.
/// Запросы идут так же, как у браузера: в TUN — через DNS системы, в режиме прокси — через системный прокси.
/// </summary>
public static class DnsLeakTest
{
    private const string Api = "https://bash.ws";
    private const int Probes = 10;

    /// <summary>
    /// Откуда брать номер проверки, по порядку. С переводом сайта на языки (/ru/, /id/…) адрес /id стал
    /// индонезийской страницей сайта (31.10.2026); /id.php отдаёт номер по-прежнему. /id — на случай, если починят.
    /// </summary>
    private static readonly string[] IdPaths = ["/id.php", "/id"];

    /// <summary>
    /// Клиент для проверки: только IPv4 (туннель тоже только IPv4, а сломанный у провайдера IPv6 без перехода на
    /// IPv4, как у браузеров, вешал бы проверку на 20 с). <paramref name="proxy"/> — HTTP-вход режима прокси.
    /// </summary>
    public static HttpClient CreateClient(Uri? proxy = null) => new(new SocketsHttpHandler
    {
        UseProxy = proxy != null,
        Proxy = proxy == null ? null : new WebProxy(proxy),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <param name="expectedOrgs">Слова в названии сети «своих» DNS (см. <c>DnsRules.OrgKeywords</c>); null — не проверять.</param>
    public static async Task<DnsLeakResult> RunAsync(HttpClient http, IReadOnlyList<string>? expectedOrgs, CancellationToken ct = default)
    {
        var id = await GetIdAsync(http, ct).ConfigureAwait(false);

        // Ответ поддоменов не важен: важно, что их адрес кто-то разрешил.
        await Task.WhenAll(Enumerable.Range(1, Probes).Select(async i =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var _ = await http.GetAsync($"http://{i}.{id}.bash.ws/", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
            }
        })).ConfigureAwait(false);

        var json = await http.GetStringAsync($"{Api}/dnsleak/test/{id}?json", ct).ConfigureAwait(false);
        return Evaluate(JsonSerializer.Deserialize(json, DnsLeakJsonContext.Default.ListBashWsEntry) ?? [], expectedOrgs);
    }

    private static async Task<string> GetIdAsync(HttpClient http, CancellationToken ct)
    {
        string? error = null;
        foreach (var path in IdPaths)
        {
            try
            {
                using var response = await http.GetAsync(Api + path, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && ParseId(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is { } id)
                    return id;
                error = $"{path}: HTTP {(int)response.StatusCode}, не номер проверки";
            }
            catch (HttpRequestException e)
            {
                error = $"{path}: {e.Message}";
            }
        }
        throw new InvalidOperationException($"Сервис проверки DNS не выдал номер проверки ({error})");
    }

    /// <summary>Номер проверки — короткая строка из латиницы и цифр; всё остальное (например, страница сайта) — null.</summary>
    internal static string? ParseId(string body)
    {
        var id = body.Trim();
        return id.Length is 0 or > 64 || !id.All(char.IsAsciiLetterOrDigit) ? null : id;
    }

    internal static DnsLeakResult Evaluate(IReadOnlyList<BashWsEntry> entries, IReadOnlyList<string>? expectedOrgs)
    {
        static DnsLeakEntry Map(BashWsEntry e) =>
            new(e.Ip ?? "", string.IsNullOrEmpty(e.CountryName) ? e.Country ?? "" : e.CountryName, string.IsNullOrEmpty(e.Asn) ? e.Org ?? "" : e.Asn);

        var exit = entries.Where(e => e.Type == "ip").Select(Map).ToList();
        var resolvers = entries.Where(e => e.Type == "dns").Select(Map).DistinctBy(e => e.Ip).ToList();
        if (resolvers.Count == 0)
            return new(exit, resolvers, null, "Не удалось определить DNS-серверы: сервис проверки их не увидел. Попробуйте ещё раз.");
        if (expectedOrgs == null)
            return new(exit, resolvers, null, "Сравните список с ожидаемым: DNS вашего интернет-провайдера в нём быть не должно.");

        var foreign = resolvers.Where(r => !expectedOrgs.Any(k => r.Org.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
        return foreign.Count == 0
            ? new(exit, resolvers, false, "Утечки нет: адреса сайтов разрешает только выбранный DNS.")
            : new(exit, resolvers, true, $"Возможна утечка: запросы видят посторонние DNS — {string.Join(", ", foreign.Select(f => $"{f.Ip} ({f.Org})"))}.");
    }
}

internal sealed record BashWsEntry(
    [property: JsonPropertyName("ip")] string? Ip,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("country_name")] string? CountryName,
    [property: JsonPropertyName("asn")] string? Asn,
    [property: JsonPropertyName("org")] string? Org,
    [property: JsonPropertyName("type")] string? Type);

[JsonSerializable(typeof(List<BashWsEntry>))]
internal partial class DnsLeakJsonContext : JsonSerializerContext;
