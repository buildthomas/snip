using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Snip;

internal enum RedactStyle { Black = 0, Pixelate = 1, Blur = 2 }
// Values are stored in settings; keep them stable.
internal enum RedactShape { Box = 0, Brush = 2 }

// A redacted area. Black is a solid fill. In secure mode, Pixelate and Blur are generated from fresh randomness and
// only a 4-tone palette of the covered area plus the colours just outside its edge, never from the covered pixels'
// positions, so nothing underneath can be reconstructed, not even by averaging many captures.
// In insecure mode they are a real pixelation/blur of the covered pixels, which can be partly reversed.
internal sealed class RedactMark : Annotation, IDisposable
{
    public required Bitmap Source { get; init; }
    public RedactStyle Style { get; init; }
    public RedactShape Shape { get; init; }
    public bool Secure { get; init; } = true;
    public ulong Seed { get; init; }
    public RectangleF Rect { get; set; }
    public List<PointF> Points { get; } = new();
    public float BrushWidth { get; init; } = 24;
    // Pixelation cell / blur radius in pixels; adjustable live with Ctrl+scroll.
    public int CellSize { get; set; } = 12;

    public static readonly int[] CellSteps = { 4, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48 };
    // While drawing, a cheaper rendering without edge blending keeps dragging smooth.
    public bool Preview { get; set; }

    private int _version;
    private Bitmap? _tile;
    private Rectangle _tileRect;
    private int _tileVersion = -1;
    private bool _tilePreview;

    public void Changed() => _version++;

    public static float BrushWidthFor(float size) => 8 + size * 4;

    public GraphicsPath BuildPath()
    {
        var path = new GraphicsPath(FillMode.Winding);
        switch (Shape)
        {
            case RedactShape.Box:
                if (Rect.Width > 0 && Rect.Height > 0) path.AddRectangle(Rect);
                break;
            case RedactShape.Brush:
                if (Points.Count == 1)
                    path.AddEllipse(Points[0].X - BrushWidth / 2, Points[0].Y - BrushWidth / 2, BrushWidth, BrushWidth);
                else if (Points.Count > 1)
                {
                    path.AddLines(Points.ToArray());
                    using var pen = new Pen(Color.Black, BrushWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    path.Widen(pen);
                    // Widen yields an outline whose overlaps would cancel out under the default fill mode.
                    path.FillMode = FillMode.Winding;
                }
                break;
        }
        return path;
    }

    public override RectangleF Bounds
    {
        get
        {
            using var path = BuildPath();
            return path.PointCount == 0 ? RectangleF.Empty : Inflate(path.GetBounds(), 2);
        }
    }

    public bool IsEmpty
    {
        get
        {
            using var path = BuildPath();
            if (path.PointCount == 0) return true;
            var b = path.GetBounds();
            return b.Width < 2 || b.Height < 2;
        }
    }

    public override void Draw(Graphics g)
    {
        using var path = BuildPath();
        if (path.PointCount == 0) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        if (Style == RedactStyle.Black)
            g.FillPath(Brushes.Black, path);
        else if (Tile(path) is Bitmap tile)
        {
            g.SetClip(path, CombineMode.Intersect);
            g.DrawImage(tile, _tileRect.X, _tileRect.Y, _tileRect.Width, _tileRect.Height);
        }
        g.Restore(state);
    }

    private Bitmap? Tile(GraphicsPath path)
    {
        var pb = path.GetBounds();
        var b = Rectangle.FromLTRB((int)Math.Floor(pb.Left), (int)Math.Floor(pb.Top), (int)Math.Ceiling(pb.Right), (int)Math.Ceiling(pb.Bottom));
        b.Intersect(new Rectangle(0, 0, Source.Width, Source.Height));
        if (b.Width <= 0 || b.Height <= 0) return null;
        if (_tile != null && _tileVersion == _version && _tileRect == b && _tilePreview == Preview) return _tile;

        _tile?.Dispose();
        _tile = !Secure
            ? Style == RedactStyle.Pixelate ? RedactRender.RealPixelate(Source, b, CellSize) : RedactRender.RealBlur(Source, b, CellSize)
            : RedactRender.Secure(Source, b, path, Seed, CellSize, smooth: Style == RedactStyle.Blur, edges: !Preview,
                // On a thin brush stroke every block is near an edge; a full fade would make it look erased, not redacted.
                edgeReach: Shape == RedactShape.Brush ? (int)(BrushWidth * 0.3f) : null,
                edgeStrength: Shape == RedactShape.Brush ? 0.45 : 0.7);
        _tileRect = b;
        _tileVersion = _version;
        _tilePreview = Preview;
        return _tile;
    }

    public static ulong NewSeed() =>
        BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    public void Dispose()
    {
        _tile?.Dispose();
        _tile = null;
    }
}

internal static class RedactRender
{
    public const int MinContrast = 70;

