using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace Snip.Tests;

// Size and CPU of 10 s recordings of the primary display, static and with a scrolling window on top.
// Opt-in because it shows a window and takes ~25 s: SNIP_BENCH=1 dotnet test --filter RecordingBenchmark --logger "console;verbosity=detailed"
public class RecordingBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("SNIP_BENCH") != "1") return;
        var area = Screen.PrimaryScreen!.Bounds;
        foreach (var scrolling in new[] { false, true })
        {
            Form? form = null;
            if (scrolling)
            {
                var ready = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    form = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true,
                        Bounds = new Rectangle(area.X + 200, area.Y + 150, 1400, 900) };
                    var list = new ListBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 14) };
                    for (int i = 0; i < 2000; i++) list.Items.Add($"Line {i}: the quick brown fox jumps over the lazy dog");
                    form.Controls.Add(list);
                    var timer = new System.Windows.Forms.Timer { Interval = 50 };
                    timer.Tick += (_, _) => list.TopIndex = (list.TopIndex + 1) % 1900;
                    form.Shown += (_, _) => { form.Activate(); timer.Start(); ready.Set(); };
                    Application.Run(form);
                }) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ready.Wait();
                Thread.Sleep(300);
            }
            var path = Path.Combine(Path.GetTempPath(), $"snip-bench-{Guid.NewGuid():N}.mp4");
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var rec = ScreenRecorder.Start(area, path);
            Thread.Sleep(10_000);
            var r = rec.Stop();
            proc.Refresh();
            var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            output.WriteLine($"{(scrolling ? "scrolling" : "static")} {area.Width}x{area.Height}: {r.Frames} frames, " +
                $"{r.Bytes / 1048576.0:0.00} MB, CPU {cpuMs / 100:0}% of one core");
            r.LastFrame?.Dispose();
            form?.Invoke(() => form.Close());
            File.Delete(path);
        }
    }
}
