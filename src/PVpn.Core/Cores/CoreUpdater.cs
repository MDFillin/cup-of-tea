using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PVpn.Core.Config;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.Core.Cores;

public enum CoreKind
{
    Xray,
    SingBox,
}

/// <summary>Релиз ядра на GitHub: версия, архив для Windows x64 и его SHA256 из GitHub API.</summary>
public sealed record CoreRelease(CoreKind Kind, string Version, Uri Download, string Sha256, long Size);

public sealed record CoreUpdateResult(CoreKind Kind, string? OldVersion, string? NewVersion, bool Changed, string Message);

public sealed class CoreUpdateException(string message) : Exception(message);

/// <summary>
/// Обновление xray и sing-box с официальных релизов GitHub. Выполняет служба (права системы): интерфейс только
/// просит, а файлы ядер подсунуть не может. Проверки нового ядра до замены: SHA256 архива совпадает с тем, что
/// GitHub API указывает для этого файла; ядро называет ожидаемую версию; ядро принимает конфиги, которые строит
/// PVpn. Прежнее ядро сохраняется в cores-previous — к нему можно откатиться. Запущенное ядро Windows
/// переименовать даёт, поэтому замена не рвёт текущее подключение: новое ядро подействует со следующего.
/// </summary>
public sealed partial class CoreUpdater(CorePaths cores, HttpClient http, Action<string>? log = null)
{
    public const long MaxArchiveBytes = 200L * 1024 * 1024;

    /// <summary>Откуда разрешено скачивать архивы (GitHub перенаправляет на свои хранилища).</summary>
    private static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public string PreviousDirectory => PreviousDirectoryOf(cores);
    private string StagingDirectory => Path.Combine(Path.GetDirectoryName(cores.Directory.TrimEnd('\\'))!, "cores-staging");

    /// <summary>Куда откладывается прежняя версия: папка cores-previous рядом с cores.</summary>
    public static string PreviousDirectoryOf(CorePaths cores) => Path.Combine(Path.GetDirectoryName(cores.Directory.TrimEnd('\\'))!, "cores-previous");

    public static string ExeName(CoreKind kind) => kind == CoreKind.Xray ? "xray.exe" : "sing-box.exe";
    public static string Title(CoreKind kind) => kind == CoreKind.Xray ? "xray" : "sing-box";
    private static string Repository(CoreKind kind) => kind == CoreKind.Xray ? "XTLS/Xray-core" : "SagerNet/sing-box";

