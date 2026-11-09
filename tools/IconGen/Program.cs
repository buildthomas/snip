using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// dotnet run --project tools/IconGen -- preview <out.png>
// dotnet run --project tools/IconGen -- ico <design> <out.ico>
// dotnet run --project tools/IconGen -- png <design> <size> <out.png>
switch (args.FirstOrDefault())
{
    case "preview": Preview.Write(args[1]); break;
    case "preview-scissors": Preview.WriteGrid(args[1]); break;
    case "ico": Ico.Write(Designs.Get(args[1]), args[2]); break;
    case "png":
        using (var bmp = Designs.Render(Designs.Get(args[1]), int.Parse(args[2]))) bmp.Save(args[3], ImageFormat.Png);
        break;
    default:
        Console.WriteLine("usage: preview <out.png> | ico <design> <out.ico> | png <design> <size> <out.png>");
        Console.WriteLine("designs: " + string.Join(", ", Designs.All.Select(d => d.Key)));
        break;
}

record Design(string Key, string Name, Action<Graphics, float> Draw);

static class Designs
{
    static readonly Color Blue1 = Color.FromArgb(0, 170, 255), Blue2 = Color.FromArgb(0, 100, 230);
    static readonly Color Red = Color.FromArgb(235, 50, 50), Yellow = Color.FromArgb(255, 214, 0);
    static readonly Color Charcoal = Color.FromArgb(36, 38, 44);

