using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PVpn.Core;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;
using PVpn.Core.Sessions;
using PVpn.Core.SystemProxy;

Console.OutputEncoding = Encoding.UTF8;
// Серверы и настройки из папок PVpn (до переименования) — в папки The Cup of Tea.
LegacyData.MigrateUserData();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    return args[0] switch
    {
        "import" => Import(args),
        "list" => List(),
        "remove" => Remove(args),
        "config" => PrintConfig(args),
        "check" => await Check(args),
        "test" => await Test(args),
        "proxy" => await RunProxy(args),
        "ping" => await Ping(args),
        "netinfo" => await NetInfo(),
        "restore-proxy" => RestoreProxy(),
        "export" => Export(args),
        "killswitch" => KillSwitchCommand(args),
        "svc" => await Svc(args),
        "dnsleak" => await DnsLeak(args),
        "sub" => await Sub(args),
        _ => Fail($"Неизвестная команда: {args[0]}"),
    };
}
catch (Exception e) when (e is LinkParseException or ConfigException or InvalidOperationException or FileNotFoundException or ServiceUnavailableException
                               or System.ComponentModel.Win32Exception or JsonException
                               or HttpRequestException or TaskCanceledException or SubscriptionException)
{
    return Fail(e.Message);
}

static void PrintHelp() => Console.WriteLine("""
    cupoftea-cli — консольный помощник The Cup of Tea (разработка и диагностика)

      import <файл> [--filter <префикс>]   импорт ссылок vless:// и hy2:// из файла («-» — из stdin)
      list                                 список серверов
      remove <имя>                         удалить сервер
      config <имя> [--tun]                 показать конфиги ядер (секреты скрыты)
      check [<имя>]                        проверить конфиги самими ядрами (xray -test, sing-box check)
      test [<имя>|--all] [--bypass файл]   поднять временный прокси: внешний IP и задержка
      proxy <имя> [--system-proxy] [--seconds N] [--no-fragment] [--bypass файл] [--log debug]
                                           режим прокси: SOCKS 10828, HTTP 10829 (Ctrl+C — стоп)
      ping [<имя>] [--count N]             TCP-пинг серверов через физический адаптер
      netinfo                              адаптеры, маршруты по умолчанию, конфликты с другими VPN
      restore-proxy                        вернуть системный прокси, как было до подключения
      export [--filter <префикс>]          вывести ссылки серверов (для переноса на другой ПК)
      killswitch [off]                     число фильтров kill switch / снять их (от администратора)
      sub add <url> [--name N]             добавить подписку и загрузить её серверы
      sub list                             подписки: трафик, срок, последнее обновление
      sub update [<имя>|--all]             обновить подписку (без имени — все)
      sub remove <имя>|--all               удалить подписку вместе с её серверами
      dnsleak [--dns D]                    проверка утечки DNS (bash.ws); --dns — какой DNS ожидать

    Служба (режим TUN):
      svc status                           состояние подключения
      svc connect <имя> [--test N] [--no-killswitch] [--ignore-conflicts] [--no-fragment] [--mtu N] [--stack gvisor|system|mixed] [--split файл] [--bypass файл]
                                           [--dns cloudflare|google|quad9|adguard|https://…]
                                           подключиться; --test N — автоотключение через N с без подтверждения;
                                           --split — раздельное туннелирование из JSON (mode, apps, domains);
                                           --bypass — обход блокировок из JSON (fragment, mux, noises, xmux…)
      svc reconfigure [--split файл]       новые правила раздельного туннелирования без переподключения
      svc confirm                          подтвердить тестовое подключение
      svc disconnect                       отключиться
      svc repair                           восстановить сеть (маршруты, DNS, процессы ядер)
      svc logs                             журнал службы
      svc cores [check|update|rollback]    версии xray и sing-box; проверить, обновить с GitHub, откатить
      --dev                                не проверять, что служба запущена из Program Files
    """);

static int Fail(string message)
{
    Console.Error.WriteLine($"Ошибка: {message}");
    return 1;
}

static string? Option(string[] args, string name) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static bool Flag(string[] args, string name) => args.Contains(name);

static string? Positional(string[] args, int index)
{
    var positional = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[i] is "--filter" or "--seconds" or "--count" or "--log" or "--test" or "--mtu" or "--stack" or "--split" or "--bypass" or "--dns" or "--name")
                i++;
            continue;
        }
        positional.Add(args[i]);
    }
    return index < positional.Count ? positional[index] : null;
}

