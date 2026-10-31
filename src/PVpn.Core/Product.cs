namespace PVpn.Core;

/// <summary>
/// Имена продукта для Windows и пользователя. Рабочее имя кода (проекты, пространства имён) — PVpn.
/// Прежние имена — Tempest VPN (TempestVPN) и PVpn: их данные переносит <see cref="LegacyData"/> и установщик.
/// </summary>
public static class Product
{
    /// <summary>Имя для пользователя: окно, трей, ярлык, «Приложения» Windows.</summary>
    public const string Name = "The Cup of Tea";

    /// <summary>Служба, адаптер TUN, папки данных, автозапуск.</summary>
    public const string Id = "CupOfTea";

    public const string AppExe = "CupOfTea.exe";
    public const string ServiceExe = "CupOfTea.Service.exe";
    public const string CliExe = "cupoftea-cli.exe";

    /// <summary>Прежние имена (служба, папки, автозапуск), от новых к старым — для переноса данных и удаления старой установки.</summary>
    public static readonly string[] LegacyIds = ["TempestVPN", "PVpn"];

    /// <summary>
    /// Репозиторий GitHub («владелец/имя»), в релизах которого лежит установщик CupOfTea-Setup-x.y.z.exe — оттуда
    /// программа обновляет себя. Пусто — обновление из программы не настроено.
    /// </summary>
    public const string UpdateRepository = "MDFillin/cup-of-tea";

    /// <summary>Папка данных пользователя или машины: %APPDATA%\CupOfTea, %LOCALAPPDATA%\CupOfTea, C:\ProgramData\CupOfTea.</summary>
    public static string DataDir(Environment.SpecialFolder root) => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data", root == Environment.SpecialFolder.LocalApplicationData ? "local" : "roaming")
        : Path.Combine(Environment.GetFolderPath(root), Id);

    /// <summary>
    /// Портативная версия (рядом с exe лежит portable.txt): данные пользователя — в папке data рядом с программой,
    /// без установки и прав администратора (только режим прокси). Серверы зашифрованы для этого пользователя Windows.
    /// </summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
}

/// <summary>Перенос данных пользователя из папок прежних имён (TempestVPN, PVpn) в папки The Cup of Tea.</summary>
public static class LegacyData
{
    /// <summary>
    /// Серверы, настройки и копия системного прокси переезжают в новые папки, если новых ещё нет.
    /// Папка переносится целиком (на том же диске это мгновенно); при ошибке остаётся на месте.
    /// </summary>
    public static IReadOnlyList<string> MigrateUserData()
    {
        var moved = new List<string>();
        // Портативная версия живёт в своей папке и чужие данные из профиля Windows не забирает.
        if (Product.IsPortable)
            return moved;
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var current = Product.DataDir(root);
            // Берётся самая новая из прежних папок; остальные остаются как есть.
            var old = Product.LegacyIds.Select(id => Path.Combine(Environment.GetFolderPath(root), id)).FirstOrDefault(Directory.Exists);
            if (old == null || Directory.Exists(current))
                continue;
            try
            {
                Directory.Move(old, current);
                moved.Add(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return moved;
    }
}
