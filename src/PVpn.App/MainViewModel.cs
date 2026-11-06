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
    [NotifyPropertyChangedFor(nameof(IsProxyMode), nameof(ModeDescription))]
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

    // ───────────── Серверы ─────────────

    private void LoadProfiles()
    {
        try
        {
            data = store.Load();
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or IOException)
        {
            Notice = $"Не удалось прочитать список серверов: {e.Message}";
            data = new ProfileData();
        }
        var selectedId = SelectedProfile?.Profile.Id ?? data.SelectedId;
        Profiles.Clear();
        for (var i = 0; i < data.Profiles.Count; i++)
        {
            var p = data.Profiles[i];
            Profiles.Add(new ProfileItem(p, data.Subscriptions.FirstOrDefault(s => s.Id == p.SubscriptionId)?.Name, i));
        }
        foreach (var auto in BuildAutoItems())
            Profiles.Add(auto);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        LoadSubscriptions();
    }

    /// <summary>
    /// Пока VPN включён или подключается, выделение в списке стоит на его сервере: одиночный щелчок по другому
    /// серверу его не переносит (переключает только двойной щелчок, <see cref="SwitchServerAsync"/>).
    /// </summary>
    partial void OnSelectedProfileChanged(ProfileItem? oldValue, ProfileItem? newValue)
    {
        if (!movingSelection && oldValue != null && newValue != null && State is UiState.On or UiState.Busy)
        {
            var active = Profiles.FirstOrDefault(p => ProfileData.PlainName(p.Profile.Name) == ConnectedServer) ?? oldValue;
            if (newValue != active)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MoveSelection(active));
                return;
            }
        }
        OnSelectedProfileChangedCore(newValue);
    }

    private bool movingSelection;

    private void MoveSelection(ProfileItem item)
    {
        movingSelection = true;
        try
        {
            SelectedProfile = item;
        }
        finally
        {
            movingSelection = false;
        }
    }

    private void OnSelectedProfileChangedCore(ProfileItem? value)
    {
        OnPropertyChanged(nameof(CurrentServer));
        UpdateAnnounce();
        if (value == null || data.SelectedId == value.Profile.Id)
            return;
        data.SelectedId = value.Profile.Id;
        TrySaveProfiles();
    }

    [RelayCommand]
    private void ImportClipboard()
    {
        string text;
        try
        {
            // Картинка в буфере (снимок экрана с QR-кодом) — читаем коды с неё.
            text = Clipboard.ContainsImage() && Clipboard.GetImage() is { } image
                ? string.Join(Environment.NewLine, QrCodes.Decode(image))
                : Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            text = "";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Notice = "В буфере обмена нет ссылок или QR-кода. Скопируйте ссылки vless:// или hy2:// (или картинку с QR) и нажмите ещё раз.";
            return;
        }
        // Ссылка действия от панели или бота (…://onadd/…, …://routing/onadd/…).
        if (IsAppLink(text))
        {
            _ = HandleLinkAsync(text.Trim(), fromOutside: false);
            return;
        }
        // Одна ссылка https:// — это ссылка подписки.
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _ = AddSubscriptionAsync(text.Trim());
            return;
        }
        Import(text);
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Файл со ссылками или картинка с QR-кодом",
            Filter = "Ссылки или QR (*.txt;*.png;*.jpg;*.jpeg;*.bmp)|*.txt;*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(dialog.FileName);
                image.EndInit();
                ImportQr(QrCodes.Decode(image), "на картинке");
            }
            else
                Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Notice = $"Не удалось прочитать файл: {e.Message}";
        }
    }

    /// <summary>QR-коды на экране: окно PVpn сворачивается, чтобы не закрывать код.</summary>
    [RelayCommand]
    private async Task ImportQrScreenAsync()
    {
        var window = Application.Current.MainWindow;
        var restore = window is { IsVisible: true, WindowState: not WindowState.Minimized };
        if (restore)
        {
            window!.WindowState = WindowState.Minimized;
            await Task.Delay(400);
        }
        try
        {
            ImportQr(QrCodes.DecodeScreens(), "на экране");
        }
        finally
        {
            if (restore)
            {
                window!.WindowState = WindowState.Normal;
                window.Activate();
            }
        }
    }

    private void ImportQr(IReadOnlyList<string> codes, string where)
    {
        if (codes.Count == 0)
        {
            Notice = $"QR-код {where} не найден. Откройте код крупнее и попробуйте ещё раз.";
            return;
        }
        // В коде может быть и ссылка подписки.
        if (codes.Count == 1 && Uri.TryCreate(codes[0].Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _ = AddSubscriptionAsync(codes[0].Trim());
            return;
        }
        Import(string.Join(Environment.NewLine, codes));
    }

    [RelayCommand(CanExecute = nameof(CanShowQr))]
    private void EditProfile()
    {
        if (SelectedProfile is not { } item)
            return;
        var dialog = new EditProfileWindow(item.Profile, item.Profile.SubscriptionId != null) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
            return;
        var index = data.Profiles.FindIndex(p => p.Id == edited.Id);
        if (index < 0)
            return;
        data.Profiles[index] = edited;
        if (TrySaveProfiles())
            Notice = State == UiState.On && ConnectedServer == item.Name ? "Сервер изменён. Новая ссылка подействует при следующем подключении." : "Сервер изменён.";
        LoadProfiles();
    }

    // У сервера с полным конфигом из подписки ссылки нет — ни QR, ни правки ссылки.
    private bool CanShowQr() => SelectedProfile is { Profile: not XrayConfigProfile };

    [RelayCommand(CanExecute = nameof(CanShowQr))]
    private void ShowQr()
    {
        if (SelectedProfile is not { } item)
            return;
        new QrWindow(item.Name, item.Profile.SourceLink) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    private void Import(string text)
    {
        var result = LinkParser.ParseMany(text);
        var added = 0;
        Profile? last = null;
        foreach (var p in result.Profiles)
        {
            if (data.Upsert(p))
                added++;
            last = p;
        }
        if (result.Profiles.Count > 0 && !TrySaveProfiles())
            return;
        LoadProfiles();
        if (last != null)
            SelectedProfile = Profiles.FirstOrDefault(i => string.Equals(i.Profile.Name, last.Name, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.Append(result.Profiles.Count == 0 ? "Ссылок не найдено." : $"Серверов: новых {added}, обновлено {result.Profiles.Count - added}.");
        foreach (var e in result.Errors.Take(3))
            sb.Append($" Строка {e.Line}: {e.Message}.");
        if (result.Errors.Count > 3)
            sb.Append($" И ещё ошибок: {result.Errors.Count - 3}.");
        Notice = sb.ToString();
    }

    private bool CanDeleteProfile() => SelectedProfile is { IsAuto: false };

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } item)
            return;
        if (MessageBox.Show($"Удалить сервер «{item.Name}»?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        data.Profiles.RemoveAll(p => p.Id == item.Profile.Id);
        if (data.SelectedId == item.Profile.Id)
            data.SelectedId = null;
        TrySaveProfiles();
        SelectedProfile = null;
        LoadProfiles();
    }

    private bool TrySaveProfiles()
    {
        try
        {
            store.Save(data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Notice = $"Не удалось сохранить список серверов: {e.Message}";
            return false;
        }
    }

    /// <summary>Сколько серверов проверяется одновременно (у каждого свой временный xray).</summary>
    private const int ParallelDelayChecks = 4;
    private bool checkingDelays;

    /// <summary>
    /// Настоящая задержка: временный xray и запрос generate_204 через сервер (как у браузера).
    /// Серверы проверяются по несколько сразу: по одному подписка на 20 серверов проверялась бы минуты.
    /// </summary>
    [RelayCommand]
    private async Task CheckDelaysAsync()
    {
        if (checkingDelays)
            return;
        CorePaths cores;
        try
        {
            cores = CorePaths.Resolve(allowDevFallback: true);
        }
        catch (FileNotFoundException e)
        {
            Notice = e.Message;
            return;
        }
        checkingDelays = true;
        var items = Profiles.ToList();
        foreach (var item in items)
            item.Checking = true;
        try
        {
            // Один xray на все серверы; не запустился (например, из-за одного кривого сервера) — по одному, как раньше.
            var byId = items.ToDictionary(i => i.Profile.Id);
            try
            {
                // Автовыбор не пингуется сам: его задержка — лучшая из его серверов (ShowAutoDelays).
                await BatchPing.MeasureAsync(items.Where(i => !i.IsAuto).Select(i => new PingTarget(i.Profile, BypassFor(i.Profile, settings.Bypass))).ToList(), cores,
                    (id, delay) => Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (byId.TryGetValue(id, out var item))
                        {
                            item.Delay = delay;
                            item.Checking = false;
                        }
                    }));
                return;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                AppendLog("app", $"Пинг одним процессом не вышел ({e.Message.Split('\n')[0]}), проверяю по одному");
            }
            await PingOneByOneAsync(items.Where(i => i.Checking && !i.IsAuto).ToList(), cores);
        }
        finally
        {
            foreach (var item in items)
                item.Checking = false;
            ShowAutoDelays();
            checkingDelays = false;
        }
    }

    private async Task PingOneByOneAsync(IReadOnlyList<ProfileItem> items, CorePaths cores)
    {
        using var limit = new SemaphoreSlim(ParallelDelayChecks);
        {
            await Task.WhenAll(items.Select(async item =>
            {
                await limit.WaitAsync();
                try
                {
                    await using var session = await ProxySession.StartAsync(item.Profile, cores,
                        new ProxySessionOptions { SocksPort = 0, HttpPort = -1, Bypass = BypassFor(item.Profile, settings.Bypass), UseServerRouting = settings.UseServerRouting }, (_, _) => { });
                    item.Delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10));
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ConfigException)
                {
                    item.Delay = new DelayResult(null, e.Message);
                }
                finally
                {
                    item.Checking = false;
                    limit.Release();
                }
            }));
        }
    }

    // ───────────── Подключение ─────────────

    /// <summary>Кнопка, трей и горячая клавиша: решение пользователя, автоматика в этих сетях больше не вмешивается.</summary>
    [RelayCommand]
    private async Task ToggleConnectAsync()
    {
        RememberUserChoice();
        await ConnectOrDisconnectAsync();
    }

    private async Task ConnectOrDisconnectAsync()
    {
        Notice = null;
        if (IsActive)
        {
            await DisconnectAsync();
            return;
        }
        if (SelectedProfile is not { } item)
        {
            Notice = "Выберите сервер в списке (или добавьте ссылки кнопкой «Вставить ссылки»).";
            return;
        }
        if (IsTunMode)
            await ConnectTunAsync(item.Profile);
        else
            await ConnectProxyAsync(item.Profile);
    }

    public async Task DisconnectAsync()
    {
        if (proxy != null || proxyStarting)
        {
            await StopProxyAsync();
            return;
        }
        try
        {
            SetState(UiState.Busy, "Отключение…", null);
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Disconnect }, TimeSpan.FromSeconds(60));
            if (!r.Ok)
                Notice = r.Error;
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            Notice = e.Message;
        }
        await PollAsync();
    }

    private async Task ConnectTunAsync(Profile profile)
    {
        if (!int.TryParse(MtuText, out var mtu) || mtu is < 1280 or > 1500)
        {
            Notice = "MTU в настройках должен быть числом от 1280 до 1500.";
            return;
        }
        if (Materialize(profile) is not { } materialized)
            return;
        profile = materialized;
        if (ReadBypass(profile) is not { } bypass)
            return;
        DnsOptions dns;
        try
        {
            dns = Dns.ToOptions();
        }
        catch (ConfigException e)
        {
            Notice = "Исправьте настройки DNS: " + e.Message;
            return;
        }
        try
        {
            SetState(UiState.Busy, "Подключение…", "Отправляю запрос службе");
            var r = await service.SendAsync(new IpcRequest
            {
                Command = IpcCommands.Connect,
                // Сервер из подписки с полным конфигом — конфигом (служба очистит его сама), иначе ссылкой.
                Link = profile is XrayConfigProfile ? null : profile.SourceLink,
                Config = (profile as XrayConfigProfile)?.Config,
                Options = new ConnectOptions
                {
                    UseServerRouting = UseServerRouting,
                    Routing = EffectiveRouting(),
                    KillSwitch = KillSwitch,
                    BlockQuic = BlockQuic,
                    Bypass = bypass,
                    Dns = dns,
                    Mtu = mtu,
                    Stack = TunStack,
                    SplitTunnel = Split.ToOptions(),
                },
            }, TimeSpan.FromSeconds(30));
            SplitPending = false;
            if (!r.Ok)
                SetState(UiState.Error, "Ошибка", r.Error);
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            SetState(UiState.Error, "Ошибка", e.Message);
        }
        await PollAsync();
    }

    /// <param name="restart">Перезапуск сторожем: его счётчики не сбрасываются.</param>
    private async Task ConnectProxyAsync(Profile profile, bool restart = false)
    {
        if (!restart)
            watchdog.Reset();
        if (Materialize(profile) is not { } materialized)
            return;
        profile = materialized;
        if (ReadBypass(profile) is not { } bypass)
            return;
        CorePaths cores;
        try
        {
            cores = CorePaths.Resolve(allowDevFallback: true);
        }
        catch (FileNotFoundException e)
        {
            SetState(UiState.Error, "Ошибка", e.Message);
            return;
        }
        proxyStarting = true;
        SetState(UiState.Busy, "Подключение…", "Запускаю xray");
        try
        {
            var session = await ProxySession.StartAsync(profile, cores, new ProxySessionOptions
            {
                SetSystemProxy = true,
                Bypass = bypass,
                SplitTunnel = Split.ToOptions(),
                UseServerRouting = UseServerRouting,
                Routing = EffectiveRouting(),
            }, (source, line) => Application.Current?.Dispatcher.BeginInvoke(() => AppendLog(source, line)));
            proxy = session;
            proxySince = DateTimeOffset.Now;
            SplitPending = false;
            AppendLog("app", $"Прокси: {profile.Name}, SOCKS 127.0.0.1:{session.SocksPort}, HTTP 127.0.0.1:{session.HttpPort}, адаптер «{session.BoundInterface ?? "по умолчанию"}»");
            proxyNextCheck = DateTime.MinValue;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ConfigException)
        {
            SetState(UiState.Error, "Ошибка", e.Message);
        }
        finally
        {
            proxyStarting = false;
        }
        await PollAsync();
    }

    private async Task StopProxyAsync()
    {
        var session = proxy;
        proxy = null;
        if (session != null)
        {
            SetState(UiState.Busy, "Отключение…", "Возвращаю системный прокси");
            await session.DisposeAsync();
            AppendLog("app", "Прокси выключен, системный прокси восстановлен");
        }
        PublicIp = null;
        DelayText = null;
        SetState(UiState.Off, "Отключено", null);
        TrayChanged?.Invoke();
    }

    // ───────────── Состояние ─────────────

    private async Task PollAsync()
    {
        if (polling)
            return;
        polling = true;
        try
        {
            tick++;
            if (tick % 60 == 5)
                _ = AutoUpdateSubscriptionsAsync();
            // Повтор автоподключения (служба ещё не запустилась после загрузки, сервер не отвечал).
            if (tick % 60 == 30)
                _ = EvaluateNetworkAsync();
            // Свой профиль маршрутизации по ссылке и базы geoip/geosite — проверка, не пора ли обновить.
            if (tick % 60 == 45)
                _ = RefreshCustomRoutingAsync();
            if (tick % 300 == 50)
                _ = MaybeUpdateGeoAsync();
            if (tick % 300 == 70)
                _ = MaybeCheckAppUpdateAsync();
            if (proxy != null || proxyStarting)
                await PollProxyAsync();
            else
                await PollServiceAsync();
            await MeasureTrafficAsync();
            UpdateSessionTime();
        }
        finally
        {
            polling = false;
            TrayChanged?.Invoke();
        }
    }

    private async Task MeasureTrafficAsync()
    {
        TrafficSample? sample = null;
        if (State == UiState.On)
        {
            // Счётчики адаптера wintun не годятся: Windows показывает в них загрузку и как отправленное.
            var port = proxy is { } session ? session.MetricsPort : serviceMetricsPort;
            if (port is > 0)
                sample = await TrafficSources.ReadXrayAsync(metricsHttp, port.Value, XrayConfigBuilder.ProxyTag);
        }
        if (sample is not { } s)
        {
            meter.Reset();
            SpeedText = TrafficText = null;
            ShowTraffic(null);
            return;
        }
        meter.Add(s, DateTime.UtcNow);
        ShowTraffic(meter);
        SpeedText = $"↓ {TrafficMeter.FormatSpeed(meter.DownPerSecond)}   ↑ {TrafficMeter.FormatSpeed(meter.UpPerSecond)}";
        TrafficText = $"↓ {TrafficMeter.FormatBytes(meter.SessionDown)}   ↑ {TrafficMeter.FormatBytes(meter.SessionUp)}";
    }

    private async Task PollProxyAsync()
    {
        if (proxy is not { } session)
            return;
        ConnectedServer = ProfileData.PlainName(session.Profile.Name);
        KillSwitchText = null;
        sessionSince = proxySince;
        if (session.Xray.HasExited)
        {
            if (watchdog.CoreExited() == WatchdogAction.Restart)
            {
                await RestartProxyAsync("xray неожиданно завершился");
                return;
            }
            AppendLog("app", "xray неожиданно завершился, прокси выключен");
            await StopProxyAsync();
            SetState(UiState.Error, "Ошибка", "xray завершился. Подробности — в «Журнале».");
            return;
        }
        SetState(UiState.On, "Подключено", $"Режим прокси · SOCKS 127.0.0.1:{session.SocksPort} · HTTP 127.0.0.1:{session.HttpPort}");
        if (DateTime.UtcNow < proxyNextCheck)
            return;
        proxyNextCheck = DateTime.UtcNow.Add(ProxyWatchdog.CheckInterval);
        var delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10));
        var ip = delay.Ok ? await DelayTester.GetPublicIpViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10)) : null;
        if (proxy != session)
            return;
        PublicIp = ip ?? "—";
        DelayText = delay.Ok ? $"{delay.Milliseconds} мс" : "нет ответа";
        var gaveUp = watchdog.GaveUp;
        var action = watchdog.Report(delay.Ok, NetworkInterface.GetIsNetworkAvailable());
        proxyNextCheck = DateTime.UtcNow.Add(watchdog.NextCheckIn);
        if (action == WatchdogAction.Restart)
            await RestartProxyAsync($"сервер не отвечает ({delay.Error})");
        else if (watchdog.GaveUp && !gaveUp)
        {
            AppendLog("app", $"Сторож: после {ProxyWatchdog.MaxRestarts} перезапусков сервер так и не ответил — дальше только наблюдаю");
            Notification?.Invoke("Сервер не отвечает", $"Перезапуск xray не помог. Попробуйте другой сервер в {Brand.Name}.");
        }
    }

    /// <summary>
    /// Сторож: перезапуск xray с тем же сервером, не трогая состояние «выключено» (системный прокси вернётся к
    /// новому порту сразу после запуска).
    /// </summary>
    private async Task RestartProxyAsync(string reason)
    {
        if (proxy is not { } old)
            return;
        AppendLog("app", $"Сторож: {reason} — перезапускаю xray (попытка {watchdog.Restarts} из {ProxyWatchdog.MaxRestarts})");
        Notification?.Invoke("Переподключение", $"{ProfileData.PlainName(old.Profile.Name)}: {reason}. Перезапускаю соединение.");
        proxy = null;
        proxyStarting = true;
        SetState(UiState.Busy, "Переподключение…", reason);
        await old.DisposeAsync();
        proxyStarting = false;
        await ConnectProxyAsync(old.Profile, restart: true);
        if (proxy == null && State == UiState.Busy)
            SetState(UiState.Off, "Отключено", null);
    }

    /// <summary>Сменился физический адаптер (Wi-Fi → кабель): xray привязан к старому — перезапускаем.</summary>
    private async Task CheckProxyAdapterAsync()
    {
        if (proxy is not { BoundInterface: { } bound } || proxyStarting)
            return;
        var current = await Task.Run(() => NetworkInfo.GetDefaultPhysicalInterface()?.Name);
        if (current != null && current != bound)
            await RestartProxyAsync($"сеть сменилась: «{bound}» → «{current}»");
    }

    private async Task PollServiceAsync()
    {
        var installed = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{IpcConstants.ServiceName}") != null;
        ServiceStatus status;
        try
        {
            status = (await service.StatusAsync()).Status ?? new ServiceStatus();
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            ServiceVersion = "—";
            CanInstallService = !installed && File.Exists(InstallScript);
            ServiceNotice = installed
                ? $"Служба {Brand.Name} не отвечает ({e.Message}). Режим TUN недоступен, режим прокси работает."
                : $"Служба {Brand.Name} не установлена: режим TUN недоступен. Установите её (один раз нужны права администратора) или пользуйтесь режимом прокси.";
            if (IsTunMode && State != UiState.Error)
                SetState(UiState.Off, "Отключено", null);
            return;
        }

        ServiceNotice = null;
        CanInstallService = false;
        ServiceVersion = status.ServiceVersion;
        // Служба подключена (например, интерфейс запустили заново) — показываем её режим.
        if (status.State != SessionState.Disconnected && !IsTunMode)
            IsTunMode = true;
        ApplyServiceStatus(status);

        if (tick % 2 == 0)
            await PollLogsAsync();
    }

    private void ApplyServiceStatus(ServiceStatus s)
    {
        serviceMetricsPort = s.MetricsPort;
        sessionSince = s.State == SessionState.Connected ? s.Since : null;
        ConnectedServer = s.ProfileName != null ? ProfileData.PlainName(s.ProfileName) : null;
        PublicIp = s.State == SessionState.Connected ? s.PublicIp ?? (s.PublicIpChecked ? "—" : "проверяю…") : null;
        DelayText = s.State == SessionState.Connected ? (s.DelayMs is { } d ? $"{d} мс" : "—") : null;
        KillSwitchText = s.State == SessionState.Connected ? (s.KillSwitchActive ? "включён" : "выключен") : null;
        var server = ConnectedServer != null ? $"{ConnectedServer} · {s.Protocol}" : null;
        switch (s.State)
        {
            case SessionState.Connected:
                SetState(UiState.On, "Подключено", s.Message is null or "Подключено" ? server : $"{server}. {s.Message}");
                break;
            case SessionState.Connecting:
                SetState(UiState.Busy, "Подключение…", s.Message);
                break;
            case SessionState.Reconnecting:
                SetState(UiState.Busy, "Переподключение…", s.Message);
                break;
            case SessionState.Disconnecting:
                SetState(UiState.Busy, "Отключение…", s.Message);
                break;
            case SessionState.Error:
                SetState(UiState.Error, "Ошибка", s.Message);
                break;
            default:
                SetState(UiState.Off, "Отключено", s.Message is null or "Отключено" ? null : s.Message);
                break;
        }
        // Скрытые крестиком предупреждения не возвращаются, пока служба их не сняла (тогда скрытие забывается).
        dismissedWarnings.IntersectWith(s.Warnings);
        var visible = s.Warnings.Where(w => !dismissedWarnings.Contains(w)).ToList();
        if (!visible.SequenceEqual(Warnings))
        {
            Warnings.Clear();
            foreach (var w in visible)
                Warnings.Add(w);
        }
    }

    private readonly HashSet<string> dismissedWarnings = [];

    [RelayCommand]
    private void DismissWarning(string? text)
    {
        if (text != null && Warnings.Remove(text))
            dismissedWarnings.Add(text);
    }

    partial void OnStateChanged(UiState value)
    {
        UpdateSessionTime();
        OnHomeChanged();
        if (value != UiState.On)
            SplitPending = false;
    }

    /// <summary>Уведомление для трея: заголовок и текст.</summary>
    public event Action<string, string>? Notification;

    private void SetState(UiState newState, string title, string? detail)
    {
        if (settings.Notifications && (newState != State || title != StatusTitle) && Describe(State, StatusTitle, newState, title, detail) is { } note)
            Notification?.Invoke(note.Title, note.Text);
        State = newState;
        StatusTitle = title;
        StatusDetail = detail;
    }

    /// <summary>О каких переходах сообщать: подключение, обрыв и восстановление, ошибка, отключение не по кнопке.</summary>
    private (string Title, string Text)? Describe(UiState oldState, string oldTitle, UiState newState, string newTitle, string? detail)
    {
        var server = ConnectedServer ?? "сервер";
        return (oldState, newState) switch
        {
            (UiState.Busy, UiState.On) when oldTitle.StartsWith("Переподключение", StringComparison.Ordinal) => ("Связь восстановлена", server),
            (UiState.Busy, UiState.On) => ("VPN подключён", server),
            (UiState.On, UiState.Busy) when newTitle.StartsWith("Переподключение", StringComparison.Ordinal) =>
                ("Связь прервалась", "Переподключаюсь… Kill switch не пускает трафик мимо VPN."),
            (UiState.On or UiState.Busy, UiState.Error) => ("Ошибка VPN", detail ?? $"Подробности в окне {Brand.Name}"),
            (UiState.On, UiState.Off) => ("VPN отключён", detail ?? server),
            _ => null,
        };
    }

    /// <summary>Архив для разбора проблем на рабочем столе (без ключей серверов и токенов подписок).</summary>
    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        var files = new Dictionary<string, string>
        {
            ["info.txt"] = string.Join(Environment.NewLine,
                $"{Brand.Name} {AppVersion}, служба {ServiceVersion}, {Environment.OSVersion}, {DateTime.Now:dd.MM.yyyy HH:mm:ss}",
                $"Режим: {(IsTunMode ? "TUN" : "прокси")}, состояние: {StatusTitle}. {StatusDetail}",
                $"Сервер: {ConnectedServer ?? "—"}, IP {PublicIp ?? "—"}, задержка {DelayText ?? "—"}, kill switch {KillSwitchText ?? "—"}",
                $"Раздельное туннелирование: {SplitSummary ?? "выключено"}",
                "Предупреждения: " + string.Join("; ", Warnings)),
            ["network.txt"] = DiagnosticsReport.Network(IpcConstants.TunInterfaceName),
            ["servers.txt"] = DiagnosticsReport.Servers(data),
            ["app-log.txt"] = FullLog(),
        };
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Logs }, TimeSpan.FromSeconds(10));
            files["service-log.txt"] = string.Join(Environment.NewLine, (r.Logs ?? []).Select(l => $"{l.Time.ToLocalTime():dd.MM HH:mm:ss} [{l.Source}] {l.Text}"));
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            files["service-log.txt"] = $"Служба недоступна: {e.Message}";
        }
        foreach (var (name, path) in new[] { ("settings.json", AppSettings.FilePath),
                     ("app-errors.log", Path.Combine(Product.DataDir(Environment.SpecialFolder.LocalApplicationData), "app-errors.log")) })
        {
            if (File.Exists(path))
                files[name] = await File.ReadAllTextAsync(path);
        }

        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{Brand.Name.Replace(' ', '-')}-диагностика-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            DiagnosticsReport.Write(zip, files, DiagnosticsReport.Secrets(data));
            Notice = $"Диагностика сохранена: {zip}. Ключей серверов и ссылок подписок в ней нет.";
            Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = $"Не удалось сохранить диагностику: {e.Message}";
        }
    }

    // ───────────── Логи ─────────────

    private async Task PollLogsAsync()
    {
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Logs, LogsAfter = logSeq }, TimeSpan.FromSeconds(5));
            if (r.Logs is not { Count: > 0 } lines)
                return;
            foreach (var l in lines)
            {
                logSeq = Math.Max(logSeq, l.Seq);
                logLines.Add($"{l.Time.ToLocalTime():HH:mm:ss} [{l.Source}] {l.Text}");
            }
            TrimAndPublishLogs();
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
        }
    }

    private void AppendLog(string source, string text)
    {
        logLines.Add($"{DateTime.Now:HH:mm:ss} [{source}] {text}");
        TrimAndPublishLogs();
    }

    /// <summary>Строки пришли, а страница журнала закрыта: текст соберём, когда её откроют.</summary>
    private bool logDirty;

    private void TrimAndPublishLogs()
    {
        if (logLines.Count > MaxLogLines)
            logLines.RemoveRange(0, logLines.Count - MaxLogLines);
        // Собирать 3000 строк и перерисовывать их в TextBox каждые 2 с, пока журнал никто не смотрит, — зря грузить ЦП.
        if (Page != LogPage)
        {
            logDirty = true;
            return;
        }
        logDirty = false;
        LogText = FullLog();
    }

    private string FullLog() => string.Join(Environment.NewLine, logLines);

    partial void OnPageChanged(int value)
    {
        SettingsPage = null;
        if (value == LogPage && logDirty)
            TrimAndPublishLogs();
    }

    [RelayCommand]
    private void CopyLogs()
    {
        try
        {
            Clipboard.SetText(FullLog());
            Notice = "Логи скопированы в буфер обмена (ключи и пароли в них скрыты).";
        }
        catch (System.Runtime.InteropServices.ExternalException e)
        {
            Notice = $"Не удалось скопировать: {e.Message}";
        }
    }

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

    /// <summary>
    /// Выбрать сервер из трея или двойным щелчком в списке. Если VPN включён — сразу переключиться на него:
    /// служба сама остановит старое подключение, в режиме прокси перезапускается xray. Если выключен —
    /// подключиться (<paramref name="connectIfOff"/>, двойной щелчок) или только выбрать (трей).
    /// </summary>
    public async Task SwitchServerAsync(Guid id, bool connectIfOff = false)
    {
        if (Profiles.FirstOrDefault(p => p.Profile.Id == id) is not { } item)
            return;
        // Идёт подключение в TUN — служба сама отменит его и подключится к новому серверу.
        // Отключение и запуск прокси не прерываем.
        if (State == UiState.Busy)
        {
            if (!IsTunMode || proxy != null || proxyStarting || StatusTitle.StartsWith("Отключение", StringComparison.Ordinal))
                return;
            MoveSelection(item);
            RememberUserChoice();
            await ConnectTunAsync(item.Profile);
            return;
        }
        MoveSelection(item);
        RememberUserChoice();
        if (State != UiState.On)
        {
            if (connectIfOff)
                await ConnectOrDisconnectAsync();
            return;
        }
        // Уже на этом сервере (сравнение по имени: после перезапуска окна служба знает только имя).
        if (ConnectedServer == ProfileData.PlainName(item.Profile.Name))
            return;
        if (proxy != null)
        {
            await StopProxyAsync();
            await ConnectProxyAsync(item.Profile);
        }
        else
            await ConnectTunAsync(item.Profile);
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

    public async ValueTask DisposeAsync()
    {
        timer.Stop();
        if (SnapshotMode)
            return;
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