    public string CurrentPath(CoreKind kind) => Path.Combine(cores.Directory, ExeName(kind));
    public string PreviousPath(CoreKind kind) => Path.Combine(PreviousDirectory, ExeName(kind));

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Id + "-core-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Версия ядра по его собственному ответу на «version»; null — файла нет или он не запускается.</summary>
    public static async Task<string?> GetVersionAsync(CoreKind kind, string exe, CancellationToken ct = default)
    {
        if (!File.Exists(exe))
            return null;
        var psi = new ProcessStartInfo(exe, "version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        try
        {
            using var p = new Process { StartInfo = psi };
            ProcessStarter.Start(p);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                p.Kill(true);
                return null;
            }
            return p.ExitCode == 0 ? ParseVersion(kind, output) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>«Xray 26.10.21 (Xray, Penetrates Everything.) …», «sing-box version 1.14.4».</summary>
    internal static string? ParseVersion(CoreKind kind, string output)
    {
        var m = (kind == CoreKind.Xray ? XrayVersionRegex() : SingBoxVersionRegex()).Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<CoreRelease> GetLatestAsync(CoreKind kind, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository(kind)}/releases/latest", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new CoreUpdateException($"GitHub ответил {(int)response.StatusCode} на запрос о релизе {Title(kind)}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseRelease(kind, doc.RootElement);
    }

    internal static CoreRelease ParseRelease(CoreKind kind, JsonElement root)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = tag.TrimStart('v');
        if (!VersionRegex().IsMatch(version))
            throw new CoreUpdateException($"Непонятная версия релиза {Title(kind)}: «{tag}»");
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
            throw new CoreUpdateException($"Последний релиз {Title(kind)} — предварительный, не ставлю");

        var expected = kind == CoreKind.Xray ? "Xray-windows-64.zip" : $"sing-box-{version}-windows-amd64.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expected)
                continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64)
                throw new CoreUpdateException($"GitHub не указал SHA256 для {expected} — без него обновление не ставлю");
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            CheckHost(url);
            return new CoreRelease(kind, version, url, digest[7..].ToLowerInvariant(), asset.GetProperty("size").GetInt64());
        }
        throw new CoreUpdateException($"В релизе {Title(kind)} {version} нет архива {expected}");
    }

    private static void CheckHost(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new CoreUpdateException($"Ссылка на архив ведёт не на GitHub: {url.Host}");
    }

    /// <summary>
    /// Скачивает, проверяет и ставит новую версию. Без <paramref name="force"/> ничего не делает, если стоит та же.
    /// <paramref name="exclusive"/> — выполнить замену файлов под блокировкой службы (чтобы подключение не стартовало
    /// ровно в момент замены).
    /// </summary>
    public async Task<CoreUpdateResult> UpdateAsync(CoreKind kind, bool force, Func<Func<Task>, Task>? exclusive = null, CancellationToken ct = default)
    {
        var current = await GetVersionAsync(kind, CurrentPath(kind), ct).ConfigureAwait(false);
        var release = await GetLatestAsync(kind, ct).ConfigureAwait(false);
        if (!force && current == release.Version)
            return new CoreUpdateResult(kind, current, current, false, $"{Title(kind)} {current} — последняя версия");

        // Своя папка на каждый запуск: только что распакованный exe ещё держит антивирус, и прошлую папку
        // не всегда удаётся удалить сразу — её остатки убираются здесь же, при следующем обновлении.
        foreach (var old in Directory.EnumerateDirectories(Path.GetDirectoryName(StagingDirectory)!, "cores-staging*"))
            TryDeleteDirectory(old);
        var staging = $"{StagingDirectory}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);
        try
        {
            log?.Invoke($"Обновление {Title(kind)}: {current ?? "—"} → {release.Version}, скачиваю {release.Download}");
            var zip = Path.Combine(staging, "core.zip");
            await DownloadAsync(release, zip, ct).ConfigureAwait(false);
            var staged = Path.Combine(staging, ExeName(kind));
            Extract(zip, ExeName(kind), staged);

            var reported = await GetVersionAsync(kind, staged, ct).ConfigureAwait(false);
            if (reported != release.Version)
                throw new CoreUpdateException($"Новый {Title(kind)} сообщает версию «{reported ?? "не запускается"}», а ожидалась {release.Version}");
            if (await CoreSelfTest.RunAsync(kind, staged, ct).ConfigureAwait(false) is { } problem)
                throw new CoreUpdateException($"Новый {Title(kind)} {release.Version} не принимает конфиги {Product.Name}: {problem}");

            async Task Swap()
            {
                Replace(kind, staged);
                // Проверка уже с рабочего места; заодно антивирус посмотрит новый exe сейчас, а не при подключении.
                if (await GetVersionAsync(kind, CurrentPath(kind), ct).ConfigureAwait(false) != release.Version)
                {
                    SwapWithPrevious(kind);
                    throw new CoreUpdateException($"Новый {Title(kind)} не запустился на месте старого — вернул {current}");
                }
            }
            if (exclusive != null)
                await exclusive(Swap).ConfigureAwait(false);
            else
                await Swap().ConfigureAwait(false);
            await WriteVersionsAsync(ct).ConfigureAwait(false);
            log?.Invoke($"{Title(kind)} обновлён: {current ?? "—"} → {release.Version}, прежний сохранён для отката");
            return new CoreUpdateResult(kind, current, release.Version, true, $"{Title(kind)} обновлён: {current ?? "—"} → {release.Version}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Вернуть прежнюю версию (и сохранить текущую на её место — откат можно отменить тем же действием).</summary>
    public async Task<CoreUpdateResult> RollbackAsync(CoreKind kind, Func<Func<Task>, Task>? exclusive = null, CancellationToken ct = default)
    {
        var previous = await GetVersionAsync(kind, PreviousPath(kind), ct).ConfigureAwait(false)
                       ?? throw new CoreUpdateException($"Прежней версии {Title(kind)} нет — откатываться не к чему");
        var current = await GetVersionAsync(kind, CurrentPath(kind), ct).ConfigureAwait(false);
        Task Swap()
        {
            SwapWithPrevious(kind);
            return Task.CompletedTask;
        }
        if (exclusive != null)
            await exclusive(Swap).ConfigureAwait(false);
        else
            await Swap().ConfigureAwait(false);
        await WriteVersionsAsync(ct).ConfigureAwait(false);
        log?.Invoke($"{Title(kind)} откачен: {current ?? "—"} → {previous}");
        return new CoreUpdateResult(kind, current, previous, true, $"{Title(kind)} откачен: {current ?? "—"} → {previous}");
    }

    private async Task DownloadAsync(CoreRelease release, string path, CancellationToken ct)
    {
        if (release.Size > MaxArchiveBytes)
            throw new CoreUpdateException($"Архив {Title(release.Kind)} слишком большой ({release.Size / 1024 / 1024} МБ)");
        using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // После перенаправлений адрес тоже должен остаться на GitHub.
        if (response.RequestMessage?.RequestUri is { } final)
            CheckHost(final);
        if (!response.IsSuccessStatusCode)
            throw new CoreUpdateException($"GitHub ответил {(int)response.StatusCode} при скачивании {Title(release.Kind)}");

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
                if (total > MaxArchiveBytes)
                    throw new CoreUpdateException($"Архив {Title(release.Kind)} больше {MaxArchiveBytes / 1024 / 1024} МБ");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (actual != release.Sha256)
            throw new CoreUpdateException($"SHA256 архива {Title(release.Kind)} не совпадает с указанным на GitHub ({actual} ≠ {release.Sha256}) — архив повреждён или подменён");
    }

    /// <summary>Достаёт из архива только нужный exe (по имени файла, в любой папке архива).</summary>
    internal static void Extract(string zipPath, string exeName, string target)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                    ?? throw new CoreUpdateException($"В архиве нет {exeName}");
        if (entry.Length > MaxArchiveBytes)
            throw new CoreUpdateException($"{exeName} в архиве слишком большой");
        entry.ExtractToFile(target, overwrite: true);
    }

    /// <summary>Текущее ядро → cores-previous, новое → cores. При сбое второго шага текущее возвращается на место.</summary>
    private void Replace(CoreKind kind, string staged)
    {
        Directory.CreateDirectory(PreviousDirectory);
        var current = CurrentPath(kind);
        var previous = PreviousPath(kind);
        RemoveOrPark(previous);
        File.Move(current, previous);
        try
        {
            File.Move(staged, current);
        }
        catch
        {
            File.Move(previous, current);
            throw;
        }
    }

    private void SwapWithPrevious(CoreKind kind)
    {
        var current = CurrentPath(kind);
        var previous = PreviousPath(kind);
        var temp = Path.Combine(PreviousDirectory, ExeName(kind) + ".swap");
        RemoveOrPark(temp);
        File.Move(current, temp);
        try
        {
            File.Move(previous, current);
        }
        catch
        {
            File.Move(temp, current);
            throw;
        }
        File.Move(temp, previous);
    }

    /// <summary>Удаляет файл; если он ещё запущен (старое ядро работает), отодвигает его в сторону.</summary>
    private static void RemoveOrPark(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.Move(path, $"{path}.old-{Guid.NewGuid():N}");
        }
        catch (IOException)
        {
            File.Move(path, $"{path}.old-{Guid.NewGuid():N}");
        }
    }

    private async Task WriteVersionsAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var kind in Enum.GetValues<CoreKind>())
            lines.Add($"{Title(kind)} {await GetVersionAsync(kind, CurrentPath(kind), ct).ConfigureAwait(false) ?? "—"}");
        await File.WriteAllLinesAsync(Path.Combine(cores.Directory, "VERSIONS.txt"), lines, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\bXray (\d+\.\d+\.\d+)")]
    private static partial Regex XrayVersionRegex();

    [GeneratedRegex(@"sing-box version (\d+\.\d+\.\d+)")]
    private static partial Regex SingBoxVersionRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();
}

