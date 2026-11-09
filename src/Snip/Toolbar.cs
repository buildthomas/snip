using System.Drawing.Drawing2D;

namespace Snip;

internal enum ButtonKind { Tool, Color, Undo, Redo, Copy, Save, Close, RedactStyle, RedactShape, RedactSecure, EmojiPick }

internal sealed class ToolButton
{
    public required ButtonKind Kind { get; init; }
    public Tool Tool { get; init; }
    public int Option { get; init; }
    public required string Tip { get; set; }
    public Rectangle Rect { get; set; }
}

internal static class Palette
{
    public static readonly Color[] Colors =
    {
        Color.FromArgb(230, 40, 40), Color.FromArgb(255, 140, 0), Color.FromArgb(255, 214, 0),
        Color.FromArgb(40, 190, 70), Color.FromArgb(0, 190, 220), Color.FromArgb(30, 110, 255),
        Color.FromArgb(150, 70, 230), Color.FromArgb(240, 70, 170), Color.White,
        Color.FromArgb(128, 128, 128), Color.FromArgb(40, 40, 40), Color.Black,
    };
    public const int Columns = 6;
}

internal static class Icons
{
    private static readonly string? SymbolFont =
        FontFamily.Families.Any(f => f.Name == "Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : null;

    // The emoji shown on the Emoji tool button.
    public static string CurrentEmoji = EmojiFont.Set[0];

    public static void Draw(Graphics g, ToolButton b, Color current, bool enabled)
    {
        var r = b.Rect;
        var s = r.Width / 30f;
        var fg = enabled ? Color.FromArgb(235, 235, 235) : Color.FromArgb(110, 110, 110);
        using var pen = new Pen(fg, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(fg);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = 7.5f * s;

        switch (b.Kind)
        {
            case ButtonKind.Color:
                using (var cb = new SolidBrush(current)) g.FillEllipse(cb, cx - u, cy - u, u * 2, u * 2);
                using (var ring = new Pen(Color.FromArgb(235, 235, 235), 1.5f * s)) g.DrawEllipse(ring, cx - u, cy - u, u * 2, u * 2);
                return;
            case ButtonKind.RedactStyle:
                DrawRedactStyle(g, (RedactStyle)b.Option, cx, cy, u, fg, enabled);
                return;
            case ButtonKind.RedactShape:
                DrawRedactShape(g, (RedactShape)b.Option, cx, cy, u, pen, s);
                return;
            case ButtonKind.EmojiPick:
                DrawEmoji(g, EmojiFont.Set[b.Option], r);
                return;
            case ButtonKind.RedactSecure:
                DrawShield(g, b.Option == 1, cx, cy, u, pen, fg);
                return;
            case ButtonKind.Undo: Glyph(g, "", "↶", r, fg); return;
            case ButtonKind.Redo: Glyph(g, "", "↷", r, fg); return;
            case ButtonKind.Copy: Glyph(g, "", "⧉", r, fg); return;
            case ButtonKind.Save: Glyph(g, "", "S", r, fg); return;
            case ButtonKind.Close: Glyph(g, "", "✕", r, fg); return;
        }

        switch (b.Tool)
        {
            case Tool.Emoji:
                DrawEmoji(g, CurrentEmoji, r);
                break;
            case Tool.Pen:
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(cx - u, cy + u * 0.6f, cx - u * 0.3f, cy - u * 1.2f, cx + u * 0.2f, cy + u * 1.2f, cx + u, cy - u * 0.6f);
                    g.DrawPath(pen, path);
                }
                break;
            case Tool.Line:
                g.DrawLine(pen, cx - u, cy + u, cx + u, cy - u);
                break;
            case Tool.Arrow:
                g.DrawLine(pen, cx - u, cy + u, cx + u * 0.6f, cy - u * 0.6f);
                g.FillPolygon(brush, new[] { new PointF(cx + u, cy - u), new PointF(cx + u * 0.05f, cy - u * 0.75f), new PointF(cx + u * 0.75f, cy - u * 0.05f) });
                break;
            case Tool.Rectangle:
                g.DrawRectangle(pen, cx - u, cy - u * 0.75f, u * 2, u * 1.5f);
                break;
            case Tool.Ellipse:
                g.DrawEllipse(pen, cx - u, cy - u * 0.75f, u * 2, u * 1.5f);
                break;
            case Tool.Highlighter:
                using (var hp = new Pen(Color.FromArgb(enabled ? 170 : 80, 255, 214, 0), 6 * s) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                    g.DrawLine(hp, cx - u, cy + u * 0.3f, cx + u, cy + u * 0.3f);
                g.DrawLine(pen, cx - u * 0.4f, cy - u * 0.9f, cx + u * 0.5f, cy - u * 0.9f);
                g.DrawLine(pen, cx + u * 0.05f, cy - u * 0.9f, cx + u * 0.05f, cy + u * 0.8f);
                break;
            case Tool.Text:
                g.DrawLine(pen, cx - u * 0.8f, cy - u, cx + u * 0.8f, cy - u);
                g.DrawLine(pen, cx, cy - u, cx, cy + u);
                break;
            case Tool.Number:
                g.DrawEllipse(pen, cx - u, cy - u, u * 2, u * 2);
                using (var f = new Font("Segoe UI", u * 1.3f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("1", f, brush, new RectangleF(cx - u, cy - u + s, u * 2, u * 2), sf);
                break;
            case Tool.Redact:
                g.FillRectangle(brush, cx - u, cy - u * 0.55f, u * 2, u * 1.1f);
                break;
        }
    }

    private static void DrawRedactStyle(Graphics g, RedactStyle style, float cx, float cy, float u, Color fg, bool enabled)
    {
        switch (style)
        {
            case RedactStyle.Black:
                using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, cx - u, cy - u * 0.7f, u * 2, u * 1.4f);
                using (var p = new Pen(fg, 1)) g.DrawRectangle(p, cx - u, cy - u * 0.7f, u * 2, u * 1.4f);
                break;
            case RedactStyle.Pixelate:
                var cell = u * 2 / 3;
                int[] shades = { 235, 150, 200, 120, 220, 170, 185, 240, 135 };
                for (int k = 0; k < 9; k++)
                {
                    var v = enabled ? shades[k] : shades[k] / 2;
                    using var cb = new SolidBrush(Color.FromArgb(v, v, v));
                    g.FillRectangle(cb, cx - u + (k % 3) * cell, cy - u + (k / 3) * cell, cell, cell);
                }
                break;
            case RedactStyle.Blur:
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - u * 1.1f, cy - u * 1.1f, u * 2.2f, u * 2.2f);
                    using var pg = new PathGradientBrush(path) { CenterColor = fg, SurroundColors = new[] { Color.FromArgb(0, fg) } };
                    g.FillPath(pg, path);
                }
                break;
        }
    }

    private static void DrawRedactShape(Graphics g, RedactShape shape, float cx, float cy, float u, Pen pen, float s)
    {
        switch (shape)
        {
            case RedactShape.Box:
                g.DrawRectangle(pen, cx - u, cy - u * 0.7f, u * 2, u * 1.4f);
                break;
            case RedactShape.Brush:
                using (var thick = new Pen(pen.Color, 4.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(cx - u, cy + u * 0.5f, cx - u * 0.3f, cy - u * 1.2f, cx + u * 0.3f, cy + u * 1.2f, cx + u, cy - u * 0.5f);
                    g.DrawPath(thick, path);
                }
                break;
        }
    }

    // Option 1 = secure (shield with a check), 0 = insecure (shield with an exclamation mark).
    private static void DrawShield(Graphics g, bool secure, float cx, float cy, float u, Pen pen, Color fg)
    {
        using var path = new GraphicsPath();
        path.AddLines(new[] { new PointF(cx, cy - u), new PointF(cx + u * 0.85f, cy - u * 0.6f), new PointF(cx + u * 0.75f, cy + u * 0.2f) });
        path.AddBezier(cx + u * 0.75f, cy + u * 0.2f, cx + u * 0.6f, cy + u * 0.7f, cx + u * 0.2f, cy + u * 0.95f, cx, cy + u * 1.05f);
        path.AddBezier(cx, cy + u * 1.05f, cx - u * 0.2f, cy + u * 0.95f, cx - u * 0.6f, cy + u * 0.7f, cx - u * 0.75f, cy + u * 0.2f);
        path.AddLines(new[] { new PointF(cx - u * 0.75f, cy + u * 0.2f), new PointF(cx - u * 0.85f, cy - u * 0.6f) });
        path.CloseFigure();
        g.DrawPath(pen, path);
        if (secure)
            g.DrawLines(pen, new[] { new PointF(cx - u * 0.35f, cy + u * 0.05f), new PointF(cx - u * 0.05f, cy + u * 0.35f), new PointF(cx + u * 0.4f, cy - u * 0.3f) });
        else
        {
            g.DrawLine(pen, cx, cy - u * 0.45f, cx, cy + u * 0.2f);
            using var dot = new SolidBrush(fg);
            g.FillEllipse(dot, cx - pen.Width * 0.6f, cy + u * 0.45f, pen.Width * 1.2f, pen.Width * 1.2f);
        }
    }

    private static void DrawEmoji(Graphics g, string emoji, Rectangle r)
    {
        var state = g.Save();
        g.TranslateTransform(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        EmojiFont.Draw(g, emoji, r.Width * 0.62f);
        g.Restore(state);
    }

    private static void Glyph(Graphics g, string symbol, string fallback, Rectangle r, Color fg)
    {
        using var font = SymbolFont != null
            ? new Font(SymbolFont, r.Height * 0.45f, GraphicsUnit.Pixel)
            : new Font("Segoe UI", r.Height * 0.5f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(fg);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(SymbolFont != null ? symbol : fallback, font, brush, r, sf);
    }
}
