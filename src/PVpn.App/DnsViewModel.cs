using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Config;
using PVpn.Core.Diagnostics;

namespace PVpn.App;

/// <summary>Как сейчас идёт трафик — от этого зависит, чего ждать от проверки утечки DNS.</summary>
public enum DnsLeakContext
{
    Off,
    Tun,
    Proxy,
}

/// <param name="Proxy">В режиме прокси — HTTP-вход xray: проверка идёт тем же путём, что и браузер.</param>
public sealed record DnsLeakEnvironment(DnsLeakContext Mode, Uri? Proxy);

public sealed record DnsChoice(DnsProvider Provider, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Раздел настроек «DNS»: выбор сервера DNS-over-HTTPS для TUN и проверка утечки DNS.</summary>
public sealed partial class DnsViewModel : ObservableObject
{
    private readonly Func<DnsOptions> get;
    private readonly Action<DnsOptions> set;
    private readonly Func<DnsLeakEnvironment> context;
    private bool loading;

    public DnsChoice[] Choices { get; } =
    [
        new(DnsProvider.Cloudflare, "Cloudflare (1.1.1.1)"),
        new(DnsProvider.Google, "Google (8.8.8.8)"),
        new(DnsProvider.Quad9, "Quad9 (9.9.9.9)"),
        new(DnsProvider.AdGuard, "AdGuard — без рекламы и трекеров"),
        new(DnsProvider.Custom, "Свой адрес DoH"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    private DnsChoice selected;

    [ObservableProperty] private string customUrl = "";

    /// <summary>Ошибка в адресе своего DNS; null — всё верно и сохранено.</summary>
    [ObservableProperty] private string? error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckLeakCommand))]
    private bool checking;

    [ObservableProperty] private string? leakVerdict;
    [ObservableProperty] private string? leakDetails;

    /// <summary>true — утечка, false — всё в порядке, null — нет вердикта.</summary>
    [ObservableProperty] private bool? leak;

    public bool IsCustom => Selected.Provider == DnsProvider.Custom;

    public DnsViewModel(Func<DnsOptions> get, Action<DnsOptions> set, Func<DnsLeakEnvironment> context)
    {
        this.get = get;
        this.set = set;
        this.context = context;
        var o = get();
        loading = true;
        selected = Choices.FirstOrDefault(c => c.Provider == o.Provider) ?? Choices[0];
        CustomUrl = o.CustomUrl ?? "";
        loading = false;
    }

    /// <summary>Настройки для подключения (проверенные). При ошибке — <see cref="ConfigException"/>.</summary>
    public DnsOptions ToOptions() => DnsRules.Validate(Read());

    private DnsOptions Read() => new() { Provider = Selected.Provider, CustomUrl = string.IsNullOrWhiteSpace(CustomUrl) ? null : CustomUrl.Trim() };

    partial void OnSelectedChanged(DnsChoice value) => Apply();
    partial void OnCustomUrlChanged(string value) => Apply();

    private void Apply()
    {
        if (loading)
            return;
        try
        {
            // Свой адрес сохраняется как введён (не нормализованным), чтобы поле не прыгало под курсором.
            DnsRules.Validate(Read());
            set(Read());
            Error = null;
        }
        catch (ConfigException e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckLeak))]
    private async Task CheckLeakAsync()
    {
        var env = context();
        var mode = env.Mode;
        Checking = true;
        Leak = null;
        LeakVerdict = "Проверяю… (около 10 секунд)";
        LeakDetails = null;
        try
        {
            // Системный прокси .NET запоминает при запуске программы, поэтому прокси задаётся явно.
            using var http = DnsLeakTest.CreateClient(env.Proxy);
            var expected = mode == DnsLeakContext.Tun ? DnsRules.OrgKeywords(get().Provider) : null;
            var r = await DnsLeakTest.RunAsync(http, expected);
            Leak = r.Leak;
            LeakVerdict = mode switch
            {
                DnsLeakContext.Off => "VPN выключен — это DNS без VPN. " + r.Verdict,
                DnsLeakContext.Proxy => "Режим прокси: адреса сайтов разрешает сервер VPN, выбор DNS не действует. " + r.Verdict,
                _ => r.Verdict,
            };
            LeakDetails = string.Join(Environment.NewLine,
                r.Exit.Select(e => $"Внешний IP: {e.Ip} — {e.Org}, {e.Country}")
                    .Concat(r.Resolvers.Select(e => $"DNS: {e.Ip} — {e.Org}, {e.Country}")));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            LeakVerdict = $"Проверка не удалась: {e.Message}";
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanCheckLeak() => !Checking;
}
