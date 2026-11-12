using Xunit;

namespace Snip.Tests;

public class AudioMixerTests
{
    private static float[] Stereo(int frames, float v) => Enumerable.Repeat(v, frames * 2).ToArray();

    [Fact]
    public void GapsBecomeSilenceAndPacketsLandAtTheirTime()
    {
        var m = new AudioMixer();
        m.Add(Stereo(10, 0.5f), firstFrame: 100);
        var pcm = m.TakeUntil(120);
        Assert.Equal(240, pcm.Length);
        Assert.All(pcm.Take(200), s => Assert.Equal(0, (int)s));
        Assert.All(pcm.Skip(200).Take(20), s => Assert.Equal(16384, (int)s));
        Assert.All(pcm.Skip(220), s => Assert.Equal(0, (int)s));
        Assert.Equal(120, m.Written);
    }

    [Fact]
    public void SourcesAreSummedAndClipped()
    {
        var m = new AudioMixer();
        m.Add(Stereo(4, 0.25f), 0);
        m.Add(Stereo(4, 0.25f), 0);
        m.Add(Stereo(4, 0.9f), 2);
        var pcm = m.TakeUntil(4);
        Assert.Equal(16384, (int)pcm[0]);
        Assert.Equal(32767, (int)pcm[4]);
    }

    [Theory]
    [InlineData(10_000_000L)]
    [InlineData(3_579_545L)]
    [InlineData(24_000_000L)]
    public void FrameAt_StaysExactAfterDaysOfUptime(long ticksPerSecond)
    {
        var start = ticksPerSecond * 60 * 60 * 24 * 30;
        var startIn100ns = (ulong)(60L * 60 * 24 * 30 * 10_000_000);
        Assert.Equal(0, AudioCapture.FrameAt(startIn100ns, start, ticksPerSecond));
        Assert.Equal(48_000, AudioCapture.FrameAt(startIn100ns + 10_000_000, start, ticksPerSecond));
        Assert.Equal(-4_800, AudioCapture.FrameAt(startIn100ns - 1_000_000, start, ticksPerSecond));
    }

    [Fact]
    public void LateAudioIsDroppedNotShifted()
    {
        var m = new AudioMixer();
        m.TakeUntil(50);
        m.Add(Stereo(20, 0.5f), firstFrame: 40);
        var pcm = m.TakeUntil(70);
        Assert.Equal(40, pcm.Length);
        Assert.All(pcm.Take(20), s => Assert.Equal(16384, (int)s));
        Assert.All(pcm.Skip(20), s => Assert.Equal(0, (int)s));
    }
}
