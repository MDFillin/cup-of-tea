using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PVpn.App;

/// <summary>
/// Глобальное сочетание клавиш (RegisterHotKey) на невидимом окне сообщений: работает, даже когда
/// главное окно ни разу не открывали (запуск в трей).
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x5056;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource source;
    private readonly Action pressed;
    private bool registered;

    public GlobalHotkey(Action pressed)
    {
        this.pressed = pressed;
        // HWND_MESSAGE (-3): окно только для сообщений, его не видно и нет на панели задач.
        source = new HwndSource(new HwndSourceParameters(Product.Id + " hotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(Hook);
    }

    /// <summary>Разбирает «Ctrl+Alt+P»; null — пусто. Без Ctrl, Alt или Win сочетание не принимается.</summary>
    public static KeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var gesture = (KeyGesture?)new KeyGestureConverter().ConvertFromInvariantString(text.Trim());
            if (gesture == null || (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
                throw new FormatException("Нужна клавиша вместе с Ctrl, Alt или Win, например Ctrl+Alt+P");
            return gesture;
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            throw new FormatException($"Не понимаю сочетание «{text}». Пример: Ctrl+Alt+P");
        }
    }

    /// <summary>Назначает сочетание (null — снять). false — его уже заняла другая программа.</summary>
    public bool Set(KeyGesture? gesture)
    {
        if (registered)
        {
            UnregisterHotKey(source.Handle, Id);
            registered = false;
        }
        if (gesture == null)
            return true;
        var mods = MOD_NOREPEAT
                   | ((gesture.Modifiers & ModifierKeys.Alt) != 0 ? 1u : 0)
                   | ((gesture.Modifiers & ModifierKeys.Control) != 0 ? 2u : 0)
                   | ((gesture.Modifiers & ModifierKeys.Shift) != 0 ? 4u : 0)
                   | ((gesture.Modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
        registered = RegisterHotKey(source.Handle, Id, mods, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));
        return registered;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Set(null);
        source.RemoveHook(Hook);
        source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
