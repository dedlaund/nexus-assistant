using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NexusAssistant.Services;

internal enum QuickShortcut { S_F1, Ctrl_Alt_Space, Ctrl_Shift_Space, Ctrl_Alt_N, Disabled }

internal sealed class QuickInputHotkey(Dispatcher dispatcher, Action show) : IDisposable
{
    private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    private KeyboardProc? _callback;
    private IntPtr _hook;
    private int _suppressed;
    private bool _sChord;
    public QuickShortcut Shortcut { get; set; }
    public bool Active => _hook != IntPtr.Zero;

    public void Start()
    {
        _callback = Handle;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var down = message == 0x100 || message == 0x104;
            if (_sChord && key == 0x53)
            {
                if (down) return 1;
                _sChord = false;
            }
            if (key == _suppressed)
            {
                if (!down) _suppressed = 0;
                return 1;
            }
            if (down && Matches(key))
            {
                _suppressed = key;
                _sChord = Shortcut == QuickShortcut.S_F1;
                dispatcher.BeginInvoke(DispatcherPriority.Input, show);
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private bool Matches(int key) => Shortcut switch
    {
        QuickShortcut.S_F1 => key == 0x70 && Pressed(0x53) && !Pressed(0x11) && !Pressed(0x12) && !Pressed(0x10),
        QuickShortcut.Ctrl_Alt_Space => key == 0x20 && Pressed(0x11) && Pressed(0x12) && !Pressed(0x10),
        QuickShortcut.Ctrl_Shift_Space => key == 0x20 && Pressed(0x11) && Pressed(0x10) && !Pressed(0x12),
        QuickShortcut.Ctrl_Alt_N => key == 0x4E && Pressed(0x11) && Pressed(0x12) && !Pressed(0x10),
        _ => false
    };

    private static bool Pressed(int key) => GetAsyncKeyState(key) < 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, KeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
}
