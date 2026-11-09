using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Snip.Tests;

public class RedactTests
{
    private const int W = 120, H = 90;
    private static readonly Rectangle Area = new(30, 20, 48, 40);

    private static Bitmap Image(Action<Graphics> paint)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(40, 90, 160));
        paint(g);
        return bmp;
    }

    private static Bitmap Render(Bitmap capture, RedactMark mark)
    {
        using (mark) return Renderer.Compose(capture, new Rectangle(0, 0, W, H), new[] { mark });
    }

    private static RedactMark Box(Bitmap src, RedactStyle style, ulong seed = 1) =>
        new() { Source = src, Style = style, Shape = RedactShape.Box, Rect = Area, Seed = seed };

    [Theory]
    [InlineData((int)RedactStyle.Pixelate)]
    [InlineData((int)RedactStyle.Blur)]
    public void SecureStyles_DependOnlyOnColourDistribution_NotOnWhereThingsAre(int styleValue)
    {
        var style = (RedactStyle)styleValue;
        // Same pixels inside the area (half white, half black) arranged differently; the surroundings are identical.
        using var leftRight = Image(g =>
        {
            g.FillRectangle(Brushes.White, Area.X, Area.Y, Area.Width / 2, Area.Height);
            g.FillRectangle(Brushes.Black, Area.X + Area.Width / 2, Area.Y, Area.Width / 2, Area.Height);
        });
        using var topBottom = Image(g =>
        {
            g.FillRectangle(Brushes.White, Area.X, Area.Y, Area.Width, Area.Height / 2);
            g.FillRectangle(Brushes.Black, Area.X, Area.Y + Area.Height / 2, Area.Width, Area.Height / 2);
        });

        using var a = Render(leftRight, Box(leftRight, style, seed: 99));
        using var b = Render(topBottom, Box(topBottom, style, seed: 99));

        for (var x = Area.Left; x < Area.Right; x++)
            for (var y = Area.Top; y < Area.Bottom; y++)
                Assert.Equal(a.GetPixel(x, y).ToArgb(), b.GetPixel(x, y).ToArgb());
    }

    [Theory]
    [InlineData((int)RedactStyle.Black, true)]
    [InlineData((int)RedactStyle.Pixelate, true)]
    [InlineData((int)RedactStyle.Blur, true)]
    [InlineData((int)RedactStyle.Pixelate, false)]
    [InlineData((int)RedactStyle.Blur, false)]
    public void EveryCoveredPixelIsOpaqueAndChanged_OutsideUntouched(int styleValue, bool secure)
    {
        var style = (RedactStyle)styleValue;
        var marker = Color.FromArgb(255, 1, 254, 3);
        using var capture = Image(g => g.FillRectangle(new SolidBrush(marker), Area));
        var mark = new RedactMark { Source = capture, Style = style, Shape = RedactShape.Box, Rect = Area, Seed = 1, Secure = secure };
        using var img = Render(capture, mark);

        for (var x = Area.Left + 2; x < Area.Right - 2; x++)
            for (var y = Area.Top + 2; y < Area.Bottom - 2; y++)
            {
                var px = img.GetPixel(x, y);
                Assert.Equal(255, px.A);
                if (secure) Assert.NotEqual(marker.ToArgb(), px.ToArgb());
            }
        Assert.Equal(capture.GetPixel(5, 5).ToArgb(), img.GetPixel(5, 5).ToArgb());
        Assert.Equal(capture.GetPixel(Area.Right + 20, Area.Bottom + 10).ToArgb(), img.GetPixel(Area.Right + 20, Area.Bottom + 10).ToArgb());
    }

    [Fact]
    public void SameSeedIsStable_DifferentSeedsDiffer()
    {
        using var capture = Image(g => g.FillEllipse(Brushes.Orange, Area));
        using var a = Render(capture, Box(capture, RedactStyle.Pixelate, 5));
        using var b = Render(capture, Box(capture, RedactStyle.Pixelate, 5));
        using var c = Render(capture, Box(capture, RedactStyle.Pixelate, 6));
        var same = true;
        var differs = false;
        for (var x = Area.Left; x < Area.Right; x++)
            for (var y = Area.Top; y < Area.Bottom; y++)
            {
                same &= a.GetPixel(x, y) == b.GetPixel(x, y);
                differs |= a.GetPixel(x, y) != c.GetPixel(x, y);
            }
        Assert.True(same);
        Assert.True(differs);
    }

    [Fact]
    public void FlatArea_StillLooksRedacted()
    {
        using var capture = Image(g => g.FillRectangle(Brushes.White, 0, 0, W, H));
        using var img = Render(capture, Box(capture, RedactStyle.Pixelate));
        var lums = new List<int>();
        for (var x = Area.Left; x < Area.Right; x++)
            for (var y = Area.Top; y < Area.Bottom; y++)
                lums.Add(RedactRender.Lum(img.GetPixel(x, y).ToArgb()));
        Assert.True(lums.Max() - lums.Min() >= 40, "a redacted white area must not look untouched");
    }

    [Fact]
    public void Brush_CoversItsWidth_IncludingWhereTheStrokeCrossesItself()
    {
        var marker = Color.FromArgb(255, 1, 254, 3);
        using var capture = Image(g => g.FillRectangle(new SolidBrush(marker), 0, 0, W, H));
        var mark = new RedactMark { Source = capture, Style = RedactStyle.Black, Shape = RedactShape.Brush, BrushWidth = 10 };
        mark.Points.AddRange(new PointF[] { new(10, 45), new(110, 45), new(60, 10), new(60, 80) });
        using var img = Render(capture, mark);

        Assert.Equal(Color.Black.ToArgb(), img.GetPixel(60, 45).ToArgb()); // crossing point
        Assert.Equal(Color.Black.ToArgb(), img.GetPixel(30, 48).ToArgb());
        Assert.Equal(marker.ToArgb(), img.GetPixel(30, 60).ToArgb());
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    public void CellSize_SetsThePixelationGrid(int cell, bool secure)
    {
        using var capture = Image(g => g.FillEllipse(Brushes.Orange, Area));
        var mark = new RedactMark
        {
            Source = capture, Style = RedactStyle.Pixelate, Shape = RedactShape.Box, Secure = secure, Seed = 4, CellSize = cell,
            Rect = new RectangleF(16, 16, 64, 64),
        };
        using var img = Render(capture, mark);
        // Every cell on the absolute grid is one flat colour (edge blending is per cell too).
        for (var cy = 16; cy < 80; cy += cell)
            for (var cx = 16; cx < 80; cx += cell)
            {
                var c = img.GetPixel(cx, cy);
                Assert.Equal(c, img.GetPixel(cx + cell - 1, cy + cell - 1));
            }
    }

    [Fact]
    public void SingleBrushDab_IsKept()
    {
        using var capture = Image(_ => { });
        var mark = new RedactMark { Source = capture, Style = RedactStyle.Black, Shape = RedactShape.Brush, BrushWidth = 12 };
        mark.Points.Add(new PointF(50, 50));
        Assert.False(mark.IsEmpty);
        using var img = Render(capture, mark);
        Assert.Equal(Color.Black.ToArgb(), img.GetPixel(50, 50).ToArgb());
    }

    [Theory]
    [InlineData((int)RedactStyle.Pixelate)]
    [InlineData((int)RedactStyle.Blur)]
    public void InsecureMode_IsARealFilterOfTheContent(int styleValue)
    {
        // The counterpart of the secure test: same colours, different arrangement, different output.
        var style = (RedactStyle)styleValue;
        using var leftRight = Image(g =>
        {
            g.FillRectangle(Brushes.White, Area.X, Area.Y, Area.Width / 2, Area.Height);
            g.FillRectangle(Brushes.Black, Area.X + Area.Width / 2, Area.Y, Area.Width / 2, Area.Height);
        });
        using var topBottom = Image(g =>
        {
            g.FillRectangle(Brushes.White, Area.X, Area.Y, Area.Width, Area.Height / 2);
            g.FillRectangle(Brushes.Black, Area.X, Area.Y + Area.Height / 2, Area.Width, Area.Height / 2);
        });
        RedactMark Mark(Bitmap src) => new() { Source = src, Style = style, Shape = RedactShape.Box, Rect = Area, Secure = false };
        using var a = Render(leftRight, Mark(leftRight));
        using var b = Render(topBottom, Mark(topBottom));

        Assert.True(RedactRender.Lum(a.GetPixel(Area.Left + 2, Area.Bottom - 3).ToArgb()) > RedactRender.Lum(a.GetPixel(Area.Right - 3, Area.Bottom - 3).ToArgb()));
        Assert.True(RedactRender.Lum(b.GetPixel(Area.Left + 2, Area.Top + 2).ToArgb()) > RedactRender.Lum(b.GetPixel(Area.Left + 2, Area.Bottom - 3).ToArgb()));
    }
}