    public static Bitmap Secure(Bitmap source, Rectangle b, GraphicsPath path, ulong seed, int block, bool smooth, bool edges,
        int? edgeReach = null, double edgeStrength = 0.7)
    {
        // Small shapes would be all "edge" and fade into their surroundings, looking erased rather than redacted.
        var reach = Math.Max(2, Math.Min(edgeReach ?? (int)(2.5 * block), Math.Min(b.Width, b.Height) / 4));
        var e = Rectangle.Inflate(b, reach + 4, reach + 4);
        e.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        var px = Read(source, e);
        var inside = Mask(path, e);

        var palette = EnsureContrast(Tones(px, inside, 4, 0.15, 0.85), MinContrast);

        int bx0 = b.Left / block, by0 = b.Top / block, bx1 = (b.Right - 1) / block, by1 = (b.Bottom - 1) / block;
        int nbx = bx1 - bx0 + 1, nby = by1 - by0 + 1;
        var field = new double[nbx * nby];
        for (int j = 0; j < nby; j++)
            for (int i = 0; i < nbx; i++)
                field[j * nbx + i] = Noise(seed, bx0 + i, by0 + j);
        double min = field.Min(), max = field.Max();

        var colors = new Color[nbx * nby];
        for (int j = 0; j < nby; j++)
            for (int i = 0; i < nbx; i++)
            {
                var t = max > min ? (field[j * nbx + i] - min) / (max - min) : 0.5;
                var c = Ramp(palette, t);
                if (edges)
                {
                    int cx = (bx0 + i) * block + block / 2 - e.X, cy = (by0 + j) * block + block / 2 - e.Y;
                    if (NearestOutside(px, inside, e.Width, e.Height, cx, cy, reach) is (double dist, Color o))
                    {
                        var k = 1 - dist / reach;
                        c = Mix(o, c, edgeStrength * k * k * (3 - 2 * k));
                    }
                }
                colors[j * nbx + i] = c;
            }

        var tile = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        var outPx = new int[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int ax = b.X + x, ay = b.Y + y;
                Color c;
                if (!smooth) c = colors[(ay / block - by0) * nbx + (ax / block - bx0)];
                else
                {
                    double gx = Math.Clamp((ax + 0.5) / block - 0.5 - bx0, 0, nbx - 1), gy = Math.Clamp((ay + 0.5) / block - 0.5 - by0, 0, nby - 1);
                    int x0 = (int)gx, y0 = (int)gy, x1 = Math.Min(x0 + 1, nbx - 1), y1 = Math.Min(y0 + 1, nby - 1);
                    double tx = gx - x0, ty = gy - y0;
                    var top = Mix(colors[y0 * nbx + x1], colors[y0 * nbx + x0], tx);
                    var bottom = Mix(colors[y1 * nbx + x1], colors[y1 * nbx + x0], tx);
                    c = Mix(bottom, top, ty);
                }
                outPx[y * b.Width + x] = c.ToArgb() | unchecked((int)0xFF000000);
            }
        Write(tile, outPx);
        return tile;
    }

