using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Snip;

/// Recordings live in %TEMP%\Snip until they're saved or discarded; leftovers are removed when Snip starts.
internal static class RecordingFiles
{
    public static string Directory => Path.Combine(Path.GetTempPath(), "Snip");

    // The name is reserved by creating the file, so two recorders prepared within a second never share one.
    public static string NewPath()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"Snip {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
        for (int i = 1; ; i++)
        {
            var path = Path.Combine(Directory, i == 1 ? name + ".mp4" : $"{name} ({i}).mp4");
            try
            {
                using (new FileStream(path, FileMode.CreateNew)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    public static void CleanUp()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var f in dir.EnumerateFiles("*.mp4")) if (f.Extension == ".mp4") TryDelete(f.FullName);
        }
        catch (Exception ex) { Log.Write($"recording cleanup failed: {ex.Message}"); }
    }

    /// Returns the error, or null when the file is gone.
    public static string? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"couldn't delete {Path.GetFileName(path)}: {ex.Message}");
            return ex.Message;
        }
    }

    public static void Delete(string path, IWin32Window? owner)
    {
        if (TryDelete(path) is string error)
            MessageBox.Show(owner, $"Couldn't delete the recording (is it open in a player?). It stays in {Directory} until Snip next starts.\n\n{error}",
                "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
    private readonly RecordingBorder _border;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    private bool _done;

    public event Action? Ended;

    private RecordingController(ScreenRecorder recorder, Settings settings)
    {
        _recorder = recorder;
        _settings = settings;
        var r = recorder.Rect;
        var scale = RecordingUi.ScaleAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        _border = new RecordingBorder(r, Math.Max(1, (int)Math.Round(scale)));
        var hotkeys = settings.PrintScreenEnabled ? "Win+Shift+S or PrtSc" : "Win+Shift+S";
        _bar = new RecordingBar(recorder, scale, Stop, Discard, settings.RecordSystemSound, settings.RecordMicrophone, hotkeys);
        _bar.PlaceNear(r);
        recorder.AutoStopped += _ => _ui.Post(_ => Stop(), null);
    }

    /// Shows the border and bar right away and begins the (ideally already prepared) recorder.
    public static RecordingController Start(ScreenRecorder recorder, Settings settings)
    {
        var c = new RecordingController(recorder, settings);
        c._border.Show();
        c._bar.Show();
        recorder.WhenStarted(() => c._ui.Post(_ => { if (!c._done) c._bar.MarkStarted(); }, null));
        recorder.WhenFailed(message => c._ui.Post(_ => c.Fail(message), null));
        recorder.Begin();
        return c;
    }

    private void Fail(string message)
    {
        if (_done) return;
        _done = true;
        _border.Close();
        _bar.Close();
        Ended?.Invoke();
        MessageBox.Show($"Couldn't start recording.\n\n{message}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public void Stop() => Finish(discard: false);

    private void Discard() => Finish(discard: true);

    /// Snip is exiting: stop and throw the recording away.
    public void Abort()
    {
        if (_done) return;
        _done = true;
        _border.Close();
        _bar.Close();
        try
        {
            var result = _recorder.Stop();
            result.LastFrame?.Dispose();
        }
        catch (Exception ex) { Log.Write($"recording abort: {ex.Message}"); }
        RecordingFiles.TryDelete(_recorder.OutputPath);
        Ended?.Invoke();
    }

    private void Finish(bool discard)
    {
        if (_done) return;
        _done = true;
        _border.Close();
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
            result?.LastFrame?.Dispose();
            if (result == null) RecordingFiles.TryDelete(_recorder.OutputPath);
            else RecordingFiles.Delete(result.Path, null);
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
    public static readonly Color Accent = Color.FromArgb(0, 150, 255);

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

/// Borderless, topmost and never activated; kept out of every screen capture, including the recording itself.
internal abstract class ChromeForm : Form
{
    protected ChromeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
    }

    protected virtual int ExtraExStyle => 0;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Topmost via the style: WinForms' TopMost calls SetWindowPos without NOACTIVATE, which takes focus.
            cp.ExStyle |= 0x80 /* TOOLWINDOW */ | 0x08 /* TOPMOST */ | 0x08000000 /* NOACTIVATE */ | ExtraExStyle;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecordingUi.ExcludeFromCapture(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        var region = disposing ? Region : null;
        base.Dispose(disposing);
        region?.Dispose();
    }
}

/// One frame-shaped window just outside the recorded area; the hole lets clicks through to the apps inside.
internal sealed class RecordingBorder : ChromeForm
{
    public RecordingBorder(Rectangle area, int thickness)
    {
        BackColor = RecordingUi.Red;
        Bounds = Rectangle.Inflate(area, thickness, thickness);
        var frame = new Region(new Rectangle(Point.Empty, Size));
        frame.Exclude(new Rectangle(thickness, thickness, area.Width, area.Height));
        Region = frame;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SquareCorners(Handle);
    }
}

/// Floating bar with the elapsed time, Stop and Discard. Never takes focus from the app being recorded.
internal sealed class RecordingBar : ChromeForm
{
    private readonly ScreenRecorder _recorder;
    private readonly float _s;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private readonly ToolTip _tips = new();
    private readonly BarButton _stop, _discard;
    private readonly Font _font, _symbols;
    private readonly string _soundIcons;
    private bool _started;

    public RecordingBar(ScreenRecorder recorder, float scale, Action stop, Action discard, bool systemSound, bool microphone, string hotkeys)
    {
        _recorder = recorder;
        _s = scale;
        _soundIcons = (systemSound ? "\uE767" : "") + (microphone ? "\uE720" : "");
        _symbols = new Font("Segoe MDL2 Assets", 13f * _s, GraphicsUnit.Pixel);
        BackColor = RecordingUi.Back;
        _font = new Font("Segoe UI", 13f * _s, GraphicsUnit.Pixel);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        int h = S(36), b = S(28);
        Size = new Size(S(150) + _soundIcons.Length * S(22), h);
        _stop = new BarButton(BarButton.Glyph.Stop) { Bounds = new Rectangle(Width - 2 * b - S(8), (h - b) / 2, b, b) };
        _discard = new BarButton(BarButton.Glyph.Discard) { Bounds = new Rectangle(Width - b - S(4), (h - b) / 2, b, b) };
        _stop.Click += (_, _) => stop();
        _discard.Click += (_, _) => discard();
        Controls.Add(_stop);
        Controls.Add(_discard);
        _tips.SetToolTip(_stop, $"Stop recording ({hotkeys})");
        _tips.SetToolTip(_discard, "Discard recording");
        var sound = _soundIcons.Length == 0 ? "" : $" with {(systemSound && microphone ? "system sound and microphone" : systemSound ? "system sound" : "microphone")}";
        _tips.SetToolTip(this, $"Recording{sound} - drag to move, {hotkeys} to stop");
        Cursor = Cursors.SizeAll;
        _tick.Tick += (_, _) => Invalidate(new Rectangle(0, 0, _stop.Left, Height));
        _tick.Start();
    }

    private int S(float v) => (int)Math.Round(v * _s);

    public void MarkStarted()
    {
        _started = true;
        Invalidate();
    }

    public void PlaceNear(Rectangle area) => Location = Place(area, Screen.FromRectangle(area).WorkingArea, Size, S(8));

    /// Below the area, else above it, else inside its bottom-right corner; always within the work area.
    public static Point Place(Rectangle area, Rectangle work, Size size, int gap)
    {
        int x = area.Right - size.Width, y = area.Bottom + gap;
        if (y + size.Height > work.Bottom) y = area.Top - gap - size.Height;
        if (y < work.Top)
        {
            y = Math.Min(area.Bottom, work.Bottom) - size.Height - gap;
            x = Math.Min(area.Right, work.Right) - size.Width - gap;
        }
        return new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height)));
    }

    private Point? _dragFrom;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _dragFrom = e.Location;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is Point from) Location = new Point(Left + e.X - from.X, Top + e.Y - from.Y);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragFrom = null;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = S(10);
        using (var dot = new SolidBrush(_started ? RecordingUi.Red : Color.FromArgb(120, 120, 124))) g.FillEllipse(dot, S(12), (Height - d) / 2, d, d);
        var text = RecordingFiles.FormatDuration(_recorder.Elapsed);
        var iconsW = _soundIcons.Length * S(22);
        TextRenderer.DrawText(g, text, _font, new Rectangle(S(28), 0, _stop.Left - S(28) - iconsW, Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        for (int i = 0; i < _soundIcons.Length; i++)
            TextRenderer.DrawText(g, _soundIcons[i].ToString(), _symbols, new Rectangle(_stop.Left - iconsW + i * S(22), 0, S(22), Height),
                Color.FromArgb(200, 200, 200), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _tips.Dispose(); _font.Dispose(); _symbols.Dispose(); }
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
    private readonly Font _uiFont = new("Segoe UI", 9.5f);
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
        Font = _uiFont;
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
        Native.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.ForceForeground(this);
    }

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
        if (!_copied) RecordingFiles.Delete(_result.Path, this);
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
        RecordingFiles.Delete(_result.Path, this);
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
            RecordingFiles.Delete(_result.Path, this);
            _resolved = true;
        }
        base.OnFormClosing(e);
    }

    /// Snip is exiting: a recording nobody saved or copied is deleted.
    public void DiscardOnExit()
    {
        if (!_resolved) RecordingFiles.TryDelete(_result.Path);
        _resolved = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _result.LastFrame?.Dispose(); _tips.Dispose(); _uiFont.Dispose(); }
        base.Dispose(disposing);
    }
}
