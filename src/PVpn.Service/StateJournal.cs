using System.Text.Json;
using PVpn.Core.Net;

namespace PVpn.Service;

internal sealed record JournalData
{
    public List<HostRoute> Routes { get; init; } = [];
    public bool KillSwitch { get; init; }
    public bool DnsPolicy { get; init; }
    public string? Profile { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>
/// Журнал изменений системы (state.json). Пишется до того, как изменение применено,
/// чтобы после падения службы Recovery знал, что убирать.
/// </summary>
internal static class StateJournal
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static JournalData Load()
    {
        lock (Sync)
        {
            try
            {
                return File.Exists(ServicePaths.StateFile)
                    ? JsonSerializer.Deserialize<JournalData>(File.ReadAllText(ServicePaths.StateFile), Options) ?? new JournalData()
                    : new JournalData();
            }
            catch (JsonException)
            {
                return new JournalData();
            }
        }
    }

    public static void Update(Func<JournalData, JournalData> change)
    {
        lock (Sync)
        {
            var data = change(Load());
            var temp = ServicePaths.StateFile + ".tmp";
            // Сразу на диск: журнал пишется до изменения системы и должен пережить отключение питания.
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(data, Options));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, ServicePaths.StateFile, overwrite: true);
        }
    }

    public static void Clear()
    {
        lock (Sync)
            File.Delete(ServicePaths.StateFile);
    }
}
