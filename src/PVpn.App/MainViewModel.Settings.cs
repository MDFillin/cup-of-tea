using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Sessions;
using PVpn.Core.SystemProxy;

namespace PVpn.App;

public sealed partial class MainViewModel
{
    // ───────────── Служба и восстановление ─────────────

    private static string InstallScript => Path.Combine(AppContext.BaseDirectory, "install.ps1");

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (proxy != null)
            await StopProxyAsync();
        if (WinInetProxy.HasPendingRestore)
            WinInetProxy.Restore();
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Repair }, TimeSpan.FromSeconds(60));
            Notice = r.Ok ? "Сеть восстановлена: маршруты, kill switch и DNS — как до подключения." : r.Error;
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            Notice = $"Служба недоступна ({e.Message}). Системный прокси восстановлен. Если интернета нет, перезагрузите компьютер — " +
                     $"все изменения {Brand.Name} (маршруты, фильтры) при перезагрузке исчезают.";
        }
        await PollAsync();
    }

    [RelayCommand]
    private void InstallService()
    {
        try
        {
            // Один запрос UAC: установка службы и правила брандмауэра (scripts\install.ps1 из комплекта).
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{InstallScript}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Notice = "Устанавливаю службу… Через несколько секунд режим TUN станет доступен.";
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Notice = $"Установка отменена: {e.Message}";
        }
    }

    // ───────────── Настройки ─────────────

    /// <summary>Настройки обхода для подключения; при ошибке в полях — сообщение и null.</summary>
    private BypassOptions? ReadBypass(Profile profile)
    {
        BypassOptions user;
        try
        {
            user = Bypass.ToOptions();
        }
        catch (ConfigException e)
        {
            Notice = "Исправьте настройки «Обход блокировок»: " + e.Message;
            return null;
        }
        return BypassFor(profile, user);
    }

    /// <summary>
    /// Настройки обхода для сервера: свои, а если панель его подписки прислала fragmentation-*/noises-* и это
    /// разрешено, — с её значениями. Неверные значения панели игнорируются (остаются свои) и пишутся в журнал.
    /// </summary>
    private BypassOptions BypassFor(Profile profile, BypassOptions user)
    {
        if (!UseSubscriptionBypass || profile.SubscriptionId is not { } id
            || data.Subscriptions.FirstOrDefault(s => s.Id == id) is not { Bypass: { } panel } sub)
            return user;
        try
        {
            return BypassRules.Validate(panel.ApplyTo(user));
        }
        catch (ConfigException e)
        {
            AppendLog("app", $"Настройки обхода от подписки «{Flags.Strip(sub.Name)}» не подошли ({e.Message}) — действуют свои");
            return user;
        }
    }

    partial void OnIsTunModeChanged(bool value) => SaveSettings();
    partial void OnKillSwitchChanged(bool value) => SaveSettings();
    partial void OnBlockQuicChanged(bool value) => SaveSettings();

    partial void OnUseServerRoutingChanged(bool value)
    {
        SaveSettings();
        if (!loadingSettings && IsActive && CurrentServer?.Profile is XrayConfigProfile)
            Notice = "Маршруты подписки изменятся при следующем подключении.";
    }
    partial void OnTunStackChanged(string value) => SaveSettings();
    partial void OnNotificationsChanged(bool value) => SaveSettings();

    partial void OnHotkeyTextChanged(string value)
    {
        try
        {
            GlobalHotkey.Parse(value);
            HotkeyError = null;
        }
        catch (FormatException e)
        {
            HotkeyError = e.Message;
            return;
        }
        SaveSettings();
        if (!loadingSettings)
            HotkeyChanged?.Invoke();
    }

    partial void OnMtuTextChanged(string value)
    {
        if (int.TryParse(value, out var mtu) && mtu is >= 1280 and <= 1500)
            SaveSettings();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (loadingSettings)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutostartKey);
            if (value)
                key.SetValue(Product.Id, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(Product.Id, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Notice = $"Не удалось изменить автозапуск: {e.Message}";
        }
    }

    private void SaveSettings()
    {
        if (loadingSettings)
            return;
        settings.TunMode = IsTunMode;
        settings.KillSwitch = KillSwitch;
        settings.BlockQuic = BlockQuic;
        settings.UseServerRouting = UseServerRouting;
        settings.TunStack = TunStack;
        settings.Notifications = Notifications;
        if (HotkeyError == null)
            settings.Hotkey = HotkeyText.Trim();
        if (int.TryParse(MtuText, out var mtu) && mtu is >= 1280 and <= 1500)
            settings.Mtu = mtu;
        try
        {
            settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = $"Не удалось сохранить настройки: {e.Message}";
        }
    }
}
