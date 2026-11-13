using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace Snip.Tests;

// Flash + tone recorded and decoded like a player would; compares file timing with reality. Makes noise: SNIP_AV_SYNC=1.
[Collection("screen")]
public unsafe class AvSyncCheck(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static MemoryStream Tone()
    {
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        int n = Rate / 5;
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / Rate) * 8000));
        wav.Position = 0;
        return wav;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    [OptInFact("SNIP_AV_SYNC")]
    public void FlashAndToneStayInSync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-avsync-{Guid.NewGuid():N}.mp4");
        var area = new Rectangle(Screen.PrimaryScreen!.Bounds.X + 100, Screen.PrimaryScreen.Bounds.Y + 100, 320, 240);
        var shown = new ManualResetEventSlim();
        Form? form = null;
        var ui = new Thread(() =>
        {
            form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = area, TopMost = true, BackColor = Color.Black };
            form.Shown += (_, _) => shown.Set();
            Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        shown.Wait();
        using var player = new System.Media.SoundPlayer(Tone());
        player.Load();

        var tap = new List<(long First, short[] Pcm)>();
        var flashes = new List<long>();
        var rec = ScreenRecorder.Prepare(area, path, systemSound: true, microphone: false);
        rec.AudioTap = (pcm, first) => { lock (tap) tap.Add((first, pcm)); };
        using (var started = new ManualResetEventSlim())
        {
            rec.WhenStarted(started.Set);
            rec.Begin(0);
            started.Wait();
        }
        try
        {
            Thread.Sleep(1500);
            for (int i = 0; i < 6; i++)
            {
                long at = 0;
                form!.Invoke(() =>
                {
                    form.BackColor = Color.White;
                    form.Refresh();
                    DwmFlush();
                    at = Stopwatch.GetTimestamp();
                    player.Play();
                });
                flashes.Add(at);
                Thread.Sleep(400);
                form.Invoke(() => { form.BackColor = Color.Black; form.Refresh(); });
                Thread.Sleep(900);
            }
            Thread.Sleep(500);
            var result = rec.Stop();
            result.LastFrame?.Dispose();
            output.WriteLine($"recorder wrote {result.Frames} frames over {result.Duration.TotalMilliseconds:0} ms");

            double Ms(long qpc) => (qpc - rec.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            var flashMs = flashes.Select(Ms).ToList();
            var tapOnsets = Onsets(tap.SelectMany(c => c.Pcm.Select((v, j) => (Frame: c.First + j / 2, v, Left: j % 2 == 0))).Where(s => s.Left).Select(s => (s.Frame, s.v)).ToList());
            var (videoFrames, audio) = Decode(path);
            var fileFlashes = new List<double>();
            for (int k = 1; k < videoFrames.Count; k++)
                if (videoFrames[k].Bright > 128 && videoFrames[k - 1].Bright <= 128) fileFlashes.Add(videoFrames[k].Ms);
            var fileOnsets = Onsets(audio);
            output.WriteLine($"video frames decoded: {videoFrames.Count}; white from: {string.Join(", ", fileFlashes.Select(x => $"{x:0}"))}");

            output.WriteLine("event | flash: real -> file (ms) | tone: real -> file (ms) | tone after flash: real -> file (ms)");
            for (int i = 0; i < flashMs.Count; i++)
            {
                double? Near(List<double> list, double t) => list.Where(x => x > t - 150 && x < t + 400).Select(x => (double?)x).FirstOrDefault();
                var ff = Near(fileFlashes, flashMs[i]);
                var ta = Near(tapOnsets, flashMs[i]);
                var fa = Near(fileOnsets, flashMs[i]);
                string F(double? v) => v is double d ? $"{d,8:0.0}" : "     n/a";
                output.WriteLine($"{i} | {flashMs[i],8:0.0} -> {F(ff)} | {F(ta)} -> {F(fa)} | {F(ta - flashMs[i])} -> {F(fa - ff)}");
            }
        }
        finally
        {
            form!.BeginInvoke(() => form.Close());
            ui.Join();
            if (Environment.GetEnvironmentVariable("SNIP_AV_SYNC_KEEP") is string keep) File.Move(path, keep, true); else File.Delete(path);
        }
    }

    private static List<double> Onsets(List<(long Frame, short V)> samples)
    {
        var onsets = new List<double>();
        long lastLoud = long.MinValue;
        foreach (var (frame, v) in samples)
        {
            if (Math.Abs((int)v) < 2000) continue;
            if (frame - lastLoud > Rate / 2) onsets.Add(frame * 1000.0 / Rate);
            lastLoud = frame;
        }
        return onsets;
    }

    // ---------- Media Foundation source reader: decodes the file like a player (edit lists, encoder delay) ----------

    private static IntPtr Slot(IntPtr com, int i) => (*(IntPtr**)com)[i];
    private static void Release(IntPtr com) { if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com); }
    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what}: 0x{hr:X8}"); }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attrs, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attrs, out IntPtr reader);

    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    private static void SetGuid(IntPtr a, Guid k, Guid v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &k, &v), "set");
    private static void SetU32(IntPtr a, Guid k, uint v) => Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &k, v), "set");

    private static (List<(double Ms, double Bright)> Video, List<(long Frame, short V)> Audio) Decode(string path)
    {
        Check(MFStartup(0x20070, 0), "MFStartup");
        Check(MFCreateAttributes(out var attrs, 1), "attrs");
        SetU32(attrs, MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);
        Check(MFCreateSourceReaderFromURL(path, attrs, out var reader), "open");
        Release(attrs);
        const uint Video = 0xFFFFFFFC, Audio = 0xFFFFFFFD;
        foreach (var (stream, major, sub, bits) in new[] { (Video, MFMediaType_Video, MFVideoFormat_RGB32, 0u), (Audio, MFMediaType_Audio, MFAudioFormat_PCM, 16u) })
        {
            Check(MFCreateMediaType(out var t), "type");
            SetGuid(t, MF_MT_MAJOR_TYPE, major);
            SetGuid(t, MF_MT_SUBTYPE, sub);
            if (bits != 0) SetU32(t, MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(reader, 7))(reader, stream, IntPtr.Zero, t), "set type");
            Release(t);
        }
        var video = new List<(double, double)>();
        var audio = new List<(long, short)>();
        foreach (var stream in new[] { Video, Audio })
        {
            while (true)
            {
                uint actual, flags; long ts; IntPtr sample;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(reader, stream, 0, &actual, &flags, &ts, &sample), "read");
                if ((flags & 2 /* END_OF_STREAM */) != 0) { Release(sample); break; }
                if (sample == IntPtr.Zero) continue;
                IntPtr buffer;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer), "buffer");
                byte* data; uint max, len;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &len), "lock");
                if (stream == Video)
                {
                    long sum = 0;
                    for (uint i = 0; i < len; i += 4) sum += (data[i] + data[i + 1] + data[i + 2]) / 3;
                    video.Add((ts / 10_000.0, sum / (double)(len / 4)));
                }
                else
                {
                    var first = ts * Rate / 10_000_000;
                    var s = (short*)data;
                    for (int i = 0; i < len / 4; i++) audio.Add((first + i, s[i * 2]));
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
                Release(buffer);
                Release(sample);
            }
        }
        Release(reader);
        return (video, audio);
    }
}
