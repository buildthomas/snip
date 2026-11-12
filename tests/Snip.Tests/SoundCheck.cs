using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace Snip.Tests;

// Plays a quiet 1 s tone while recording with system sound and checks it reached the file's sound track.
// Opt-in because it makes noise: SNIP_SOUND_CHECK=1 dotnet test --filter SoundCheck
public class SoundCheck(ITestOutputHelper output)
{
    [Fact]
    public void ToneIsRecorded()
    {
        if (Environment.GetEnvironmentVariable("SNIP_SOUND_CHECK") != "1") return;
        var path = Path.Combine(Path.GetTempPath(), $"snip-sound-{Guid.NewGuid():N}.mp4");
        const int rate = 48000;
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write("RIFF"u8); w.Write(36 + rate * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(rate * 2);
        for (int i = 0; i < rate; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 3000));
        wav.Position = 0;
        try
        {
            var rec = ScreenRecorder.Start(new Rectangle(0, 0, 320, 240), path, systemSound: true);
            Thread.Sleep(400);
            using (var player = new System.Media.SoundPlayer(wav)) player.PlaySync();
            Thread.Sleep(600);
            var r = rec.Stop();
            r.LastFrame?.Dispose();
            output.WriteLine($"peak {rec.AudioPeak}, {r.Duration.TotalSeconds:0.0} s");
            Assert.Null(r.StopReason);
            Assert.True(rec.AudioPeak > 500, $"tone not recorded (peak {rec.AudioPeak})");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
