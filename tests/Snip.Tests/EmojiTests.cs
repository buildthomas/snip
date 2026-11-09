using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Snip.Tests;

public class EmojiTests
{
    private static Bitmap Draw(Action<Graphics> paint, int size = 200)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        Renderer.Prepare(g);
        paint(g);
        return bmp;
    }

    private static Rectangle InkBounds(Bitmap bmp)
    {
        int l = bmp.Width, t = bmp.Height, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).ToArgb() != Color.White.ToArgb())
                {
                    l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
                }
        return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
    }

    [Fact]
    public void EveryEmojiInThePicker_RendersInColour()
    {
        foreach (var e in EmojiFont.Set)
        {
            var glyph = EmojiFont.Get(e);
            Assert.True(glyph != null, $"{e} has no glyph");
            Assert.True(glyph!.Layers.Select(l => l.Color.ToArgb()).Distinct().Count() >= 2, $"{e} is not multi-coloured");
        }
    }

    [Fact]
    public void BoundsCoverEveryRotation()
    {
        foreach (var angle in new[] { 0f, 30f, 45f, 90f, 135f, 200f })
        {
            var mark = new EmojiMark { Emoji = "🚀", Center = new PointF(100, 100), Size = 8, Angle = angle };
            using var bmp = Draw(mark.Draw);
            var ink = InkBounds(bmp);
            Assert.False(ink.IsEmpty);
            Assert.True(Rectangle.Round(mark.Bounds).Contains(ink) || RectangleF.Inflate(mark.Bounds, 1, 1).Contains(ink),
                $"angle {angle}: ink {ink} outside bounds {mark.Bounds}");
        }
    }

    [Fact]
    public void SizeSettingScalesTheStamp()
    {
        using var small = Draw(new EmojiMark { Emoji = "😀", Center = new PointF(100, 100), Size = 2 }.Draw);
        using var big = Draw(new EmojiMark { Emoji = "😀", Center = new PointF(100, 100), Size = 10 }.Draw);
        Assert.True(InkBounds(big).Width > InkBounds(small).Width * 2);
    }

    [Fact]
    public void RotationTurnsTheStamp()
    {
        using var up = Draw(new EmojiMark { Emoji = "👍", Center = new PointF(100, 100), Size = 10 }.Draw);
        using var turned = Draw(new EmojiMark { Emoji = "👍", Center = new PointF(100, 100), Size = 10, Angle = 90 }.Draw);
        var a = InkBounds(up);
        var b = InkBounds(turned);
        Assert.True(Math.Abs(a.Width - b.Height) <= 3 && Math.Abs(a.Height - b.Width) <= 3, $"{a} vs {b}");
    }
}
