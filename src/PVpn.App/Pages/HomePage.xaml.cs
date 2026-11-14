using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PVpn.App.Pages;

public partial class HomePage : UserControl
{
    private MainViewModel? subscribed;

    public HomePage()
    {
        InitializeComponent();
        // Пасхалка: снег из середины главной (MainViewModel.Celebrate).
        DataContextChanged += (_, _) =>
        {
            if (subscribed != null)
                subscribed.Celebrate -= OnCelebrate;
            subscribed = DataContext as MainViewModel;
            if (subscribed != null)
                subscribed.Celebrate += OnCelebrate;
        };
        Unloaded += (_, _) =>
        {
            if (subscribed != null)
                subscribed.Celebrate -= OnCelebrate;
            subscribed = null;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnCelebrate()
    {
        if (Snow.IsVisible)
            Snow.Burst(new Point(Snow.ActualWidth / 2, Snow.ActualHeight * 0.4), 60);
    }

    /// <summary>Щелчок мимо выдвижной панели серверов закрывает её (и больше ничего не делает).</summary>
    private void SheetBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Vm.CloseHomeServersCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Зимой щелчок по пустому месту главной — снежок.</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Root) && Snow.IsVisible)
            Snow.Burst(e.GetPosition(Snow));
    }
}
