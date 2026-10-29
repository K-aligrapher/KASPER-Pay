using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Kasper.Desktop;

/// <summary>System-wide Ctrl+Space hotkey (FR-101), via RegisterHotKey on a message-only window.</summary>
internal sealed class GlobalHotkey(Action onPressed) : IDisposable
{
    private const int HotkeyId = 0x4B53; // "KS"
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    private HwndSource? _source;

    public bool Register()
    {
        _source = new HwndSource(new HwndSourceParameters("KASPER.Hotkey") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _source.AddHook(WndProc);
        return RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModNoRepeat, VkSpace);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            onPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is null) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
