using CommunityToolkit.Mvvm.ComponentModel;
using PVpn.Core.Ipc;

namespace PVpn.App;

/// <summary>
/// Восстановление после перезагрузки. Два уровня:
/// программа после входа в Windows подключается к последнему серверу, если VPN был включён, когда ПК выключили
/// (оба режима, включено по умолчанию); «Защита до входа в Windows» — служба сама поднимает TUN при загрузке
/// (только TUN, выключено по умолчанию; подключение хранит служба, см. IpcCommands.Resume).
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool resumeAfterRestart;
    [ObservableProperty] private bool protectBeforeLogin;
    private bool loadingResume;

    /// <summary>Защита до входа — дело службы: в режиме прокси и в портативной версии её нет.</summary>
    public bool CanProtectBeforeLogin => IsTunMode && !Product.IsPortable;

    /// <summary>VPN был включён при выключении ПК: подключиться, как только будет сеть (один раз после запуска).</summary>
    private bool resumePending;

    private void LoadResumeSettings()
    {
        loadingResume = true;
        ResumeAfterRestart = settings.ResumeAfterRestart;
        ProtectBeforeLogin = settings.ProtectBeforeLogin;
        loadingResume = false;
        resumePending = !SnapshotMode && settings.ResumeAfterRestart && settings.WasConnected;
        if (resumePending)
            AppendLog("app", "VPN был включён при выключении компьютера — подключусь, как только будет сеть");
    }

    partial void OnResumeAfterRestartChanged(bool value)
    {
        if (loadingResume)
            return;
        settings.ResumeAfterRestart = value;
        SaveSettings();
    }

    partial void OnProtectBeforeLoginChanged(bool value)
    {
        if (loadingResume)
            return;
        settings.ProtectBeforeLogin = value;
        SaveSettings();
        _ = SendRememberAsync(value);
    }

    /// <summary>Служба запоминает текущее подключение или сразу забывает запомненное.</summary>
    private async Task SendRememberAsync(bool remember)
    {
        try
        {
            var r = await service.SendAsync(new IpcRequest { Command = IpcCommands.Resume, Remember = remember }, TimeSpan.FromSeconds(10));
            if (!r.Ok)
                Notice = r.Error;
        }
        catch (Exception e) when (e is ServiceUnavailableException or IOException or TimeoutException or OperationCanceledException)
        {
            Notice = remember
                ? $"Служба {Brand.Name} сейчас недоступна: защита включится при следующем подключении в режиме TUN."
                : $"Служба {Brand.Name} сейчас недоступна ({e.Message}): запомненное подключение удалится при следующем отключении.";
        }
    }

    /// <summary>Передать ли службе Remember при подключении в режиме TUN.</summary>
    private bool RememberForService => ProtectBeforeLogin && CanProtectBeforeLogin;

    /// <summary>VPN подключился: после перезагрузки его надо будет вернуть.</summary>
    private void TrackConnected(UiState state)
    {
        if (state != UiState.On || SnapshotMode)
            return;
        resumePending = false;
        _ = CheckAntivirusAsync();
        if (settings.WasConnected)
            return;
        settings.WasConnected = true;
        SaveSettings();
    }

    /// <summary>Пользователь сам выключил VPN или вышел из программы: после перезагрузки не подключаться.</summary>
    private void ForgetConnected()
    {
        resumePending = false;
        if (!settings.WasConnected || SnapshotMode)
            return;
        settings.WasConnected = false;
        SaveSettings();
    }
}
