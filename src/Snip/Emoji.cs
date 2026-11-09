using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Snip;

// Colour emoji from the system's Segoe UI Emoji, without extra libraries: GDI+ only draws emoji in monochrome, so
// the font's COLR/CPAL colour layers are read with GetFontData and each layer's outline (GetGlyphOutline) is turned
// into a GraphicsPath. Glyphs are vector paths, so they stay sharp at any size and rotation.
internal static class EmojiFont
{
    public static readonly string[] Set =
    {
        "😀", "😂", "😍", "😎", "🤔", "😮", "😢", "😡",
        "👍", "👎", "👏", "🙏", "❤", "🔥", "⭐", "💯",
        "✅", "❌", "⚠", "❓", "💡", "👀", "🎉", "🚀",
    };

    public sealed record Layer(GraphicsPath Path, Color Color);
    public sealed record Glyph(IReadOnlyList<Layer> Layers, RectangleF Bounds);

    private const string FontName = "Segoe UI Emoji";
    private const int EmPx = 256;
    private static readonly Dictionary<string, Glyph?> Cache = new();
    private static Tables? _tables;
    private static bool _loaded;

    private sealed class Tables
    {
        public required byte[] Cmap, Colr, Cpal;
    }

    public static Glyph? Get(string emoji)
    {
        if (Cache.TryGetValue(emoji, out var g)) return g;
        g = Load(emoji);
        Cache[emoji] = g;
        return g;
    }

    private static Glyph? Load(string emoji)
    {
        var cp = char.ConvertToUtf32(emoji, 0);
        using var font = new Font(FontName, EmPx, GraphicsUnit.Pixel);
        if (font.Name != FontName) return null;
        var hfont = font.ToHfont();
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var old = SelectObject(dc, hfont);
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                var cmap = Table(dc, "cmap");
                var colr = Table(dc, "COLR");
                var cpal = Table(dc, "CPAL");
                if (cmap != null) _tables = new Tables { Cmap = cmap, Colr = colr ?? Array.Empty<byte>(), Cpal = cpal ?? Array.Empty<byte>() };
            }
            if (_tables == null) return null;
            var gid = GlyphIndex(_tables.Cmap, cp);
            if (gid == 0) return null;

            var layers = new List<Layer>();
            foreach (var (layerGid, paletteIndex) in ColorLayers(_tables, gid))
            {
                var path = Outline(dc, layerGid);
                if (path != null) layers.Add(new Layer(path, PaletteColor(_tables.Cpal, paletteIndex)));
            }
            if (layers.Count == 0 && Outline(dc, gid) is GraphicsPath mono)
                layers.Add(new Layer(mono, Color.FromArgb(60, 60, 60)));
            if (layers.Count == 0) return null;

