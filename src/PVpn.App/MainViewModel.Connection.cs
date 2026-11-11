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
    // ───────────── Подключение ─────────────

    /// <summary>Кнопка, трей и горячая клавиша: решение пользователя, автоматика в этих сетях больше не вмешивается.</summary>
    [RelayCommand]
    private async Task ToggleConnectAsync()
    {
        // Отключиться можно всегда, а подключиться — только обновлённой программой.
        if (!IsActive && BlockedByUpdate())
            return;
        RememberUserChoice();
        if (IsActive)
            ForgetConnected();
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
                Remember = RememberForService,
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

    /// <summary>
    /// Выбрать сервер из трея или двойным щелчком в списке. Если VPN включён — сразу переключиться на него:
    /// служба сама остановит старое подключение, в режиме прокси перезапускается xray. Если выключен —
    /// подключиться (<paramref name="connectIfOff"/>, двойной щелчок) или только выбрать (трей).
    /// </summary>
    public async Task SwitchServerAsync(Guid id, bool connectIfOff = false)
    {
        if (BlockedByUpdate())
            return;
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
}