    public static readonly Design[] All =
    {
        new("classic", "1  Classic (current)", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            Corners(g, s, Color.White, 0.24f, 0.2f, 0.09f);
            Dot(g, s, 0.5f, 0.5f, 0.16f, Color.White);
        }),
        new("viewfinder", "2  Viewfinder", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            Corners(g, s, Color.White, 0.22f, 0.18f, 0.08f, all: true);
            using var p = Pen(s, Color.White, 0.07f);
            g.DrawLine(p, s * 0.5f, s * 0.38f, s * 0.5f, s * 0.62f);
            g.DrawLine(p, s * 0.38f, s * 0.5f, s * 0.62f, s * 0.5f);
        }),
        new("scissors", "3  Scissors", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            using var p = Pen(s, Color.White, 0.085f);
            g.DrawEllipse(p, s * 0.2f, s * 0.58f, s * 0.22f, s * 0.22f);
            g.DrawEllipse(p, s * 0.58f, s * 0.58f, s * 0.22f, s * 0.22f);
            g.DrawLine(p, s * 0.38f, s * 0.6f, s * 0.7f, s * 0.2f);
            g.DrawLine(p, s * 0.62f, s * 0.6f, s * 0.3f, s * 0.2f);
        }),
        new("crop-arrow", "4  Crop + arrow", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            Corners(g, s, Color.White, 0.2f, 0.2f, 0.085f);
            Arrow(g, s, new PointF(0.3f, 0.72f), new PointF(0.72f, 0.3f), Red, 0.11f, outline: Color.White);
        }),
        new("selection", "5  Dashed selection", (g, s) =>
        {
            Tile(g, s, Charcoal, Color.FromArgb(20, 22, 26));
            var w = Math.Max(1.5f, s * 0.07f);
            using var p = new Pen(Color.White, w) { DashPattern = new[] { 1.6f, 1.2f } };
            g.DrawRectangle(p, s * 0.24f, s * 0.24f, s * 0.52f, s * 0.52f);
            foreach (var (x, y) in new[] { (0.24f, 0.24f), (0.76f, 0.24f), (0.24f, 0.76f), (0.76f, 0.76f) })
                Square(g, s, x, y, 0.16f, Blue1);
        }),
        new("monogram", "6  S monogram", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            Corners(g, s, Color.FromArgb(200, 255, 255, 255), 0.17f, 0.17f, 0.06f, all: true);
            using var f = new Font("Segoe UI Black", s * 0.56f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("S", f, Brushes.White, new RectangleF(0, s * 0.02f, s, s), sf);
        }),
        new("marker", "7  Highlighter", (g, s) =>
        {
            Tile(g, s, Color.White, Color.FromArgb(232, 236, 242));
            using (var hp = new Pen(Color.FromArgb(220, Yellow), s * 0.2f)) g.DrawLine(hp, s * 0.18f, s * 0.56f, s * 0.82f, s * 0.56f);
            Corners(g, s, Blue2, 0.2f, 0.2f, 0.09f);
        }),
        new("red-pen", "8  Red tile + pen", (g, s) =>
        {
            Tile(g, s, Color.FromArgb(255, 95, 80), Color.FromArgb(215, 35, 45));
            Corners(g, s, Color.White, 0.22f, 0.18f, 0.08f);
            using var p = Pen(s, Color.White, 0.09f);
            using var path = new GraphicsPath();
            path.AddBezier(s * 0.3f, s * 0.6f, s * 0.42f, s * 0.36f, s * 0.56f, s * 0.7f, s * 0.7f, s * 0.42f);
            g.DrawPath(p, path);
        }),
        new("brackets", "9  Brackets, no tile", (g, s) =>
        {
            Corners(g, s, Charcoal, 0.1f, 0.3f, 0.2f, all: true);
            Corners(g, s, Color.White, 0.1f, 0.3f, 0.11f, all: true);
            Dot(g, s, 0.5f, 0.5f, 0.3f, Charcoal);
            Dot(g, s, 0.5f, 0.5f, 0.22f, Red);
        }),
        new("step", "10 Frame + step badge", (g, s) =>
        {
            Tile(g, s, Blue1, Blue2);
            using (var p = Pen(s, Color.White, 0.08f)) g.DrawRectangle(p, s * 0.2f, s * 0.24f, s * 0.48f, s * 0.48f);
            var r = s * 0.2f;
            var c = new PointF(s * 0.7f, s * 0.7f);
            using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, c.X - r - s * 0.04f, c.Y - r - s * 0.04f, (r + s * 0.04f) * 2, (r + s * 0.04f) * 2);
            using (var b = new SolidBrush(Red)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
            using var f = new Font("Segoe UI", r * 1.35f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("1", f, Brushes.White, new RectangleF(c.X - r, c.Y - r + s * 0.01f, r * 2, r * 2), sf);
        }),
    };

    public static readonly (string Key, Color A, Color B)[] Palettes =
    {
        ("blue", Color.FromArgb(0, 170, 255), Color.FromArgb(0, 100, 230)),
        ("coral", Color.FromArgb(255, 105, 85), Color.FromArgb(215, 35, 45)),
        ("teal", Color.FromArgb(25, 205, 160), Color.FromArgb(0, 135, 110)),
        ("purple", Color.FromArgb(160, 100, 255), Color.FromArgb(100, 50, 210)),
        ("orange", Color.FromArgb(255, 175, 40), Color.FromArgb(240, 105, 10)),
        ("graphite", Color.FromArgb(78, 82, 92), Color.FromArgb(32, 34, 40)),
    };

    public static readonly (string Key, string Name)[] ScissorShapes =
    {
        ("open", "Open (as #3)"),
        ("bold", "Bold, chunky handles"),
        ("sideways", "Sideways (classic ✂)"),
        ("round", "Round tile"),
        ("plain", "No tile, outlined"),
    };

    public static IEnumerable<Design> ScissorVariants =>
        from shape in ScissorShapes
        from pal in Palettes
        select new Design($"scissors-{shape.Key}-{pal.Key}", $"{shape.Name}, {pal.Key}", (g, s) => DrawScissorVariant(g, s, shape.Key, pal.A, pal.B));

    static void DrawScissorVariant(Graphics g, float s, string shape, Color a, Color b)
    {
        switch (shape)
        {
            case "open":
                Tile(g, s, a, b);
                Scissors(g, s, Color.White, bold: false);
                break;
            case "bold":
                Tile(g, s, a, b);
                Scissors(g, s, Color.White, bold: true);
                break;
            case "sideways":
                Tile(g, s, a, b);
                var state = g.Save();
                g.TranslateTransform(s / 2, s / 2);
                g.RotateTransform(90);
                g.ScaleTransform(0.95f, 0.95f);
                g.TranslateTransform(-s / 2, -s / 2);
                Scissors(g, s, Color.White, bold: true);
                g.Restore(state);
                break;
            case "round":
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), a, b))
                    g.FillEllipse(br, s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
                Scissors(g, s, Color.White, bold: true);
                break;
            case "plain":
                Scissors(g, s, Color.White, bold: true, extra: 0.09f);
                Scissors(g, s, b, bold: true);
                break;
        }
    }

    static void Scissors(Graphics g, float s, Color c, bool bold, float extra = 0)
    {
        var w = (bold ? 0.11f : 0.085f) + extra;
        using var p = Pen(s, c, w);
        float r = bold ? 0.2f : 0.22f, y = bold ? 0.6f : 0.58f;
        g.DrawEllipse(p, s * 0.2f, s * y, s * r, s * r);
        g.DrawEllipse(p, s * (0.8f - r), s * y, s * r, s * r);
        g.DrawLine(p, s * 0.38f, s * 0.6f, s * 0.7f, s * 0.18f);
        g.DrawLine(p, s * 0.62f, s * 0.6f, s * 0.3f, s * 0.18f);
    }

    public static Design Get(string key) =>
        All.Concat(ScissorVariants).FirstOrDefault(d => d.Key == key) ?? throw new ArgumentException($"unknown design '{key}'");

    public static Bitmap Render(Design d, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        d.Draw(g, size);
        return bmp;
    }

    static Pen Pen(float s, Color c, float width) =>
        new(c, Math.Max(1.5f, s * width)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    static void Tile(Graphics g, float s, Color c1, Color c2)
    {
        var m = s * 0.03f;
        var rect = new RectangleF(m, m, s - 2 * m, s - 2 * m);
        var d = s * 0.4f;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var b = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), c1, c2);
        g.FillPath(b, path);
    }

    static void Corners(Graphics g, float s, Color c, float inset, float len, float width, bool all = false)
    {
        using var p = Pen(s, c, width);
        float a = s * inset, b = s * (1 - inset), l = s * len;
        g.DrawLines(p, new[] { new PointF(a, a + l), new PointF(a, a), new PointF(a + l, a) });
        g.DrawLines(p, new[] { new PointF(b - l, b), new PointF(b, b), new PointF(b, b - l) });
        if (!all) return;
        g.DrawLines(p, new[] { new PointF(b - l, a), new PointF(b, a), new PointF(b, a + l) });
        g.DrawLines(p, new[] { new PointF(a, b - l), new PointF(a, b), new PointF(a + l, b) });
    }

    static void Dot(Graphics g, float s, float x, float y, float d, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillEllipse(b, s * x - s * d / 2, s * y - s * d / 2, s * d, s * d);
    }

    static void Square(Graphics g, float s, float x, float y, float d, Color c)
    {
        using var b = new SolidBrush(c);
        g.FillRectangle(b, s * x - s * d / 2, s * y - s * d / 2, s * d, s * d);
    }

    static void Arrow(Graphics g, float s, PointF from, PointF to, Color c, float width, Color outline)
    {
        var a = new PointF(from.X * s, from.Y * s);
        var t = new PointF(to.X * s, to.Y * s);
        float dx = t.X - a.X, dy = t.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy), ux = dx / len, uy = dy / len;
        var head = s * 0.28f;
        var bx = t.X - ux * head;
        var by = t.Y - uy * head;
        var half = head * 0.6f;
        var tri = new[] { t, new PointF(bx - uy * half, by + ux * half), new PointF(bx + uy * half, by - ux * half) };
        var w = Math.Max(1.5f, s * width);
        using (var op = new Pen(outline, w + Math.Max(1f, s * 0.06f)) { StartCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(op, a, new PointF(bx, by));
            g.DrawPolygon(op, tri);
        }
        using (var p = new Pen(c, w) { StartCap = LineCap.Round }) g.DrawLine(p, a, new PointF(bx + ux, by + uy));
        using var b = new SolidBrush(c);
        g.FillPolygon(b, tri);
    }
}

