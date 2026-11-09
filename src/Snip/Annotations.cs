using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Snip;

internal enum Tool { None, Pen, Line, Arrow, Rectangle, Ellipse, Highlighter, Text, Number, Redact }

// All coordinates are in capture-bitmap pixels.
internal abstract class Annotation
{
    public Color Color { get; init; }
    public float Size { get; set; }
    public abstract void Draw(Graphics g);
    public abstract RectangleF Bounds { get; }

    protected Pen MakePen(float width, Color color) => new(color, width)
    {
        StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
    };

    protected static RectangleF Inflate(RectangleF r, float by) => RectangleF.Inflate(r, by, by);
}

internal sealed class Stroke : Annotation
{
    public List<PointF> Points { get; } = new();
    public bool Highlighter { get; init; }

    public const int HighlighterAlpha = 110;
    public float Width => Highlighter ? Size * 4 : Size;

    public override void Draw(Graphics g)
    {
        var color = Highlighter ? Color.FromArgb(HighlighterAlpha, Color) : Color;
        if (Points.Count == 1)
        {
            using var b = new SolidBrush(color);
            var w = Width;
            g.FillEllipse(b, Points[0].X - w / 2, Points[0].Y - w / 2, w, w);
            return;
        }
        using var pen = MakePen(Width, color);
        using var path = new GraphicsPath();
        path.AddLines(Points.ToArray());
        g.DrawPath(pen, path);
    }

    public override RectangleF Bounds
    {
        get
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in Points)
            {
                l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
            }
            return Inflate(RectangleF.FromLTRB(l, t, r, b), Width / 2 + 2);
        }
    }
}

internal sealed class LineShape : Annotation
{
    public PointF Start { get; set; }
    public PointF End { get; set; }
    public bool Arrow { get; init; }

    public float HeadLength => Math.Max(12, Size * 4);

    public override void Draw(Graphics g)
    {
        var dx = End.X - Start.X;
        var dy = End.Y - Start.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (!Arrow || len < 1)
        {
            using var pen = MakePen(Size, Color);
            g.DrawLine(pen, Start, End);
            return;
        }

        var head = Math.Min(HeadLength, len);
        var ux = dx / len;
        var uy = dy / len;
        var baseX = End.X - ux * head;
        var baseY = End.Y - uy * head;
        var half = head * 0.55f;
        using (var pen = MakePen(Size, Color))
        {
            pen.EndCap = LineCap.Flat;
            g.DrawLine(pen, Start, new PointF(baseX + ux * 1, baseY + uy * 1));
        }
        using var brush = new SolidBrush(Color);
        g.FillPolygon(brush, new[]
        {
            End,
            new PointF(baseX - uy * half, baseY + ux * half),
            new PointF(baseX + uy * half, baseY - ux * half),
        });
    }

    public override RectangleF Bounds =>
        Inflate(RectangleF.FromLTRB(Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y), Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y)),
            (Arrow ? HeadLength : 0) + Size + 2);
}

internal enum BoxKind { Rectangle, Ellipse, Redact }

internal sealed class BoxShape : Annotation
{
    public RectangleF Rect { get; set; }
    public BoxKind Kind { get; init; }

    public override void Draw(Graphics g)
    {
        if (Kind == BoxKind.Redact)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(Brushes.Black, Rect);
            g.SmoothingMode = old;
            return;
        }
        using var pen = new Pen(Color, Size) { LineJoin = LineJoin.Miter };
        if (Kind == BoxKind.Rectangle) g.DrawRectangle(pen, Rect.X, Rect.Y, Rect.Width, Rect.Height);
        else g.DrawEllipse(pen, Rect);
    }

    public override RectangleF Bounds => Inflate(Rect, Size + 2);
}

internal sealed class TextNote : Annotation
{
    public PointF Origin { get; init; }
    public string Text { get; set; } = "";

    public float FontPx => 8 + Size * 3;
    public Font MakeFont() => new("Segoe UI Semibold", FontPx, FontStyle.Regular, GraphicsUnit.Pixel);

    public override void Draw(Graphics g)
    {
        if (Text.Length == 0) return;
        using var font = MakeFont();
        using var brush = new SolidBrush(Color);
        g.DrawString(Text, font, brush, Origin, StringFormat.GenericTypographic);
    }

    public SizeF Measure(Graphics g)
    {
        using var font = MakeFont();
        var s = g.MeasureString(Text.Length == 0 ? " " : Text + (Text.EndsWith('\n') ? " " : ""), font, PointF.Empty, StringFormat.GenericTypographic);
        return new SizeF(Math.Max(s.Width, 2), Math.Max(s.Height, font.GetHeight()));
    }

    public override RectangleF Bounds
    {
        get
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            return Inflate(new RectangleF(Origin, Measure(g)), 6);
        }
    }
}

internal sealed class NumberMark : Annotation
{
    public PointF Center { get; set; }
    public int Number { get; init; }

    public float Radius => 10 + Size * 2;

    public override void Draw(Graphics g)
    {
        var r = Radius;
        var rect = new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
        using (var b = new SolidBrush(Color)) g.FillEllipse(b, rect);
        var light = Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114 > 170;
        var fg = light ? Color.Black : Color.White;
        using (var p = new Pen(fg, Math.Max(1.5f, r / 10))) g.DrawEllipse(p, rect);
        using var font = new Font("Segoe UI", r * (Number >= 10 ? 0.95f : 1.15f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var fb = new SolidBrush(fg);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Number.ToString(), font, fb, new RectangleF(rect.X, rect.Y + r * 0.04f, rect.Width, rect.Height), sf);
    }

    public override RectangleF Bounds => new(Center.X - Radius - 3, Center.Y - Radius - 3, Radius * 2 + 6, Radius * 2 + 6);
}

internal static class Renderer
{
    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    // Flattens the selected region and its annotations into a new opaque bitmap.
    public static Bitmap Compose(Bitmap capture, Rectangle selection, IEnumerable<Annotation> annotations)
    {
        var result = new Bitmap(selection.Width, selection.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(capture, new Rectangle(0, 0, selection.Width, selection.Height), selection, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        Prepare(g);
        g.TranslateTransform(-selection.X, -selection.Y);
        foreach (var a in annotations) a.Draw(g);
        return result;
    }

    public static PointF Snap45(PointF from, PointF to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        var len = Math.Sqrt(dx * dx + dy * dy);
        return new PointF(from.X + (float)(Math.Cos(angle) * len), from.Y + (float)(Math.Sin(angle) * len));
    }

    public static RectangleF Box(PointF a, PointF b, bool square)
    {
        var w = b.X - a.X;
        var h = b.Y - a.Y;
        if (square)
        {
            var s = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w == 0 ? 1 : w) * s;
            h = Math.Sign(h == 0 ? 1 : h) * s;
        }
        return RectangleF.FromLTRB(Math.Min(a.X, a.X + w), Math.Min(a.Y, a.Y + h), Math.Max(a.X, a.X + w), Math.Max(a.Y, a.Y + h));
    }
}
