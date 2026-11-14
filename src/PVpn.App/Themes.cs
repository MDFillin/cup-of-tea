using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PVpn.App;

/// <summary>
/// Обычное или зимнее оформление. Цвета — в Palette.xaml, зимой поверх неё — WinterTheme.xaml (у словаря, добавленного
/// позже, приоритет). Стили (Theme.xaml) берут цвета через StaticResource при создании, поэтому Theme.xaml загружается
/// заново, и оформление меняется для окон, созданных после <see cref="Apply"/>.
/// </summary>
public static class Themes
{
    private const string Component = "pack://application:,,,/CupOfTea;component/";
    private static readonly string[] Ours = ["Palette.xaml", "WinterTheme.xaml", "Theme.xaml"];

    /// <summary>Какое оформление сейчас загружено (null — ещё никакое не применялось).</summary>
    public static bool? Winter { get; private set; }

    public static void Apply(Application app, bool winter)
    {
        if (Winter == winter)
            return;
        var dictionaries = app.Resources.MergedDictionaries;
        var at = dictionaries.Count;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            if (dictionaries[i].Source is { } s && Ours.Any(name => s.OriginalString.EndsWith(name, StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
                at = i;
            }
        }
        // Словарь сначала встаёт на место, потом загружается: так StaticResource в Theme.xaml находит цвета палитры.
        foreach (var name in winter ? ["Palette.xaml", "WinterTheme.xaml", "Theme.xaml"] : new[] { "Palette.xaml", "Theme.xaml" })
        {
            var dictionary = new ResourceDictionary();
            dictionaries.Insert(at++, dictionary);
            dictionary.Source = new Uri(Component + name);
        }

        StatusBrushes.Apply(winter);
        // Кнопки и переключатели WPF-UI — того же цвета, что акцент (тёмная тема сама его осветляет).
        var accent = (Color)app.FindResource("AccentColor");
        if (winter)
            ApplicationAccentColorManager.Apply(accent, Color.FromRgb(0x2A, 0x9F, 0xD0), accent, Color.FromRgb(0x9E, 0xE0, 0xFA));
        else
            ApplicationAccentColorManager.Apply(accent, Color.FromRgb(0x2B, 0xB9, 0x88), accent, Color.FromRgb(0x6E, 0xE7, 0xB7));
        Winter = winter;
    }
}
