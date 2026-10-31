using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Net;

namespace PVpn.App;

/// <summary>Автоподключение, доверенные сети и вход в сеть со страницей входа (гостиница, кафе).</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? networkDebounce;
    private bool loadingNetwork;
    private bool evaluating;

    /// <summary>Набор сетей, в котором пользователь сам включил или выключил VPN: здесь автоматика молчит.</summary>
    private string? userChoiceKey;

    public ObservableCollection<string> TrustedNetworks { get; } = [];

    [ObservableProperty] private bool autoConnect;
    [ObservableProperty] private bool disconnectOnTrusted;
    [ObservableProperty] private string trustedNetworkText = "";

    /// <summary>Подключённые сети сейчас (для подсказки и кнопки «Доверять этой сети»).</summary>
    [ObservableProperty] private string currentNetworks = "";

    /// <summary>Сеть просит войти (страница входа) — показать кнопку «Войти в сеть».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNetworkLogin))]
    private bool captiveDetected;

    /// <summary>Ждём, пока пользователь войдёт в сеть в браузере.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginToNetworkCommand))]
    private bool waitingForLogin;

    /// <summary>
    /// Кнопка «Войти в сеть» видна, когда портал найден или VPN не может подключиться (за порталом и с kill
    /// switch проверить сеть изнутри программы нельзя — так что предлагаем на всякий случай).
    /// </summary>
    public bool ShowNetworkLogin => CaptiveDetected || State == UiState.Error || StatusTitle.StartsWith("Переподключение", StringComparison.Ordinal);

    private void StartNetworkWatch()
    {
        loadingNetwork = true;
        AutoConnect = settings.AutoConnect;
        DisconnectOnTrusted = settings.DisconnectOnTrusted;
        TrustedNetworks.Clear();
        foreach (var n in settings.TrustedNetworks)
            TrustedNetworks.Add(n);
        loadingNetwork = false;

        networkDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        networkDebounce.Tick += async (_, _) =>
        {
            networkDebounce.Stop();
            await CheckProxyAdapterAsync();
            await EvaluateNetworkAsync();
        };
        // Событие приходит пачкой (адреса, маршруты, TUN) — решаем через 3 с после последнего.
        NetworkChange.NetworkAddressChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            networkDebounce.Stop();
            networkDebounce.Start();
        });
        _ = EvaluateNetworkAsync();
    }

    private void RememberUserChoice() => userChoiceKey = AutoConnectPolicy.Key(NetworkNames.GetConnected());

    /// <summary>Смена сети или запуск: подключиться, отключиться или ничего не делать.</summary>
    private async Task EvaluateNetworkAsync()
    {
        if (evaluating || networkDebounce == null || WaitingForLogin)
            return;
        evaluating = true;
        try
        {
            var networks = await Task.Run(NetworkNames.GetConnected);
            CurrentNetworks = networks.Count == 0 ? "нет" : string.Join(", ", networks.Select(n => $"«{n}»"));
            var key = AutoConnectPolicy.Key(networks);
            if (userChoiceKey != null && userChoiceKey != key)
                userChoiceKey = null;

            // Пока идёт подключение или отключение, не вмешиваемся.
            if (State == UiState.Busy && !StatusTitle.StartsWith("Переподключение", StringComparison.Ordinal))
                return;
            var action = AutoConnectPolicy.Decide(networks, TrustedNetworks, AutoConnect, DisconnectOnTrusted, IsActive, userChoiceKey == key);
            switch (action)
            {
                case AutoConnectAction.Disconnect:
                    AppendLog("app", $"Доверенная сеть ({CurrentNetworks}) — отключаю VPN");
                    await DisconnectAsync();
                    break;
                case AutoConnectAction.Connect when SelectedProfile != null:
                    // Без VPN и kill switch программа видит сеть как есть: проверяем, не просит ли она войти.
                    using (var http = CaptivePortal.CreateClient())
                    {
                        var internet = await CaptivePortal.CheckAsync(http);
                        CaptiveDetected = internet == InternetState.Portal;
                        if (internet == InternetState.Portal)
                        {
                            AppendLog("app", "Сеть просит войти (страница входа) — автоподключение ждёт");
                            Notification?.Invoke("Сеть просит войти", $"Нажмите «Войти в сеть» в {Brand.Name}: откроется страница входа, после неё VPN подключится сам.");
                            return;
                        }
                        if (internet == InternetState.Offline)
                            return;
                    }
                    AppendLog("app", $"Автоподключение: сеть {CurrentNetworks} не в доверенных");
                    await ConnectOrDisconnectAsync();
                    break;
            }
        }
        finally
        {
            evaluating = false;
        }
    }

    private bool CanLoginToNetwork() => !WaitingForLogin;

    /// <summary>
    /// Вход в сеть со страницей входа: отключить VPN (kill switch закрывал бы страницу), открыть страницу входа
    /// в браузере и подождать интернет. Потом VPN подключится сам, если был включён или включено автоподключение.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoginToNetwork))]
    private async Task LoginToNetworkAsync()
    {
        var reconnect = IsActive || State == UiState.Error || AutoConnect;
        WaitingForLogin = true;
        try
        {
            if (IsActive || State == UiState.Error)
                await DisconnectAsync();
            Process.Start(new ProcessStartInfo(CaptivePortal.LoginUrl) { UseShellExecute = true });
            Notice = "Войдите в сеть на открывшейся странице. Как только появится интернет, " + (reconnect ? "VPN подключится сам." : "можно подключаться.");
            using var http = CaptivePortal.CreateClient();
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (await CaptivePortal.CheckAsync(http) != InternetState.Online)
                    continue;
                CaptiveDetected = false;
                // Подключение очищает сообщение — пишем его после.
                if (reconnect && !IsActive)
                    await ConnectOrDisconnectAsync();
                Notice = reconnect ? "Вход в сеть выполнен, VPN подключается." : "Вход в сеть выполнен.";
                return;
            }
            Notice = "Интернет так и не появился за 10 минут. Проверьте вход на странице сети.";
        }
        finally
        {
            WaitingForLogin = false;
        }
    }

    [RelayCommand]
    private void TrustCurrentNetwork()
    {
        var networks = NetworkNames.GetConnected();
        if (networks.Count == 0)
        {
            Notice = "Сейчас нет подключённой сети.";
            return;
        }
        foreach (var n in networks)
            AddTrusted(n);
    }

    [RelayCommand]
    private void AddTrustedNetwork()
    {
        AddTrusted(TrustedNetworkText);
        TrustedNetworkText = "";
    }

    [RelayCommand]
    private void RemoveTrustedNetwork(string? name)
    {
        if (name != null && TrustedNetworks.Remove(name))
            SaveNetworkSettings();
    }

    private void AddTrusted(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || TrustedNetworks.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        TrustedNetworks.Add(name);
        SaveNetworkSettings();
    }

    partial void OnAutoConnectChanged(bool value)
    {
        SaveNetworkSettings();
        if (value)
            _ = EvaluateNetworkAsync();
    }

    partial void OnDisconnectOnTrustedChanged(bool value) => SaveNetworkSettings();

    private void SaveNetworkSettings()
    {
        if (loadingNetwork)
            return;
        settings.AutoConnect = AutoConnect;
        settings.DisconnectOnTrusted = DisconnectOnTrusted;
        settings.TrustedNetworks = [.. TrustedNetworks];
        SaveSettings();
        // Новая доверенная сеть может быть той, где мы сейчас.
        _ = EvaluateNetworkAsync();
    }
}
