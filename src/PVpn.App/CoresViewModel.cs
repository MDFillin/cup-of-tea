using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PVpn.Core.Ipc;

namespace PVpn.App;

/// <summary>
/// Раздел настроек «Ядра»: версии xray и sing-box, проверка и обновление с GitHub, откат. Всё делает служба
/// (у неё права на папку программы); интерфейс только просит и показывает ход.
/// </summary>
public sealed partial class CoresViewModel : ObservableObject
{
    private readonly ServiceClient service;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<string> Lines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(UpdateCommand), nameof(RollbackCommand), nameof(UpdateGeoCommand))]
    private bool busy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool updateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    private bool rollbackAvailable;

    [ObservableProperty] private string? message;
    [ObservableProperty] private string? error;

    /// <summary>«Базы geoip/geosite: 31.10.2026, Россия (runetfreedom)».</summary>
    [ObservableProperty] private string? geoText;

    /// <summary>Последнее состояние от службы (для автообновления баз).</summary>
    public CoreStatus? Last { get; private set; }

    private readonly Func<string> geoSource;

    public CoresViewModel(ServiceClient service, Func<string> geoSource)
    {
        this.service = service;
        this.geoSource = geoSource;
        poll.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Показать, что стоит сейчас (при открытии настроек).</summary>
    public Task LoadAsync() => SendAsync(CoreActions.Status);

    /// <summary>Автообновление баз (без вопросов и сообщений об ошибке связи со службой).</summary>
    public Task UpdateGeoQuietAsync() => SendAsync(CoreActions.GeoUpdate);

    private bool CanCheck() => !Busy;
    private bool CanUpdate() => !Busy && UpdateAvailable;
    private bool CanRollback() => !Busy && RollbackAvailable;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => SendAsync(CoreActions.Check);

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync() => SendAsync(CoreActions.Update);

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => SendAsync(CoreActions.Rollback);

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task UpdateGeoAsync() => SendAsync(CoreActions.GeoUpdate);

    private async Task SendAsync(string action)
    {
        try
        {
            var r = await service.SendAsync(new IpcRequest
            {
                Command = IpcCommands.Cores, CoreAction = action, GeoSource = action == CoreActions.GeoUpdate ? geoSource() : null,
            }, TimeSpan.FromSeconds(30));
            if (r.Cores != null)
                Apply(r.Cores);
            if (!r.Ok)
                Error = r.Error;
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            Error = $"Служба {Brand.Name} не отвечает — ядра обновляет она: " + e.Message;
        }
    }

    private async Task RefreshAsync()
    {
        poll.Stop();
        await SendAsync(CoreActions.Status);
    }

    private void Apply(CoreStatus s)
    {
        Last = s;
        GeoText = "Базы geoip/geosite: " + (s.GeoUpdated is { } at ? at.LocalDateTime.ToString("dd.MM.yyyy") : "нет")
                  + (s.GeoSource != null ? $", {PVpn.Core.Cores.GeoUpdater.SourceOf(s.GeoSource).Title}" : ", из комплекта xray");
        Busy = s.Busy;
        Message = s.Message;
        Error = s.Error;
        if (s.Cores.Count > 0)
        {
            Lines.Clear();
            foreach (var c in s.Cores)
            {
                Lines.Add($"{c.Name} {c.Current ?? "не найден"}" +
                          (c.UpdateAvailable ? $" — есть {c.Latest}" : c.Latest != null ? " — последняя версия" : "") +
                          (c.Previous != null ? $" (для отката: {c.Previous})" : ""));
            }
            UpdateAvailable = s.Cores.Any(c => c.UpdateAvailable);
            RollbackAvailable = s.Cores.Any(c => c.Previous != null);
        }
        if (s.Busy)
            poll.Start();
    }
}
