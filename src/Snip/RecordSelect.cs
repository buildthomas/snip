using System.Drawing.Drawing2D;

namespace Snip;

internal static class SelectionMath
{
    public static Rectangle FromPoints(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    // 0 top-left, 1 top, 2 top-right, 3 right, 4 bottom-right, 5 bottom, 6 bottom-left, 7 left.
    public static Point[] Handles(Rectangle r)
    {
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
            new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy),
        };
    }

    public static Rectangle Resize(Rectangle r, int handle, Point delta)
    {
        int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (handle is 0 or 6 or 7) l += delta.X;
        if (handle is 2 or 3 or 4) rt += delta.X;
        if (handle is 0 or 1 or 2) t += delta.Y;
        if (handle is 4 or 5 or 6) b += delta.Y;
        return FromPoints(new Point(l, t), new Point(rt, b));
    }

    public static Rectangle Move(Rectangle r, Point delta, Rectangle bounds)
    {
        var x = Math.Clamp(r.X + delta.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - r.Width));
        var y = Math.Clamp(r.Y + delta.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }
}

/// Win+Shift+R: pick what to record over the live screen. The screen keeps moving under a light dim; drag an area,
/// click a window, or Ctrl+A for the display, then Record (Enter). Esc or right-click cancels.
/// This window takes the input and is almost fully transparent; the dim, the selection frame and the bar are
/// separate windows, all kept out of screen captures.
internal sealed class RecordSelectOverlay : Form
{
    private enum Drag { None, Pending, New, Move, Resize }

    private readonly Rectangle _virtual;
    private readonly List<Rectangle> _windows;
    private readonly Settings _settings;
    private readonly DimLayer _dim;
    private readonly SelectionFrame _frame;
    private readonly RecordSetupBar _bar;
    private Rectangle? _selection, _hover;
    private Drag _drag;
    private Point _down;
    private Rectangle _start;
    private int _handle = -1;

    /// The pointer settled on an area; a good moment to get the recorder ready.
    public event Action<Rectangle>? SelectionSettled;
    public event Action<Rectangle>? RecordRequested;

