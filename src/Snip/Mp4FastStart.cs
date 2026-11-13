using System.Buffers.Binary;
using System.Text;

namespace Snip;

/// Moves an MP4's index (moov) in front of its media data, like qt-faststart, so browsers and chat apps can play and
/// preview it before the whole file has arrived. Media Foundation always writes the index last.
internal static class Mp4FastStart
{
    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl"];

    public static bool Apply(string path)
    {
        long mdatStart = -1, moovStart = -1, moovEnd = -1, length;
        using (var input = File.OpenRead(path))
        {
            length = input.Length;
            var header = new byte[16];
            for (long p = 0; p + 8 <= length;)
            {
                input.Position = p;
                input.ReadExactly(header, 0, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                if (size == 1) { input.ReadExactly(header, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); }
                else if (size == 0) size = length - p;
                if (size < 8 || p + size > length) return false;
                if (type == "mdat" && mdatStart < 0) mdatStart = p;
                if (type == "moov") { moovStart = p; moovEnd = p + size; }
                p += size;
            }
        }
        if (mdatStart < 0 || moovStart < mdatStart) return false;

        var moov = new byte[moovEnd - moovStart];
        using (var input = File.OpenRead(path)) { input.Position = moovStart; input.ReadExactly(moov); }
        if (!ShiftChunkOffsets(moov, moov.Length)) return false;

        var temp = path + ".faststart";
        using (var input = File.OpenRead(path))
        using (var output = File.Create(temp))
        {
            Copy(input, output, 0, mdatStart);
            output.Write(moov);
            Copy(input, output, mdatStart, moovStart);
            Copy(input, output, moovEnd, length);
        }
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// Adds delta to every chunk offset (stco/co64) inside a moov box; false if a 32-bit offset would overflow.
    public static bool ShiftChunkOffsets(Span<byte> box, long delta)
    {
        int header = BinaryPrimitives.ReadUInt32BigEndian(box) == 1 ? 16 : 8;
        var type = Encoding.ASCII.GetString(box.Slice(4, 4));
        var body = box[header..];
        if (type == "stco" || type == "co64")
        {
            var count = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            for (int i = 0; i < count; i++)
            {
                if (type == "stco")
                {
                    var at = body.Slice(8 + i * 4, 4);
                    var v = BinaryPrimitives.ReadUInt32BigEndian(at) + delta;
                    if (v > uint.MaxValue) return false;
                    BinaryPrimitives.WriteUInt32BigEndian(at, (uint)v);
                }
                else
                {
                    var at = body.Slice(8 + i * 8, 8);
                    BinaryPrimitives.WriteUInt64BigEndian(at, BinaryPrimitives.ReadUInt64BigEndian(at) + (ulong)delta);
                }
            }
            return true;
        }
        if (!Containers.Contains(type)) return true;
        for (int p = 0; p + 8 <= body.Length;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(body[p..]);
            if (size == 1) size = (long)BinaryPrimitives.ReadUInt64BigEndian(body[(p + 8)..]);
            if (size < 8 || p + size > body.Length) return false;
            if (!ShiftChunkOffsets(body.Slice(p, (int)size), delta)) return false;
            p += (int)size;
        }
        return true;
    }

    private static void Copy(Stream input, Stream output, long from, long to)
    {
        input.Position = from;
        var buffer = new byte[1 << 20];
        for (long left = to - from; left > 0;)
        {
            var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, n);
            left -= n;
        }
    }
}
