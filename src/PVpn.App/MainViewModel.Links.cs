using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using PVpn.Core.Profiles;
using PVpn.Core.Routing;

namespace PVpn.App;

/// <summary>
/// Ссылки «одним нажатием»: cupoftea://add/&lt;адрес подписки&gt;, cupoftea://routing/onadd/&lt;профиль&gt; и т. п.
/// Те же пути понимает кнопка «Вставить ссылки» с любой схемой — так их выдают панели и боты.
/// </summary>
public sealed partial class MainViewModel
{
    public const string LinkScheme = "cupoftea";

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://(?<action>add|onadd|import|sub|routing/onadd|routing/add)/(?<payload>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AppLinkRegex();

    /// <summary>Похоже ли на ссылку действия (а не на ссылку сервера vless:// / hy2://).</summary>
    public static bool IsAppLink(string? text) => text != null && AppLinkRegex().IsMatch(text.Trim());

    /// <summary>
    /// Регистрирует схему cupoftea:// для текущего пользователя (без прав администратора), чтобы ссылки с сайтов
    /// и из ботов открывались в программе. Вызывается при каждом запуске: путь к exe мог смениться.
    /// </summary>
    public static void RegisterLinkScheme()
    {
        if (Environment.ProcessPath is not { } exe || Product.IsPortable)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{LinkScheme}");
            key.SetValue("", $"URL:{Brand.Name}");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>
    /// Выполняет ссылку действия. <paramref name="fromOutside"/> — пришла из браузера или другой программы:
    /// тогда перед добавлением спрашиваем (ссылку мог открыть любой сайт).
    /// </summary>
    public async Task HandleLinkAsync(string link, bool fromOutside)
    {
        var m = AppLinkRegex().Match(link.Trim());
        if (!m.Success)
        {
            Notice = "Ссылка не распознана.";
            return;
        }
        var action = m.Groups["action"].Value.ToLowerInvariant();
        var payload = Uri.UnescapeDataString(m.Groups["payload"].Value);

        if (action.StartsWith("routing", StringComparison.Ordinal))
        {
            if (RoutingProfiles.TryParse(payload) is not { } profile)
            {
                Notice = "В ссылке нет профиля маршрутизации.";
                return;
            }
            if (fromOutside && !Confirm($"Добавить профиль маршрутизации «{profile.Name}» ({profile.RuleCount} правил) и включить его?"))
                return;
            ApplyImportedRouting(profile);
            return;
        }

        // add/onadd/import/sub: адрес подписки, base64 от него или сами ссылки серверов.
        var text = payload.Contains("://", StringComparison.Ordinal) || !LinkParser.TryDecodeBase64(payload, out var decoded) ? payload : decoded.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            if (fromOutside && !Confirm($"Добавить подписку с {uri.Host}?"))
                return;
            await AddSubscriptionAsync(uri.AbsoluteUri);
            return;
        }
        if (fromOutside && !Confirm("Добавить серверы из ссылки?"))
            return;
        Import(text);
    }

    private static bool Confirm(string question) =>
        MessageBox.Show(question, Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
