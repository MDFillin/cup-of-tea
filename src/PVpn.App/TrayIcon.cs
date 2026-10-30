using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PVpn.App;

/// <summary>Значок в трее: цвет по состоянию, меню «Открыть / Подключить / Выход».</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel vm;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem connectItem;
    private readonly Dictionary<UiState, Drawing.Icon> icons;
    private UiState? shown;

    public TrayIcon(MainViewModel vm, Action open, Action exit)
    {
        this.vm = vm;
        icons = new()
        {
            [UiState.Off] = Load("tray-off"),
            [UiState.Busy] = Load("tray-busy"),
            [UiState.On] = Load("tray-on"),
            [UiState.Error] = Load("tray-error"),
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add($"Открыть {Brand.Name}", null, (_, _) => open());
        connectItem = new Forms.ToolStripMenuItem("Подключить", null, async (_, _) => await vm.ToggleConnectCommand.ExecuteAsync(null));
        menu.Items.Add(connectItem);
        // Серверы — подменю, собирается при каждом открытии: список мог измениться (подписка, импорт).
        var servers = new Forms.ToolStripMenuItem("Сервер");
        servers.DropDownItems.Add("—");
        menu.Opening += (_, _) =>
        {
            servers.DropDownItems.Clear();
            foreach (var p in vm.Profiles)
            {
                var id = p.Profile.Id;
                servers.DropDownItems.Add(new Forms.ToolStripMenuItem(p.Name, null, async (_, _) => await vm.SwitchServerAsync(id))
                {
                    Checked = vm.SelectedProfile?.Profile.Id == id,
                    ToolTipText = p.Details,
                });
            }
            servers.Enabled = servers.DropDownItems.Count > 0;
        };
        menu.Items.Add(servers);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());

        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Icon = icons[UiState.Off], Text = Brand.Name, Visible = true };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
        vm.TrayChanged += Update;
        Update();
    }

    private void Update()
    {
        if (shown != vm.State)
        {
            icon.Icon = icons[vm.State];
            shown = vm.State;
        }
        var text = vm.TrayText;
        icon.Text = text.Length > 120 ? text[..120] : text;
        connectItem.Text = vm.ConnectButtonText;
    }

    public void ShowBalloon(string title, string text) => icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static Drawing.Icon Load(string name)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}.ico"))
                       ?? throw new FileNotFoundException(name);
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        vm.TrayChanged -= Update;
        icon.Visible = false;
        icon.Dispose();
        foreach (var i in icons.Values)
            i.Dispose();
    }
}
