using System.Drawing;
using Xunit;

namespace Snip.Tests;

public class RecordingMathTests
{
    [Fact]
    public void Even_DropsOddRowAndColumn()
    {
        Assert.Equal(new Rectangle(5, 7, 640, 360), RecordingMath.Even(new Rectangle(5, 7, 641, 361)));
    }

    [Theory]
    [InlineData(8, 100, true)]
    [InlineData(1920, 1080, false)]
    [InlineData(3840, 2160, false)]
    [InlineData(7680, 1440, true)]
    [InlineData(4096, 4096, true)]
    public void SizeProblem_RejectsTinyAndHugeAreas(int w, int h, bool rejected)
    {
        Assert.Equal(rejected, RecordingMath.SizeProblem(new Rectangle(0, 0, w, h)) != null);
    }

    [Fact]
    public void ShouldWrite_OnlyWhenDueAndChangedOrKeepAlive()
    {
        Assert.False(RecordingMath.ShouldWrite(10, 33, dirty: true, lastWriteMs: 0));
        Assert.True(RecordingMath.ShouldWrite(33, 33, dirty: true, lastWriteMs: 0));
        Assert.False(RecordingMath.ShouldWrite(500, 33, dirty: false, lastWriteMs: 0));
        Assert.True(RecordingMath.ShouldWrite(1000, 33, dirty: false, lastWriteMs: 0));
    }
}

public class ScreenRecorderTests
{
    [Fact]
    public void RecordsAPlayableMp4()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-test-{Guid.NewGuid():N}.mp4");
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var rec = ScreenRecorder.Start(new Rectangle(area.X, area.Y, 641, 361), path);
            Thread.Sleep(1500);
            var result = rec.Stop();

            Assert.Null(result.StopReason);
            Assert.True(result.Frames >= 2, $"frames: {result.Frames}");
            Assert.InRange(result.Duration.TotalSeconds, 1.2, 3);
            Assert.NotNull(result.LastFrame);
            Assert.Equal(new Size(640, 360), result.LastFrame!.Size);
            var head = File.ReadAllBytes(path).AsSpan(4, 4).ToArray();
            Assert.Equal("ftyp"u8.ToArray(), head);
            result.LastFrame.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecordsWithASoundTrack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snip-test-{Guid.NewGuid():N}.mp4");
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var rec = ScreenRecorder.Start(new Rectangle(area.X, area.Y, 320, 240), path, systemSound: true);
            Thread.Sleep(1500);
            var result = rec.Stop();
            result.LastFrame?.Dispose();

            Assert.Null(result.StopReason);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().IndexOf("mp4a"u8) >= 0, "no AAC track");
            Assert.True(bytes.AsSpan().IndexOf("avc1"u8) >= 0, "no H.264 track");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
