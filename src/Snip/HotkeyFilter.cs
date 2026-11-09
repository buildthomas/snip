namespace Snip;

[Flags]
internal enum Mods { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Win = 8 }

internal readonly record struct HotkeyDecision(bool Swallow, bool Fire, bool SuppressStartMenu);

// Pure keystroke state machine behind KeyboardHook, kept free of Win32 calls so it can be unit tested.
internal sealed class HotkeyFilter
{
    public const uint VkS = 0x53, VkSnapshot = 0x2C;
    // Key repeat fires at least every ~1 s while held, so an older swallowed press means its key-up was lost.
    public const uint StaleMs = 1500;

    private static readonly HotkeyDecision Pass = new(false, false, false);
    private static readonly HotkeyDecision Swallow = new(true, false, false);

    private bool _swallowSUp;
    private uint _sTime;
    private bool _snapshotDown;
    private bool _swallowSnapshotUp;
    private uint _snapshotTime;

    public bool PrintScreenEnabled { get; set; } = true;

    public HotkeyDecision OnKey(uint vk, bool isDown, uint time, Mods mods)
    {
        if (vk == VkS) return OnS(isDown, time, mods);
        if (vk == VkSnapshot) return OnSnapshot(isDown, time, mods);
        return Pass;
    }

    private HotkeyDecision OnS(bool isDown, uint time, Mods mods)
    {
        if (!isDown)
        {
            if (!_swallowSUp) return Pass;
            _swallowSUp = false;
            return Swallow;
        }
        if (_swallowSUp && time - _sTime > StaleMs) _swallowSUp = false;
        if (_swallowSUp)
        {
            _sTime = time;
            return Swallow;
        }
        if ((mods & Mods.Win) != 0 && (mods & Mods.Shift) != 0 && (mods & (Mods.Ctrl | Mods.Alt)) == 0)
        {
            _swallowSUp = true;
            _sTime = time;
            return new HotkeyDecision(true, true, true);
        }
        return Pass;
    }

    private HotkeyDecision OnSnapshot(bool isDown, uint time, Mods mods)
    {
        var plain = (mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) == 0;
        if (isDown)
        {
            if (_snapshotDown && time - _snapshotTime > StaleMs) _snapshotDown = _swallowSnapshotUp = false;
            var first = !_snapshotDown;
            _snapshotDown = true;
            _snapshotTime = time;
            if (_swallowSnapshotUp) return Swallow;
            if (first && PrintScreenEnabled && plain)
            {
                _swallowSnapshotUp = true;
                return new HotkeyDecision(true, true, false);
            }
            return Pass;
        }

        var sawDown = _snapshotDown;
        var swallowed = _swallowSnapshotUp;
        _snapshotDown = _swallowSnapshotUp = false;
        if (swallowed) return Swallow;
        // Some keyboards only report PrintScreen on release; only then does the release trigger.
        if (!sawDown && PrintScreenEnabled && plain) return new HotkeyDecision(true, true, false);
        return Pass;
    }
}
