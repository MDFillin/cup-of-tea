using System.Security.Cryptography;

namespace PVpn.Core.Cores;

/// <summary>
/// Скачивание файла релиза GitHub: только https и только хосты GitHub (в том числе после перенаправлений),
/// ограничение размера и сверка SHA256 с опубликованным. Общее для ядер и обновлений программы.
/// </summary>
internal static class GitHubDownload
{
    private static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static bool IsAllowed(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA256 из поля digest ассета («sha256:…»); null — GitHub его не указал.</summary>
    public static string? Sha256Of(string? digest) =>
        digest is { Length: 7 + 64 } && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..].ToLowerInvariant() : null;

    /// <param name="fail">Исключение с понятным текстом для вызывающего.</param>
    /// <param name="progress">Сколько байт скачано.</param>
    public static async Task DownloadAsync(HttpClient http, Uri url, string sha256, long maxBytes, string path,
        Func<string, Exception> fail, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!IsAllowed(url))
            throw fail($"Ссылка ведёт не на GitHub: {url.Host}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // После перенаправлений адрес тоже должен остаться на GitHub.
        if (response.RequestMessage?.RequestUri is { } final && !IsAllowed(final))
            throw fail($"Ссылка ведёт не на GitHub: {final.Host}");
        if (!response.IsSuccessStatusCode)
            throw fail($"GitHub ответил {(int)response.StatusCode}");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw fail($"файл больше {maxBytes / 1024 / 1024} МБ");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(total);
            }
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (actual != sha256)
        {
            File.Delete(path);
            throw fail($"SHA256 не совпадает с указанным на GitHub ({actual} ≠ {sha256}) — файл повреждён или подменён");
        }
    }
}
