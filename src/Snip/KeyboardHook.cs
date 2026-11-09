using System.Runtime.InteropServices;
using static Snip.Native;

namespace Snip;

// Low-level hook so Win+Shift+S can be taken from the shell, which RegisterHotKey cannot do.
// The callback runs for every keystroke system-wide, so it lives on its own thread (never blocked by
// overlay painting) and does nothing beyond a few key-state checks.
internal sealed class KeyboardHook : IDisposable
{
    private readonly LowLevelKeyboardProc _proc;
    private readonly SynchronizationContext _ui;
    private readonly Action _onTrigger;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _swallowSUp;
    private bool _swallowSnapshotUp;

    public volatile bool PrintScreenEnabled = true;

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
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            if (error != null) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Snip keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = (uint)Marshal.ReadInt32(lParam);
            var msg = (int)wParam;
            var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (vk == VK_S)
            {
                if (isDown && (_swallowSUp || IsWinShiftOnly()))
                {
                    if (!_swallowSUp)
                    {
                        _swallowSUp = true;
                        SuppressStartMenu();
                        Fire();
                    }
                    return (IntPtr)1;
                }
                if (isUp && _swallowSUp)
                {
                    _swallowSUp = false;
                    return (IntPtr)1;
                }
            }
            else if (vk == VK_SNAPSHOT && PrintScreenEnabled)
            {
                if (isDown && !Down(VK_CONTROL) && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN))
                {
                    if (!_swallowSnapshotUp)
                    {
                        _swallowSnapshotUp = true;
                        Fire();
                    }
                    return (IntPtr)1;
                }
                if (isUp)
                {
                    // Some keyboards only report PrintScreen on release.
                    var hadDown = _swallowSnapshotUp;
                    _swallowSnapshotUp = false;
                    if (hadDown) return (IntPtr)1;
                    if (!Down(VK_CONTROL) && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN))
                    {
                        Fire();
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsWinShiftOnly() =>
        (Down(VK_LWIN) || Down(VK_RWIN)) && Down(VK_SHIFT) && !Down(VK_CONTROL) && !Down(VK_MENU);

    private void Fire()
    {
        Log.Trace("hotkey");
        _ui.Post(_ => _onTrigger(), null);
    }

    private static void SuppressStartMenu()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_DUMMY;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_DUMMY;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
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
