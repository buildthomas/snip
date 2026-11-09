using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Snip;

// Usage: dotnet run --project tools/DemoShot -- <output.png>
var output = args.Length > 0 ? args[0] : "screenshot.png";
const int W = 1600, H = 900;

var backdrop = Backdrop.Draw(W, H);
var red = Color.FromArgb(230, 40, 40);
var settings = new Settings { ColorArgb = red.ToArgb(), Size = 4 };
using var overlay = new OverlayForm(backdrop, new Rectangle(0, 0, W, H), new List<Rectangle>(), settings);

var highlight = new Stroke { Highlighter = true, Color = Color.FromArgb(255, 214, 0), Size = 4 };
highlight.Points.AddRange(new PointF[] { new(386, 700), new(912, 700) });
var underline = new Stroke { Color = red, Size = 3 };
for (var x = 410f; x <= 508; x += 4) underline.Points.Add(new PointF(x, 366 + MathF.Sin(x / 5f) * 2.5f));

var annotations = new Annotation[]
{
    highlight,
    new BoxShape { Kind = BoxKind.Rectangle, Rect = new RectangleF(904, 192, 256, 116), Color = red, Size = 4 },
    new NumberMark { Center = new PointF(610, 224), Number = 1, Color = red, Size = 4 },
    new NumberMark { Center = new PointF(870, 224), Number = 2, Color = red, Size = 4 },
    new NumberMark { Center = new PointF(1130, 224), Number = 3, Color = red, Size = 4 },
    new LineShape { Start = new PointF(946, 428), End = new PointF(862, 386), Arrow = true, Color = red, Size = 4 },
    new TextNote { Origin = new PointF(954, 420), Text = "Spike after launch", Color = red, Size = 4 },
    new BoxShape { Kind = BoxKind.Ellipse, Rect = new RectangleF(598, 654, 62, 34), Color = red, Size = 3 },
    new BoxShape { Kind = BoxKind.Redact, Rect = new RectangleF(1022, 670, 160, 26) },
    underline,
};
overlay.SetDemoState(new Rectangle(378, 144, 862, 638), annotations, Tool.Arrow, hoverActiveTool: true);

using var result = new Bitmap(W, H, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(result)) overlay.Render(g, new Rectangle(0, 0, W, H));
result.Save(output, ImageFormat.Png);
Console.WriteLine($"wrote {Path.GetFullPath(output)}");

static class Backdrop
{
    static readonly Font Title = new("Segoe UI Semibold", 22, GraphicsUnit.Pixel);
    static readonly Font Body = new("Segoe UI", 15, GraphicsUnit.Pixel);
    static readonly Font Small = new("Segoe UI", 13, GraphicsUnit.Pixel);
    static readonly Font Big = new("Segoe UI Semibold", 30, GraphicsUnit.Pixel);
    static readonly Color Ink = Color.FromArgb(33, 37, 41);
    static readonly Color Muted = Color.FromArgb(108, 117, 125);
    static readonly Color Line = Color.FromArgb(222, 226, 230);
    static readonly Color Brand = Color.FromArgb(52, 98, 220);

