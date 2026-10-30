using System.Text.RegularExpressions;

namespace PVpn.Core.Diagnostics;

/// <summary>Вычищает из строк логов UUID, пароли и прочие секреты.</summary>
public sealed partial class LogSanitizer(IEnumerable<string> secrets)
{
    private readonly string[] secrets = secrets.Where(s => s.Length >= 4).Distinct().OrderByDescending(s => s.Length).ToArray();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidRegex();

    public string Clean(string line)
    {
        foreach (var secret in secrets)
            line = line.Replace(secret, "***", StringComparison.Ordinal);
        return UuidRegex().Replace(line, "********-****");
    }
}
