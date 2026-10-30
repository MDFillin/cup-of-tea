namespace PVpn.Core.Cores;

/// <summary>Где лежат xray.exe и sing-box.exe.</summary>
public sealed record CorePaths(string Directory)
{
    public string Xray => Path.Combine(Directory, "xray.exe");
    public string SingBox => Path.Combine(Directory, "sing-box.exe");

    /// <summary>
    /// Ищет папку cores рядом с exe. Если <paramref name="allowDevFallback"/>, то ещё переменная PVPN_CORES_DIR
    /// и поиск вверх по дереву (запуск из bin\Debug при разработке). Служба вызывает без fallback:
    /// она запускает ядра только из своей папки в Program Files.
    /// </summary>
    public static CorePaths Resolve(bool allowDevFallback)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "cores");
        if (File.Exists(Path.Combine(local, "xray.exe")))
            return new CorePaths(local);

        if (allowDevFallback)
        {
            var env = Environment.GetEnvironmentVariable("PVPN_CORES_DIR");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "xray.exe")))
                return new CorePaths(env);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "cores");
                if (File.Exists(Path.Combine(candidate, "xray.exe")))
                    return new CorePaths(candidate);
            }
        }

        throw new FileNotFoundException($"Не найдены ядра (xray.exe, sing-box.exe) в {local}");
    }
}
