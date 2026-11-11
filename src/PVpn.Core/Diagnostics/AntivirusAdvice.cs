namespace PVpn.Core.Diagnostics;

/// <summary>
/// Подсказка про сторонний антивирус. Антивирусы, которые проверяют сетевой трафик и защищённые соединения,
/// замедляют VPN в разы: например, XHTTP через Kaspersky шёл в 10–20 раз медленнее, пока ядра не добавили
/// в доверенные программы. Список антивирусов берёт интерфейс (Центр безопасности Windows).
/// </summary>
public static class AntivirusAdvice
{
    /// <summary>productState из Центра безопасности: второй байт 0x10 — защита включена, 0x00/0x01 — выключена или отложена.</summary>
    public static bool IsEnabled(uint productState) => (productState & 0x1000) != 0;

    /// <summary>Встроенный антивирус Windows трафик VPN не тормозит — о нём не подсказываем.</summary>
    public static bool IsBuiltIn(string name) =>
        name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase);

    /// <summary>Что сделать в найденных антивирусах: для известных — где именно, иначе общий совет.</summary>
    public static string For(IReadOnlyList<string> names, string coresFolder)
    {
        var text = $"Найден антивирус: {string.Join(", ", names)}. Если VPN работает медленно, антивирус может проверять его трафик. " +
                   $"Добавьте xray.exe и sing-box.exe из папки «{coresFolder}» в доверенные программы (исключения) и отключите для них проверку трафика.";
        var specific = Specific(names);
        return specific == null ? text : $"{text}\n{specific}";
    }

    private static string? Specific(IReadOnlyList<string> names)
    {
        bool Has(string part) => names.Any(n => n.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (Has("Kaspersky"))
            return "Kaspersky: «Настройки → Угрозы и исключения → Указать доверенные программы», добавьте оба файла и отметьте «Не проверять весь трафик».";
        if (Has("ESET"))
            return "ESET: «Дополнительные настройки → Защита доступа в Интернет → Фильтрация протоколов → Исключённые приложения», добавьте оба файла.";
        if (Has("Dr.Web"))
            return "Dr.Web: «Исключения → Приложения», добавьте оба файла и отметьте исключение для сетевого трафика.";
        if (Has("Avast") || Has("AVG"))
            return "Avast/AVG: «Меню → Настройки → Общее → Исключения», добавьте папку ядер.";
        return null;
    }
}
