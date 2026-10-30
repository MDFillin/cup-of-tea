using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Config;

namespace PVpn.App;

/// <summary>
/// Раздел настроек «Обход блокировок». Поля редактируются как текст; верные значения сразу сохраняются,
/// при ошибке показывается сообщение, а в настройках остаются последние верные.
/// </summary>
public sealed partial class BypassViewModel : ObservableObject
{
    public const string FromLink = "из ссылки";

    private readonly Func<BypassOptions> get;
    private readonly Action<BypassOptions> set;
    private bool loading;

    public string[] FingerprintChoices { get; } = [FromLink, .. BypassRules.Fingerprints];
    public string[] XhttpModeChoices { get; } = [FromLink, .. BypassRules.XhttpModes];
    public string[] NoiseTypeChoices { get; } = BypassRules.NoiseTypes;

    [ObservableProperty] private string fingerprint = FromLink;
    [ObservableProperty] private string xhttpMode = FromLink;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool fragment;
    [ObservableProperty] private string fragmentPackets = "";
    [ObservableProperty] private string fragmentLength = "";
    [ObservableProperty] private string fragmentInterval = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool mux;
    [ObservableProperty] private string muxConcurrency = "";
    [ObservableProperty] private string xudpConcurrency = "";

    [ObservableProperty] private bool customXmux;
    [ObservableProperty] private string xmuxMaxConcurrency = "";
    [ObservableProperty] private string xmuxMaxConnections = "";
    [ObservableProperty] private string xmuxCMaxReuseTimes = "";
    [ObservableProperty] private string xmuxHMaxRequestTimes = "";
    [ObservableProperty] private string xmuxHMaxReusableSecs = "";
    [ObservableProperty] private string xmuxHKeepAlivePeriod = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool noises;
    [ObservableProperty] private string noiseType = "rand";
    [ObservableProperty] private string noisePacket = "";
    [ObservableProperty] private string noiseDelay = "";

    [ObservableProperty] private string hopInterval = "";

    /// <summary>Ошибка в полях; null — всё верно и сохранено.</summary>
    [ObservableProperty] private string? error;

    public BypassViewModel(Func<BypassOptions> get, Action<BypassOptions> set)
    {
        this.get = get;
        this.set = set;
        Load(get());
    }

    public string NoisePacketLabel => NoiseType switch
    {
        "rand" => "Длина пакета, байт",
        "str" => "Текст пакета",
        "hex" => "Пакет (hex)",
        _ => "Пакет (base64)",
    };

    /// <summary>Что включено — для строки в списке настроек.</summary>
    public string Summary => string.Join(", ", new[] { Fragment ? "фрагментация" : null, Mux ? "mux" : null, Noises ? "шумы" : null }.OfType<string>()) is { Length: > 0 } on ? on : "выключен";

    /// <summary>Настройки для подключения (проверенные). При ошибке в полях — <see cref="ConfigException"/>.</summary>
    public BypassOptions ToOptions() => BypassRules.Validate(Read());

    [RelayCommand]
    private void Reset()
    {
        Load(BypassOptions.Default);
        Apply();
    }

    private void Load(BypassOptions o)
    {
        loading = true;
        Fingerprint = o.Fingerprint ?? FromLink;
        XhttpMode = o.XhttpMode ?? FromLink;
        Fragment = o.Fragment;
        FragmentPackets = o.FragmentPackets;
        FragmentLength = o.FragmentLength;
        FragmentInterval = o.FragmentInterval;
        Mux = o.Mux;
        MuxConcurrency = o.MuxConcurrency.ToString();
        XudpConcurrency = o.XudpConcurrency.ToString();
        CustomXmux = o.Xmux != null;
        var x = o.Xmux ?? new XmuxOptions();
        XmuxMaxConcurrency = x.MaxConcurrency ?? "";
        XmuxMaxConnections = x.MaxConnections ?? "";
        XmuxCMaxReuseTimes = x.CMaxReuseTimes ?? "";
        XmuxHMaxRequestTimes = x.HMaxRequestTimes ?? "";
        XmuxHMaxReusableSecs = x.HMaxReusableSecs ?? "";
        XmuxHKeepAlivePeriod = x.HKeepAlivePeriod > 0 ? x.HKeepAlivePeriod.ToString() : "";
        Noises = o.Noises;
        NoiseType = o.NoiseType;
        NoisePacket = o.NoisePacket;
        NoiseDelay = o.NoiseDelay;
        HopInterval = o.HopIntervalSeconds.ToString();
        Error = null;
        loading = false;
    }

    private BypassOptions Read() => new()
    {
        Fingerprint = Fingerprint == FromLink ? null : Fingerprint,
        XhttpMode = XhttpMode == FromLink ? null : XhttpMode,
        Fragment = Fragment,
        FragmentPackets = FragmentPackets,
        FragmentLength = FragmentLength,
        FragmentInterval = FragmentInterval,
        Mux = Mux,
        MuxConcurrency = Int(MuxConcurrency, "Mux: число потоков на соединение"),
        XudpConcurrency = Int(XudpConcurrency, "Mux: число UDP-потоков (xudp)"),
        Xmux = CustomXmux
            ? new XmuxOptions
            {
                MaxConcurrency = Blank(XmuxMaxConcurrency),
                MaxConnections = Blank(XmuxMaxConnections),
                CMaxReuseTimes = Blank(XmuxCMaxReuseTimes),
                HMaxRequestTimes = Blank(XmuxHMaxRequestTimes),
                HMaxReusableSecs = Blank(XmuxHMaxReusableSecs),
                HKeepAlivePeriod = string.IsNullOrWhiteSpace(XmuxHKeepAlivePeriod) ? 0 : Int(XmuxHKeepAlivePeriod, "XMUX: keep-alive"),
            }
            : null,
        Noises = Noises,
        NoiseType = NoiseType,
        NoisePacket = NoisePacket,
        NoiseDelay = NoiseDelay,
        HopIntervalSeconds = Int(HopInterval, "Смена портов Hysteria2: интервал"),
    };

    private static int Int(string text, string what) =>
        int.TryParse(text.Trim(), out var value) ? value : throw new ConfigException($"{what}: нужно целое число, а не «{text}»");

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(NoiseType))
            OnPropertyChanged(nameof(NoisePacketLabel));
        if (!loading && e.PropertyName is not (nameof(Error) or nameof(NoisePacketLabel)))
            Apply();
    }

    private void Apply()
    {
        try
        {
            var valid = ToOptions();
            Error = null;
            if (valid != get())
                set(valid);
        }
        catch (ConfigException ex)
        {
            Error = ex.Message;
        }
    }
}
