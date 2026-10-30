using System.Globalization;
using System.Text.Json;

namespace PVpn.Core.Diagnostics;

/// <summary>Сколько байт ушло и пришло (с начала работы источника).</summary>
public readonly record struct TrafficSample(long Up, long Down);

/// <summary>
/// Счётчики трафика через VPN — статистика исходящего «proxy» у xray (в TUN — у xray службы, в режиме прокси — у своего).
/// Счётчики адаптера wintun не подходят: Windows считает в них каждый пакет в обе стороны.
/// </summary>
public static class TrafficSources
{
    /// <summary>
    /// Статистика xray через metrics (HTTP /debug/vars на 127.0.0.1): трафик исходящего <paramref name="outbound"/>.
    /// </summary>
    public static async Task<TrafficSample?> ReadXrayAsync(HttpClient http, int metricsPort, string outbound, CancellationToken ct = default)
    {
        try
        {
            await using var stream = await http.GetStreamAsync($"http://127.0.0.1:{metricsPort}/debug/vars", ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseXray(doc.RootElement, outbound);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static TrafficSample? ParseXray(JsonElement root, string outbound) =>
        root.TryGetProperty("stats", out var stats) && stats.TryGetProperty("outbound", out var outbounds)
            ? outbounds.TryGetProperty(outbound, out var o)
                ? new TrafficSample(Long(o, "uplink"), Long(o, "downlink"))
                : new TrafficSample(0, 0)
            : null;

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>Скорость за последний интервал и трафик с начала подключения.</summary>
public sealed class TrafficMeter
{
    private TrafficSample? start;
    private TrafficSample? last;
    private DateTime lastTime;

    public double UpPerSecond { get; private set; }
    public double DownPerSecond { get; private set; }
    public long SessionUp { get; private set; }
    public long SessionDown { get; private set; }

    public void Reset()
    {
        start = last = null;
        UpPerSecond = DownPerSecond = 0;
        SessionUp = SessionDown = 0;
    }

    public void Add(TrafficSample sample, DateTime now)
    {
        // Источник перезапустился (переподключение, новый xray): счётчики начались заново.
        if (last is { } prev && (sample.Up < prev.Up || sample.Down < prev.Down))
        {
            start = new TrafficSample(sample.Up - SessionUp, sample.Down - SessionDown);
            last = null;
        }
        start ??= sample;
        if (last is { } p && now > lastTime)
        {
            var seconds = (now - lastTime).TotalSeconds;
            UpPerSecond = (sample.Up - p.Up) / seconds;
            DownPerSecond = (sample.Down - p.Down) / seconds;
        }
        last = sample;
        lastTime = now;
        SessionUp = sample.Up - start.Value.Up;
        SessionDown = sample.Down - start.Value.Down;
    }

    public static string FormatBytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} Б",
        < 1024 * 1024 => (bytes / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " КБ",
        < 1024L * 1024 * 1024 => (bytes / 1024 / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " МБ",
        _ => (bytes / 1024 / 1024 / 1024).ToString("0.##", CultureInfo.CurrentCulture) + " ГБ",
    };

    /// <summary>В битах, как скорость интернета у провайдеров: «12,4 Мбит/с».</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        return bits switch
        {
            < 1000 => $"{bits:0} бит/с",
            < 1_000_000 => (bits / 1000).ToString("0", CultureInfo.CurrentCulture) + " Кбит/с",
            _ => (bits / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + " Мбит/с",
        };
    }
}