    // NOT secure: a real pixelation (block averages) of the covered pixels, on the absolute block grid.
    public static Bitmap RealPixelate(Bitmap source, Rectangle b, int block)
    {
        var e = Rectangle.FromLTRB(b.Left / block * block, b.Top / block * block,
            Math.Min(source.Width, ((b.Right - 1) / block + 1) * block), Math.Min(source.Height, ((b.Bottom - 1) / block + 1) * block));
        var px = Read(source, e);
        var outPx = new int[b.Width * b.Height];
        for (int by = e.Top; by < e.Bottom; by += block)
            for (int bx = e.Left; bx < e.Right; bx += block)
            {
                long r = 0, g = 0, bl = 0, n = 0;
                for (int y = by; y < Math.Min(e.Bottom, by + block); y++)
                    for (int x = bx; x < Math.Min(e.Right, bx + block); x++)
                    {
                        var p = px[(y - e.Y) * e.Width + (x - e.X)];
                        r += (p >> 16) & 255; g += (p >> 8) & 255; bl += p & 255; n++;
                    }
                var argb = unchecked((int)0xFF000000) | (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(bl / n);
                for (int y = Math.Max(by, b.Top); y < Math.Min(by + block, b.Bottom); y++)
                    for (int x = Math.Max(bx, b.Left); x < Math.Min(bx + block, b.Right); x++)
                        outPx[(y - b.Y) * b.Width + (x - b.X)] = argb;
            }
        var tile = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        Write(tile, outPx);
        return tile;
    }

    // NOT secure: a real blur of the covered pixels.
    public static Bitmap RealBlur(Bitmap source, Rectangle b, int radius)
    {
        var e = Rectangle.Inflate(b, radius * 3, radius * 3);
        e.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        var px = Read(source, e);
        int w = e.Width, h = e.Height;
        var r = new int[px.Length]; var g = new int[px.Length]; var bl = new int[px.Length];
        for (int i = 0; i < px.Length; i++) { r[i] = (px[i] >> 16) & 255; g[i] = (px[i] >> 8) & 255; bl[i] = px[i] & 255; }
        foreach (var ch in new[] { r, g, bl })
            for (int pass = 0; pass < 3; pass++) { BoxBlur(ch, w, h, radius, true); BoxBlur(ch, w, h, radius, false); }
        var outPx = new int[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var i = (b.Y - e.Y + y) * w + (b.X - e.X + x);
                outPx[y * b.Width + x] = unchecked((int)0xFF000000) | (r[i] << 16) | (g[i] << 8) | bl[i];
            }
        var tile = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        Write(tile, outPx);
        return tile;
    }

    private static void BoxBlur(int[] ch, int w, int h, int radius, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var buf = new int[len];
        for (int line = 0; line < lines; line++)
        {
            int Idx(int k) => horizontal ? line * w + k : k * w + line;
            long sum = 0;
            int lo = 0, hi = -1;
            for (int i = 0; i < len; i++)
            {
                int want0 = Math.Max(0, i - radius), want1 = Math.Min(len - 1, i + radius);
                while (hi < want1) sum += ch[Idx(++hi)];
                while (lo < want0) sum -= ch[Idx(lo++)];
                buf[i] = (int)(sum / (hi - lo + 1));
            }
            for (int i = 0; i < len; i++) ch[Idx(i)] = buf[i];
        }
    }

    // Average colour of the nearest ring of pixels outside the shape, searched outward up to `reach`.
    private static (double, Color)? NearestOutside(int[] px, bool[] inside, int w, int h, int cx, int cy, int reach)
    {
        for (int d = 1; d <= reach; d += 2)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int k = -d; k <= d; k += 2)
            {
                Sample(cx + k, cy - d); Sample(cx + k, cy + d); Sample(cx - d, cy + k); Sample(cx + d, cy + k);
            }
            if (n > 0) return (d, Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n)));

