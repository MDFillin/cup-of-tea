using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PVpn.App;

/// <summary>Ссылка сервера QR-кодом — перенести в клиент на телефоне или на другой ПК.</summary>
internal sealed class QrWindow : Window
{
    public QrWindow(string name, string link)
    {
        Title = $"QR — {name}";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        var copy = new Button { Content = "Скопировать ссылку", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(link);
                copy.Content = "Скопировано";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copy.Content = "Буфер обмена занят";
            }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new Image { Source = QrCodes.Encode(link), Width = 320, Height = 320, Stretch = Stretch.Uniform, SnapsToDevicePixels = true },
                new TextBlock
                {
                    Text = "В коде — ключ доступа к серверу. Не показывайте его посторонним и не выкладывайте снимок.",
                    TextWrapping = TextWrapping.Wrap, Width = 320, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0),
                },
                copy,
            },
        };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }
}
