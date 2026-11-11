using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PVpn.Core.SystemProxy;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PVpn.App;

// CupOfTea.exe                            — окно и значок в трее
// CupOfTea.exe --tray                     — сразу в трей (автозапуск)
// CupOfTea.exe --snapshot <png> [--tab N] [--sub dns|bypass|tun|trusted|subs|cores] [--width N] [--height N]
//                                           — (разработка) отрисовать окно в PNG и выйти
public partial class App : Application
{
    private const string InstanceName = @"Local\" + Product.Id + ".App";

    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private MainViewModel? vm;
    private MainWindow? window;
    private TrayIcon? tray;
    private GlobalHotkey? hotkey;
    private bool trayHintShown;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        var snapshot = ArgValue(e.Args, "--snapshot");
        // Ссылка cupoftea://… (с сайта, из бота): Windows запускает программу с ней в аргументе.
        var link = e.Args.FirstOrDefault(MainViewModel.IsAppLink);

        if (snapshot == null && !BecomeSingleInstance(link))
        {
            Shutdown();
            return;
        }
        // Только тёмная тема; акцент — свой зелёный, а не цвет Windows.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        // Тёмная тема WPF-UI сама осветляет акцент: задаём оттенки явно, чтобы кнопки и переключатели были того же зелёного.
        var accent = (Color)FindResource("AccentColor");
        ApplicationAccentColorManager.Apply(accent, Color.FromRgb(0x2B, 0xB9, 0x88), accent, Color.FromRgb(0x6E, 0xE7, 0xB7));

        vm = new MainViewModel { SnapshotMode = snapshot != null };
        await vm.InitializeAsync();

        if (snapshot != null)
        {
            await SnapshotAsync(EnsureWindow(), snapshot, ArgValue(e.Args, "--tab"), ArgValue(e.Args, "--sub"), ArgValue(e.Args, "--width"), ArgValue(e.Args, "--height"));
            return;
        }

