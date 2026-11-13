using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PVpn.Core.Ipc;

namespace PVpn.Service;

/// <summary>
/// Защита до входа в Windows: запрос подключения, который служба поднимет сама после перезагрузки.
/// В нём ссылка или конфиг сервера (ключи), поэтому файл зашифрован DPAPI от имени SYSTEM — расшифровать его может
/// только служба на этом компьютере — и лежит в папке, куда пускают только SYSTEM и администраторов.
/// </summary>
internal static class ResumeStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Product.Id + ".resume");
    private static readonly object Sync = new();

    /// <param name="file">Куда: <see cref="ServicePaths.ResumeFile"/> (по умолчанию) или <see cref="ServicePaths.SessionFile"/>.</param>
    public static void Save(IpcRequest request, string? file = null)
    {
        // Сохраняется только то, что нужно для подключения: команда, сервер и настройки.
        var saved = new IpcRequest
        {
            Command = IpcCommands.Connect,
            Link = request.Link,
            Config = request.Config,
            Options = request.Options,
            Remember = request.Remember || file == null,
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(saved, IpcJsonContext.Default.IpcRequest);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        lock (Sync)
        {
            file ??= ServicePaths.ResumeFile;
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
        }
    }

    /// <summary>null — нечего восстанавливать (или файл испорчен: тогда он удаляется).</summary>
    public static IpcRequest? Load(string? file = null)
    {
        lock (Sync)
        {
            file ??= ServicePaths.ResumeFile;
            if (!File.Exists(file))
                return null;
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return JsonSerializer.Deserialize(plain, IpcJsonContext.Default.IpcRequest);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException)
            {
                File.Delete(file);
                return null;
            }
        }
    }

    public static bool Exists => File.Exists(ServicePaths.ResumeFile);

    public static void Clear(string? file = null)
    {
        lock (Sync)
            File.Delete(file ?? ServicePaths.ResumeFile);
    }
}
