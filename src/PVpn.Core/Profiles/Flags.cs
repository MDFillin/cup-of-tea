using System.Text;

namespace PVpn.Core.Profiles;

/// <summary>
/// Флаги-эмодзи в именах серверов («Финляндия🇫🇮 xhttp»). Флаг — две буквы Regional Indicator (🇫 + 🇮 = FI).
/// Windows своими шрифтами флаги не рисует и показывает вместо них буквы, поэтому интерфейс подставляет картинки,
/// а там, где картинок нет (трей, уведомления), флаги убираются.
/// </summary>
public static class Flags
{
    private const int First = 0x1F1E6, Last = 0x1F1FF;

    /// <summary>Кусок имени: текст или флаг страны (Country — код ISO, «FI»).</summary>
    public readonly record struct Segment(string Text, string? Country);

    public static IReadOnlyList<Segment> Split(string text)
    {
        var result = new List<Segment>();
        var sb = new StringBuilder();
        var runes = text.EnumerateRunes().ToArray();
        for (var i = 0; i < runes.Length; i++)
        {
            if (IsIndicator(runes[i]) && i + 1 < runes.Length && IsIndicator(runes[i + 1]))
            {
                if (sb.Length > 0)
                {
                    result.Add(new Segment(sb.ToString(), null));
                    sb.Clear();
                }
                var code = string.Concat(Letter(runes[i]), Letter(runes[i + 1]));
                result.Add(new Segment(char.ConvertFromUtf32(runes[i].Value) + char.ConvertFromUtf32(runes[i + 1].Value), code));
                i++;
                continue;
            }
            // Одинокая буква-индикатор, флаги регионов (🏴 + теги) и вариационные селекторы — не текст.
            if (IsIndicator(runes[i]) || runes[i].Value is 0x1F3F4 or 0xFE0F or (>= 0xE0020 and <= 0xE007F))
                continue;
            sb.Append(runes[i].ToString());
        }
        if (sb.Length > 0)
            result.Add(new Segment(sb.ToString(), null));
        return result;
    }

    /// <summary>Код страны первого флага в имени; null — флагов нет.</summary>
    public static string? FirstCountry(string text) => Split(text).FirstOrDefault(s => s.Country != null).Country;

    /// <summary>Имя без флагов — для трея и уведомлений. «Финляндия🇫🇮🇫🇮 xhttp» → «Финляндия xhttp».</summary>
    public static string Strip(string text)
    {
        var parts = Split(text).Where(s => s.Country == null).Select(s => s.Text);
        var joined = string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));
        return joined.Length > 0 ? joined : text.Trim();
    }

    private static bool IsIndicator(Rune r) => r.Value is >= First and <= Last;

    private static char Letter(Rune r) => (char)('A' + r.Value - First);
}
