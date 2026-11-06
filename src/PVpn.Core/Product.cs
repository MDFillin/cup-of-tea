namespace PVpn.Core;

/// <summary>
/// Имена продукта для Windows и пользователя. Рабочее имя кода (проекты, пространства имён) — PVpn.
/// Программа самостоятельная: с клиентами, от которых пошёл код, у неё нет общих имён, папок, служб и ключей
/// (установщик и программа их не трогают).
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
