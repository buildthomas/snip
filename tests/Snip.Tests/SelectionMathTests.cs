using System.Drawing;
using Xunit;

namespace Snip.Tests;

public class SelectionMathTests
{
    [Fact]
    public void FromPoints_NormalisesAnyDragDirection()
    {
        Assert.Equal(Rectangle.FromLTRB(10, 20, 110, 220), SelectionMath.FromPoints(new Point(110, 220), new Point(10, 20)));
    }

    [Fact]
    public void Resize_BottomRightGrows()
    {
        Assert.Equal(new Rectangle(0, 0, 150, 120), SelectionMath.Resize(new Rectangle(0, 0, 100, 100), 4, new Point(50, 20)));
    }

    [Fact]
    public void Resize_PastTheOppositeEdgeFlipsInsteadOfGoingNegative()
    {
        var r = SelectionMath.Resize(new Rectangle(100, 100, 100, 100), 7, new Point(150, 0));
        Assert.Equal(Rectangle.FromLTRB(200, 100, 250, 200), r);
    }

    [Fact]
    public void Resize_TopEdgeOnlyMovesTheTop()
    {
        Assert.Equal(Rectangle.FromLTRB(0, 30, 100, 100), SelectionMath.Resize(new Rectangle(0, 0, 100, 100), 1, new Point(40, 30)));
    }

    [Fact]
    public void Move_StaysOnScreen()
    {
        var screen = new Rectangle(-1920, 0, 4480, 1440);
        Assert.Equal(new Rectangle(-1920, 1340, 200, 100), SelectionMath.Move(new Rectangle(0, 0, 200, 100), new Point(-5000, 5000), screen));
    }
}
