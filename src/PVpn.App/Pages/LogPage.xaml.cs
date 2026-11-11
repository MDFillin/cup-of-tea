using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PVpn.App.Pages;

public partial class LogPage : UserControl
{
    public LogPage() => InitializeComponent();

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    // Пока страница скрыта, прокрутка не работает: листаем к концу, когда её открыли.
    private void LogBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(((TextBox)sender).ScrollToEnd, DispatcherPriority.Loaded);
    }
}
