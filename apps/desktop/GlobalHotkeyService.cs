using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowsAICompanion.Desktop;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint VkSpace = 0x20;
    private const int HotkeyId = 1; 

    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public void Register(System.Windows.Window window)
    {
        if (_registered)
        {
            return;
        }

        _windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        if (!RegisterHotKey(_windowHandle, HotkeyId, ModControl, VkSpace))
        {
            throw new InvalidOperationException("Could not register the Ctrl+Space global hotkey.");
        }

        _registered = true;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}