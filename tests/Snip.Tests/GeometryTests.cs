using System.Drawing;
using Xunit;

namespace Snip.Tests;

public class GeometryTests
{
    [Theory]
    [InlineData(100, 3, 1, 0)]
    [InlineData(100, 90, 0.7071, 0.7071)]
    [InlineData(-5, 100, 0, 1)]
    public void Snap45_SnapsToNearest45Degrees(float x, float y, float ux, float uy)
    {
        var len = MathF.Sqrt(x * x + y * y);
        var p = Renderer.Snap45(PointF.Empty, new PointF(x, y));
        Assert.Equal(ux * len, p.X, 1);
        Assert.Equal(uy * len, p.Y, 1);
    }

    [Fact]
    public void Box_NormalizesAndSquares()
    {
        var r = Renderer.Box(new PointF(50, 50), new PointF(10, 30), square: false);
        Assert.Equal(RectangleF.FromLTRB(10, 30, 50, 50), r);

        var sq = Renderer.Box(new PointF(50, 50), new PointF(10, 30), square: true);
        Assert.Equal(40, sq.Width);
        Assert.Equal(40, sq.Height);
        Assert.Equal(50, sq.Right);
        Assert.Equal(50, sq.Bottom);
    }

    [Theory]
    [InlineData(0, -10, -10, 110, 110)]
    [InlineData(4, 10, 10, 110, 110)]
    [InlineData(3, 5, 99, 105, 100)]
    [InlineData(1, 0, -20, 100, 120)]
    public void ResizeRect_MovesOnlyGrabbedEdges(int handle, int dx, int dy, int w, int h)
    {
        var r = OverlayForm.ResizeRect(new Rectangle(0, 0, 100, 100), handle, dx, dy);
        Assert.Equal(w, r.Width);
        Assert.Equal(h, r.Height);
    }

    [Fact]
    public void ResizeRect_FlipsPastOppositeEdge()
    {
        var r = OverlayForm.ResizeRect(new Rectangle(0, 0, 100, 100), 3, -150, 0);
        Assert.Equal(new Rectangle(-50, 0, 50, 100), r);
    }

    [Theory]
    [InlineData("hello world", "hello ")]
    [InlineData("hello world  ", "hello ")]
    [InlineData("one", "")]
    [InlineData("", "")]
    public void TrimWord_RemovesLastWord(string input, string expected) =>
        Assert.Equal(expected, OverlayForm.TrimWord(input));

    [Fact]
    public void WindowHit_ReturnsTopmostContainingRect()
    {
        var windows = new List<Rectangle> { new(10, 10, 50, 50), new(0, 0, 200, 200) };
        Assert.Equal(new Rectangle(10, 10, 50, 50), WindowFinder.Hit(windows, new Point(20, 20)));
        Assert.Equal(new Rectangle(0, 0, 200, 200), WindowFinder.Hit(windows, new Point(100, 100)));
        Assert.Null(WindowFinder.Hit(windows, new Point(500, 500)));
    }

    [Fact]
    public void InputStructMatchesNativeSize() =>
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
}
