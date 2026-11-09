using Xunit;

namespace Snip.Tests;

public class HotkeyFilterTests
{
    private const uint S = HotkeyFilter.VkS, Snap = HotkeyFilter.VkSnapshot, Other = 0x41;
    private static readonly Mods WinShift = Mods.Win | Mods.Shift;

    [Fact]
    public void WinShiftS_FiresOnce_SwallowsRepeatsAndRelease()
    {
        var f = new HotkeyFilter();
        var first = f.OnKey(S, true, 100, WinShift);
        Assert.True(first.Swallow && first.Fire && first.SuppressStartMenu);

        var repeat = f.OnKey(S, true, 130, WinShift);
        Assert.True(repeat.Swallow);
        Assert.False(repeat.Fire);

        Assert.True(f.OnKey(S, false, 200, Mods.None).Swallow);
        Assert.False(f.OnKey(S, true, 300, Mods.None).Swallow); // plain "s" afterwards types normally
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(1 | 2 | 8)]
    [InlineData(1 | 4 | 8)]
    public void SWithoutExactlyWinShift_PassesThrough(int modBits)
    {
        var mods = (Mods)modBits;
        var f = new HotkeyFilter();
        var d = f.OnKey(S, true, 100, mods);
        Assert.False(d.Swallow || d.Fire);
        Assert.False(f.OnKey(S, false, 150, mods).Swallow);
    }

    [Fact]
    public void LostSKeyUp_DoesNotEatLaterTyping()
    {
        var f = new HotkeyFilter();
        f.OnKey(S, true, 1000, WinShift);
        // Key-up never arrives (e.g. Win+L while held). Much later the user types "s".
        var later = f.OnKey(S, true, 1000 + HotkeyFilter.StaleMs + 1, Mods.None);
        Assert.False(later.Swallow);

        var again = new HotkeyFilter();
        again.OnKey(S, true, 1000, WinShift);
        var next = again.OnKey(S, true, 1000 + HotkeyFilter.StaleMs + 1, WinShift);
        Assert.True(next.Fire && next.SuppressStartMenu);
    }

    [Fact]
    public void PlainPrintScreen_FiresOnce_SwallowsRelease()
    {
        var f = new HotkeyFilter();
        var down = f.OnKey(Snap, true, 100, Mods.None);
        Assert.True(down.Swallow && down.Fire);
        Assert.False(down.SuppressStartMenu);
        Assert.False(f.OnKey(Snap, true, 130, Mods.None).Fire);
        var up = f.OnKey(Snap, false, 200, Mods.None);
        Assert.True(up.Swallow);
        Assert.False(up.Fire);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(2)]
    public void ModifiedPrintScreen_IsLeftToWindows_EvenIfModifierReleasedFirst(int modBits)
    {
        var mods = (Mods)modBits;
        var f = new HotkeyFilter();
        var down = f.OnKey(Snap, true, 100, mods);
        Assert.False(down.Swallow || down.Fire);
        // Modifier released before PrtSc: the release must not trigger Snip.
        var up = f.OnKey(Snap, false, 200, Mods.None);
        Assert.False(up.Swallow || up.Fire);
    }

    [Fact]
    public void ReleaseOnlyKeyboard_FiresOnRelease()
    {
        var f = new HotkeyFilter();
        var up = f.OnKey(Snap, false, 100, Mods.None);
        Assert.True(up.Swallow && up.Fire);
    }

    [Fact]
    public void DisablingPrintScreenMidPress_StillSwallowsTheMatchingRelease()
    {
        var f = new HotkeyFilter();
        f.OnKey(Snap, true, 100, Mods.None);
        f.PrintScreenEnabled = false;
        Assert.True(f.OnKey(Snap, false, 200, Mods.None).Swallow);
        Assert.False(f.OnKey(Snap, true, 300, Mods.None).Swallow);
    }

    [Fact]
    public void PrintScreenDisabled_PassesThrough()
    {
        var f = new HotkeyFilter { PrintScreenEnabled = false };
        Assert.False(f.OnKey(Snap, true, 100, Mods.None).Swallow);
        Assert.False(f.OnKey(Snap, false, 150, Mods.None).Swallow);
    }

    [Fact]
    public void LostPrintScreenKeyUp_RecoversOnNextPress()
    {
        var f = new HotkeyFilter();
        f.OnKey(Snap, true, 1000, Mods.None);
        var next = f.OnKey(Snap, true, 1000 + HotkeyFilter.StaleMs + 1, Mods.None);
        Assert.True(next.Fire);
    }

    [Fact]
    public void TickCountWraparound_IsHandled()
    {
        var f = new HotkeyFilter();
        f.OnKey(S, true, uint.MaxValue - 10, WinShift);
        Assert.True(f.OnKey(S, true, 20, WinShift).Swallow); // 31 ms later across the wrap: still a repeat
        Assert.False(f.OnKey(S, true, 20, WinShift).Fire);
    }

    [Fact]
    public void OtherKeys_AlwaysPass()
    {
        var f = new HotkeyFilter();
        Assert.False(f.OnKey(Other, true, 1, WinShift).Swallow);
    }
}
