using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace PVpn.Service;

/// <summary>Данные службы в C:\ProgramData\TempestVPN. Доступ только у SYSTEM и администраторов.</summary>
internal static class ServicePaths
{
    public static string DataDir => Product.DataDir(Environment.SpecialFolder.CommonApplicationData);
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string ConfigFile => Path.Combine(DataDir, "service.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SingBoxDir => Path.Combine(DataDir, "sing-box");

    public static void EnsureDirectories()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (Directory.Exists(DataDir))
            new DirectoryInfo(DataDir).SetAccessControl(security);
        else
            security.CreateDirectory(DataDir);

        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(SingBoxDir);
    }
}

/// <summary>Настройки службы, их пишет install.ps1.</summary>
internal sealed record ServiceConfig
{
    /// <summary>SID пользователей, которым разрешено управлять службой через канал (кроме SYSTEM и администраторов).</summary>
    public List<string> AllowedUserSids { get; init; } = [];

    public static ServiceConfig Load()
    {
        try
        {
            if (File.Exists(ServicePaths.ConfigFile))
                return JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(ServicePaths.ConfigFile), JsonOptions) ?? new ServiceConfig();
        }
        catch (JsonException)
        {
        }
        return new ServiceConfig();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
