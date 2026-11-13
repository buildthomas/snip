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
    public void Frames_SitOnAnExact30FpsGrid()
    {
        Assert.Equal(0, RecordingMath.FrameTime100ns(0));
        Assert.Equal(333_333, RecordingMath.FrameTime100ns(1));
        Assert.Equal(10_000_000, RecordingMath.FrameTime100ns(30));
        Assert.Equal(600_000_000, RecordingMath.FrameTime100ns(1800));
        Assert.Equal(33, RecordingMath.FrameDueMs(1));
        Assert.Equal(1000, RecordingMath.FrameDueMs(30));
    }
}

[Collection("screen")]
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

[Collection("screen")]
public class RecordingFilesTests
{
    [Fact]
    public void NewPath_ReservesDistinctNamesAndTheRecorderWritesIntoThem()
    {
        var a = RecordingFiles.NewPath();
        var b = RecordingFiles.NewPath();
        try
        {
            Assert.NotEqual(a, b);
            Assert.True(File.Exists(a) && File.Exists(b));
            var area = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var rec = ScreenRecorder.Start(new Rectangle(area.X, area.Y, 320, 240), a);
            Thread.Sleep(300);
            rec.Stop().LastFrame?.Dispose();
            Assert.True(new FileInfo(a).Length > 0);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void CancelledPreparation_LeavesNoFile()
    {
        var path = RecordingFiles.NewPath();
        var area = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var rec = ScreenRecorder.Prepare(new Rectangle(area.X, area.Y, 320, 240), path, false, false);
        rec.Cancel();
        var deadline = DateTime.Now.AddSeconds(5);
        while (File.Exists(path) && DateTime.Now < deadline) Thread.Sleep(50);
        Assert.False(File.Exists(path));
    }
}
