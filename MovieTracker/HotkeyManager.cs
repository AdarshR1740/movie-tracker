using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MovieTracker;

public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hWnd;
    private readonly HwndSource _source;
    private readonly List<int> _registeredIds = [];
    public List<string> ActiveHotkeyNames { get; } = [];

    public event Action? HotkeyPressed;

    public HotkeyManager()
    {
        // Create an explicit, active Win32 message-only HwndSource for reliable message processing
        var parameters = new HwndSourceParameters("MovieTrackerHotkeyListener")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _hWnd = _source.Handle;
        _source.AddHook(HwndHook);

        // Also subscribe to WPF's global thread message pump as a dual safeguard
        ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
    }

    public void RegisterHotkeys()
    {
        // Candidate hotkeys in order of preference
        var candidates = new (string Name, int Id, uint Modifiers, uint Vk)[]
        {
            ("Ctrl + Shift + M", 9001, MOD_CONTROL | MOD_SHIFT, 0x4D), // 'M'
            ("Ctrl + Alt + L", 9002, MOD_CONTROL | MOD_ALT, 0x4C),     // 'L'
            ("Ctrl + Shift + L", 9003, MOD_CONTROL | MOD_SHIFT, 0x4C), // 'L'
            ("Ctrl + Alt + M", 9004, MOD_CONTROL | MOD_ALT, 0x4D)      // 'M'
        };

        foreach (var (name, id, mods, vk) in candidates)
        {
            bool success = RegisterHotKey(_hWnd, id, mods | MOD_NOREPEAT, vk);
            if (success)
            {
                _registeredIds.Add(id);
                ActiveHotkeyNames.Add(name);
                Debug.WriteLine($"[Hotkey] Successfully registered: {name}");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[Hotkey] Skipped {name} (Error: {err})");
            }
        }
    }

    private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == WM_HOTKEY && _registeredIds.Contains(msg.wParam.ToInt32()))
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registeredIds.Contains(wParam.ToInt32()))
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;

        foreach (int id in _registeredIds)
        {
            UnregisterHotKey(_hWnd, id);
        }
        _registeredIds.Clear();

        _source.RemoveHook(HwndHook);
        _source.Dispose();
    }
}
