using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace Snip.Tests;

// Records system sound while a quiet 440 Hz tone plays after silence, pauses, and plays again, then checks the
// audio handed to the encoder: the tone is there, never doubled up, and free of clicks. Needs a quiet PC and makes
// noise, so it's opt-in: SNIP_SOUND_CHECK=1 dotnet test --filter SoundCheck --logger "console;verbosity=detailed"
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

    [Fact]
    public void ToneAfterSilenceIsClean()
    {
        if (Environment.GetEnvironmentVariable("SNIP_SOUND_CHECK") != "1") return;
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
            int clicks = 0;
            for (int i = 2; i < a.Length / 2; i++)
            {
                int s0 = a[(i - 2) * 2], s1 = a[(i - 1) * 2], s2 = a[i * 2];
                if (Math.Abs(s2 - 2 * s1 + s0) > 60 && clicks++ < 10) output.WriteLine($"click at {i / (double)Rate:0.0000} s: {s0} {s1} {s2}");
            }
            output.WriteLine($"peak {peak}, clicks {clicks}");
            Assert.InRange(peak, 2500, 3300);
            Assert.Equal(0, clicks);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
