using System.Diagnostics;
using System.Text;
using PVpn.Core.Diagnostics;

namespace PVpn.Core.Cores;

/// <summary>
/// Процесс ядра (xray или sing-box). Конфиг передаётся через stdin и на диск не пишется.
/// Процесс сразу попадает в общий Job Object и не переживёт наш процесс.
/// </summary>
public sealed class CoreProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LogSanitizer sanitizer;
    private readonly Action<string, string> onLine;
    private readonly List<string> lastLines = [];

    public string Name { get; }
    public int Id => process.Id;
    public bool HasExited => exited.Task.IsCompleted;
    public Task<int> Exited => exited.Task;

    private CoreProcess(string name, Process process, LogSanitizer sanitizer, Action<string, string> onLine)
    {
        Name = name;
        this.process = process;
        this.sanitizer = sanitizer;
        this.onLine = onLine;
    }

    /// <param name="onLine">Получает (имя ядра, строка лога) — уже без секретов.</param>
    public static CoreProcess Start(string name, string exePath, IEnumerable<string> args, string configJson,
        LogSanitizer sanitizer, Action<string, string> onLine, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exePath)!,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        // Без цветных escape-последовательностей в логах.
        psi.Environment["NO_COLOR"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var core = new CoreProcess(name, process, sanitizer, onLine);
        process.OutputDataReceived += (_, e) => core.HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => core.HandleLine(e.Data);
        process.Exited += (_, _) => core.exited.TrySetResult(SafeExitCode(process));

        ProcessStarter.Start(process);
        try
        {
            JobObject.Shared.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(configJson);
            process.StandardInput.Close();
        }
        catch
        {
            TryKill(process);
            throw;
        }
        return core;
    }

    /// <summary>Последние строки лога: показываются пользователю, если ядро упало при запуске.</summary>
    public string Tail(int count = 8)
    {
        lock (lastLines)
            return string.Join(Environment.NewLine, lastLines.TakeLast(count));
    }

    private void HandleLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        var clean = sanitizer.Clean(StripAnsi(line));
        lock (lastLines)
        {
            lastLines.Add(clean);
            if (lastLines.Count > 50)
                lastLines.RemoveAt(0);
        }
        onLine(Name, clean);
    }

    /// <summary>
    /// Штатная остановка по PID (Ctrl+C через процесс-помощник, см. <see cref="ConsoleSignal"/>).
    /// Задаёт служба; true — сигнал отправлен. Не задано — ядра останавливаются через Kill.
    /// </summary>
    public static Func<int, bool>? GracefulStop { get; set; }

    /// <summary>Сначала штатно (<see cref="GracefulStop"/>), не вышло за <paramref name="graceful"/> (5 с) — Kill.</summary>
    public async Task StopAsync(TimeSpan? graceful = null)
    {
        if (!HasExited && GracefulStop is { } stop && await Task.Run(() => stop(process.Id)).ConfigureAwait(false))
            await Task.WhenAny(exited.Task, Task.Delay(graceful ?? TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (!HasExited)
            TryKill(process);
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        process.Dispose();
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static string StripAnsi(string s)
    {
        if (!s.Contains('\u001b'))
            return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && !char.IsAsciiLetter(s[i]))
                    i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
