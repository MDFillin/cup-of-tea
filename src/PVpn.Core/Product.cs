namespace PVpn.Core;

/// <summary>
/// Имена продукта для Windows и пользователя. Рабочее имя кода (проекты, пространства имён) — PVpn;
/// до пункта H так же назывались exe, служба, канал и папки — их данные переносит <see cref="LegacyData"/> и установщик.
/// </summary>
public static class Product
{
    /// <summary>Имя для пользователя: окно, трей, ярлык, «Приложения» Windows.</summary>
    public const string Name = "Tempest VPN";

    /// <summary>Служба, адаптер TUN, папки данных, автозапуск.</summary>
    public const string Id = "TempestVPN";

    public const string AppExe = "TempestVPN.exe";
    public const string ServiceExe = "TempestVPN.Service.exe";
    public const string CliExe = "tempest-cli.exe";

    /// <summary>Прежнее имя (служба, папки, автозапуск) — для переноса данных и удаления старой установки.</summary>
    public const string LegacyId = "PVpn";

    /// <summary>Папка данных пользователя или машины: %APPDATA%\TempestVPN, %LOCALAPPDATA%\TempestVPN, C:\ProgramData\TempestVPN.</summary>
    public static string DataDir(Environment.SpecialFolder root) => Path.Combine(Environment.GetFolderPath(root), Id);
}

/// <summary>Перенос данных пользователя из папок PVpn (до переименования) в папки Tempest VPN.</summary>
public static class LegacyData
{
    /// <summary>
    /// Серверы, настройки и копия системного прокси переезжают в новые папки, если новых ещё нет.
    /// Папка переносится целиком (на том же диске это мгновенно); при ошибке остаётся на месте.
    /// </summary>
    public static IReadOnlyList<string> MigrateUserData()
    {
        var moved = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var old = Path.Combine(Environment.GetFolderPath(root), Product.LegacyId);
            var current = Product.DataDir(root);
            if (!Directory.Exists(old) || Directory.Exists(current))
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
