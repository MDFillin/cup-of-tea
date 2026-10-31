using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PVpn.Core.Diagnostics;
using PVpn.Core.Profiles;

namespace PVpn.App;

/// <summary>Сервер в списке: профиль и результат последней проверки задержки.</summary>
public sealed partial class ProfileItem(Profile profile, string? subscription = null) : ObservableObject
{
    public Profile Profile { get; } = profile;

    /// <summary>Имя без флага-эмодзи: Windows рисует флаги буквами («NL»).</summary>
    public string Name => ProfileData.PlainName(Profile.Name) is { Length: > 0 } plain ? plain : Profile.Name;
    public string Details => Profile is XrayConfigProfile { Description: { } d }
        ? $"{Profile.ProtocolLabel} · {d}"
        : $"{Profile.ProtocolLabel} · {Profile.Host}:{Profile.Port}";

    /// <summary>Имя как в подписке, с флагами (их рисует <see cref="FlagText"/> картинками).</summary>
    public string DisplayName => Profile.Name.Trim();

    /// <summary>Группа в списке: имя подписки или «Свои» (добавленные вручную).</summary>
    public string Group => subscription ?? "Свои";

    /// <summary>Значок сервера: страна из флага в имени («NL») или коротко протокол.</summary>
    public string Badge => FlagCode(Profile.Name) ?? Profile switch
    {
        Hysteria2Profile => "HY2",
        VlessProfile { Network: "xhttp" } => "XH",
        VlessProfile { Flow: "xtls-rprx-vision" } => "VIS",
        _ => "VL",
    };

    /// <summary>Флаг-эмодзи — две буквы Regional Indicator в начале имени.</summary>
    private static string? FlagCode(string name)
    {
        var runes = name.EnumerateRunes().Take(2).ToArray();
        return runes.Length == 2 && runes.All(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF)
            ? string.Concat(runes.Select(r => (char)('A' + r.Value - 0x1F1E6)))
            : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayText), nameof(DelayBrush))]
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
    public static readonly Brush Off = Freeze(0x8A, 0x93, 0xA6);
    public static readonly Brush Busy = Freeze(0xF5, 0xB5, 0x44);
    public static readonly Brush On = Freeze(0x34, 0xD3, 0x99);
    public static readonly Brush Error = Freeze(0xF8, 0x71, 0x71);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
