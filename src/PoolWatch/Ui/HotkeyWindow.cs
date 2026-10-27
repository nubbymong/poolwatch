using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// A message-only window that owns PoolWatch's global hotkey (RegisterHotKey: PoolWatch's own registration, no hook
/// into other processes). Fails quietly when another app already holds the combination; <see cref="Registered"/> says so.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, Id = 0x5057;
    private static readonly IntPtr HwndMessage = new(-3);
    private readonly Action _pressed;

    public HotkeyWindow(string? hotkey, Action pressed)
    {
        _pressed = pressed;
        Text = hotkey ?? "";
        if (Core.Experiments.Hotkey.Parse(hotkey) is not { } k) return;
        CreateHandle(new CreateParams { Parent = HwndMessage });
        Registered = RegisterHotKey(Handle, Id, k.modifiers | Core.Experiments.Hotkey.NoRepeat, k.vk);
    }

    public bool Registered { get; }
    public string Text { get; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == Id) _pressed();
        else base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        if (Registered) UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
