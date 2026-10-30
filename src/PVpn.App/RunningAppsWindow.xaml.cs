using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PVpn.Core.Diagnostics;
using PVpn.Core.Routing;
using Wpf.Ui.Controls;

namespace PVpn.App;

/// <summary>Выбор программ из запущенных: имя из описания exe, путь, при желании — папка программы.</summary>
public partial class RunningAppsWindow : FluentWindow
{
    public sealed record RunningApp(string Name, string Path);

    private List<RunningApp> all = [];

    public IReadOnlyList<SplitApp> Selected { get; private set; } = [];

    public RunningAppsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            all = await Task.Run(Collect);
            Loading.Visibility = Visibility.Collapsed;
            ApplyFilter();
            Search.Focus();
        };
    }

    /// <summary>
    /// Свои процессы пользователя (к процессам SYSTEM и администратора доступа нет — их и не нужно),
    /// без системных из C:\Windows и без самого PVpn.
    /// </summary>
    private static List<RunningApp> Collect()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;
        var ours = AppContext.BaseDirectory;
        var result = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var path = ProcessPaths.TryGet(p.Id);
                if (path == null || result.ContainsKey(path) ||
                    path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || path.StartsWith(ours, StringComparison.OrdinalIgnoreCase))
                    continue;
                string name;
                try
                {
                    name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
                }
                catch (FileNotFoundException)
                {
                    name = "";
                }
                if (name.Length == 0)
                    name = Path.GetFileNameWithoutExtension(path);
                result[path] = new RunningApp(name, path);
            }
        }
        return [.. result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private void ApplyFilter()
    {
        var q = Search.Text?.Trim() ?? "";
        List.ItemsSource = q.Length == 0
            ? all
            : all.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Path.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var asFolder = AsFolder.IsChecked == true;
        Selected = List.SelectedItems.Cast<RunningApp>()
            .Select(a => asFolder ? new SplitApp(SplitTunnelRules.SuggestFolder(a.Path), IsFolder: true) : new SplitApp(a.Path, IsFolder: false))
            .ToList();
        DialogResult = Selected.Count > 0;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null)
            Add_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
