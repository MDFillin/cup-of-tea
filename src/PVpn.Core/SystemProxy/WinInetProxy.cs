using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PVpn.Core.SystemProxy;

/// <summary>Настройки системного прокси текущего пользователя (WinINET, «Параметры → Прокси»).</summary>
public sealed record ProxySettings(int Flags, string? Server, string? Bypass, string? AutoConfigUrl)
{
    public bool ProxyEnabled => (Flags & WinInetProxy.PROXY_TYPE_PROXY) != 0;
}

/// <summary>
/// Включает и восстанавливает системный прокси через InternetSetOption(INTERNET_OPTION_PER_CONNECTION_OPTION),
/// как это делает сама Windows: так настройки видят и WinINET, и WinHTTP, и браузеры.
/// Работает в контексте пользователя (не из службы).
/// </summary>
public static partial class WinInetProxy
{
    internal const int PROXY_TYPE_DIRECT = 0x1;
    internal const int PROXY_TYPE_PROXY = 0x2;
    private const int PROXY_TYPE_AUTO_PROXY_URL = 0x4;
    private const int PROXY_TYPE_AUTO_DETECT = 0x8;

    private const int INTERNET_PER_CONN_FLAGS = 1;
    private const int INTERNET_PER_CONN_PROXY_SERVER = 2;
    private const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
    private const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
    private const int INTERNET_PER_CONN_FLAGS_UI = 10;

    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

    public const string DefaultBypass =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;" +
        "172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;26.*;<local>";

    private static string BackupPath =>
        Path.Combine(Product.DataDir(Environment.SpecialFolder.LocalApplicationData), "proxy-backup.json");

    public static ProxySettings Query()
    {
        var options = new INTERNET_PER_CONN_OPTION[4];
        options[0].dwOption = INTERNET_PER_CONN_FLAGS_UI;
        options[1].dwOption = INTERNET_PER_CONN_PROXY_SERVER;
        options[2].dwOption = INTERNET_PER_CONN_PROXY_BYPASS;
        options[3].dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL;

        var handle = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = IntPtr.Zero,
                dwOptionCount = options.Length,
                pOptions = handle.AddrOfPinnedObject(),
            };
            var size = list.dwSize;
            if (!InternetQueryOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref size))
            {
                // Старые системы не знают FLAGS_UI.
                options[0].dwOption = INTERNET_PER_CONN_FLAGS;
                if (!InternetQueryOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InternetQueryOption");
            }
            return new ProxySettings(options[0].Value.dwValue, TakeString(options[1]), TakeString(options[2]), TakeString(options[3]));
        }
        finally
        {
            handle.Free();
        }
    }

    public static void Apply(ProxySettings settings)
    {
        var strings = new List<IntPtr>();
        IntPtr Str(string? s)
        {
            if (s == null)
                return IntPtr.Zero;
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        var options = new INTERNET_PER_CONN_OPTION[4];
        options[0].dwOption = INTERNET_PER_CONN_FLAGS;
        options[0].Value.dwValue = settings.Flags;
        options[1].dwOption = INTERNET_PER_CONN_PROXY_SERVER;
        options[1].Value.pszValue = Str(settings.Server);
        options[2].dwOption = INTERNET_PER_CONN_PROXY_BYPASS;
        options[2].Value.pszValue = Str(settings.Bypass);
        options[3].dwOption = INTERNET_PER_CONN_AUTOCONFIG_URL;
        options[3].Value.pszValue = Str(settings.AutoConfigUrl);

        var handle = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>(),
                pszConnection = IntPtr.Zero,
                dwOptionCount = options.Length,
                pOptions = handle.AddrOfPinnedObject(),
            };
            if (!InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, list.dwSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InternetSetOption");
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        finally
        {
            handle.Free();
            strings.ForEach(Marshal.FreeHGlobal);
        }
    }

    /// <summary>Включает HTTP-прокси 127.0.0.1:port, сохранив прежние настройки для восстановления.</summary>
    public static void Enable(int httpPort)
    {
        var current = Query();
        if (!PointsToUs(current) || !File.Exists(BackupPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(current));
        }
        Apply(new ProxySettings(PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY, $"127.0.0.1:{httpPort}", DefaultBypass, null));
    }

    /// <summary>Возвращает настройки, которые были до включения. Без резервной копии просто выключает прокси.</summary>
    public static void Restore()
    {
        ProxySettings? backup = null;
        if (File.Exists(BackupPath))
        {
            try { backup = JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(BackupPath)); }
            catch (JsonException) { }
        }
        Apply(backup ?? new ProxySettings(PROXY_TYPE_DIRECT, null, null, null));
        File.Delete(BackupPath);
    }

    /// <summary>Прокси указывает на наш локальный порт (значит, его включили мы).</summary>
    public static bool PointsToUs(ProxySettings s) =>
        s.ProxyEnabled && s.Server != null && s.Server.StartsWith("127.0.0.1:", StringComparison.Ordinal) && File.Exists(BackupPath);

    /// <summary>Есть резервная копия, а значит, прошлый сеанс не восстановил прокси (например, упал).</summary>
    public static bool HasPendingRestore => File.Exists(BackupPath);

    private static string? TakeString(INTERNET_PER_CONN_OPTION option)
    {
        if (option.Value.pszValue == IntPtr.Zero)
            return null;
        var s = Marshal.PtrToStringUni(option.Value.pszValue);
        GlobalFree(option.Value.pszValue);
        return s;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)] public int dwValue;
        [FieldOffset(0)] public IntPtr pszValue;
        [FieldOffset(0)] public long ftValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION
    {
        public int dwOption;
        public OptionValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount;
        public int dwOptionError;
        public IntPtr pOptions;
    }

    [LibraryImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOptionW(IntPtr hInternet, int option, ref INTERNET_PER_CONN_OPTION_LIST buffer, int length);

    [LibraryImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOptionW(IntPtr hInternet, int option, IntPtr buffer, int length);

    [LibraryImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetQueryOptionW(IntPtr hInternet, int option, ref INTERNET_PER_CONN_OPTION_LIST buffer, ref int length);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}
