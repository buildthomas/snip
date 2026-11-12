using System.Drawing;
using Xunit;

namespace Snip.Tests;

public class RecordingBarPlacementTests
{
    private static readonly Rectangle Monitor = new(0, 0, 2560, 1440);
    private static readonly Rectangle Work = new(0, 0, 2560, 1392);
    private static readonly Size Bar = new(150, 36);

    private static void AssertVisible(Point p) =>
        Assert.True(Work.Contains(new Rectangle(p, Bar)), $"bar at {p} is outside the work area");

    [Fact]
    public void BelowTheAreaWhenThereIsRoom()
    {
        var p = RecordingBar.Place(new Rectangle(600, 300, 640, 400), Work, Bar, 8);
        Assert.Equal(new Point(1240 - 150, 708), p);
    }

    [Fact]
    public void AboveWhenTheTaskbarIsBelow()
    {
        var p = RecordingBar.Place(new Rectangle(600, 300, 640, 1080), Work, Bar, 8);
        Assert.Equal(300 - 8 - 36, p.Y);
    }

    [Fact]
    public void InsideAndAboveTheTaskbarForAWholeScreen()
    {
        var p = RecordingBar.Place(Monitor, Work, Bar, 8);
        AssertVisible(p);
        Assert.Equal(1392 - 36 - 8, p.Y);
    }

    [Fact]
    public void InsideForAMaximizedWindow()
    {
        AssertVisible(RecordingBar.Place(Work, Work, Bar, 8));
    }

    [Fact]
    public void OnASecondMonitorLeftOfThePrimary()
    {
        var work = new Rectangle(-1920, 120, 1920, 1032);
        var p = RecordingBar.Place(new Rectangle(-1920, 120, 1920, 1032), work, Bar, 8);
        Assert.True(work.Contains(new Rectangle(p, Bar)));
    }
}
