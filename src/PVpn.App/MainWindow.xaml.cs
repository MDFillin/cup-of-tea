using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace PVpn.App;

/// <summary>
/// Окно: боковое меню, страницы (Pages\*: главная, серверы, маршруты, настройки, журнал), строка сообщений и экран
/// обязательного обновления. Состояние — в MainViewModel, поэтому окно можно закрыть и создать заново (см. App).
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Размер, под который нарисован интерфейс: окно больше — всё крупнее.</summary>
    private const double BaseWidth = 1100, BaseHeight = 720;

    private readonly MainViewModel vm;

    /// <summary>true — закрыть окно по-настоящему (выход из приложения), иначе окно прячется в трей.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Окно спрятали в трей.</summary>
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

    private void DismissNotice_Click(object sender, RoutedEventArgs e) => vm.Notice = null;
}
