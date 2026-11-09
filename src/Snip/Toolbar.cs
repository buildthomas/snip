using System.Drawing.Drawing2D;

namespace Snip;

internal enum ButtonKind { Tool, Color, Undo, Redo, Copy, Save, Close }

internal sealed class ToolButton
{
    public required ButtonKind Kind { get; init; }
    public Tool Tool { get; init; }
    public required string Tip { get; init; }
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
            case ButtonKind.Undo: Glyph(g, "", "↶", r, fg); return;
            case ButtonKind.Redo: Glyph(g, "", "↷", r, fg); return;
            case ButtonKind.Copy: Glyph(g, "", "⧉", r, fg); return;
            case ButtonKind.Save: Glyph(g, "", "S", r, fg); return;
            case ButtonKind.Close: Glyph(g, "", "✕", r, fg); return;
        }

        switch (b.Tool)
        {
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
