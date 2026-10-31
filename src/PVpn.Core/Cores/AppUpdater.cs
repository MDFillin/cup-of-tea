using System.Text.Json;
using System.Text.RegularExpressions;

namespace PVpn.Core.Cores;

/// <summary>Новая версия программы: установщик из релиза GitHub и его SHA256.</summary>
public sealed record AppRelease(Version Version, Uri Setup, string Sha256, long Size, Uri Page, string? Notes);

public sealed class AppUpdateException(string message) : Exception(message);

/// <summary>
/// Обновление программы из неё самой: последний релиз репозитория <see cref="Product.UpdateRepository"/> на GitHub,
/// установщик CupOfTea-Setup-x.y.z.exe той же версии, что и тег, с SHA256 от GitHub. Предварительные релизы
/// пропускаются. Скачанный установщик запускает интерфейс — Windows спросит права администратора.
/// </summary>
public static partial class AppUpdater
{
    public const long MaxSetupBytes = 300L * 1024 * 1024;

    public static bool Configured => Product.UpdateRepository.Length > 0;

    /// <summary>Последний релиз; null — новее <paramref name="current"/> ничего нет.</summary>
    public static Task<AppRelease?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default) =>
        CheckAsync(http, Product.UpdateRepository, current, ct);

    internal static async Task<AppRelease?> CheckAsync(HttpClient http, string repository, Version current, CancellationToken ct = default)
    {
        if (repository.Length == 0)
            throw new AppUpdateException("источник обновлений не настроен");
        var url = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 404)
            throw new AppUpdateException("на GitHub пока нет ни одного релиза");
        if (!response.IsSuccessStatusCode)
            throw new AppUpdateException($"GitHub ответил {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var release = ParseRelease(doc.RootElement, repository);
        return release.Version > current ? release : null;
    }

    internal static AppRelease ParseRelease(JsonElement root, string repository)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!VersionRegex().IsMatch(tag.TrimStart('v')) || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new AppUpdateException($"непонятная версия релиза: «{tag}»");
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            throw new AppUpdateException($"последний релиз {tag} — предварительный");

        var expected = $"{Product.Id}-Setup-{version.ToString(3)}.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expected)
                continue;
            var sha256 = GitHubDownload.Sha256Of(asset.TryGetProperty("digest", out var d) ? d.GetString() : null)
                         ?? throw new AppUpdateException($"GitHub не указал SHA256 для {expected} — без него не ставлю");
            var setup = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (!GitHubDownload.IsAllowed(setup))
                throw new AppUpdateException($"ссылка на установщик ведёт не на GitHub: {setup.Host}");
            var page = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var p) && GitHubDownload.IsAllowed(p)
                ? p : new Uri($"https://github.com/{repository}/releases");
            var notes = root.TryGetProperty("body", out var b) ? b.GetString()?.Trim() : null;
            return new AppRelease(version, setup, sha256, asset.GetProperty("size").GetInt64(), page,
                string.IsNullOrEmpty(notes) ? null : notes.Length > 600 ? notes[..600] + "…" : notes);
        }
        throw new AppUpdateException($"в релизе {tag} нет установщика {expected}");
    }

    /// <summary>Скачивает и проверяет установщик в <paramref name="directory"/> (старые установщики там удаляются).</summary>
    public static async Task<string> DownloadAsync(HttpClient http, AppRelease release, string directory,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (release.Size > MaxSetupBytes)
            throw new AppUpdateException($"установщик слишком большой ({release.Size / 1024 / 1024} МБ)");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, $"{Product.Id}-Setup-*"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Прошлый установщик ещё работает или занят антивирусом — уберётся в следующий раз.
            }
        }
        var path = Path.Combine(directory, $"{Product.Id}-Setup-{release.Version.ToString(3)}.exe");
        var part = path + ".part";
        await GitHubDownload.DownloadAsync(http, release.Setup, release.Sha256, MaxSetupBytes, part,
            problem => new AppUpdateException($"установщик: {problem}"), progress, ct).ConfigureAwait(false);
        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Параметры тихой установки: без вопросов, кроме UAC; после установки установщик сам запустит программу
    /// (/RELAUNCH, см. installer\CupOfTea.iss).
    /// </summary>
    public const string SilentArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();
}
