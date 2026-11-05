using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core;
using PVpn.Core.Cores;

namespace PVpn.App;

/// <summary>
/// Обновление программы из неё самой (см. <see cref="AppUpdater"/>): проверка при запуске, раз в несколько часов и по
/// кнопке, уведомление о новой версии (щелчок по нему — установить), скачивание установщика с проверкой SHA256 и тихая
/// установка — Windows спросит права администратора, установщик закроет программу и службу и запустит новую версию.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Как часто проверять обновления, пока программа работает.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(3);

    /// <summary>Проверка при запуске — не сразу: после входа в Windows сеть и VPN поднимаются не мгновенно.</summary>
    private static readonly TimeSpan StartupUpdateDelay = TimeSpan.FromSeconds(15);

    /// <summary>Есть новая версия — показать уведомление, щелчок по которому её ставит (см. App).</summary>
    public event Action<AppRelease>? UpdateNotification;

    [ObservableProperty] private string? updateText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private AppRelease? availableUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    private bool updating;

    public string UpdateButtonText => AvailableUpdate is { } r ? $"Установить {r.Version.ToString(3)}" : "Проверить";

    private static Version CurrentVersion => typeof(MainViewModel).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    private bool CanUpdateApp() => !Updating;

    [RelayCommand(CanExecute = nameof(CanUpdateApp))]
    private async Task UpdateAppAsync()
    {
        if (AvailableUpdate is { } release)
            await InstallUpdateAsync(release);
        else
            await CheckForUpdateAsync(quiet: false);
    }

    private async Task MaybeCheckAppUpdateAsync()
    {
        if (!AppUpdater.Configured || Updating || settings.AppUpdateChecked > DateTimeOffset.Now - UpdateCheckInterval)
            return;
        await CheckForUpdateAsync(quiet: true);
    }

    /// <summary>При запуске программы: есть новая версия — уведомление каждый раз, а не один раз на версию.</summary>
    public async Task CheckUpdateOnStartAsync()
    {
        if (SnapshotMode || !AppUpdater.Configured)
            return;
        await Task.Delay(StartupUpdateDelay);
        if (!Updating)
            await CheckForUpdateAsync(quiet: true, startup: true);
    }

    /// <summary>Щелчок по уведомлению: поставить найденную версию (или проверить заново, если её уже нет).</summary>
    public Task InstallFromNotificationAsync() => UpdateAppCommand.CanExecute(null) ? UpdateAppCommand.ExecuteAsync(null) : Task.CompletedTask;

    /// <param name="quiet">Проверка по расписанию: ошибки только в журнал, о новой версии — уведомление один раз.</param>
    /// <param name="startup">Проверка при запуске: о новой версии — уведомление, даже если о ней уже сообщали.</param>
    private async Task CheckForUpdateAsync(bool quiet, bool startup = false)
    {
        if (!AppUpdater.Configured)
        {
            UpdateText = "В этой сборке не указан источник обновлений.";
            return;
        }
        Updating = true;
        if (!quiet)
            UpdateText = "Проверяю…";
        try
        {
            using var http = CoreUpdater.CreateClient();
            var release = await AppUpdater.CheckAsync(http, CurrentVersion);
            settings.AppUpdateChecked = DateTimeOffset.Now;
            AvailableUpdate = release;
            if (release == null)
            {
                if (!quiet)
                    UpdateText = $"Установлена последняя версия ({AppVersion}).";
            }
            else
            {
                var version = release.Version.ToString(3);
                UpdateText = $"Доступна версия {version}." + (release.Notes is { } notes ? $"\n{notes}" : "");
                if (quiet && (startup || settings.AppUpdateNotified != version))
                {
                    settings.AppUpdateNotified = version;
                    AppendLog("app", $"Доступна новая версия {Brand.Name}: {version}");
                    UpdateNotification?.Invoke(release);
                }
            }
            SaveSettings();
        }
        catch (Exception e) when (e is AppUpdateException or HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            if (quiet)
                AppendLog("app", $"Проверка обновлений программы не удалась: {e.Message}");
            else
                UpdateText = "Не удалось проверить: " + e.Message;
        }
        finally
        {
            Updating = false;
        }
    }

    private async Task InstallUpdateAsync(AppRelease release)
    {
        // Портативная версия не ставится установщиком: новый архив — со страницы релиза.
        if (Product.IsPortable)
        {
            OpenLink(release.Page.AbsoluteUri);
            UpdateText = "Портативная версия обновляется вручную: скачайте новый архив со страницы релиза и распакуйте поверх.";
            return;
        }
        Updating = true;
        try
        {
            using var http = CoreUpdater.CreateClient();
            var progress = new Progress<long>(bytes => UpdateText = $"Скачиваю установщик… {bytes * 100 / Math.Max(release.Size, 1)}%");
            var path = await AppUpdater.DownloadAsync(http, release, Path.Combine(Product.DataDir(Environment.SpecialFolder.LocalApplicationData), "updates"), progress);
            // Установщик закроет программу; системный прокси возвращаем сами, пока программа жива. TUN выключит служба.
            if (proxy != null)
                await StopProxyAsync();
            AppendLog("app", $"Запускаю установщик {Brand.Name} {release.Version.ToString(3)}");
            // Без «runas»: установщик сам попросит права и сможет запустить программу от имени пользователя.
            Process.Start(new ProcessStartInfo(path, AppUpdater.SilentArguments) { UseShellExecute = true });
            UpdateText = "Установщик запущен: подтвердите запрос Windows. Программа закроется и откроется уже новой версией.";
        }
        catch (Exception e) when (e is AppUpdateException or HttpRequestException or TaskCanceledException or IOException or Win32Exception)
        {
            UpdateText = "Не удалось обновить: " + e.Message;
        }
        finally
        {
            Updating = false;
        }
    }
}