        tray = new TrayIcon(vm, ShowMainWindow, () => _ = ExitAsync());
        // Пока пользователь смотрит на окно, всплывающие уведомления не нужны: всё видно и так.
        vm.Notification += (title, text) =>
        {
            if (window is not { IsActive: true })
                tray.ShowBalloon(title, text);
        };
        // Новая версия обязательна: окно с требованием обновиться выходит на передний план.
        vm.UpdateRequiredShown += ShowMainWindow;
        vm.ExitRequested += () => _ = ExitAsync();
        releaseTimer = new DispatcherTimer { Interval = ReleaseDelay };
        releaseTimer.Tick += (_, _) => ReleaseHiddenWindow();
        // Закрыли окно, не обновившись, — через некоторое время оно появится снова.
        nagTimer = new DispatcherTimer { Interval = NagInterval };
        nagTimer.Tick += (_, _) =>
        {
            if (vm.RequiredUpdate != null && window is not { IsVisible: true })
                ShowMainWindow();
        };
        nagTimer.Start();
        hotkey = new GlobalHotkey(() => _ = vm.ToggleConnectCommand.ExecuteAsync(null));
        ApplyHotkey();
        vm.HotkeyChanged += ApplyHotkey;
        if (!e.Args.Contains("--tray"))
            ShowMainWindow();
        MainViewModel.RegisterLinkScheme();
        _ = vm.CheckUpdateOnStartAsync();
        if (link != null)
            await vm.HandleLinkAsync(link, fromOutside: true);
    }

    private void ApplyHotkey()
    {
        if (hotkey == null || vm == null)
            return;
        try
        {
            if (!hotkey.Set(GlobalHotkey.Parse(vm.HotkeyText)))
                vm.HotkeyError = $"Сочетание {vm.HotkeyText} уже занято другой программой — выберите другое.";
        }
        catch (FormatException ex)
        {
            vm.HotkeyError = ex.Message;
        }
    }

    /// <summary>Ссылка от второго запуска для первого (файл рядом с данными; живёт секунды).</summary>
    private static string PendingLinkPath => Path.Combine(Product.DataDir(Environment.SpecialFolder.LocalApplicationData), "pending-link.txt");

    /// <summary>Второй запуск не открывает второе окно, а показывает окно первого (и передаёт ему ссылку).</summary>
    private bool BecomeSingleInstance(string? link)
    {
        instance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            if (link != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PendingLinkPath)!);
                    File.WriteAllText(PendingLinkPath, link);
                }
                catch (IOException)
                {
                }
            }
            try
            {
                using var other = EventWaitHandle.OpenExisting(InstanceName + ".Show");
                other.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            instance.Dispose();
            instance = null;
            return false;
        }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var signal = showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(async () =>
                {
                    ShowMainWindow();
                    if (TakePendingLink() is { } pending && vm != null)
                        await vm.HandleLinkAsync(pending, fromOutside: true);
                });
        }) { IsBackground = true, Name = "Show signal" }.Start();
        return true;
    }

    private static string? TakePendingLink()
    {
        try
        {
            if (!File.Exists(PendingLinkPath))
                return null;
            var text = File.ReadAllText(PendingLinkPath).Trim();
            File.Delete(PendingLinkPath);
            return MainViewModel.IsAppLink(text) ? text : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void ShowMainWindow()
    {
        if (vm == null || exiting)
            return;
        releaseTimer?.Stop();
        var w = EnsureWindow();
        w.Show();
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Сколько окно живёт спрятанным, прежде чем освободить его (быстро открыть снова — без задержки).</summary>
    private static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Как часто возвращать окно «Нужно обновить», если его закрыли, не обновившись.</summary>
    private static readonly TimeSpan NagInterval = TimeSpan.FromMinutes(15);

    private DispatcherTimer? releaseTimer;
    private DispatcherTimer? nagTimer;

    /// <summary>
    /// Окно создаётся, только когда его показывают (запуск в трей — без окна вовсе). Всё состояние — в MainViewModel,
    /// так что новое окно открывается на той же странице.
    /// </summary>
    private MainWindow EnsureWindow()
    {
        if (window != null)
            return window;
        window = new MainWindow(vm!);
        MainWindow = window;
        window.HiddenToTray += OnHiddenToTray;
        return window;
    }

    private void OnHiddenToTray()
    {
        releaseTimer?.Stop();
        releaseTimer?.Start();
        if (trayHintShown || tray == null)
            return;
        trayHintShown = true;
        tray.ShowBalloon($"{Brand.Name} работает в трее", "Подключение не прерывается. Открыть окно — щелчок по значку.");
    }

    /// <summary>
    /// Окно спрятано в трей: освобождаем его целиком — дерево элементов, картинки флагов, ресурсы отрисовки. Скрытое
    /// окно держало в памяти около 200 МБ; без него программа в трее занимает в разы меньше.
    /// </summary>
    private void ReleaseHiddenWindow()
    {
        releaseTimer?.Stop();
        if (window is not { } w || w.IsVisible || exiting)
            return;
        window = null;
        w.HiddenToTray -= OnHiddenToTray;
        w.AllowClose = true;
        w.Close();
        if (ReferenceEquals(MainWindow, w))
            MainWindow = null;
        // Сборка мусора — когда окно действительно закрылось и интерфейс ничем не занят.
        Dispatcher.BeginInvoke(MemoryTrim.Run, DispatcherPriority.ApplicationIdle);
    }

    private async Task ExitAsync()
    {
        if (exiting)
            return;
        exiting = true;
        releaseTimer?.Stop();
        nagTimer?.Stop();
        // Каждый шаг — отдельно: ошибка в одном (вызов из меню трея уходит в «_ =», исключение никто не увидит)
        // не должна оставить процесс без значка и без окна, но живым.
        Try(() => tray?.Dispose());
        if (vm != null)
        {
            try
            {
                await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception e)
            {
                LogError("выход", e);
            }
        }
        Try(() => hotkey?.Dispose());
        Try(() =>
        {
            if (window != null)
            {
                window.AllowClose = true;
                window.Close();
            }
        });
        Try(() => instance?.ReleaseMutex());
        Try(() => instance?.Dispose());
        Shutdown();
        // Страховка: если что-то ещё держит процесс (поток, окно), через несколько секунд он завершается сам.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                LogError("выход", e);
            }
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Выход из Windows: режим прокси живёт в этом процессе — вернуть системный прокси прямо сейчас.
        if (WinInetProxy.HasPendingRestore)
            WinInetProxy.Restore();
        base.OnSessionEnding(e);
    }

    private async Task SnapshotAsync(MainWindow w, string path, string? tab, string? sub, string? width, string? height)
    {
        // Рисуем вне экрана.
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = 0;
        w.ShowActivated = false;
        if (int.TryParse(tab, out var index))
            w.SelectPage(index, sub);
        // Другой размер — проверить масштаб интерфейса.
        if (int.TryParse(width, out var wd))
            w.Width = wd;
        if (int.TryParse(height, out var h))
            w.Height = h;
        w.Show();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        var content = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        await ExitAsync();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("интерфейс", e.Exception);
        System.Windows.MessageBox.Show($"Ошибка в интерфейсе {Brand.Name}: {e.Exception.Message}", Brand.Name,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Ошибка в app-errors.log (%LOCALAPPDATA%\CupOfTea) — попадает в диагностику.</summary>
    private static void LogError(string where, Exception e)
    {
        try
        {
            var dir = Product.DataDir(Environment.SpecialFolder.LocalApplicationData);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"), $"{DateTime.Now:O} [{where}] {e}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