    public RecordSelectOverlay(Rectangle virtualScreen, List<Rectangle> windows, Settings settings)
    {
        _virtual = virtualScreen;
        _windows = windows;
        _settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Text = "Snip record";
        BackColor = Color.Black;
        Opacity = 0.01;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        _dim = new DimLayer(virtualScreen);
        _frame = new SelectionFrame();
        _bar = new RecordSetupBar(settings, Record, Close, () => { if (_selection is Rectangle s) SelectionSettled?.Invoke(s); });
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /* TOOLWINDOW */;
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

    protected override void OnShown(EventArgs e)
    {
        _dim.Show();
        base.OnShown(e);
        Native.ForceForeground(this);
        UpdateHover(Cursor.Position);
    }

    private Point Screen(Point client) => new(client.X + _virtual.X, client.Y + _virtual.Y);

    private int Px(float v) => (int)Math.Round(v * RecordingUi.ScaleAt(Cursor.Position));

    // ---------- Mouse ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = Screen(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (_drag is Drag.New or Drag.Move or Drag.Resize) { _selection = _start.IsEmpty ? null : _start; _drag = Drag.None; if (_selection != null) Settle(); else ShowArea(null); }
            else Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _down = p;
        _start = _selection ?? Rectangle.Empty;
        _handle = _selection is Rectangle s ? HitHandle(s, p) : -1;
        _drag = _handle >= 0 ? Drag.Resize : _selection is Rectangle sel && sel.Contains(p) ? Drag.Move : Drag.Pending;
        _bar.Hide();
    }


    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = Screen(e.Location);
        var delta = new Point(p.X - _down.X, p.Y - _down.Y);
        switch (_drag)
        {
            case Drag.None:
                if (_selection == null) UpdateHover(p);
                else Cursor = CursorFor(p);
                return;
            case Drag.Pending:
                if (Math.Abs(delta.X) + Math.Abs(delta.Y) < Px(4)) return;
                _drag = Drag.New;
                goto case Drag.New;
            case Drag.New:
                _selection = Clamp(SelectionMath.FromPoints(_down, p));
                break;
            case Drag.Move:
                _selection = SelectionMath.Move(_start, delta, _virtual);
                break;
            case Drag.Resize:
                _selection = Clamp(SelectionMath.Resize(_start, _handle, delta));
                break;
        }
        ShowArea(_selection);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _drag == Drag.None) return;
        var tiny = _selection is Rectangle s && (s.Width < Px(8) || s.Height < Px(8));
        if (_drag == Drag.Pending || _drag == Drag.New && tiny) _selection = WindowFinder.Hit(_windows, _down) ?? MonitorAt(_down);
        else if (tiny) _selection = _start;
        _drag = Drag.None;
        Settle();
    }

    private void Settle()
    {
        if (_selection is not Rectangle s) return;
        ShowArea(s);
        _bar.ShowNear(s);
        Cursor = CursorFor(Cursor.Position);
        SelectionSettled?.Invoke(s);
    }

    private void UpdateHover(Point p)
    {
        var w = WindowFinder.Hit(_windows, p);
        if (w == _hover) return;
        _hover = w;
        ShowArea(w);
    }

    private void ShowArea(Rectangle? r)
    {
        _dim.SetHole(r);
        if (r is Rectangle rect) _frame.ShowAround(rect, _selection != null && _drag == Drag.None);
        else _frame.Hide();
    }

    private Rectangle Clamp(Rectangle r) => Rectangle.Intersect(r, _virtual);

    private static Rectangle MonitorAt(Point p) => System.Windows.Forms.Screen.FromPoint(p).Bounds;

    private int HitHandle(Rectangle s, Point p)
    {
        var pts = SelectionMath.Handles(s);
        var hit = Px(7);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= hit && Math.Abs(p.Y - pts[i].Y) <= hit) return i;
        return -1;
    }

    private Cursor CursorFor(Point p)
    {
        if (_selection is not Rectangle s) return Cursors.Cross;
        return HitHandle(s, p) switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            3 or 7 => Cursors.SizeWE,
            _ => s.Contains(p) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    // ---------- Keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var ctrl = (keyData & Keys.Control) != 0;
        if (key == Keys.Escape) { Close(); return true; }
        if (key == Keys.Enter) { Record(); return true; }
        if (ctrl && key == Keys.A) { _selection = MonitorAt(Cursor.Position); Settle(); return true; }
        if (!ctrl && key == Keys.S) { _bar.ToggleSystemSound(); return true; }
        if (!ctrl && key == Keys.M) { _bar.ToggleMicrophone(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Record()
    {
        var area = _selection ?? _hover;
        if (area is not Rectangle a) return;
        RecordRequested?.Invoke(a);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bar.Close();
        _frame.Close();
        _dim.Close();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bar.Dispose(); _frame.Dispose(); _dim.Dispose(); }
        base.Dispose(disposing);
    }

    /// Dims the screen except the hole; clicks pass straight through it.
    private sealed class DimLayer : Form
    {
        private readonly Rectangle _bounds;

        public DimLayer(Rectangle bounds)
        {
            _bounds = bounds;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Opacity = 0.35;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */ | 0x08000000 /* NOACTIVATE */;
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

        public void SetHole(Rectangle? hole)
        {
            var old = Region;
            if (hole is Rectangle h)
            {
                var region = new Region(new Rectangle(Point.Empty, _bounds.Size));
                region.Exclude(new Rectangle(h.X - _bounds.X, h.Y - _bounds.Y, h.Width, h.Height));
                Region = region;
            }
            else Region = null;
            old?.Dispose();
        }
    }

    /// The selection's border, handles and size label; click-through, opaque, square-cornered.
    private sealed class SelectionFrame : Form
    {
        private static readonly Color Accent = Color.FromArgb(0, 150, 255);
        private Rectangle _sel;
        private bool _handles;
        private Font? _font;
        private float _scale = 1;

        public SelectionFrame()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x20 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var square = 1;
            RecordingUi.DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int));
            RecordingUi.ExcludeFromCapture(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        private int S(float v) => (int)Math.Round(v * _scale);

        private Rectangle LabelRect(Rectangle bounds)
        {
            var size = TextRenderer.MeasureText(Label, _font);
            var r = new Rectangle(_sel.X - bounds.X, _sel.Y - bounds.Y - size.Height - S(6), size.Width + S(10), size.Height + S(2));
            return r;
        }

        private string Label => $"{_sel.Width} × {_sel.Height}";

        public void ShowAround(Rectangle sel, bool handles)
        {
            _sel = sel;
            _handles = handles;
            var scale = RecordingUi.ScaleAt(new Point(sel.X + sel.Width / 2, sel.Y + sel.Height / 2));
            if (_font == null || Math.Abs(scale - _scale) > 0.01f)
            {
                _scale = scale;
                _font?.Dispose();
                _font = new Font("Segoe UI", 12f * _scale, GraphicsUnit.Pixel);
            }
            int t = Math.Max(1, S(1)), hs = S(4), labelH = TextRenderer.MeasureText(Label, _font).Height + S(8);
            var bounds = Rectangle.FromLTRB(sel.Left - hs - t, sel.Top - Math.Max(hs + t, labelH), sel.Right + hs + t, sel.Bottom + hs + t);
            var local = new Rectangle(sel.X - bounds.X, sel.Y - bounds.Y, sel.Width, sel.Height);
            var region = new Region(Rectangle.Inflate(local, t, t));
            region.Exclude(local);
            if (handles)
                foreach (var p in SelectionMath.Handles(local))
                    region.Union(new Rectangle(p.X - hs, p.Y - hs, hs * 2, hs * 2));
            region.Union(LabelRect(bounds));
            var old = Region;
            Bounds = bounds;
            Region = region;
            old?.Dispose();
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Accent);
            var local = new Rectangle(_sel.X - Left, _sel.Y - Top, _sel.Width, _sel.Height);
            if (_handles)
            {
                int hs = S(4);
                foreach (var p in SelectionMath.Handles(local))
                    g.FillRectangle(Brushes.White, p.X - hs + 1, p.Y - hs + 1, hs * 2 - 2, hs * 2 - 2);
            }
            var lr = LabelRect(Bounds);
            using (var b = new SolidBrush(RecordingUi.Back)) g.FillRectangle(b, lr);
            TextRenderer.DrawText(g, Label, _font, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font?.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// Sound and microphone toggles, Record and Cancel, shown next to the selection.
internal sealed class RecordSetupBar : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 150, 255);
    private readonly Settings _settings;
    private readonly Action _record, _cancel, _changed;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private readonly ToolButton _sound = new() { Kind = ButtonKind.SystemSound, Tip = "System sound on/off (S)" };
    private readonly ToolButton _mic = new() { Kind = ButtonKind.Microphone, Tip = "Microphone on/off (M)" };
    private readonly ToolButton _close = new() { Kind = ButtonKind.Close, Tip = "Cancel (Esc)" };
    private Rectangle _recordRect;
    private object? _hover;
    private string? _shownTip;
    private float _s = 1;
    private Font? _font;

    public RecordSetupBar(Settings settings, Action record, Action cancel, Action changed)
    {
        _settings = settings;
        _record = record;
        _cancel = cancel;
        _changed = changed;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = RecordingUi.Back;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

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

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    private int S(float v) => (int)Math.Round(v * _s);

    public void ShowNear(Rectangle area)
    {
        _s = RecordingUi.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
        _font?.Dispose();
        _font = new Font("Segoe UI", 13f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
        int b = S(30), pad = S(3);
        var recordW = TextRenderer.MeasureText("Record", _font).Width + S(34);
        _sound.Rect = new Rectangle(pad, pad, b, b);
        _mic.Rect = new Rectangle(pad + b, pad, b, b);
        _recordRect = new Rectangle(pad + 2 * b + S(4), pad, recordW, b);
        _close.Rect = new Rectangle(_recordRect.Right + S(4), pad, b, b);
        Size = new Size(_close.Rect.Right + pad, b + 2 * pad);
        Location = RecordingBar.Place(area, System.Windows.Forms.Screen.FromRectangle(area).WorkingArea, Size, S(8));
        if (!Visible) Show();
        Invalidate();
    }

    public void ToggleSystemSound()
    {
        _settings.RecordSystemSound = !_settings.RecordSystemSound;
        Invalidate();
        _changed();
    }

    public void ToggleMicrophone()
    {
        _settings.RecordMicrophone = !_settings.RecordMicrophone;
        Invalidate();
        _changed();
    }

    private object? HitTest(Point p) =>
        _sound.Rect.Contains(p) ? _sound : _mic.Rect.Contains(p) ? _mic : _close.Rect.Contains(p) ? _close : _recordRect.Contains(p) ? "record" : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        var tip = hit switch { ToolButton t => t.Tip, "record" => "Start recording (Enter)", _ => null };
        if (tip != _shownTip)
        {
            _shownTip = tip;
            if (tip == null) _tips.Hide(this);
            else _tips.Show(tip, this, e.X, Height + S(4), 3000);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _shownTip = null;
        _tips.Hide(this);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case ToolButton t when t == _sound: ToggleSystemSound(); break;
            case ToolButton t when t == _mic: ToggleMicrophone(); break;
            case ToolButton t when t == _close: _cancel(); break;
            case "record": _record(); break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(RecordingUi.Back);
        foreach (var (b, on) in new[] { (_sound, _settings.RecordSystemSound), (_mic, _settings.RecordMicrophone), (_close, false) })
        {
            if (on || _hover == b)
            {
                using var fill = new SolidBrush(on ? Accent : Color.FromArgb(70, 255, 255, 255));
                g.FillRectangle(fill, Rectangle.Inflate(b.Rect, -S(1), -S(1)));
            }
            Icons.Draw(g, b, Color.White, enabled: true);
        }
        using (var rb = new SolidBrush(_hover is "record" ? Color.FromArgb(232, 48, 48) : Color.FromArgb(205, 38, 38)))
            g.FillRectangle(rb, _recordRect);
        int d = S(10);
        g.FillEllipse(Brushes.White, _recordRect.X + S(10), _recordRect.Y + (_recordRect.Height - d) / 2, d, d);
        TextRenderer.DrawText(g, "Record", _font, Rectangle.FromLTRB(_recordRect.X + S(26), _recordRect.Y, _recordRect.Right, _recordRect.Bottom),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tips.Dispose(); _font?.Dispose(); }
        base.Dispose(disposing);
    }
}
