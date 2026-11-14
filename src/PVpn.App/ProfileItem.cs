using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PVpn.Core.Diagnostics;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>Сервер в списке: профиль и результат последней проверки задержки.</summary>
public sealed partial class ProfileItem(Profile profile, string? subscription = null, int order = 0, bool isAuto = false) : ObservableObject
{
    public Profile Profile { get; } = profile;

    /// <summary>Автовыбор из серверов группы по ссылкам (см. MainViewModel.Auto): всегда первый в группе.</summary>
    public bool IsAuto { get; } = isAuto;

    /// <summary>Для сортировки: автовыбор выше остальных серверов группы.</summary>
    public int SortTop => IsAuto ? 0 : 1;

    /// <summary>Имя без флага-эмодзи: Windows рисует флаги буквами («NL»).</summary>
    public string Name => ProfileData.PlainName(Profile.Name) is { Length: > 0 } plain ? plain : Profile.Name;
    public string Details => IsAuto && Profile is XrayConfigProfile auto
        ? $"Лучший по пингу из {auto.ServerCount} серверов группы"
        : Profile is XrayConfigProfile { Description: { } d }
        ? $"{Profile.ProtocolLabel} · {Flags.Strip(d)}"
        : $"{Profile.ProtocolLabel} · {Profile.Host}:{Profile.Port}";

    /// <summary>Имя как в подписке, с флагами (их рисует <see cref="FlagText"/> картинками).</summary>
    public string DisplayName => Profile.Name.Trim();

    /// <summary>Группа в списке: имя подписки или «Свои» (добавленные вручную).</summary>
    public string Group => subscription ?? "Свои";

    /// <summary>Значок сервера: картинка флага из имени, а без флага — коротко протокол.</summary>
    public object Badge => (object?)FlagText.Image(Flags.FirstCountry(Profile.Name)) ?? Profile switch
    {
        Hysteria2Profile => "HY2",
        VlessProfile { Network: "xhttp" } => "XH",
        VlessProfile { Flow: "xtls-rprx-vision" } => "VIS",
        TrojanProfile => "TRJ",
        VmessProfile => "VM",
        ShadowsocksProfile => "SS",
        XrayConfigProfile { ServerCount: > 1 } => "AUTO",
        XrayConfigProfile c when c.MainProtocol.StartsWith("Hysteria", StringComparison.Ordinal) => "HY2",
        XrayConfigProfile c when c.MainProtocol.StartsWith("Trojan", StringComparison.Ordinal) => "TRJ",
        XrayConfigProfile c when c.MainProtocol.Contains("XHTTP", StringComparison.Ordinal) => "XH",
        XrayConfigProfile c when c.MainProtocol.Contains("Vision", StringComparison.Ordinal) => "VIS",
        _ => "VL",
    };

    /// <summary>Место в списке, как пришло из подписки (сортировка «как в подписке»).</summary>
    public int Order { get; } = order;

    /// <summary>Для сортировки по пингу: без ответа и непроверенные — в конце.</summary>
    public int SortDelay => Delay is { Milliseconds: { } ms } ? ms : Delay == null ? int.MaxValue : int.MaxValue - 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayText), nameof(DelayBrush), nameof(SortDelay))]
    private DelayResult? delay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayText))]
    private bool checking;

    public string DelayText => Checking ? "проверяю…" : Delay switch
    {
        null => "",
        { Ok: true } d => $"{d.Milliseconds} мс",
        _ => "нет связи",
    };

    public Brush DelayBrush => Delay switch
    {
        { Ok: false } => StatusBrushes.Error,
        { Milliseconds: < 150 } => StatusBrushes.On,
        { Milliseconds: < 400 } => StatusBrushes.Busy,
        { Ok: true } => StatusBrushes.Error,
        _ => StatusBrushes.Off,
    };
}

public static class StatusBrushes
{
    private static readonly Brush Green = Freeze(0x34, 0xD3, 0x99);
    private static readonly Brush Ice = Freeze(0x5E, 0xDC, 0xF2);

    public static Brush Off { get; } = Freeze(0x8A, 0x93, 0xA6);
    public static Brush Busy { get; } = Freeze(0xF5, 0xB5, 0x44);

    /// <summary>«Защищено» и хороший пинг: зелёный, зимой — ледяной (см. <see cref="Themes"/>).</summary>
    public static Brush On { get; private set; } = Green;
    public static Brush Error { get; } = Freeze(0xF8, 0x71, 0x71);

    /// <summary>До создания окна: x:Static читает значения при загрузке разметки.</summary>
    public static void Apply(bool winter) => On = winter ? Ice : Green;

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