    public static Bitmap Draw(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(38, 70, 110), Color.FromArgb(90, 60, 120), 35f))
            g.FillRectangle(bg, 0, 0, w, h);

        var win = new Rectangle(140, 90, 1320, 760);
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(shadow, win.X + 6, win.Y + 8, win.Width, win.Height);
        g.FillRectangle(Brushes.White, win);
        using (var bar = new SolidBrush(Color.FromArgb(243, 244, 246))) g.FillRectangle(bar, win.X, win.Y, win.Width, 36);
        Text(g, "Acme Analytics — Demo workspace", Body, Muted, win.X + 16, win.Y + 9);
        foreach (var (i, glyph) in new[] { (0, "—"), (1, "☐"), (2, "✕") })
            Text(g, glyph, Body, Muted, win.Right - 120 + i * 40, win.Y + 8);

        var side = new Rectangle(win.X, win.Y + 36, 220, win.Height - 36);
        using (var sb = new SolidBrush(Color.FromArgb(248, 249, 251))) g.FillRectangle(sb, side);
        Text(g, "◆ Acme", Title, Brand, side.X + 24, side.Y + 22);
        var items = new[] { "Overview", "Reports", "Customers", "Billing", "Settings" };
        for (int i = 0; i < items.Length; i++)
        {
            if (i == 0)
                using (var sel = new SolidBrush(Color.FromArgb(229, 236, 252))) g.FillRectangle(sel, side.X + 12, side.Y + 74, side.Width - 24, 34);
            Text(g, items[i], Body, i == 0 ? Brand : Ink, side.X + 30, side.Y + 81 + i * 42);
        }

        int cx = side.Right + 32, top = win.Y + 60;
        Text(g, "Overview", Title, Ink, cx, top);
        Text(g, "Last 30 days", Small, Muted, cx + 108, top + 7);

        var kpis = new[] { ("Visitors", "48,210", "+12%"), ("Sign-ups", "3,904", "+8%"), ("Revenue", "$91.4k", "+21%") };
        for (int i = 0; i < kpis.Length; i++)
        {
            var r = new Rectangle(cx + i * 260, top + 50, 240, 100);
            using (var p = new Pen(Line)) g.DrawRectangle(p, r);
            Text(g, kpis[i].Item1, Small, Muted, r.X + 18, r.Y + 16);
            Text(g, kpis[i].Item2, Big, Ink, r.X + 16, r.Y + 38);
            Text(g, kpis[i].Item3, Body, Color.FromArgb(25, 135, 84), r.X + 170, r.Y + 50);
        }

        var chart = new Rectangle(cx, top + 175, 760, 210);
        using (var p = new Pen(Line)) g.DrawRectangle(p, chart);
        Text(g, "Daily sign-ups", Body, Ink, chart.X + 16, chart.Y + 12);
        float[] values = { 60, 64, 58, 70, 66, 72, 69, 75, 71, 78, 74, 80, 77, 84, 140, 128, 118, 112, 108, 104, 110, 106, 112, 115 };
        var pts = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
            pts[i] = new PointF(chart.X + 30 + i * (chart.Width - 60) / (values.Length - 1f), chart.Bottom - 20 - values[i]);
        using (var grid = new Pen(Color.FromArgb(240, 242, 245)))
            for (int y = chart.Bottom - 20; y > chart.Y + 40; y -= 40) g.DrawLine(grid, chart.X + 20, y, chart.Right - 20, y);
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(pts);
            fill.AddLine(pts[^1], new PointF(pts[^1].X, chart.Bottom - 20));
            fill.AddLine(new PointF(pts[^1].X, chart.Bottom - 20), new PointF(pts[0].X, chart.Bottom - 20));
            using var fb = new SolidBrush(Color.FromArgb(40, Brand));
            g.FillPath(fb, fill);
        }
        using (var lp = new Pen(Brand, 3) { LineJoin = LineJoin.Round }) g.DrawLines(lp, pts);

        var table = new Rectangle(cx, top + 405, 520, 210);
        Text(g, "Plans", Body, Ink, table.X, table.Y);
        var rows = new[] { ("Plan", "Seats", "Status"), ("Starter", "1,204", "Active"), ("Pro", "860", "Active"), ("Team", "312", "Trial"), ("Enterprise", "41", "Active") };
        for (int i = 0; i < rows.Length; i++)
        {
            var y = table.Y + 32 + i * 34;
            var f = i == 0 ? Small : Body;
            var c = i == 0 ? Muted : Ink;
            Text(g, rows[i].Item1, f, c, table.X + 4, y);
            Text(g, rows[i].Item2, f, c, table.X + 220, y);
            Text(g, rows[i].Item3, f, c, table.X + 380, y);
            using var p = new Pen(Line);
            g.DrawLine(p, table.X, y + 26, table.Right, y + 26);
        }

        var acct = new Rectangle(cx + 560, top + 405, 300, 210);
        using (var p = new Pen(Line)) g.DrawRectangle(p, acct);
        Text(g, "Workspace", Body, Ink, acct.X + 16, acct.Y + 14);
        Text(g, "Region   EU-West", Body, Ink, acct.X + 16, acct.Y + 50);
        Text(g, "Members  14", Body, Ink, acct.X + 16, acct.Y + 82);
        Text(g, "Token    7c1e-44ab-9d02-58f1", Body, Ink, acct.X + 16, acct.Y + 114);
        Text(g, "Renews   1 Jan 2027", Body, Ink, acct.X + 16, acct.Y + 146);
        return bmp;
    }

    static void Text(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, x, y);
    }
}