            void Sample(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return;
                var i = y * w + x;
                if (inside[i]) return;
                r += (px[i] >> 16) & 255; g += (px[i] >> 8) & 255; b += px[i] & 255; n++;
            }
        }
        return null;
    }

    // Hashed two-octave value noise on the absolute block grid, so the pattern stays put while a shape grows.
    private static double Noise(ulong seed, int bx, int by)
    {
        double Octave(int spacing, ulong salt)
        {
            double fx = (double)bx / spacing, fy = (double)by / spacing;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double tx = S(fx - ix), ty = S(fy - iy);
            double L(int x, int y) => (Hash(seed ^ salt, x, y) >> 11) * (1.0 / (1UL << 53));
            return (L(ix, iy) * (1 - tx) + L(ix + 1, iy) * tx) * (1 - ty) + (L(ix, iy + 1) * (1 - tx) + L(ix + 1, iy + 1) * tx) * ty;
        }
        static double S(double t) => t * t * (3 - 2 * t);
        var jitter = (Hash(seed ^ 0x51, bx, by) >> 11) * (1.0 / (1UL << 53)) - 0.5;
        return 0.7 * Octave(4, 0xA1) + 0.3 * Octave(2, 0xB2) + 0.08 * jitter;
    }

    private static ulong Hash(ulong seed, int x, int y)
    {
        var z = seed ^ (ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // Representative colours at evenly spaced brightness quantiles (between lo and hi) of the covered pixels.
    internal static Color[] Tones(int[] px, bool[] inside, int n, double lo, double hi)
    {
        var count = inside.Count(v => v);
        var stride = Math.Max(1, count / 60000);
        var list = new List<int>(count / stride + 1);
        for (int i = 0, k = 0; i < px.Length; i++)
            if (inside[i] && k++ % stride == 0) list.Add(px[i]);
        if (list.Count == 0) return new[] { Color.Gray, Color.Gray, Color.Gray, Color.Gray };
        list.Sort((a, b) => Lum(a).CompareTo(Lum(b)) is var c && c != 0 ? c : a.CompareTo(b));
        int from = (int)(lo * list.Count), to = Math.Max(from + 1, (int)(hi * list.Count));
        var span = list.GetRange(from, to - from);
        return Enumerable.Range(0, n).Select(k =>
        {
            int a = k * span.Count / n, z = Math.Max(a + 1, (k + 1) * span.Count / n);
            long r = 0, g = 0, b = 0;
            for (int i = a; i < z; i++) { r += (span[i] >> 16) & 255; g += (span[i] >> 8) & 255; b += span[i] & 255; }
            var m = z - a;
            return Color.FromArgb((int)(r / m), (int)(g / m), (int)(b / m));
        }).ToArray();
    }

    // A redaction must always look like one: tones too close together (flat UI, white page) are spread apart.
    internal static Color[] EnsureContrast(Color[] palette, int minRange)
    {
        var lums = palette.Select(c => Lum(c.ToArgb())).ToArray();
        if (lums.Max() - lums.Min() >= minRange) return palette;
        var center = Math.Clamp(lums.Average(), minRange / 2.0 + 10, 245 - minRange / 2.0);
        return palette.Select((c, i) =>
        {
            var shift = (int)Math.Round(center + (i / (palette.Length - 1.0) - 0.5) * minRange - lums[i]);
            return Color.FromArgb(Math.Clamp(c.R + shift, 0, 255), Math.Clamp(c.G + shift, 0, 255), Math.Clamp(c.B + shift, 0, 255));
        }).ToArray();
    }

    internal static int Lum(int argb) => (((argb >> 16) & 255) * 299 + ((argb >> 8) & 255) * 587 + (argb & 255) * 114) / 1000;

    private static Color Ramp(Color[] palette, double t)
    {
        var x = Math.Clamp(t, 0, 1) * (palette.Length - 1);
        var i = Math.Min((int)x, palette.Length - 2);
        return Mix(palette[i + 1], palette[i], x - i);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)Math.Round(a.R * t + b.R * (1 - t)), (int)Math.Round(a.G * t + b.G * (1 - t)), (int)Math.Round(a.B * t + b.B * (1 - t)));

    private static int[] Read(Bitmap bmp, Rectangle r)
    {
        var px = new int[r.Width * r.Height];
        var d = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < r.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, px, y * r.Width, r.Width);
        }
        finally { bmp.UnlockBits(d); }
        return px;
    }

    private static void Write(Bitmap bmp, int[] px)
    {
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * bmp.Width, d.Scan0 + y * d.Stride, bmp.Width);
        }
        finally { bmp.UnlockBits(d); }
    }

    private static bool[] Mask(GraphicsPath path, Rectangle e)
    {
        using var bmp = new Bitmap(e.Width, e.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.None;
            g.TranslateTransform(-e.X, -e.Y);
            g.FillPath(Brushes.White, path);
        }
        var px = Read(bmp, new Rectangle(0, 0, e.Width, e.Height));
        return px.Select(p => (p & 0xFF) > 127).ToArray();
    }
}
