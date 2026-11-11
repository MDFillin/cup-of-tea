using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Sessions;
using PVpn.Core.SystemProxy;

namespace PVpn.App;

public sealed partial class MainViewModel
{
    // ───────────── Логи ─────────────

    private async Task PollLogsAsync()
    {
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Logs, LogsAfter = logSeq }, TimeSpan.FromSeconds(5));
            if (r.Logs is not { Count: > 0 } lines)
                return;
            foreach (var l in lines)
            {
                logSeq = Math.Max(logSeq, l.Seq);
                logLines.Add($"{l.Time.ToLocalTime():HH:mm:ss} [{l.Source}] {l.Text}");
            }
            TrimAndPublishLogs();
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
        }
    }

    private void AppendLog(string source, string text)
    {
        logLines.Add($"{DateTime.Now:HH:mm:ss} [{source}] {text}");
        TrimAndPublishLogs();
    }

    /// <summary>Строки пришли, а страница журнала закрыта: текст соберём, когда её откроют.</summary>
    private bool logDirty;

    private void TrimAndPublishLogs()
    {
        if (logLines.Count > MaxLogLines)
            logLines.RemoveRange(0, logLines.Count - MaxLogLines);
        // Собирать 3000 строк и перерисовывать их в TextBox каждые 2 с, пока журнал никто не смотрит, — зря грузить ЦП.
        if (Page != LogPage)
        {
            logDirty = true;
            return;
        }
        logDirty = false;
        LogText = FullLog();
    }

    private string FullLog() => string.Join(Environment.NewLine, logLines);

    partial void OnPageChanged(int value)
    {
        SettingsPage = null;
        if (value == LogPage && logDirty)
            TrimAndPublishLogs();
    }

    [RelayCommand]
    private void CopyLogs()
    {
        try
        {
            Clipboard.SetText(FullLog());
            Notice = "Логи скопированы в буфер обмена (ключи и пароли в них скрыты).";
        }
        catch (System.Runtime.InteropServices.ExternalException e)
        {
            Notice = $"Не удалось скопировать: {e.Message}";
        }
    }
}
