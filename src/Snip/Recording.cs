using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Snip;

/// Recordings live in %TEMP%\Snip until they're saved or discarded; leftovers are removed when Snip starts.
internal static class RecordingFiles
{
    public static string Directory => Path.Combine(Path.GetTempPath(), "Snip");

    public static string NewPath()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"Snip {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
        var path = Path.Combine(Directory, name + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Directory, $"{name} ({i}).mp4");
        return path;
    }

    public static void CleanUp()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.mp4")) TryDelete(f);
        }
        catch (Exception ex) { Log.Write($"recording cleanup failed: {ex.Message}"); }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Log.Write($"couldn't delete {Path.GetFileName(path)}: {ex.Message}"); }
    }

    public static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static string FormatSize(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}

/// One recording: the recorder, the border around the area and the floating Stop bar.
internal sealed class RecordingController
{
    private readonly ScreenRecorder _recorder;
    private readonly Settings _settings;
    private readonly RecordingBar _bar;
    private readonly List<Form> _border = new();
    private bool _done;

    public event Action? Ended;

    private RecordingController(ScreenRecorder recorder, Settings settings)
    {
        _recorder = recorder;
        _settings = settings;
        var r = recorder.Rect;
        var scale = RecordingUi.ScaleAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        var t = Math.Max(2, (int)Math.Round(2 * scale));
        foreach (var b in new[]
        {
            new Rectangle(r.Left - t, r.Top - t, r.Width + 2 * t, t), new Rectangle(r.Left - t, r.Bottom, r.Width + 2 * t, t),
            new Rectangle(r.Left - t, r.Top, t, r.Height), new Rectangle(r.Right, r.Top, t, r.Height),
        })
            _border.Add(new BorderStrip(b));
        _bar = new RecordingBar(recorder, scale, Stop, Discard);
        _bar.PlaceNear(r);
        recorder.AutoStopped += _ => _bar.BeginInvoke(Stop);
    }

