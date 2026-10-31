using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;
using PVpn.Core.SystemProxy;

namespace PVpn.Core.Sessions;

public sealed record ProxySessionOptions
{
    // Не 10808/10809: их по умолчанию занимают другие VPN-клиенты.
    public const int DefaultSocksPort = 10828;
    public const int DefaultHttpPort = 10829;

    /// <summary>0 — выбрать свободный порт. Если порт занят, берётся свободный.</summary>
    public int SocksPort { get; init; } = DefaultSocksPort;

    /// <summary>0 — выбрать свободный порт, -1 — без HTTP-входа. Если порт занят, берётся свободный.</summary>
    public int HttpPort { get; init; } = DefaultHttpPort;

    /// <summary>Привязать соединение с сервером к физическому адаптеру (мимо чужих TUN).</summary>
    public bool BindToPhysicalInterface { get; init; } = true;

    /// <summary>Включить системный прокси Windows на HTTP-вход.</summary>
    public bool SetSystemProxy { get; init; }

    /// <summary>Обход блокировок (см. <see cref="BypassOptions"/>); проверяется при запуске.</summary>
    public BypassOptions Bypass { get; init; } = BypassOptions.Default;

    public string LogLevel { get; init; } = "warning";

    /// <summary>Раздельное туннелирование: в режиме прокси действуют только сайты (см. XrayOptions.SplitTunnel).</summary>
    public SplitTunnelOptions? SplitTunnel { get; init; }

    /// <summary>У серверов с конфигом из подписки — их маршруты (см. XrayOptions.UseServerRouting).</summary>
    public bool UseServerRouting { get; init; } = true;
}

/// <summary>
/// Режим прокси: xray с SOCKS и HTTP на 127.0.0.1, по желанию — системный прокси.
/// Права администратора не нужны, служба тоже.
/// </summary>
public sealed class ProxySession : IAsyncDisposable
{
    public Profile Profile { get; }
    public int SocksPort { get; }
    public int HttpPort { get; }

    /// <summary>Порт статистики xray (см. <see cref="Diagnostics.TrafficSources.ReadXrayAsync"/>).</summary>
    public int MetricsPort { get; }
    public string? BoundInterface { get; }
    public CoreProcess Xray { get; }
    public bool SystemProxyEnabled { get; private set; }

    private ProxySession(Profile profile, int socksPort, int httpPort, int metricsPort, string? boundInterface, CoreProcess xray)
    {
        Profile = profile;
        SocksPort = socksPort;
        HttpPort = httpPort;
        MetricsPort = metricsPort;
        BoundInterface = boundInterface;
        Xray = xray;
    }

    public static async Task<ProxySession> StartAsync(Profile profile, CorePaths cores, ProxySessionOptions options,
        Action<string, string> onLog, CancellationToken ct = default)
    {
        var socksPort = PickPort(options.SocksPort, "SOCKS", onLog);
        var httpPort = options.HttpPort < 0 ? 0 : PickPort(options.HttpPort, "HTTP", onLog);
        var metricsPort = Ports.GetFreeLoopbackPort();

        string? bind = null;
        if (options.BindToPhysicalInterface)
            bind = NetworkInfo.GetDefaultPhysicalInterface()?.Name;
        var resolved = profile is XrayConfigProfile server ? await ServerConfigs.ResolveHostsAsync(server, ct).ConfigureAwait(false) : null;

        var config = XrayConfigBuilder.Build(profile, new XrayOptions
        {
            Mode = XrayMode.Proxy,
            SocksPort = socksPort,
            HttpPort = httpPort,
            BindInterface = bind,
            Bypass = BypassRules.Validate(options.Bypass),
            LogLevel = options.LogLevel,
            SplitTunnel = options.SplitTunnel is { } split ? SplitTunnelRules.Validate(split) : null,
            MetricsPort = metricsPort,
            UseServerRouting = options.UseServerRouting,
            ResolvedHosts = resolved,
        });

        var sanitizer = new LogSanitizer(profile.Secrets);
        var xray = CoreProcess.Start("xray", cores.Xray, ["run", "-c", "stdin:"], config.ToJsonString(), sanitizer, onLog);
        var ready = await Ports.WaitListeningAsync(socksPort, TimeSpan.FromSeconds(10), () => xray.HasExited, ct).ConfigureAwait(false);
        if (!ready)
        {
            var tail = xray.Tail();
            await xray.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"xray не запустился:{Environment.NewLine}{tail}");
        }

        var session = new ProxySession(profile, socksPort, httpPort, metricsPort, bind, xray);
        if (options.SetSystemProxy && httpPort > 0)
        {
            WinInetProxy.Enable(httpPort);
            session.SystemProxyEnabled = true;
        }
        return session;
    }

    private static int PickPort(int requested, string what, Action<string, string> onLog)
    {
        if (requested != 0 && Ports.IsLoopbackPortFree(requested))
            return requested;
        var port = Ports.GetFreeLoopbackPort();
        if (requested != 0)
            onLog("app", $"Порт {what} {requested} занят другой программой, используется {port}");
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (SystemProxyEnabled)
        {
            WinInetProxy.Restore();
            SystemProxyEnabled = false;
        }
        await Xray.DisposeAsync().ConfigureAwait(false);
    }
}
