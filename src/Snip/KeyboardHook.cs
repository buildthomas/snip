using System.Runtime.InteropServices;
using static Snip.Native;

namespace Snip;

// Low-level hook so Win+Shift+S can be taken from the shell, which RegisterHotKey cannot do.
// The callback runs for every keystroke system-wide, so it lives on its own thread (never blocked by
// overlay painting) and does nothing beyond a few key-state checks: no I/O, no logging.
internal sealed class KeyboardHook : IDisposable
{
    private const uint WM_REINSTALL = 0x8001;

    private readonly LowLevelKeyboardProc _proc;
    private readonly SynchronizationContext _ui;
    private readonly Action _onTrigger;
    private readonly Thread _thread;
    private readonly HotkeyFilter _filter = new();
    private uint _threadId;
    private IntPtr _hook;

    public bool PrintScreenEnabled
    {
        get => Volatile.Read(ref _printScreen);
        set => Volatile.Write(ref _printScreen, value);
    }
    private bool _printScreen = true;

    public KeyboardHook(Action onTrigger)
    {
        _onTrigger = onTrigger;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create the hook on the UI thread.");
        _proc = HookProc;
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = Install();
            if (_hook == IntPtr.Zero) error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            if (error != null) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd != IntPtr.Zero || msg.message != WM_REINSTALL) continue;
                // Windows silently drops a hook that ever exceeded LowLevelHooksTimeout; re-registering is cheap.
                var fresh = Install();
                if (fresh == IntPtr.Zero) continue;
                UnhookWindowsHookEx(_hook);
                _hook = fresh;
            }
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Snip keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
    }

    private IntPtr Install() => SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

    public void Reinstall()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = (uint)Marshal.ReadInt32(lParam);
            if (vk == HotkeyFilter.VkS || vk == HotkeyFilter.VkSnapshot)
            {
                var msg = (int)wParam;
                var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var mods = Mods.None;
                    if (Down(VK_SHIFT)) mods |= Mods.Shift;
                    if (Down(VK_CONTROL)) mods |= Mods.Ctrl;
                    if (Down(VK_MENU)) mods |= Mods.Alt;
                    if (Down(VK_LWIN) || Down(VK_RWIN)) mods |= Mods.Win;
                    _filter.PrintScreenEnabled = PrintScreenEnabled;
                    var d = _filter.OnKey(vk, isDown, (uint)Marshal.ReadInt32(lParam, 12), mods);
                    if (d.SuppressStartMenu) SuppressStartMenu();
                    if (d.Fire) _ui.Post(_ => _onTrigger(), null);
                    if (d.Swallow) return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SuppressStartMenu()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_DUMMY;
        inputs[0].u.ki.dwExtraInfo = SnipInputTag;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_DUMMY;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[1].u.ki.dwExtraInfo = SnipInputTag;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _threadId = 0;
        }
    }
}