static class Preview
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64 };

    public static void Write(string path)
    {
        const int rowH = 92, labelW = 210, zoomW = 80;
        var themes = new[] { (Name: "Dark taskbar", Back: Color.FromArgb(32, 32, 32), Fore: Color.White), (Name: "Light taskbar", Back: Color.FromArgb(238, 238, 238), Fore: Color.Black) };
        var sizesW = Sizes.Sum() + Sizes.Length * 12;
        var themeW = zoomW + 12 + sizesW + 24;
        var width = labelW + themes.Length * themeW;
        var height = 40 + Designs.All.Length * rowH;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12, GraphicsUnit.Pixel);

        for (int t = 0; t < themes.Length; t++)
        {
            var x0 = labelW + t * themeW;
            using var back = new SolidBrush(themes[t].Back);
            g.FillRectangle(back, x0, 30, themeW - 12, height - 30);
            using var hb = new SolidBrush(Color.FromArgb(90, 90, 90));
            g.DrawString($"{themes[t].Name}: 16px zoomed ×4, then 16 20 24 32 48 64", small, hb, x0, 10);
        }

        for (int i = 0; i < Designs.All.Length; i++)
        {
            var d = Designs.All[i];
            var y = 40 + i * rowH;
            g.DrawString(d.Name, title, Brushes.Black, 12, y + 30);
            for (int t = 0; t < themes.Length; t++)
            {
                var x = labelW + t * themeW + 8;
                using (var b16 = Designs.Render(d, 16))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(b16, x, y + 8, 64, 64);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                }
                x += zoomW + 12;
                foreach (var s in Sizes)
                {
                    using var bmp = Designs.Render(d, s);
                    g.DrawImageUnscaled(bmp, x, y + 40 - s / 2);
                    x += s + 12;
                }
            }
        }
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"wrote {Path.GetFullPath(path)}");
    }

    // Rows = scissor shapes, columns = colors. Each cell: 16px zoomed x3, then 16 and 32 on dark, 16 and 32 on light.
    public static void WriteGrid(string path)
    {
        const int labelW = 190, cellW = 150, rowH = 120, headH = 34;
        var shapes = Designs.ScissorShapes;
        var pals = Designs.Palettes;
        using var sheet = new Bitmap(labelW + pals.Length * cellW, headH + shapes.Length * rowH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI Semibold", 14, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        using var dark = new SolidBrush(Color.FromArgb(32, 32, 32));
        using var light = new SolidBrush(Color.FromArgb(238, 238, 238));

        for (int c = 0; c < pals.Length; c++)
            g.DrawString(pals[c].Key, title, Brushes.Black, labelW + c * cellW + 8, 8);

        for (int r = 0; r < shapes.Length; r++)
        {
            var y = headH + r * rowH;
            g.DrawString($"{(char)('A' + r)}  {shapes[r].Name}", title, Brushes.Black, 10, y + 48);
            for (int c = 0; c < pals.Length; c++)
            {
                var d = Designs.Get($"scissors-{shapes[r].Key}-{pals[c].Key}");
                var x = labelW + c * cellW;
                g.FillRectangle(dark, x + 4, y + 4, cellW - 8, 70);
                g.FillRectangle(light, x + 4, y + 74, cellW - 8, 40);
                using (var b16 = Designs.Render(d, 16))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(b16, x + 12, y + 15, 48, 48);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.DrawImageUnscaled(b16, x + 74, y + 31);
                    g.DrawImageUnscaled(b16, x + 74, y + 86);
                }
                using var b32 = Designs.Render(d, 32);
                g.DrawImageUnscaled(b32, x + 100, y + 23);
                g.DrawImageUnscaled(b32, x + 100, y + 78);
            }
        }
        using var foot = new SolidBrush(Color.FromArgb(90, 90, 90));
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"wrote {Path.GetFullPath(path)}");
    }
}

static class Ico
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    public static void Write(Design d, string path)
    {
        var pngs = Sizes.Select(s =>
        {
            using var bmp = Designs.Render(d, s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)Sizes.Length);
        var offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            var dim = Sizes[i] >= 256 ? 0 : Sizes[i];
            w.Write((byte)dim); w.Write((byte)dim); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32); w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        Console.WriteLine($"wrote {Path.GetFullPath(path)} ({d.Name.Trim()})");
    }
}
