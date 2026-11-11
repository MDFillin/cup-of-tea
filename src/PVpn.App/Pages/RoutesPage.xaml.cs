using System.Windows.Controls;
using System.Windows.Input;

namespace PVpn.App.Pages;

public partial class RoutesPage : UserControl
{
    public RoutesPage() => InitializeComponent();

    private void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ((MainViewModel)DataContext).Split.AddDomainCommand.Execute(null);
    }
}