    public static RecordingController? Start(Rectangle area, Settings settings)
    {
        string path;
        ScreenRecorder recorder;
        try
        {
            path = RecordingFiles.NewPath();
            recorder = ScreenRecorder.Start(area, path);
        }
        catch (Exception ex)
        {
            Log.Write($"recording start failed: {ex}");
            MessageBox.Show($"Couldn't start recording.\n\n{ex.Message}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        var c = new RecordingController(recorder, settings);
        foreach (var f in c._border) f.Show();
        c._bar.Show();
        return c;
    }

    public void Stop() => Finish(discard: false);

    private void Discard() => Finish(discard: true);

    private void Finish(bool discard)
    {
        if (_done) return;
        _done = true;
        foreach (var f in _border) f.Close();
        _bar.Close();
        RecordingResult? result = null;
        string? error = null;
        try { result = _recorder.Stop(); }
        catch (Exception ex)
        {
            Log.Write($"recording stop failed: {ex}");
            error = ex.Message;
        }
        Ended?.Invoke();

        if (discard || result == null)
        {
            if (result != null) { result.LastFrame?.Dispose(); RecordingFiles.TryDelete(result.Path); }
            if (error != null) MessageBox.Show($"The recording failed.\n\n{error}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Log.Write($"recording kept: {result.Frames} frames, {result.Duration.TotalSeconds:0.0} s, {result.Bytes} bytes");
        new RecordingResultForm(result, _settings).Show();
    }
}

internal static class RecordingUi
{
    public static readonly Color Back = Color.FromArgb(32, 32, 34);
    public static readonly Color Red = Color.FromArgb(230, 40, 40);

    public static float ScaleAt(Point p)
    {
        var mon = MonitorFromPoint(p, 2);
        return mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96f : 1f;
    }

    // Keeps Snip's own recording chrome out of every screen capture, including the recording itself.
    public static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

/// A thin click-through strip of the border drawn just outside the recorded area.
internal sealed class BorderStrip : Form
{
    public BorderStrip(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = RecordingUi.Red;
        Opacity = 0.9;
        Bounds = bounds;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */ | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecordingUi.ExcludeFromCapture(Handle);
    }
}

/// Floating bar with the elapsed time, Stop and Discard. Never takes focus from the app being recorded.
internal sealed class RecordingBar : Form
{
    private readonly ScreenRecorder _recorder;
    private readonly float _s;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private readonly ToolTip _tips = new();
    private readonly BarButton _stop, _discard;
    private readonly Font _font;

    public RecordingBar(ScreenRecorder recorder, float scale, Action stop, Action discard)
    {
        _recorder = recorder;
        _s = scale;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = RecordingUi.Back;
        _font = new Font("Segoe UI", 13f * _s, GraphicsUnit.Pixel);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        int h = S(36), b = S(28);
        Size = new Size(S(150), h);
        _stop = new BarButton(BarButton.Glyph.Stop) { Bounds = new Rectangle(Width - 2 * b - S(8), (h - b) / 2, b, b) };
        _discard = new BarButton(BarButton.Glyph.Discard) { Bounds = new Rectangle(Width - b - S(4), (h - b) / 2, b, b) };
        _stop.Click += (_, _) => stop();
        _discard.Click += (_, _) => discard();
        Controls.Add(_stop);
        Controls.Add(_discard);
        _tips.SetToolTip(_stop, "Stop recording (Win+Shift+S)");
        _tips.SetToolTip(_discard, "Discard recording");
        _tick.Tick += (_, _) => Invalidate(new Rectangle(0, 0, _stop.Left, Height));
        _tick.Start();
    }

    private int S(float v) => (int)Math.Round(v * _s);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecordingUi.ExcludeFromCapture(Handle);
    }

    public void PlaceNear(Rectangle area)
    {
        var mon = Screen.FromRectangle(area).Bounds;
        int gap = S(8);
        int x = Math.Clamp(area.Right - Width, mon.Left, mon.Right - Width);
        int y = area.Bottom + gap;
        if (y + Height > mon.Bottom) y = area.Top - gap - Height;
        if (y < mon.Top) y = area.Bottom - Height - gap;
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = S(10);
        using (var red = new SolidBrush(RecordingUi.Red)) g.FillEllipse(red, S(12), (Height - d) / 2, d, d);
        var text = RecordingFiles.FormatDuration(_recorder.Elapsed);
        TextRenderer.DrawText(g, text, _font, new Rectangle(S(28), 0, _stop.Left - S(28), Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _tips.Dispose(); _font.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class BarButton : Control
    {
        public enum Glyph { Stop, Discard }
        private readonly Glyph _glyph;
        private bool _hover;

        public BarButton(Glyph glyph)
        {
            _glyph = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_hover ? Color.FromArgb(70, 70, 74) : RecordingUi.Back);
            float cx = Width / 2f, cy = Height / 2f, u = Width * 0.22f;
            if (_glyph == Glyph.Stop)
            {
                using var b = new SolidBrush(Color.FromArgb(235, 235, 235));
                g.FillRectangle(b, cx - u, cy - u, u * 2, u * 2);
            }
            else
            {
                using var p = new Pen(Color.FromArgb(235, 235, 235), Math.Max(1.5f, Width / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(p, cx - u, cy - u, cx + u, cy + u);
                g.DrawLine(p, cx - u, cy + u, cx + u, cy - u);
            }
        }
    }
}

/// Shown after a recording stops: last frame, length and size, and what to do with it.
internal sealed class RecordingResultForm : Form
{
    private readonly RecordingResult _result;
    private readonly Settings _settings;
    private readonly ToolTip _tips = new();
    private bool _copied, _resolved;

    public RecordingResultForm(RecordingResult result, Settings settings)
    {
        _result = result;
        _settings = settings;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Snip recording";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        BackColor = RecordingUi.Back;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        ShowIcon = false;

        var preview = new PictureBox
        {
            Image = result.LastFrame, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Top, Height = 270, BackColor = Color.Black,
        };
        var info = new Label
        {
            Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
            Text = $"{RecordingFiles.FormatDuration(result.Duration)} - {RecordingFiles.FormatSize(result.Bytes)} - MP4",
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4, 6, 4, 6),
        };
        Button Btn(string text, string tip, Action click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, MinimumSize = new Size(84, 30), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 60), ForeColor = Color.White, Margin = new Padding(4, 0, 4, 0),
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 86);
            b.Click += (_, _) => click();
            _tips.SetToolTip(b, tip);
            buttons.Controls.Add(b);
            return b;
        }
        var copy = Btn("Copy", "Copy the video file to paste into chats and documents (Ctrl+C)", Copy);
        Btn("Save as...", "Save as MP4 (Ctrl+S)", SaveAs);
        Btn("Play", "Open in your video player", Play);
        Btn("Discard", "Delete this recording", Discard);
        copy.BackColor = Color.FromArgb(0, 120, 215);
        AcceptButton = copy;

        Controls.Add(buttons);
        if (result.StopReason != null)
            Controls.Add(new Label
            {
                Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(255, 170, 60), Text = result.StopReason,
            });
        Controls.Add(info);
        Controls.Add(preview);
        ClientSize = new Size(480, 270 + 28 + 44 + (result.StopReason != null ? 28 : 0));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) { Copy(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.S) { SaveAs(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void Copy()
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList(new StringCollection { _result.Path });
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
            Clipboard.SetDataObject(data, true, 10, 50);
        }
        catch (Exception ex)
        {
            Log.Write($"recording copy failed: {ex}");
            MessageBox.Show(this, $"Couldn't copy to the clipboard. Another app may be holding it.\n\n{ex.Message}", "Snip",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _copied = true;
        _resolved = true;
        Close();
    }

    private void SaveAs()
    {
        var dir = _settings.LastSaveDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        using var dlg = new SaveFileDialog
        {
            Title = "Save recording",
            InitialDirectory = dir,
            FileName = Path.GetFileName(_result.Path),
            Filter = "MP4 video (*.mp4)|*.mp4",
            DefaultExt = "mp4",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.Copy(_result.Path, dlg.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"recording save failed: {ex}");
            MessageBox.Show(this, $"Couldn't save {dlg.FileName}.\n\n{ex.Message}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.LastSaveDirectory = Path.GetDirectoryName(dlg.FileName);
        _settings.Save();
        if (!_copied) RecordingFiles.TryDelete(_result.Path);
        _resolved = true;
        Close();
    }

    private void Play()
    {
        try { Process.Start(new ProcessStartInfo { FileName = _result.Path, UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the video.\n\n{ex.Message}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Discard()
    {
        RecordingFiles.TryDelete(_result.Path);
        _resolved = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_resolved && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "Discard this recording?", "Snip", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            RecordingFiles.TryDelete(_result.Path);
            _resolved = true;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _result.LastFrame?.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