/// <summary>
/// Проверка ядра конфигами, которые строит PVpn: XHTTP, Vision с mux и фрагментацией, Hysteria2 с шумами и сменой
/// портов, режим прокси с раздельным туннелированием и статистикой; TUN sing-box с DNS по DoH и правилами.
/// Новая версия, сломавшая что-то из этого, не ставится.
/// </summary>
public static class CoreSelfTest
{
    private const string Uuid = "11111111-2222-4333-8444-555555555555";
    private const string Pbk = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";
    private const string Pin = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    private static readonly string[] Links =
    [
        $"vless://{Uuid}@203.0.113.10:443?encryption=none&security=reality&sni=www.example.com&fp=chrome&pbk={Pbk}&sid=0123abcd&type=xhttp&path=%2Fself-test&mode=auto#self-xhttp",
        $"vless://{Uuid}@203.0.113.10:8443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.com&fp=chrome&pbk={Pbk}&sid=0123abcd&type=tcp#self-vision",
        $"hy2://self-test@203.0.113.10:443,20000-30000?sni=www.example.com&pinSHA256={Pin}#self-hy2",
    ];

    /// <summary>null — ядро приняло все конфиги, иначе — что не так.</summary>
    public static async Task<string?> RunAsync(CoreKind kind, string exe, CancellationToken ct = default)
    {
        var paths = new CorePaths(Path.GetDirectoryName(exe)!);
        var split = SplitTunnelRules.Validate(new SplitTunnelOptions
        {
            Mode = SplitTunnelMode.ExcludeSelected,
            Apps = [new SplitApp(@"C:\Program Files\Example", IsFolder: true)],
            Domains = ["example.ru"],
        });
        if (kind == CoreKind.Xray)
        {
            foreach (var link in Links)
            {
                var profile = LinkParser.Parse(link);
                var bypass = BypassRules.Validate(BypassOptions.Default);
                var configs = new[]
                {
                    XrayConfigBuilder.Build(profile, new XrayOptions
                    {
                        Mode = XrayMode.TunBackend, SocksPort = 23456, SocksUser = "u", SocksPassword = "p4ssw0rd", Bypass = bypass, MetricsPort = 23457,
                    }),
                    XrayConfigBuilder.Build(profile, new XrayOptions { SocksPort = 23458, HttpPort = 23459, Bypass = bypass, SplitTunnel = split, MetricsPort = 23460 }),
                };
                foreach (var config in configs)
                {
                    var r = await CoreValidator.ValidateXrayAsync(paths, config.ToJsonString(), ct).ConfigureAwait(false);
                    if (!r.Ok)
                        return $"{profile.Name}: {FirstLine(r.Output)}";
                }
            }
            return null;
        }

        foreach (var (dns, splitOptions) in new[]
                 {
                     (DnsRules.Preset(DnsProvider.Cloudflare), (SplitTunnelOptions?)null),
                     (DnsRules.ParseCustom("https://dns.example.com/dns-query"), split),
                     (DnsRules.Preset(DnsProvider.AdGuard), split with { Mode = SplitTunnelMode.OnlySelected }),
                 })
        {
            var config = SingBoxConfigBuilder.Build(new SingBoxOptions
            {
                SocksPort = 23456, SocksUser = "u", SocksPassword = "p4ssw0rd",
                ServerAddresses = [System.Net.IPAddress.Parse("203.0.113.10")],
                Dns = dns,
                SplitTunnel = splitOptions,
            });
            var r = await CoreValidator.ValidateSingBoxAsync(paths, config.ToJsonString(), ct).ConfigureAwait(false);
            if (!r.Ok)
                return FirstLine(r.Output);
        }
        return null;
    }

    private static string FirstLine(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "ошибка без текста";
        return line.Length > 300 ? line[..300] : line;
    }
}
