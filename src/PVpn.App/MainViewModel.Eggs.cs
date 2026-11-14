using System.Windows.Input;

namespace PVpn.App;

/// <summary>
/// Пасхалки: ничего не меняют в работе VPN, только радуют. Пять щелчков по логотипу, секретный код
/// (↑↑↓↓←→←→BA), поиск чая среди серверов, поздравление на Новый год, снежки на главной зимой.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string[] TeaLines =
    [
        "☕ Чай заварен. Пакетики проходят через туннель без досмотра.",
        "☕ Улун, пуэр, сенча — а сервер всё равно лучший тот, что ближе.",
        "☕ Совет дня: пинг ниже, когда чай горячее. Научно не доказано.",
        "☕ Лучшая защита — kill switch и кружка тёплого чая.",
        "☕ В этой версии чайник не закипает сам. Пока.",
        "☕ Если долго смотреть на кнопку питания, она начинает смотреть на вас.",
        "☕ Ни одна чаинка не пострадала при шифровании этого трафика.",
        "☕ Хватит щёлкать по чашке, она горячая!",
    ];

    private int logoClicks;
    private long logoClicksStarted;
    private int teaLine = -1;

    /// <summary>Пасхалка просит главную: снег из точки (зимой) или середины окна.</summary>
    public event Action? Celebrate;

    /// <summary>Щелчок по логотипу. true — набралось пять подряд: показана пасхалка, логотип качнётся.</summary>
    public bool LogoClicked()
    {
        var now = Environment.TickCount64;
        if (now - logoClicksStarted > 3000)
        {
            logoClicks = 0;
            logoClicksStarted = now;
        }
        if (++logoClicks < 5)
            return false;
        logoClicks = 0;
        teaLine = (teaLine + 1 + Random.Shared.Next(TeaLines.Length - 1)) % TeaLines.Length;
        Notice = TeaLines[teaLine];
        Celebrate?.Invoke();
        return true;
    }

    private static readonly Key[] SecretCode = [Key.Up, Key.Up, Key.Down, Key.Down, Key.Left, Key.Right, Key.Left, Key.Right, Key.B, Key.A];
    private int secretProgress;

    /// <summary>Нажатая клавиша (окно передаёт все). true — набран секретный код.</summary>
    public bool SecretKey(Key key)
    {
        secretProgress = key == SecretCode[secretProgress] ? secretProgress + 1 : key == SecretCode[0] ? 1 : 0;
        if (secretProgress < SecretCode.Length)
            return false;
        secretProgress = 0;
        Notice = IsWinter
            ? "❄ ↑↑↓↓←→←→BA: снегопад по заказу. Скорость та же, зато красиво."
            : "☕ ↑↑↓↓←→←→BA: секретный сорт «Улун-турбо» заварен. Скорость та же, на душе теплее.";
        Celebrate?.Invoke();
        return true;
    }

    /// <summary>Поздравление над кнопкой на главной: 30–31 декабря и в новогодние каникулы.</summary>
    public string? HolidayGreeting => DateTime.Now switch
    {
        { Month: 12, Day: >= 30 } => "С наступающим Новым годом!",
        { Month: 1, Day: <= 8 } => "С Новым годом!",
        _ => null,
    };

    /// <summary>Поиск по серверам ничего не нашёл (и что сказать вместо пустоты).</summary>
    public string? SearchEmptyText
    {
        get
        {
            var filter = ServerFilter.Trim();
            if (filter.Length == 0 || Profiles.Count == 0 || !ProfilesView.IsEmpty)
                return null;
            return filter.ToLowerInvariant() switch
            {
                "чай" or "tea" or "чаи" or "чаек" or "чаёк" => "Чай не найден среди серверов. Но мы уже поставили чайник ☕",
                "кофе" or "coffee" => "Кофе? В The Cup of Tea? Смело.",
                "дед мороз" or "санта" or "santa" => "Дед Мороз подключается только 31 декабря, через самый быстрый сервер 🎅",
                _ => $"Ничего не нашлось по «{filter}»",
            };
        }
    }
}
