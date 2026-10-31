using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PVpn.Core.Cores;

/// <summary>Источник баз geoip/geosite: релизы на GitHub, рядом с каждым файлом — его SHA256.</summary>
public sealed record GeoSource(string Key, string Title, string GeoipUrl, string GeositeUrl);

/// <summary>
/// Обновление баз geoip.dat и geosite.dat рядом с xray (по ним работают правила geoip:/geosite: в маршрутах).
/// Скачивание — только с GitHub по HTTPS; SHA256 сверяется с опубликованным рядом файлом (.sha256sum); перед заменой
/// xray должен принять конфиги с правилами на новых базах (иначе остаются прежние). Прежние базы — в cores-previous.
/// </summary>
public sealed partial class GeoUpdater(CorePaths cores, HttpClient http, Action<string>? log = null)
{
    public const long MaxFileBytes = 160L * 1024 * 1024;  // geosite runetfreedom — около 70 МБ

    public static readonly IReadOnlyList<GeoSource> Sources =
    [
        // Те же базы, что в релизе xray: v2fly (domain-list-community собирается в dlc.dat).
        new("v2fly", "v2fly (как в xray)",
            "https://github.com/v2fly/geoip/releases/latest/download/geoip.dat",
            "https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"),
        // Для России: всё из v2fly плюс списки заблокированного в РФ (geosite:ru-blocked, geoip:ru-blocked и др.).
        new("runetfreedom", "Россия (runetfreedom)",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat",
            "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat"),
    ];

    public static GeoSource SourceOf(string? key) => Sources.FirstOrDefault(s => s.Key == key) ?? Sources[0];

    /// <summary>Когда базы последний раз заменялись (время файла geosite.dat); null — баз нет.</summary>
    public static DateTimeOffset? InstalledAt(CorePaths cores)
    {
        var file = Path.Combine(cores.Directory, "geosite.dat");
        return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
    }

    /// <summary>Источник установленных баз (пишется при обновлении); null — из комплекта xray.</summary>
    public static string? InstalledSource(CorePaths cores)
    {
        var file = Path.Combine(cores.Directory, "geo-source.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <param name="exclusive">Замена файлов под блокировкой подключений (как у ядер).</param>
    public async Task<string> UpdateAsync(GeoSource source, Func<Func<Task>, Task>? exclusive = null, CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetDirectoryName(cores.Directory.TrimEnd('\\'))!, $"geo-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            log?.Invoke($"Базы geoip/geosite: скачиваю ({source.Title})");
            await DownloadVerifiedAsync(new Uri(source.GeoipUrl), Path.Combine(staging, "geoip.dat"), ct).ConfigureAwait(false);
            await DownloadVerifiedAsync(new Uri(source.GeositeUrl), Path.Combine(staging, "geosite.dat"), ct).ConfigureAwait(false);

            // Проверка новым базам: тот же xray и конфиги с geosite:category-ru, geoip:ru, рекламой, автовыбором.
            File.Copy(cores.Xray, Path.Combine(staging, Path.GetFileName(cores.Xray)));
            if (await CoreSelfTest.RunAsync(CoreKind.Xray, Path.Combine(staging, Path.GetFileName(cores.Xray)), ct).ConfigureAwait(false) is { } problem)
                throw new CoreUpdateException($"С новыми базами xray не принимает маршруты: {problem}");

            async Task Swap()
            {
                var previous = CoreUpdater.PreviousDirectoryOf(cores);
                Directory.CreateDirectory(previous);
                foreach (var name in CoreUpdater.GeoFiles)
                {
                    var current = Path.Combine(cores.Directory, name);
                    if (File.Exists(current))
                        File.Copy(current, Path.Combine(previous, name), overwrite: true);
                    // xray читает базы при запуске и файл не держит: работающее подключение не мешает.
                    File.Copy(Path.Combine(staging, name), current, overwrite: true);
                }
                await File.WriteAllTextAsync(Path.Combine(cores.Directory, "geo-source.txt"), source.Key, ct).ConfigureAwait(false);
            }
            if (exclusive != null)
                await exclusive(Swap).ConfigureAwait(false);
            else
                await Swap().ConfigureAwait(false);
            var message = $"Базы geoip/geosite обновлены ({source.Title})";
            log?.Invoke(message);
            return message;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Файл и его SHA256 из соседнего «имя.sha256sum» (64 hex-символа в начале).</summary>
    private async Task DownloadVerifiedAsync(Uri url, string target, CancellationToken ct)
    {
        CoreUpdater.CheckHost(url);
        var expected = await GetExpectedHashAsync(new Uri(url.AbsoluteUri + ".sha256sum"), ct).ConfigureAwait(false);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } final)
            CoreUpdater.CheckHost(final);
        if (!response.IsSuccessStatusCode)
            throw new CoreUpdateException($"GitHub ответил {(int)response.StatusCode} при скачивании {Path.GetFileName(url.AbsolutePath)}");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(target))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxFileBytes)
                    throw new CoreUpdateException($"{Path.GetFileName(url.AbsolutePath)} больше {MaxFileBytes / 1024 / 1024} МБ");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (actual != expected)
            throw new CoreUpdateException($"SHA256 {Path.GetFileName(url.AbsolutePath)} не совпадает с опубликованным — файл повреждён или подменён");
    }

    private async Task<string> GetExpectedHashAsync(Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } final)
            CoreUpdater.CheckHost(final);
        if (!response.IsSuccessStatusCode)
            throw new CoreUpdateException($"Нет контрольной суммы {Path.GetFileName(url.AbsolutePath)} (GitHub ответил {(int)response.StatusCode}) — без неё не ставим");
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Sha256Regex().Match(text) is { Success: true } m
            ? m.Value.ToLowerInvariant()
            : throw new CoreUpdateException($"В {Path.GetFileName(url.AbsolutePath)} нет SHA256");
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}", RegexOptions.Multiline)]
    private static partial Regex Sha256Regex();
}
