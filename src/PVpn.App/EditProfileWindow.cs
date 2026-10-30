using System.Windows;
using System.Windows.Controls;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>Правка сервера: имя и ссылка. Ссылка проверяется разбором; Id сервера сохраняется.</summary>
internal sealed class EditProfileWindow : Window
{
    private readonly TextBox nameBox;
    private readonly TextBox linkBox;
    private readonly TextBlock error;
    private readonly Profile original;

    /// <summary>Изменённый сервер (после «Сохранить»).</summary>
    public Profile? Result { get; private set; }

    public EditProfileWindow(Profile profile, bool fromSubscription)
    {
        original = profile;
        Title = "Изменить сервер";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        nameBox = new TextBox { Text = profile.Name, Margin = new Thickness(0, 4, 0, 12) };
        linkBox = new TextBox
        {
            Text = profile.SourceLink, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Height = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 8),
        };
        error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = StatusBrushes.Error, Visibility = Visibility.Collapsed };
        var save = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Имя" });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Ссылка (vless:// или hy2://) — например, после смены ключа на сервере" });
        panel.Children.Add(linkBox);
        if (fromSubscription)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Сервер из подписки: при её обновлении изменения ссылки заменятся данными панели, а переименованный сервер появится заново под прежним именем.",
                TextWrapping = TextWrapping.Wrap, Foreground = StatusBrushes.Busy, Margin = new Thickness(0, 0, 0, 8),
            });
        }
        panel.Children.Add(error);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
            Children = { save, cancel },
        });
        Content = panel;
    }

    private void Save()
    {
        try
        {
            var parsed = LinkParser.Parse(linkBox.Text.Trim());
            var name = string.IsNullOrWhiteSpace(nameBox.Text) ? parsed.Name : nameBox.Text.Trim();
            Result = parsed with
            {
                Id = original.Id,
                Name = name,
                SubscriptionId = original.SubscriptionId,
                // Имя в ссылке — то же, что в списке: так его увидит и тот, кому ссылку передадут (экспорт, QR).
                SourceLink = WithName(parsed.SourceLink, name),
            };
            DialogResult = true;
        }
        catch (LinkParseException e)
        {
            error.Text = e.Message;
            error.Visibility = Visibility.Visible;
        }
    }

    internal static string WithName(string link, string name)
    {
        var hash = link.IndexOf('#');
        return (hash < 0 ? link : link[..hash]) + "#" + Uri.EscapeDataString(name);
    }
}
