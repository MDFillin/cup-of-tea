using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace PVpn.App.Pages;

public partial class ServersPage : UserControl
{
    /// <summary>Два щелчка по серверу быстрее этого — подключиться к нему или переключиться (системный интервал бывает до 900 мс).</summary>
    private const int DoubleClickMs = 350;
    private ProfileItem? lastClickItem;
    private long lastClickAt;

    public ServersPage() => InitializeComponent();

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>Быстрый двойной щелчок по серверу: подключиться к нему или переключиться, если VPN уже включён.</summary>
    private async void ServerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ServerList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: ProfileItem item })
            return;
        var now = Environment.TickCount64;
        if (item == lastClickItem && now - lastClickAt <= DoubleClickMs)
        {
            lastClickItem = null;
            await Vm.SwitchServerAsync(item.Profile.Id, connectIfOff: true);
            return;
        }
        lastClickItem = item;
        lastClickAt = now;
    }

    /// <summary>Щелчок по заголовку группы серверов: свернуть или развернуть её (и запомнить).</summary>
    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: CollectionViewGroup { Name: string group } } toggle)
            Vm.SetGroupExpanded(group, toggle.IsChecked == true);
    }
}
