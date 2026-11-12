using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace Snip.Tests;

// Two tones after silence must reach the encoder whole, once, without clicks. Makes noise, so opt-in (SNIP_SOUND_CHECK=1).
public class SoundCheck(ITestOutputHelper output)
{
    private const int Rate = 48000, Amplitude = 3000;

    private static MemoryStream Tone()
    {
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write("RIFF"u8); w.Write(36 + Rate * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(Rate * 2);
        for (int i = 0; i < Rate; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * Amplitude));
        wav.Position = 0;
        return wav;
    }

    [OptInFact("SNIP_SOUND_CHECK")]
    public void ToneAfterSilenceIsClean()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-sound-{Guid.NewGuid():N}.mp4");
        var pcm = new List<short>();
        var rec = ScreenRecorder.Prepare(new Rectangle(0, 0, 320, 240), path, systemSound: true, microphone: false);
        rec.AudioTap = (chunk, _) => { lock (pcm) pcm.AddRange(chunk); };
        using (var started = new ManualResetEventSlim())
        {
            rec.WhenStarted(started.Set);
            rec.Begin(0);
            started.Wait();
        }
        try
        {
            Thread.Sleep(1500);
            int idlePeak;
            lock (pcm) idlePeak = pcm.Count == 0 ? 0 : pcm.Max(v => Math.Abs((int)v));
            Assert.True(idlePeak < 50, $"other sound is playing (peak {idlePeak}); pause it and run again");
            using (var p = new System.Media.SoundPlayer(Tone())) p.PlaySync();
            Thread.Sleep(1500);
            using (var p = new System.Media.SoundPlayer(Tone())) p.PlaySync();
            Thread.Sleep(800);
            rec.Stop().LastFrame?.Dispose();

            short[] a;
            lock (pcm) a = pcm.ToArray();
            var peak = a.Max(v => Math.Abs((int)v));
            var loud = Enumerable.Range(0, a.Length / 2).Count(i => Math.Abs((int)a[i * 2]) > 1000);
            var firstLoud = Enumerable.Range(0, a.Length / 2).First(i => Math.Abs((int)a[i * 2]) > 1000);
            int clicks = 0, startupClicks = 0;
            for (int i = 2; i < a.Length / 2; i++)
            {
                int s0 = a[(i - 2) * 2], s1 = a[(i - 1) * 2], s2 = a[i * 2];
                var toneEdge = s0 == 0 && s1 == 0 || s1 == 0 && s2 == 0;
                if (toneEdge || Math.Abs(s2 - 2 * s1 + s0) <= 60) continue;
                // Windows may deliver one packet, then pause while the output device wakes up (a flagged break).
                if (i - firstLoud < Rate && startupClicks < 2) { startupClicks++; continue; }
                if (clicks++ < 10) output.WriteLine($"click at {i / (double)Rate:0.0000} s: {s0} {s1} {s2}");
            }
            output.WriteLine($"peak {peak}, tone {loud / (double)Rate:0.00} s, start-up break edges {startupClicks}, clicks {clicks}");
            Assert.InRange(peak, 2500, 3300);
            Assert.InRange(loud, (int)(Rate * 2 * 0.7), (int)(Rate * 2 * 0.85));
            Assert.Equal(0, clicks);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
