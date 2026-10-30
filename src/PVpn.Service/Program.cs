using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using PVpn.Core.Cores;
using PVpn.Core.Ipc;
using PVpn.Service;

// TempestVPN.Service.exe              — служба Windows (или консольный режим при запуске вручную от администратора)
// TempestVPN.Service.exe --repair     — вернуть сеть в исходное состояние и выйти
// TempestVPN.Service.exe --ctrl-c PID — (служебное) послать Ctrl+C ядру, см. ConsoleSignal
if (args is ["--ctrl-c", var pidText] && int.TryParse(pidText, out var targetPid))
    return ConsoleSignal.SendCtrlC(targetPid) ? 0 : 1;

CoreProcess.GracefulStop = SendCtrlCViaHelper;

var isService = WindowsServiceHelpers.IsWindowsService();
var cores = CorePaths.Resolve(allowDevFallback: !isService);

if (args.Contains("--repair"))
{
    ServicePaths.EnsureDirectories();
    var log = new ServiceLog();
    log.Info("Восстановление сети по команде --repair");
    Recovery.Run(cores, log);
    Console.WriteLine("Сеть восстановлена");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = IpcConstants.ServiceName);
builder.Services.AddSingleton(cores);
builder.Services.AddSingleton(ServiceConfig.Load());
builder.Services.AddSingleton<ServiceLog>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<CoreUpdateManager>();
// Порядок важен: сначала SessionManager (восстановление после сбоя), потом канал команд.
builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionManager>());
builder.Services.AddHostedService<PipeServer>();
await builder.Build().RunAsync();
return 0;

static bool SendCtrlCViaHelper(int pid)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    psi.ArgumentList.Add("--ctrl-c");
    psi.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
    try
    {
        using var helper = Process.Start(psi);
        return helper != null && helper.WaitForExit(TimeSpan.FromSeconds(5)) && helper.ExitCode == 0;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return false;
    }
}
