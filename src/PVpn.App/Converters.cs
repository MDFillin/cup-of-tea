using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PVpn.App;

/// <summary>Пустая строка или null → Collapsed (строка в карточке статуса не нужна).</summary>
public sealed class EmptyToCollapsed : IValueConverter
{
    public static readonly EmptyToCollapsed Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Показать, если значение равно параметру (номер страницы, имя страницы настроек; «none» — null).</summary>
public sealed class ShowWhen : IValueConverter
{
    public static readonly ShowWhen Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value?.ToString() ?? "none") == parameter as string ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>«24,6 Мбит/с» → число (параметр value) или единица (unit) — единицу пишем мельче.</summary>
public sealed class ValuePart : IValueConverter
{
    public static readonly ValuePart Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var unit = parameter as string == "unit";
        if (value is not string s)
            return unit ? "" : "—";
        var i = s.LastIndexOf(' ');
        return i < 0 ? (unit ? "" : s) : unit ? s[i..] : s[..i];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null → Collapsed (карточка сервера, пока серверов нет).</summary>
public sealed class NullToCollapsed : IValueConverter
{
    public static readonly NullToCollapsed Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Режим подключения для подписи в боковом меню.</summary>
public sealed class ModeName : IValueConverter
{
    public static readonly ModeName Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? " · весь трафик" : " · прокси";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Заголовок страницы настроек по её имени.</summary>
public sealed class SettingsTitle : IValueConverter
{
    public static readonly SettingsTitle Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "dns" => "DNS",
        "bypass" => "Обход блокировок",
        "tun" => "Тонкая настройка TUN",
        "trusted" => "Доверенные сети",
        "subs" => "Подписки",
        "cores" => "Ядра",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
