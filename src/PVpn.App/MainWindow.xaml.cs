using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace PVpn.App;

public partial class MainWindow : FluentWindow
{
    /// <summary>Размер, под который нарисован интерфейс: окно больше — всё крупнее.</summary>
    private const double BaseWidth = 1100, BaseHeight = 720;

    private readonly MainViewModel vm;

    /// <summary>true — закрыть окно по-настоящему (выход из приложения), иначе окно прячется в трей.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Окно впервые спрятали в трей (показать подсказку один раз).</summary>
    public event Action? HiddenToTray;

    public MainWindow(MainViewModel vm)
    {
        this.vm = vm;
        DataContext = vm;
        InitializeComponent();
        SizeChanged += (_, _) => ApplyScale();
    }

    /// <summary>Интерфейс растёт вместе с окном (одинаково по ширине и высоте), но не мельче 0,9.</summary>
    private void ApplyScale()
    {
        var scale = Math.Clamp(Math.Min(ActualWidth / BaseWidth, ActualHeight / BaseHeight), 0.9, 2.0);
        Scaled.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    /// <summary>Открыть страницу (для снимков окна): номер в боковом меню и, для настроек, имя страницы.</summary>
    public void SelectPage(int index, string? settingsPage = null)
    {
        vm.Page = index;
        vm.SettingsPage = settingsPage;
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => ((System.Windows.Controls.TextBox)sender).ScrollToEnd();

    // Пока страница скрыта, прокрутка не работает: листаем к концу, когда её открыли.
    private void LogBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(((System.Windows.Controls.TextBox)sender).ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Два щелчка по серверу быстрее этого — подключиться к нему или переключиться (системный интервал бывает до 900 мс).</summary>
    private const int DoubleClickMs = 350;
    private ProfileItem? lastClickItem;
    private long lastClickAt;

    /// <summary>Быстрый двойной щелчок по серверу: подключиться к нему или переключиться, если VPN уже включён.</summary>
    private async void ServerList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ServerList, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: ProfileItem item })
            return;
        var now = Environment.TickCount64;
        if (item == lastClickItem && now - lastClickAt <= DoubleClickMs)
        {
            lastClickItem = null;
            await vm.SwitchServerAsync(item.Profile.Id, connectIfOff: true);
            return;
        }
        lastClickItem = item;
        lastClickAt = now;
    }

    private void DismissNotice_Click(object sender, RoutedEventArgs e) => vm.Notice = null;

    private void DomainBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            vm.Split.AddDomainCommand.Execute(null);
    }
}
