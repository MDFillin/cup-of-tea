namespace PVpn.Core;

/// <summary>Зимнее оформление: само включается 1 декабря и выключается 1 марта (или всегда / никогда — по выбору).</summary>
public static class Season
{
    public const string Auto = "auto", Always = "on", Never = "off";

    public static bool IsWinter(DateTime date) => date.Month is 12 or 1 or 2;

    /// <param name="mode"><see cref="Auto"/>, <see cref="Always"/> или <see cref="Never"/>; другое — как <see cref="Auto"/>.</param>
    public static bool WinterThemeFor(string? mode, DateTime now) => mode switch
    {
        Always => true,
        Never => false,
        _ => IsWinter(now),
    };
}
