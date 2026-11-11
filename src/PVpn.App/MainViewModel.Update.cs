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
/// Обновление программы из неё самой (см. <see cref="AppUpdater"/>). Обновление обязательное: как только найдена новая
/// версия (при запуске, раз в несколько часов или по кнопке), окно перекрывается экраном «Нужно обновить» — пока
/// программа не обновлена, подключиться или сменить сервер нельзя. Уже работающее подключение не рвётся (через него
/// может идти и само скачивание), автоподключение и восстановление после перезагрузки тоже работают: без них ПК
/// остался бы без защиты. Скачивание установщика с проверкой SHA256 и тихая установка — Windows спросит права
/// администратора, установщик закроет программу и службу и запустит новую версию.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Как часто проверять обновления, пока программа работает.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(3);

    /// <summary>Проверка при запуске: сразу, а если сети ещё нет (только вошли в Windows) — ещё раз через эти паузы.</summary>
    private static readonly TimeSpan[] StartupUpdateRetries = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>Найдена новая версия (или пользователь пытается подключиться без обновления) — показать окно с требованием.</summary>
    public event Action? UpdateRequiredShown;

    /// <summary>Кнопка «Выйти» на экране обновления.</summary>
    public event Action? ExitRequested;

    [ObservableProperty] private string? updateText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private AppRelease? availableUpdate;

    /// <summary>Есть новая версия: окно перекрыто экраном обновления, пока её не поставят.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredUpdateText), nameof(RequiredUpdateNotes), nameof(IsUsable))]
    private AppRelease? requiredUpdate;

    /// <summary>Описание выпуска с GitHub без разметки Markdown (жирный, код, списки, ссылки).</summary>
    public string? RequiredUpdateNotes => RequiredUpdate?.Notes is { } notes ? PlainNotes(notes) : null;

    internal static string PlainNotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var l = line.TrimEnd();
            l = System.Text.RegularExpressions.Regex.Replace(l, @"^#{1,6}\s+", "");
            l = System.Text.RegularExpressions.Regex.Replace(l, @"^(\s*)[-*]\s+", "$1• ");
            l = System.Text.RegularExpressions.Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1");
            return l.Replace("**", "").Replace("__", "").Replace("`", "");
        });
        return string.Join("\n", lines).Trim();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    private bool updating;

    /// <summary>Программой можно пользоваться (не ждёт обязательного обновления).</summary>
    public bool IsUsable => RequiredUpdate == null;

    public string UpdateButtonText => AvailableUpdate is { } r
        ? (Product.IsPortable ? $"Скачать {r.Version.ToString(3)}" : $"Обновить до {r.Version.ToString(3)}")
        : "Проверить";

    public string RequiredUpdateText => RequiredUpdate is not { } r ? "" :
        $"Вышла версия {r.Version.ToString(3)}, у вас {AppVersion}. Чтобы пользоваться {Brand.Name} дальше, обновите программу. " +
        (Product.IsPortable
            ? "Портативная версия обновляется вручную: скачайте новый архив и распакуйте его поверх этой папки."
            : "Установка займёт около минуты: Windows спросит разрешение, программа закроется и откроется уже новой версией. Серверы и настройки сохранятся.");

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

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    /// <summary>
    /// Действие пользователя (подключить, сменить сервер) при необходимом обновлении: не выполнять, а показать
    /// экран обновления. true — действие заблокировано.
    /// </summary>
    private bool BlockedByUpdate()
    {
        if (RequiredUpdate == null)
            return false;
        UpdateRequiredShown?.Invoke();
        return true;
    }

    private async Task MaybeCheckAppUpdateAsync()
    {
        if (!AppUpdater.Configured || Updating || settings.AppUpdateChecked > DateTimeOffset.Now - UpdateCheckInterval)
            return;
        await CheckForUpdateAsync(quiet: true);
    }

    /// <summary>При запуске программы: проверка сразу; сеть может появиться не сразу после входа — тогда повторы.</summary>
    public async Task CheckUpdateOnStartAsync()
    {
        if (SnapshotMode || !AppUpdater.Configured)
            return;
        if (await CheckForUpdateAsync(quiet: true))
            return;
        foreach (var delay in StartupUpdateRetries)
        {
            await Task.Delay(delay);
            if (Updating || RequiredUpdate != null)
                return;
            if (await CheckForUpdateAsync(quiet: true))
                return;
        }
    }

    /// <param name="quiet">Проверка по расписанию: ошибки только в журнал.</param>
    /// <returns>true — проверка прошла (есть новая версия или нет); false — не удалось связаться с GitHub.</returns>
    private async Task<bool> CheckForUpdateAsync(bool quiet)
    {
        if (!AppUpdater.Configured)
        {
            UpdateText = "В этой сборке не указан источник обновлений.";
            return true;
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
                RequiredUpdate = null;
                if (!quiet)
                    UpdateText = $"Установлена последняя версия ({AppVersion}).";
            }
            else
            {
                var version = release.Version.ToString(3);
                UpdateText = null;
                if (settings.AppUpdateNotified != version)
                {
                    settings.AppUpdateNotified = version;
                    AppendLog("app", $"Вышла новая версия {Brand.Name}: {version} — без обновления программой пользоваться нельзя");
                }
                if (!SnapshotMode)
                {
                    RequiredUpdate = release;
                    UpdateRequiredShown?.Invoke();
                }
            }
            SaveSettings();
            return true;
        }
        catch (Exception e) when (e is AppUpdateException or HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            if (quiet)
                AppendLog("app", $"Проверка обновлений программы не удалась: {e.Message}");
            else
                UpdateText = "Не удалось проверить: " + e.Message;
            return false;
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
            UpdateText = "Скачайте архив со страницы релиза, закройте программу и распакуйте его поверх этой папки.";
            return;
        }
        Updating = true;
        Process? installer = null;
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
            installer = Process.Start(new ProcessStartInfo(path, AppUpdater.SilentArguments) { UseShellExecute = true });
            UpdateText = "Установщик запущен: подтвердите запрос Windows. Программа закроется и откроется уже новой версией.";
        }
        catch (Exception e) when (e is AppUpdateException or HttpRequestException or TaskCanceledException or IOException or Win32Exception)
        {
            UpdateText = "Не удалось обновить: " + e.Message;
            Updating = false;
            return;
        }

        // Удачная установка закрывает эту программу. Дождались конца установщика, а мы ещё живы — её отменили
        // (отказ в запросе Windows) или она не удалась: можно попробовать снова.
        try
        {
            if (installer != null)
            {
                using (installer)
                    await installer.WaitForExitAsync();
                await Task.Delay(TimeSpan.FromSeconds(3));
                UpdateText = "Установка не завершилась (запрос Windows отклонён или ошибка). Нажмите «Обновить» ещё раз.";
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
        }
        finally
        {
            Updating = false;
        }
    }
}
