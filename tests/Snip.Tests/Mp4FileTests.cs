using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Xunit;

namespace Snip.Tests;

[Collection("screen")]
public class Mp4FileTests
{
    private static List<(string Type, long Start, long End)> Boxes(byte[] f, long start, long end)
    {
        var list = new List<(string, long, long)>();
        for (long p = start; p + 8 <= end;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan((int)p));
            var type = System.Text.Encoding.ASCII.GetString(f, (int)p + 4, 4);
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan((int)p + 8)); header = 16; }
            if (size < header) break;
            list.Add((type, p + header, p + size));
            p += size;
        }
        return list;
    }

    private static (long Start, long End) Find(byte[] f, long start, long end, params string[] path)
    {
        var box = Boxes(f, start, end).First(b => b.Type == path[0]);
        return path.Length == 1 ? (box.Start, box.End) : Find(f, box.Start, box.End, path[1..]);
    }

    [Fact]
    public void IndexComesFirstAndTheFirstFrameIsARealImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-test-{Guid.NewGuid():N}.mp4");
        var area = new Rectangle(Screen.PrimaryScreen!.Bounds.X + 100, Screen.PrimaryScreen.Bounds.Y + 100, 480, 320);
        var shown = new ManualResetEventSlim();
        Form? form = null;
        var ui = new Thread(() =>
        {
            form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = area, TopMost = true };
            form.Paint += (_, e) =>
            {
                using var b = new LinearGradientBrush(form.ClientRectangle, Color.OrangeRed, Color.MediumBlue, 30f);
                e.Graphics.FillRectangle(b, form.ClientRectangle);
                for (int i = 0; i < 12; i++) e.Graphics.DrawString($"Line {i}: the quick brown fox", form.Font, Brushes.White, 10, 10 + i * 24);
            };
            form.Shown += (_, _) => shown.Set();
            Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        shown.Wait();
        Thread.Sleep(300);
        try
        {
            var rec = ScreenRecorder.Start(area, path);
            Thread.Sleep(1200);
            rec.Stop().LastFrame?.Dispose();

            var f = File.ReadAllBytes(path);
            var top = Boxes(f, 0, f.Length).Select(b => b.Type).ToList();
            Assert.True(top.IndexOf("moov") < top.IndexOf("mdat"), $"box order: {string.Join(", ", top)}");

            var stsz = Find(f, 0, f.Length, "moov", "trak", "mdia", "minf", "stbl", "stsz");
            var count = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan((int)stsz.Start + 8));
            var sizes = Enumerable.Range(0, (int)count).Select(i => BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan((int)stsz.Start + 12 + i * 4))).ToList();
            // A blank keyframe is a few hundred bytes; this gradient with text is many kilobytes.
            Assert.True(sizes[0] > 4000, $"first frame is {sizes[0]} bytes (blank?); sizes {string.Join(", ", sizes.Take(5))}");
        }
        finally
        {
            form!.BeginInvoke(() => form.Close());
            ui.Join();
            File.Delete(path);
        }
    }
}

public class Mp4FastStartTests
{
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    private static byte[] U32(params uint[] v) => v.SelectMany(x => { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, x); return b; }).ToArray();

    [Fact]
    public void MovesTheIndexFirstAndChunkOffsetsStillPointAtTheSameData()
    {
        var ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("mp42"), U32(0));
        var payload = System.Text.Encoding.ASCII.GetBytes("FRAME-ONE-DATA");
        var mdat = Box("mdat", payload);
        var offset = (uint)(ftyp.Length + 8);
        var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", Box("stco", U32(0, 1, offset)))))));
        var path = Path.Combine(Path.GetTempPath(), $"snip-faststart-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, ftyp.Concat(mdat).Concat(moov).ToArray());
        try
        {
            Assert.True(Mp4FastStart.Apply(path));
            var f = File.ReadAllBytes(path);
            Assert.Equal(ftyp.Length + mdat.Length + moov.Length, f.Length);
            Assert.Equal("moov", System.Text.Encoding.ASCII.GetString(f, ftyp.Length + 4, 4));
            Assert.Equal("mdat", System.Text.Encoding.ASCII.GetString(f, ftyp.Length + moov.Length + 4, 4));
            var shifted = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(ftyp.Length + moov.Length - 4));
            Assert.Equal("FRAME-ONE-DATA", System.Text.Encoding.ASCII.GetString(f, (int)shifted, payload.Length));
            Assert.False(Mp4FastStart.Apply(path));
        }
        finally { File.Delete(path); }
    }
}
