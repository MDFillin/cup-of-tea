using PVpn.Core.Ipc;

namespace PVpn.Service;

/// <summary>
/// Журнал службы: кольцевой буфер для интерфейса плюс файл по дням (хранится 7 дней).
/// Строки от ядер приходят уже без секретов (LogSanitizer в CoreProcess).
/// </summary>
internal sealed class ServiceLog
{
    private const int Capacity = 2000;
    private readonly Queue<LogLine> lines = new();
    private readonly object sync = new();
    private long seq;

    public ServiceLog()
    {
        try
        {
            Directory.CreateDirectory(ServicePaths.LogDir);
            foreach (var file in Directory.GetFiles(ServicePaths.LogDir, "service-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                    File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Write(string source, string text)
    {
        var now = DateTimeOffset.Now;
        lock (sync)
        {
            var line = new LogLine(++seq, now, source, text);
            lines.Enqueue(line);
            while (lines.Count > Capacity)
                lines.Dequeue();
            try
            {
                File.AppendAllText(Path.Combine(ServicePaths.LogDir, $"service-{now:yyyyMMdd}.log"),
                    $"{now:HH:mm:ss.fff} [{source}] {text}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Info(string text) => Write("tempest", text);

    public IReadOnlyList<LogLine> After(long after)
    {
        lock (sync)
            return lines.Where(l => l.Seq > after).ToList();
    }
}