            var bounds = layers[0].Path.GetBounds();
            foreach (var l in layers.Skip(1)) bounds = RectangleF.Union(bounds, l.Path.GetBounds());
            return new Glyph(layers, bounds);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteDC(dc);
            DeleteObject(hfont);
        }
    }

    // Draws the emoji centred at the origin, scaled so its larger side is `size` pixels.
    public static void Draw(Graphics g, string emoji, float size, float opacity = 1)
    {
        var glyph = Get(emoji);
        if (glyph == null)
        {
            using var f = new Font("Segoe UI Symbol", size * 0.8f, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var b = new SolidBrush(Color.FromArgb((int)(255 * opacity), 60, 60, 60));
            g.DrawString(emoji, f, b, new RectangleF(-size, -size, size * 2, size * 2), sf);
            return;
        }
        var state = g.Save();
        var scale = size / Math.Max(glyph.Bounds.Width, glyph.Bounds.Height);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-(glyph.Bounds.Left + glyph.Bounds.Width / 2), -(glyph.Bounds.Top + glyph.Bounds.Height / 2));
        foreach (var layer in glyph.Layers)
        {
            using var brush = new SolidBrush(Color.FromArgb((int)(layer.Color.A * opacity), layer.Color));
            g.FillPath(brush, layer.Path);
        }
        g.Restore(state);
    }

    // ----- font tables (all big-endian) -----

    private static ushort U16(byte[] d, int o) => (ushort)(d[o] << 8 | d[o + 1]);
    private static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static byte[]? Table(IntPtr dc, string tag)
    {
        var t = (uint)(tag[3] << 24 | tag[2] << 16 | tag[1] << 8 | tag[0]);
        var size = GetFontData(dc, t, 0, null, 0);
        if (size == uint.MaxValue || size == 0) return null;
        var buf = new byte[size];
        return GetFontData(dc, t, 0, buf, size) == size ? buf : null;
    }

    // cmap format 12 (full Unicode) with a format 4 fallback for the Basic Multilingual Plane.
    internal static int GlyphIndex(byte[] cmap, int cp)
    {
        int n = U16(cmap, 2), fmt4 = -1;
        for (int i = 0; i < n; i++)
        {
            int rec = 4 + i * 8, platform = U16(cmap, rec), encoding = U16(cmap, rec + 2);
            var off = (int)U32(cmap, rec + 4);
            var format = U16(cmap, off);
            if (format == 12 && platform == 3 && encoding == 10)
            {
                var groups = (int)U32(cmap, off + 12);
                for (int k = 0; k < groups; k++)
                {
                    int gOff = off + 16 + k * 12;
                    uint start = U32(cmap, gOff), end = U32(cmap, gOff + 4);
                    if (cp >= start && cp <= end) return (int)(U32(cmap, gOff + 8) + (cp - start));
                }
                return 0;
            }
            if (format == 4 && platform == 3 && encoding == 1) fmt4 = off;
        }
        if (fmt4 < 0 || cp > 0xFFFF) return 0;
        int segX2 = U16(cmap, fmt4 + 6), ends = fmt4 + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int s = 0; s < segX2 / 2; s++)
        {
            int end = U16(cmap, ends + s * 2), start = U16(cmap, starts + s * 2);
            if (cp > end || cp < start) continue;
            int delta = (short)U16(cmap, deltas + s * 2), rangeOff = U16(cmap, ranges + s * 2);
            if (rangeOff == 0) return (cp + delta) & 0xFFFF;
            var gi = U16(cmap, ranges + s * 2 + rangeOff + (cp - start) * 2);
            return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
        }
        return 0;
    }

    // COLR version 0 base glyph -> (layer glyph, palette index) list, bottom layer first.
    private static IEnumerable<(int Gid, int Palette)> ColorLayers(Tables t, int gid)
    {
        var c = t.Colr;
        if (c.Length < 14) yield break;
        int numBase = U16(c, 2), baseOff = (int)U32(c, 4), layerOff = (int)U32(c, 8);
        int lo = 0, hi = numBase - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            int rec = baseOff + mid * 6, g = U16(c, rec);
            if (g < gid) lo = mid + 1;
            else if (g > gid) hi = mid - 1;
            else
            {
                int first = U16(c, rec + 2), count = U16(c, rec + 4);
                for (int i = 0; i < count; i++)
                {
                    var l = layerOff + (first + i) * 4;
                    yield return (U16(c, l), U16(c, l + 2));
                }
                yield break;
            }
        }
    }

    private static Color PaletteColor(byte[] cpal, int index)
    {
        if (index == 0xFFFF || cpal.Length < 12) return Color.FromArgb(40, 40, 40);
        var recordsOff = (int)U32(cpal, 8);
        var first = U16(cpal, 12);
        var o = recordsOff + (first + index) * 4;
        if (o + 3 >= cpal.Length) return Color.Gray;
        return Color.FromArgb(cpal[o + 3], cpal[o + 2], cpal[o + 1], cpal[o]);
    }

    // ----- glyph outlines via GetGlyphOutline (quadratic B-splines, y up) -----

    private static GraphicsPath? Outline(IntPtr dc, int gid)
    {
        var mat = new MAT2 { eM11 = new FIXED { value = 1 }, eM22 = new FIXED { value = 1 } };
        const uint format = 2 | 0x80 | 0x100; // GGO_NATIVE | GGO_GLYPH_INDEX | GGO_UNHINTED
        var size = GetGlyphOutlineW(dc, (uint)gid, format, out _, 0, null, ref mat);
        if (size == uint.MaxValue || size == 0) return null;
        var buf = new byte[size];
        if (GetGlyphOutlineW(dc, (uint)gid, format, out _, size, buf, ref mat) == uint.MaxValue) return null;

        static float Fx(byte[] b, int o) => BitConverter.ToInt16(b, o + 2) + BitConverter.ToUInt16(b, o) / 65536f;
        PointF Pt(int o) => new(Fx(buf, o), -Fx(buf, o + 4));

        var path = new GraphicsPath(FillMode.Winding);
        int pos = 0;
        while (pos < buf.Length)
        {
            var cb = BitConverter.ToInt32(buf, pos);
            var end = pos + cb;
            var start = Pt(pos + 8);
            var cur = start;
            var p = pos + 16;
            path.StartFigure();
            while (p < end)
            {
                int type = BitConverter.ToUInt16(buf, p), count = BitConverter.ToUInt16(buf, p + 2);
                var pts = Enumerable.Range(0, count).Select(i => Pt(p + 4 + i * 8)).ToArray();
                if (type == 1)
                    foreach (var q in pts) { path.AddLine(cur, q); cur = q; }
                else
                {
                    for (int i = 0; i < count - 1; i++)
                    {
                        var ctrl = pts[i];
                        var next = i < count - 2 ? new PointF((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2) : pts[i + 1];
                        path.AddBezier(cur,
                            new PointF(cur.X + 2f / 3 * (ctrl.X - cur.X), cur.Y + 2f / 3 * (ctrl.Y - cur.Y)),
                            new PointF(next.X + 2f / 3 * (ctrl.X - next.X), next.Y + 2f / 3 * (ctrl.Y - next.Y)),
                            next);
                        cur = next;
                    }
                }
                p += 4 + count * 8;
            }
            if (cur != start) path.AddLine(cur, start);
            path.CloseFigure();
            pos = end;
        }
        return path.PointCount > 0 ? path : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FIXED { public ushort fract; public short value; }
    [StructLayout(LayoutKind.Sequential)] private struct MAT2 { public FIXED eM11, eM12, eM21, eM22; }
    [StructLayout(LayoutKind.Sequential)] private struct GLYPHMETRICS { public uint gmBlackBoxX, gmBlackBoxY; public int gmptGlyphOriginX, gmptGlyphOriginY; public short gmCellIncX, gmCellIncY; }

    [DllImport("gdi32.dll")] private static extern uint GetFontData(IntPtr hdc, uint table, uint offset, byte[]? buffer, uint length);
    [DllImport("gdi32.dll")] private static extern uint GetGlyphOutlineW(IntPtr hdc, uint ch, uint format, out GLYPHMETRICS gm, uint size, byte[]? buffer, ref MAT2 mat);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

internal sealed class EmojiMark : Annotation
{
    public required string Emoji { get; init; }
    public PointF Center { get; set; }
    public float Angle { get; set; }
    public float Opacity { get; init; } = 1;

    public static float PixelsFor(float size) => 16 + size * 6;

    public override void Draw(Graphics g)
    {
        var state = g.Save();
        g.TranslateTransform(Center.X, Center.Y);
        g.RotateTransform(Angle);
        EmojiFont.Draw(g, Emoji, PixelsFor(Size), Opacity);
        g.Restore(state);
    }

    // A circle around the centre covers every rotation.
    public override RectangleF Bounds
    {
        get
        {
            var r = PixelsFor(Size) * 0.75f + 2;
            return new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
        }
    }
}
