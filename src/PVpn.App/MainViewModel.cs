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

public enum UiState { Off, Busy, On, Error }

/// <summary>
/// Всё состояние окна и трея. Режим TUN ведёт служба (интерфейс только опрашивает её),
/// режим прокси — сам интерфейс (xray + системный прокси), без службы и без прав администратора.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogLines = 3000;
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ProfileStore store = new();
    private readonly AppSettings settings = AppSettings.Load();
    private readonly ServiceClient service = new();
    private readonly DispatcherTimer timer;
    private readonly List<string> logLines = [];
    private ProfileData data = new();
    private ProxySession? proxy;
    private bool proxyStarting;
    private DateTimeOffset proxySince;
    private DateTime proxyNextCheck;
    private readonly ProxyWatchdog watchdog = new();
    private long logSeq;
    private bool polling;
    private bool loadingSettings;
    private int tick;

    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    /// <summary>Предупреждения службы о текущем подключении (без скрытых крестиком).</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand), nameof(ShowQrCommand), nameof(EditProfileCommand))]
    private ProfileItem? selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyMode), nameof(ModeDescription), nameof(CanProtectBeforeLogin))]
    private bool isTunMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush), nameof(ConnectButtonText), nameof(CanChangeMode), nameof(IsActive), nameof(ShowNetworkLogin))]
    private UiState state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText), nameof(ShowNetworkLogin))]
    private string statusTitle = "Отключено";
    [ObservableProperty] private string? statusDetail;
    [ObservableProperty] private string? connectedServer;
    [ObservableProperty] private string? publicIp;
    [ObservableProperty] private string? delayText;
    [ObservableProperty] private string? killSwitchText;

    /// <summary>Скорость сейчас и трафик с начала подключения (обновляются каждую секунду).</summary>
    [ObservableProperty] private string? speedText;
    [ObservableProperty] private string? trafficText;
    private readonly TrafficMeter meter = new();
    private int? serviceMetricsPort;
    private readonly HttpClient metricsHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    [ObservableProperty] private string? serviceNotice;
    [ObservableProperty] private bool canInstallService;
    [ObservableProperty] private string? notice;
    [ObservableProperty] private string logText = "";
    [ObservableProperty] private string serviceVersion = "—";

    [ObservableProperty] private bool killSwitch;
    [ObservableProperty] private bool blockQuic;

    /// <summary>Маршруты и DNS из конфигов подписки (для серверов, которые панель отдала полным конфигом).</summary>
    [ObservableProperty] private bool useServerRouting;
    [ObservableProperty] private string mtuText = "1500";
    [ObservableProperty] private string tunStack = "gvisor";

    /// <summary>Сетевой стек TUN: gvisor надёжнее со сторонними сетевыми экранами, system быстрее на слабых ПК.</summary>
    public string[] TunStackChoices { get; } = ["gvisor", "system", "mixed"];
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool notifications;
    [ObservableProperty] private string hotkeyText = "";

    /// <summary>Сочетание не разобрано или занято другой программой; null — всё в порядке.</summary>
    [ObservableProperty] private string? hotkeyError;

    /// <summary>Сочетание изменилось (App перерегистрирует его).</summary>
    public event Action? HotkeyChanged;

    public bool IsProxyMode
    {
        get => !IsTunMode;
        set => IsTunMode = !value;
    }

    public string ModeDescription => IsTunMode
        ? $"Весь трафик компьютера идёт через VPN (нужна служба {Brand.Name})"
        : "Через VPN идут только программы, которые используют системный прокси";

    public Brush StateBrush => State switch
    {
        UiState.On => StatusBrushes.On,
        UiState.Busy => StatusBrushes.Busy,
        UiState.Error => StatusBrushes.Error,
        _ => StatusBrushes.Off,
    };

    public string ConnectButtonText => State switch
    {
        UiState.On => "Отключить",
        UiState.Busy when StatusTitle.StartsWith("Отключение", StringComparison.Ordinal) => "Отключаю…",
        UiState.Busy => "Отменить",
        _ => "Подключить",
    };

    /// <summary>Подключено или подключается (в любом режиме).</summary>
    public bool IsActive => State is UiState.On or UiState.Busy;
    public bool CanChangeMode => !IsActive;

    /// <summary>
    /// Окно рисуется в PNG (--snapshot): подключение службы не трогать — ни автоподключением, ни отключением при выходе.
    /// Снимок читает настоящую службу, и раньше его выход отключал работающий VPN.
    /// </summary>
    public bool SnapshotMode { get; init; }

    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Сменилось состояние, имя сервера или подсказка — для трея.</summary>
    public event Action? TrayChanged;

    public MainViewModel()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await PollAsync();
        Split = new SplitTunnelViewModel(settings.SplitTunnel, SaveSettings, text => Notice = text);
        Bypass = new BypassViewModel(() => settings.Bypass, value =>
        {
            settings.Bypass = value;
            SaveSettings();
        });
        Cores = new CoresViewModel(service, () => GeoSource);
        Dns = new DnsViewModel(() => settings.Dns, value =>
        {
            settings.Dns = value;
            SaveSettings();
        }, () => State != UiState.On
            ? new DnsLeakEnvironment(DnsLeakContext.Off, null)
            : proxy is { } p
                ? new DnsLeakEnvironment(DnsLeakContext.Proxy, new Uri($"http://127.0.0.1:{p.HttpPort}"))
                : new DnsLeakEnvironment(DnsLeakContext.Tun, null));
        Split.Changed += () =>
        {
            SplitPending = State == UiState.On;
            OnPropertyChanged(nameof(SplitSummary));
            OnPropertyChanged(nameof(FooterInfo));
        };
    }

    public SplitTunnelViewModel Split { get; }

    /// <summary>Раздел настроек «Обход блокировок» (действует со следующего подключения).</summary>
    public BypassViewModel Bypass { get; }

    /// <summary>Раздел настроек «Ядра»: версии, обновление и откат (делает служба).</summary>
    public CoresViewModel Cores { get; }

    /// <summary>Раздел настроек «DNS» (режим TUN, со следующего подключения) и проверка утечки.</summary>
    public DnsViewModel Dns { get; }

    /// <summary>Правила раздельного туннелирования изменились при включённом VPN — можно применить сразу.</summary>
    [ObservableProperty] private bool splitPending;

    /// <summary>Что действует сейчас (для карточки статуса); null — выключено.</summary>
    public string? SplitSummary => Split.Summary;

    [RelayCommand]
    private async Task ApplySplitAsync()
    {
        SplitPending = false;
        if (proxy is { } session)
        {
            // Режим прокси — наш собственный xray: просто перезапускаем его с новыми правилами.
            var profile = session.Profile;
            await StopProxyAsync();
            await ConnectProxyAsync(profile);
            return;
        }
        try
        {
            var r = await service.SendAsync(new IpcRequest
            {
                Command = IpcCommands.Reconfigure,
                Options = new ConnectOptions { SplitTunnel = Split.ToOptions() },
            }, TimeSpan.FromSeconds(60));
            Notice = r.Ok ? "Правила раздельного туннелирования применены." : r.Error;
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            Notice = e.Message;
        }
        await PollAsync();
    }

    public string TrayText => State switch
    {
        UiState.On => $"{Brand.Name} — подключено ({ConnectedServer})" + (SpeedText != null ? $"\n{SpeedText}" : ""),
        UiState.Busy => $"{Brand.Name} — " + StatusTitle.ToLowerInvariant(),
        UiState.Error => $"{Brand.Name} — ошибка",
        _ => $"{Brand.Name} — отключено",
    };

    public async Task InitializeAsync()
    {
        loadingSettings = true;
        // Портативная версия работает без службы — только режим прокси.
        IsTunMode = settings.TunMode && !Product.IsPortable;
        KillSwitch = settings.KillSwitch;
        BlockQuic = settings.BlockQuic;
        UseServerRouting = settings.UseServerRouting;
        MtuText = settings.Mtu.ToString();
        TunStack = TunStackChoices.Contains(settings.TunStack) ? settings.TunStack : "gvisor";
        Notifications = settings.Notifications;
        HotkeyText = settings.Hotkey;
        StartWithWindows = Registry.CurrentUser.OpenSubKey(AutostartKey)?.GetValue(Product.Id) != null;
        LoadRoutingSettings();
        loadingSettings = false;
        // До первого опроса службы: если она уже подключена (защита до входа), восстанавливать нечего.
        LoadResumeSettings();

        // Прошлый запуск интерфейса упал в режиме прокси: вернуть системный прокси как было.
        if (WinInetProxy.HasPendingRestore)
        {
            WinInetProxy.Restore();
            AppendLog("app", "Системный прокси возвращён к прежним настройкам (прошлый запуск завершился некорректно)");
        }

        LoadProfiles();
        await PollAsync();
        timer.Start();
        StartNetworkWatch();
        _ = Cores.LoadAsync();
        PingIfEnabled();
    }

    public async ValueTask DisposeAsync()
    {
        timer.Stop();
        if (SnapshotMode)
            return;
        // Вышли из программы сами — после перезагрузки VPN не поднимаем (выключение Windows сюда не попадает).
        ForgetConnected();
        // «Выход» выключает VPN в обоих режимах: прокси живёт в интерфейсе (системный прокси возвращается),
        // TUN держит служба — её просим отключиться. Сама служба остаётся запущенной.
        if (proxy != null || proxyStarting)
        {
            await StopProxyAsync();
            return;
        }
        if (IsActive)
        {
            try
            {
                await service.SendAsync(new IpcRequest { Command = IpcCommands.Disconnect }, TimeSpan.FromSeconds(15));
            }
            catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
            {
            }
        }
    }
}
