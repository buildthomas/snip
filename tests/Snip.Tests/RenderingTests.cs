using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Snip.Tests;

public class RenderingTests
{
    private static Bitmap Solid(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    [Fact]
    public void Compose_CropsToSelection()
    {
        using var capture = Solid(200, 100, Color.White);
        using (var g = Graphics.FromImage(capture)) g.FillRectangle(Brushes.Blue, 50, 20, 10, 10);

        using var img = Renderer.Compose(capture, new Rectangle(50, 20, 30, 40), Array.Empty<Annotation>());

        Assert.Equal(30, img.Width);
        Assert.Equal(40, img.Height);
        Assert.Equal(Color.Blue.ToArgb(), img.GetPixel(5, 5).ToArgb());
        Assert.Equal(Color.White.ToArgb(), img.GetPixel(20, 30).ToArgb());
    }

    [Fact]
    public void Compose_RedactIsFullyOpaqueBlack()
    {
        using var capture = Solid(100, 100, Color.FromArgb(200, 10, 10));
        var redact = new RedactMark { Source = capture, Style = RedactStyle.Black, Rect = new RectangleF(10, 10, 30, 30) };

        using var img = Renderer.Compose(capture, new Rectangle(0, 0, 100, 100), new[] { redact });

        for (int x = 10; x < 40; x++)
            for (int y = 10; y < 40; y++)
                Assert.Equal(Color.Black.ToArgb(), img.GetPixel(x, y).ToArgb());
        Assert.Equal(Color.FromArgb(200, 10, 10).ToArgb(), img.GetPixel(50, 50).ToArgb());
    }

    [Fact]
    public void Compose_TranslatesAnnotationsIntoSelectionSpace()
    {
        using var capture = Solid(300, 300, Color.White);
        var box = new RedactMark { Source = capture, Style = RedactStyle.Black, Rect = new RectangleF(110, 120, 5, 5) };

        using var img = Renderer.Compose(capture, new Rectangle(100, 100, 50, 50), new[] { box });

        Assert.Equal(Color.Black.ToArgb(), img.GetPixel(12, 22).ToArgb());
        Assert.Equal(Color.White.ToArgb(), img.GetPixel(2, 2).ToArgb());
    }

    [Fact]
    public void Compose_OutputIsOpaqueAndHighlighterTints()
    {
        using var capture = Solid(40, 40, Color.Gray);
        var hl = new Stroke { Highlighter = true, Color = Color.Yellow, Size = 4 };
        hl.Points.Add(new PointF(5, 20));
        hl.Points.Add(new PointF(35, 20));

        using var img = Renderer.Compose(capture, new Rectangle(0, 0, 40, 40), new[] { hl });

        var px = img.GetPixel(20, 20);
        Assert.Equal(255, px.A);
        Assert.True(px.R > px.B, "highlighter should tint toward yellow");
        Assert.True(px.R < 255, "highlighter should be translucent");
    }

    [Fact]
    public void AllAnnotationTypesDrawWithoutThrowing()
    {
        using var capture = Solid(200, 200, Color.White);
        var stroke = new Stroke { Color = Color.Red, Size = 3 };
        stroke.Points.Add(new PointF(10, 10));
        var annotations = new Annotation[]
        {
            stroke,
            new LineShape { Start = new(10, 10), End = new(100, 100), Arrow = true, Color = Color.Red, Size = 4 },
            new LineShape { Start = new(10, 10), End = new(10, 10), Arrow = true, Color = Color.Red, Size = 4 },
            new BoxShape { Kind = BoxKind.Ellipse, Rect = new RectangleF(20, 20, 50, 30), Color = Color.Green, Size = 2 },
            new BoxShape { Kind = BoxKind.Rectangle, Rect = new RectangleF(20, 20, 50, 30), Color = Color.Green, Size = 2 },
            new TextNote { Origin = new(30, 30), Text = "Hello\nworld", Color = Color.Blue, Size = 4 },
            new TextNote { Origin = new(30, 30), Text = "", Color = Color.Blue, Size = 4 },
            new NumberMark { Center = new(150, 150), Number = 12, Color = Color.Yellow, Size = 4 },
        };
        using var img = Renderer.Compose(capture, new Rectangle(0, 0, 200, 200), annotations);
        foreach (var a in annotations) Assert.False(a.Bounds.IsEmpty);
    }

    [Fact]
    public void ArrowTipReachesEndPoint()
    {
        using var capture = Solid(200, 50, Color.White);
        var arrow = new LineShape { Start = new(10, 25), End = new(190, 25), Arrow = true, Color = Color.Black, Size = 4 };
        using var img = Renderer.Compose(capture, new Rectangle(0, 0, 200, 50), new[] { arrow });
        Assert.True(img.GetPixel(186, 25).R < 128);
        Assert.True(img.GetPixel(195, 25).R > 200);
    }

    [Fact]
    public void ClipboardRoundTripsImage()
    {
        Exception? error = null;
        int w = 0, h = 0;
        var t = new Thread(() =>
        {
            try
            {
                using var img = Solid(37, 21, Color.Orange);
                ClipboardWriter.SetImage(img);
                using var back = System.Windows.Forms.Clipboard.GetImage()!;
                w = back.Width;
                h = back.Height;
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.Equal(37, w);
        Assert.Equal(21, h);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    public void ImageSaver_WritesRequestedFormat(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-test-{Guid.NewGuid():N}{ext}");
        try
        {
            using (var img = Solid(16, 9, Color.Teal)) ImageSaver.Save(img, path);
            using var loaded = Image.FromFile(path);
            Assert.Equal(16, loaded.Width);
            var expected = ext switch { ".png" => ImageFormat.Png, ".jpg" => ImageFormat.Jpeg, _ => ImageFormat.Bmp };
            Assert.Equal(expected.Guid, loaded.RawFormat.Guid);
        }
        finally { File.Delete(path); }
    }
}