static CorePaths Cores() => CorePaths.Resolve(allowDevFallback: true);

static Profile RequireProfile(ProfileData data, string? name)
{
    if (name == null)
        throw new InvalidOperationException("Укажите имя сервера (cupoftea-cli list)");
    return data.Find(name) ?? throw new InvalidOperationException($"Сервер «{name}» не найден");
}

static int Import(string[] args)
{
    var source = Positional(args, 0) ?? throw new InvalidOperationException("Укажите файл со ссылками");
    var text = source == "-" ? Console.In.ReadToEnd() : File.ReadAllText(source);
    var filter = Option(args, "--filter");
    var result = LinkParser.ParseMany(text);

    var store = new ProfileStore();
    var data = store.Load();
    var imported = 0;
    foreach (var p in result.Profiles)
    {
        // Имена часто начинаются с флага-эмодзи («🇳🇱 my-xhttp»): сравниваем без него.
        if (filter != null && !ProfileData.PlainName(p.Name).StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            continue;
        var added = data.Upsert(p);
        imported++;
        Console.WriteLine($"  {(added ? "+" : "~")} {p.Name,-22} {p.ProtocolLabel,-22} {p.Host}:{p.Port}");
    }
    foreach (var e in result.Errors)
        Console.WriteLine($"  ! строка {e.Line}: {e.Message}");
    store.Save(data);
    Console.WriteLine($"Импортировано: {imported}, ошибок: {result.Errors.Count}. Хранилище: {store.FilePath}");
    return result.Errors.Count == 0 ? 0 : 2;
}

static async Task<int> Sub(string[] args)
{
    var store = new ProfileStore();
    var data = store.Load();
    using var http = SubscriptionClient.CreateClient();
    switch (Positional(args, 0) ?? "list")
    {
        case "add":
        {
            var u = await SubscriptionService.AddAsync(data, http, Positional(args, 1) ?? throw new InvalidOperationException("Укажите ссылку подписки"), Option(args, "--name"));
            store.Save(data);
            Console.WriteLine(u);
            return 0;
        }
        case "list":
            if (data.Subscriptions.Count == 0)
                Console.WriteLine("Подписок нет. Добавить: cupoftea-cli sub add <url>");
            foreach (var s in data.Subscriptions)
            {
                var count = data.Profiles.Count(p => p.SubscriptionId == s.Id);
                var traffic = s.Total > 0 ? $"{Gb(s.Upload + s.Download)} из {Gb(s.Total)} ГБ" : $"{Gb(s.Upload + s.Download)} ГБ";
                Console.WriteLine($"  {s.Name}: серверов {count}, трафик {traffic}, срок {(s.Expire is { } e ? e.LocalDateTime.ToString("dd.MM.yyyy") : "бессрочно")}, " +
                                  $"обновлена {(s.LastUpdated is { } t ? t.LocalDateTime.ToString("dd.MM HH:mm") : "—")}, раз в {s.UpdateIntervalHours} ч" +
                                  (s.LastError != null ? $"; ошибка: {s.LastError}" : ""));
            }
            return 0;
        case "update":
        {
            var name = Positional(args, 1);
            var subs = name == null || Flag(args, "--all")
                ? data.Subscriptions.ToList()
                : [data.FindSubscription(name) ?? throw new InvalidOperationException($"Подписка не найдена: {name}")];
            var failed = 0;
            foreach (var s in subs)
            {
                try
                {
                    Console.WriteLine(await SubscriptionService.UpdateAsync(data, http, s));
                }
                catch (SubscriptionException e)
                {
                    Console.WriteLine($"«{s.Name}»: {e.Message}");
                    failed++;
                }
            }
            store.Save(data);
            return failed == 0 ? 0 : 1;
        }
        case "remove":
        {
            var name = Positional(args, 1);
            var subs = Flag(args, "--all")
                ? data.Subscriptions.ToList()
                : [data.FindSubscription(name ?? throw new InvalidOperationException("Укажите подписку")) ?? throw new InvalidOperationException($"Подписка не найдена: {name}")];
            foreach (var s in subs)
            {
                data.RemoveSubscription(s.Id);
                Console.WriteLine($"Удалена подписка «{s.Name}» и её серверы");
            }
            store.Save(data);
            return 0;
        }
        default:
            return Fail("Команды: sub add | list | update | remove");
    }

    static string Gb(long bytes) => (bytes / 1073741824.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

static int List()
{
    var data = new ProfileStore().Load();
    if (data.Profiles.Count == 0)
        Console.WriteLine("Серверов нет. Импорт: cupoftea-cli import <файл>");
    foreach (var p in data.Profiles)
        Console.WriteLine($"  {p.Name,-22} {p.ProtocolLabel,-22} {p.Host}:{p.Port} ({p.Transport.ToString().ToUpperInvariant()})");
    return 0;
}

static int Remove(string[] args)
{
    var store = new ProfileStore();
    var data = store.Load();
    var p = RequireProfile(data, Positional(args, 0));
    data.Profiles.Remove(p);
    store.Save(data);
    Console.WriteLine($"Удалён: {p.Name}");
    return 0;
}

static (JsonObject Xray, JsonObject? SingBox) BuildConfigs(Profile p, bool tun)
{
    var bind = NetworkInfo.GetDefaultPhysicalInterface()?.Name;
    if (!tun)
        return (XrayConfigBuilder.Build(p, new XrayOptions { Mode = XrayMode.Proxy, SocksPort = 10808, HttpPort = 10809, BindInterface = bind }), null);

    var server = IPAddress.TryParse(p.Host, out var ip) ? ip : Dns.GetHostAddresses(p.Host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    var (user, pass, port) = ("cupoftea", Guid.NewGuid().ToString("N"), Ports.GetFreeLoopbackPort());
    var xray = XrayConfigBuilder.Build(p, new XrayOptions
    {
        Mode = XrayMode.TunBackend,
        SocksPort = port,
        SocksUser = user,
        SocksPassword = pass,
        BindInterface = bind,
        ServerAddressOverride = server.ToString(),
    });
    var singBox = SingBoxConfigBuilder.Build(new SingBoxOptions
    {
        SocksPort = port,
        SocksUser = user,
        SocksPassword = pass,
        ServerAddresses = [server],
        DirectCidrs = NetworkInfo.GetPublicLocalSubnets(Product.Id),
        BlockQuic = p.Transport == ServerTransport.Tcp,
    });
    return (xray, singBox);
}

static int PrintConfig(string[] args)
{
    var p = RequireProfile(new ProfileStore().Load(), Positional(args, 0));
    var (xray, singBox) = BuildConfigs(p, Flag(args, "--tun"));
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var sanitizer = new LogSanitizer(p.Secrets);
    Console.WriteLine("// xray");
    Console.WriteLine(sanitizer.Clean(xray.ToJsonString(options)));
    if (singBox != null)
    {
        Console.WriteLine("// sing-box");
        Console.WriteLine(sanitizer.Clean(singBox.ToJsonString(options)));
    }
    return 0;
}

static async Task<int> Check(string[] args)
{
    var cores = Cores();
    var data = new ProfileStore().Load();
    var name = Positional(args, 0);
    var profiles = name == null ? data.Profiles : [RequireProfile(data, name)];
    var failed = 0;
    foreach (var p in profiles)
    {
        var sanitizer = new LogSanitizer(p.Secrets);
        var (proxyXray, _) = BuildConfigs(p, tun: false);
        var (tunXray, singBox) = BuildConfigs(p, tun: true);
        var checks = new (string What, ValidationResult Result)[]
        {
            ("xray (прокси)", await CoreValidator.ValidateXrayAsync(cores, proxyXray.ToJsonString())),
            ("xray (TUN)", await CoreValidator.ValidateXrayAsync(cores, tunXray.ToJsonString())),
            ("sing-box (TUN)", await CoreValidator.ValidateSingBoxAsync(cores, singBox!.ToJsonString())),
        };
        foreach (var (what, r) in checks)
        {
            Console.WriteLine($"  {(r.Ok ? "OK " : "ERR")} {p.Name,-22} {what}");
            if (!r.Ok || r.Output.Contains("WARN", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("      " + sanitizer.Clean(r.Output).Replace("\n", "\n      "));
            if (!r.Ok)
                failed++;
        }
    }
    return failed == 0 ? 0 : 1;
}

static async Task<int> Test(string[] args)
{
    var cores = Cores();
    var data = new ProfileStore().Load();
    var name = Positional(args, 0);
    var profiles = name == null || Flag(args, "--all") ? data.Profiles : [RequireProfile(data, name)];
    var failed = 0;
    foreach (var p in profiles)
    {
        Console.Write($"  {p.Name,-22} {p.ProtocolLabel,-22} ");
        try
        {
            await using var session = await ProxySession.StartAsync(p, cores,
                new ProxySessionOptions { SocksPort = 0, HttpPort = -1, Bypass = ReadBypass(args) }, (_, _) => { });
            var ip = await DelayTester.GetPublicIpViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(15));
            var delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(15));
            Console.WriteLine($"IP {ip ?? "—"}, задержка {delay}, адаптер «{session.BoundInterface ?? "по умолчанию"}»");
            if (ip == null || !delay.Ok)
            {
                failed++;
                var tail = session.Xray.Tail(5);
                if (tail.Length > 0)
                    Console.WriteLine("      " + tail.Replace("\n", "\n      "));
            }
        }
        catch (InvalidOperationException e)
        {
            failed++;
            Console.WriteLine("ошибка: " + e.Message);
        }
    }
    return failed == 0 ? 0 : 1;
}

static async Task<int> RunProxy(string[] args)
{
    var p = RequireProfile(new ProfileStore().Load(), Positional(args, 0));
    var seconds = int.TryParse(Option(args, "--seconds"), out var s) ? s : 0;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    await using var session = await ProxySession.StartAsync(p, Cores(), new ProxySessionOptions
    {
        SetSystemProxy = Flag(args, "--system-proxy"),
        Bypass = ReadBypass(args),
        LogLevel = Option(args, "--log") ?? "warning",
    }, (core, line) => Console.WriteLine($"[{core}] {line}"));

    Console.WriteLine($"Прокси работает: SOCKS 127.0.0.1:{session.SocksPort}, HTTP 127.0.0.1:{session.HttpPort}" +
                      (session.SystemProxyEnabled ? ", системный прокси включён" : ""));
    Console.WriteLine($"Внешний IP: {await DelayTester.GetPublicIpViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(15)) ?? "не удалось проверить"}");
    if (session.SystemProxyEnabled)
        Console.WriteLine($"IP через системный прокси Windows: {await DelayTester.GetPublicIpViaSystemProxyAsync(TimeSpan.FromSeconds(20)) ?? "не удалось проверить"}");
    try
    {
        var wait = seconds > 0 ? Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
        await Task.WhenAny(wait, session.Xray.Exited);
    }
    catch (OperationCanceledException) { }

    if (session.Xray.HasExited)
        return Fail("xray неожиданно завершился:" + Environment.NewLine + session.Xray.Tail());
    Console.WriteLine("Остановлено" + (session.SystemProxyEnabled ? ", системный прокси восстановлен" : ""));
    return 0;
}

static async Task<int> Ping(string[] args)
{
    var data = new ProfileStore().Load();
    var name = Positional(args, 0);
    var profiles = name == null ? data.Profiles : [RequireProfile(data, name)];
    var physical = NetworkInfo.GetDefaultPhysicalInterface();
    var count = int.TryParse(Option(args, "--count"), out var c) ? c : 1;
    foreach (var p in profiles)
    {
        if (p.Transport != ServerTransport.Tcp)
        {
            Console.WriteLine($"  {p.Name,-22} {p.Host}:{p.Port,-6} UDP — только через «test»");
            continue;
        }
        var results = new List<DelayResult>();
        for (var i = 0; i < count; i++)
            results.Add(await DelayTester.TcpPingAsync(p.Host, p.Port, physical?.Index, TimeSpan.FromSeconds(5)));
        var ok = results.Where(r => r.Ok).Select(r => r.Milliseconds!.Value).ToList();
        var summary = count == 1
            ? results[0].ToString()
            : $"{ok.Count}/{count} успешно" + (ok.Count > 0 ? $", мин/сред/макс {ok.Min()}/{(int)ok.Average()}/{ok.Max()} мс" : "") +
              string.Concat(results.Where(r => !r.Ok).Select(r => r.Error).Distinct().Select(e => $"; {e}"));
        Console.WriteLine($"  {p.Name,-22} {p.Host}:{p.Port,-6} {summary}");
    }
    return 0;
}

static async Task<int> NetInfo()
{
    Console.Write(DiagnosticsReport.Network(Product.Id));
    Console.WriteLine("Сети (для доверенных): " + string.Join(", ", NetworkNames.GetConnected().Select(n => $"«{n}»")));
    using var http = CaptivePortal.CreateClient();
    Console.WriteLine($"Интернет (проверка как у Windows): {await CaptivePortal.CheckAsync(http)}");
    return 0;
}

static int RestoreProxy()
{
    WinInetProxy.Restore();
    Console.WriteLine("Системный прокси восстановлен");
    return 0;
}

/// <summary>Сколько фильтров kill switch стоит сейчас; «off» — снять их (аварийно, без службы). Нужны права администратора.</summary>
static int KillSwitchCommand(string[] args)
{
    if (Positional(args, 0) == "off")
        Console.WriteLine($"Снято фильтров kill switch: {KillSwitch.Disable()}");
    else
        Console.WriteLine($"Фильтров kill switch: {KillSwitch.CountFilters()}");
    return 0;
}

static int Export(string[] args)
{
    var filter = Option(args, "--filter");
    foreach (var p in new ProfileStore().Load().Profiles)
    {
        // У серверов с полным конфигом из подписки ссылки нет.
        if (p.SourceLink.Length > 0 && (filter == null || ProfileData.PlainName(p.Name).StartsWith(filter, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(p.SourceLink);
    }
    return 0;
}

static async Task<int> Svc(string[] args)
{
    var sub = Positional(args, 0) ?? "status";
    var client = new ServiceClient { VerifyServer = !Flag(args, "--dev") };
    var timeout = TimeSpan.FromSeconds(60);
    switch (sub)
    {
        case "status":
            PrintStatus((await client.StatusAsync()).Status);
            return 0;
        case "connect":
        {
            var p = RequireProfile(new ProfileStore().Load(), Positional(args, 1));
            var options = new ConnectOptions
            {
                KillSwitch = !Flag(args, "--no-killswitch"),
                IgnoreConflicts = Flag(args, "--ignore-conflicts"),
                Bypass = ReadBypass(args),
                TestSeconds = int.TryParse(Option(args, "--test"), out var t) ? t : 0,
                Mtu = int.TryParse(Option(args, "--mtu"), out var m) ? m : 1500,
                Stack = Option(args, "--stack"),
                SplitTunnel = ReadSplit(args),
                Dns = ReadDns(args),
            };
            var started = await client.SendAsync(new IpcRequest
            {
                Command = IpcCommands.Connect,
                Link = p is XrayConfigProfile ? null : p.SourceLink,
                Config = (p as XrayConfigProfile)?.Config,
                Options = options,
            }, timeout);
            if (!started.Ok)
                return Fail(started.Error ?? "служба отказала");

            // Ждём результата, показывая шаги подключения.
            string? lastMessage = null;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var s = (await client.StatusAsync()).Status!;
                if (s.Message != lastMessage)
                {
                    Console.WriteLine($"  {s.State}: {s.Message}");
                    lastMessage = s.Message;
                }
                if (s.State is SessionState.Connected or SessionState.Error or SessionState.Disconnected)
                {
                    if (s.State == SessionState.Connected)
                    {
                        // Даём службе закончить проверку внешнего IP и DNS.
                        for (var i = 0; i < 20 && s.PublicIp == null; i++)
                        {
                            await Task.Delay(500);
                            s = (await client.StatusAsync()).Status!;
                        }
                    }
                    PrintStatus(s);
                    return s.State == SessionState.Connected ? 0 : 1;
                }
            }
            return Fail("подключение не завершилось за 90 секунд");
        }
        case "reconfigure":
        {
            var r = await client.SendAsync(new IpcRequest
            {
                Command = IpcCommands.Reconfigure,
                Options = new ConnectOptions { SplitTunnel = ReadSplit(args) },
            }, timeout);
            if (!r.Ok)
                return Fail(r.Error ?? "служба отказала");
            PrintStatus(r.Status);
            return 0;
        }
        case "confirm":
        case "disconnect":
        case "repair":
        {
            var r = await client.SendAsync(new IpcRequest { Command = sub }, timeout);
            if (!r.Ok)
                return Fail(r.Error ?? "служба отказала");
            PrintStatus(r.Status);
            return 0;
        }
        case "cores":
        {
            var action = Positional(args, 1) ?? CoreActions.Status;
            var r = await client.SendAsync(new IpcRequest { Command = IpcCommands.Cores, CoreAction = action }, timeout);
            if (!r.Ok)
                return Fail(r.Error ?? "служба отказала");
            var status = r.Cores!;
            // Проверка и обновление идут в службе в фоне: ждём окончания (скачивание — до нескольких минут).
            for (var i = 0; status.Busy && i < 600; i++)
            {
                if (i % 10 == 0 && status.Message != null)
                    Console.WriteLine($"  {status.Message}");
                await Task.Delay(1000);
                status = (await client.SendAsync(new IpcRequest { Command = IpcCommands.Cores, CoreAction = CoreActions.Status }, timeout)).Cores!;
            }
            foreach (var c in status.Cores)
                Console.WriteLine($"{c.Name}: {c.Current ?? "нет"}" + (c.Latest != null ? $", на GitHub {c.Latest}" + (c.UpdateAvailable ? " (есть обновление)" : "") : "") +
                                  (c.Previous != null ? $", для отката {c.Previous}" : ""));
            if (status.Message != null)
                Console.WriteLine(status.Message);
            if (status.Error != null)
                Console.WriteLine($"Ошибка: {status.Error}");
            return status.Error == null ? 0 : 1;
        }
        case "logs":
        {
            var r = await client.SendAsync(new IpcRequest { Command = IpcCommands.Logs }, timeout);
            foreach (var l in r.Logs ?? [])
                Console.WriteLine($"{l.Time:HH:mm:ss} [{l.Source}] {l.Text}");
            return 0;
        }
        default:
            return Fail($"Неизвестная команда svc {sub}");
    }
}

/// <summary>--split файл.json — правила раздельного туннелирования (как в IPC: mode, apps[path, isFolder], domains).</summary>
static SplitTunnelOptions? ReadSplit(string[] args)
{
    var file = Option(args, "--split");
    return file == null ? null : JsonSerializer.Deserialize(File.ReadAllText(file), IpcJsonContext.Default.SplitTunnelOptions);
}

/// <summary>--dns cloudflare|google|quad9|adguard или адрес DoH; без параметра — null (Cloudflare).</summary>
static DnsOptions? ReadDns(string[] args)
{
    var value = Option(args, "--dns");
    if (value == null)
        return null;
    return Enum.TryParse<DnsProvider>(value, ignoreCase: true, out var provider) && provider != DnsProvider.Custom
        ? new DnsOptions { Provider = provider }
        : new DnsOptions { Provider = DnsProvider.Custom, CustomUrl = value };
}

static async Task<int> DnsLeak(string[] args)
{
    var dns = ReadDns(args);
    using var http = DnsLeakTest.CreateClient();
    var r = await DnsLeakTest.RunAsync(http, dns == null ? null : DnsRules.OrgKeywords(dns.Provider));
    foreach (var e in r.Exit)
        Console.WriteLine($"Внешний IP: {e.Ip} — {e.Org}, {e.Country}");
    foreach (var e in r.Resolvers)
        Console.WriteLine($"DNS: {e.Ip} — {e.Org}, {e.Country}");
    Console.WriteLine(r.Verdict);
    return r.Leak == true ? 2 : 0;
}

/// <summary>--bypass файл.json — настройки обхода блокировок (как в IPC), --no-fragment — без фрагментации.</summary>
static BypassOptions ReadBypass(string[] args)
{
    var file = Option(args, "--bypass");
    var bypass = file == null ? BypassOptions.Default : JsonSerializer.Deserialize(File.ReadAllText(file), IpcJsonContext.Default.BypassOptions) ?? BypassOptions.Default;
    return Flag(args, "--no-fragment") ? bypass with { Fragment = false } : bypass;
}

static void PrintStatus(ServiceStatus? s)
{
    if (s == null)
        return;
    Console.WriteLine($"Состояние: {s.State} — {s.Message}");
    if (s.ProfileName != null)
        Console.WriteLine($"Сервер: {s.ProfileName} ({s.Protocol}, {s.Server})");
    if (s.State == SessionState.Connected)
    {
        Console.WriteLine($"Адаптер: {s.PhysicalInterface}, внешний IP: {s.PublicIp ?? "—"}, задержка: {(s.DelayMs is { } d ? $"{d} мс" : "—")}");
        Console.WriteLine($"Kill switch: {(s.KillSwitchActive ? "включён" : "выключен")}");
        if (s.TestDeadline is { } deadline)
            Console.WriteLine($"Тестовый режим до {deadline:HH:mm:ss}");
    }
    foreach (var w in s.Warnings)
        Console.WriteLine($"  ! {w}");
    Console.WriteLine($"Служба {s.ServiceVersion}");
}
