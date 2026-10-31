using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>
/// Текст с флагами-эмодзи: «Финляндия🇫🇮 xhttp» показывается с картинкой флага вместо букв «FI», которые рисует
/// Windows. В XAML: &lt;TextBlock local:FlagText.Text="{Binding DisplayName}" /&gt;.
/// </summary>
public static class FlagText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(FlagText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>Картинка флага страны (ISO-код, «FI»); null — такой нет.</summary>
    public static ImageSource? Image(string? country)
    {
        if (country is not { Length: 2 })
            return null;
        return Cache.GetOrAdd(country.ToUpperInvariant(), code =>
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Assets/Flags/{code}.png");
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (IOException)
            {
                return null;
            }
        });
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        if (e.NewValue is not string text)
            return;
        foreach (var segment in Flags.Split(text))
        {
            if (segment.Country == null)
            {
                block.Inlines.Add(new Run(segment.Text));
                continue;
            }
            if (Image(segment.Country) is not { } source)
            {
                block.Inlines.Add(new Run(segment.Country));
                continue;
            }
            // Картинка Twemoji квадратная, флаг в ней по центру — высота чуть больше строки, чтобы флаг был с букву.
            var size = block.FontSize * 1.3;
            block.Inlines.Add(new InlineUIContainer(new Image
            {
                Source = source,
                Width = size,
                Height = size,
                Margin = new Thickness(1, 0, 1, 0),
                SnapsToDevicePixels = true,
            })
            {
                BaselineAlignment = BaselineAlignment.Center,
            });
        }
    }
}
