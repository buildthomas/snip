using Xunit;

namespace Snip.Tests;

public class EscapeGuardTests
{
    [Fact]
    public void WithoutAnnotations_FirstPressCloses()
    {
        Assert.True(new EscapeGuard().ShouldClose(false, 0));
    }

    [Fact]
    public void WithAnnotations_NeedsSecondPressWithinWindow()
    {
        var g = new EscapeGuard();
        Assert.False(g.ShouldClose(true, 1000));
        Assert.True(g.ShouldClose(true, 1000 + EscapeGuard.WindowMs));
    }

    [Fact]
    public void WithAnnotations_SlowSecondPressRearms()
    {
        var g = new EscapeGuard();
        Assert.False(g.ShouldClose(true, 0));
        Assert.False(g.ShouldClose(true, EscapeGuard.WindowMs + 1));
        Assert.True(g.ShouldClose(true, EscapeGuard.WindowMs + 500));
    }

    [Fact]
    public void ClosingResetsSoNextSessionStartsDisarmed()
    {
        var g = new EscapeGuard();
        g.ShouldClose(true, 0);
        Assert.True(g.ShouldClose(true, 100));
        Assert.False(g.ShouldClose(true, 200));
    }
}
