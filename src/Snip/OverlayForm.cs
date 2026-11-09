using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static Snip.Native;

namespace Snip;

// Full-virtual-screen window showing the frozen capture. Client coordinates equal capture-bitmap pixels.
internal sealed class OverlayForm : Form
{
    private enum Drag { None, Pending, NewSelection, Move, Resize, Draw }

    private static readonly Color Accent = Color.FromArgb(0, 150, 255);
    private static readonly Color BarBack = Color.FromArgb(240, 32, 32, 34);

    private readonly Bitmap _capture;
    private readonly Bitmap _dimmed;
    private readonly List<Rectangle> _windows;
    private readonly Settings _settings;
    private readonly float _scale;

    private Rectangle? _selection;
    private Rectangle? _hoverWindow;
    private Drag _drag;
    private Point _dragStart;
    private Rectangle _dragOrigSel;
    private int _resizeHandle;
    private Rectangle? _selectionBeforeDrag;

    private readonly List<Annotation> _annotations = new();
    private readonly Stack<Annotation> _redo = new();
    private Annotation? _current;
    private RectangleF _currentBounds;
    private TextNote? _editing;

    private Tool _tool = Tool.None;
    private Color _color;
    private int _size;

    private readonly List<ToolButton> _toolButtons = new();
    private readonly List<ToolButton> _actionButtons = new();
    private Rectangle _toolBar, _actionBar, _paletteRect, _tipRect;
    private bool _paletteOpen;
    private ToolButton? _hoverButton;
    private Rectangle _lastChrome;

    private string? _bubble;
    private Rectangle _bubbleRect;
    private System.Windows.Forms.Timer? _bubbleTimer;

    public const int MinSize = 1, MaxSize = 20;

    public OverlayForm(Bitmap capture, Rectangle virtualScreen, List<Rectangle> windows, Settings settings)
    {
        _capture = capture;
        _settings = settings;
        _color = Color.FromArgb(settings.ColorArgb);
        _size = Math.Clamp(settings.Size, MinSize, MaxSize);
        _windows = windows.Select(w => { w.Offset(-virtualScreen.X, -virtualScreen.Y); return w; }).ToList();

        _dimmed = ScreenCapture.Dim(capture, 136);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        Text = "Snip";
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _scale = Math.Max(1f, DeviceDpi / 96f);
        BuildButtons();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Keep the window spanning all monitors even when they have different DPIs.
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ForceForeground();
        var p = PointToClient(MousePosition);
        UpdateHover(p);
    }

    private void ForceForeground()
    {
        if (GetForegroundWindow() == Handle) return;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var me = GetCurrentThreadId();
        // Attaching to a hung app's input queue could stall this window too.
        var attached = fgThread != 0 && fgThread != me && !IsHungAppWindow(fg) && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(Handle);
        Activate();
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    // ---------- Layout ----------

    private int S(float v) => (int)Math.Round(v * _scale);

    private void BuildButtons()
    {
        void T(Tool t, string tip) => _toolButtons.Add(new ToolButton { Kind = ButtonKind.Tool, Tool = t, Tip = tip });
        T(Tool.Pen, "Pen (P)");
        T(Tool.Line, "Line (L)");
        T(Tool.Arrow, "Arrow (A)");
        T(Tool.Rectangle, "Rectangle (R)");
        T(Tool.Ellipse, "Ellipse (E)");
        T(Tool.Highlighter, "Highlighter (H)");
        T(Tool.Text, "Text (T)");
        T(Tool.Number, "Numbered step (N)");
        T(Tool.Redact, "Redact box (B)");
        _toolButtons.Add(new ToolButton { Kind = ButtonKind.Color, Tip = "Color (scroll wheel = size)" });
        _toolButtons.Add(new ToolButton { Kind = ButtonKind.Undo, Tip = "Undo (Ctrl+Z)" });
        _toolButtons.Add(new ToolButton { Kind = ButtonKind.Redo, Tip = "Redo (Ctrl+Y)" });
        _actionButtons.Add(new ToolButton { Kind = ButtonKind.Copy, Tip = "Copy (Ctrl+C)" });
        _actionButtons.Add(new ToolButton { Kind = ButtonKind.Save, Tip = "Save as (Ctrl+S)" });
        _actionButtons.Add(new ToolButton { Kind = ButtonKind.Close, Tip = "Close (Esc)" });
    }

    private Rectangle MonitorOf(Rectangle sel)
    {
        var center = new Point(sel.X + sel.Width / 2 + Left, sel.Y + sel.Height / 2 + Top);
        var b = Screen.FromPoint(center).Bounds;
        b.Offset(-Left, -Top);
        return b;
    }

    private void LayoutChrome(Rectangle sel)
    {
        var mon = MonitorOf(sel);
        int b = S(30), gap = S(6), pad = S(3);

        var toolH = _toolButtons.Count * b + pad * 2 + S(8);
        var toolW = b + pad * 2;
        int tx = sel.Right + gap;
        if (tx + toolW > mon.Right) tx = sel.Left - gap - toolW;
        if (tx < mon.Left) tx = sel.Right - toolW - gap;
        int ty = Math.Clamp(sel.Top, mon.Top, Math.Max(mon.Top, mon.Bottom - toolH));
        _toolBar = new Rectangle(tx, ty, toolW, toolH);
        int y = ty + pad;
        for (int i = 0; i < _toolButtons.Count; i++)
        {
            if (_toolButtons[i].Kind == ButtonKind.Color) y += S(4);
            if (_toolButtons[i].Kind == ButtonKind.Undo) y += S(4);
            _toolButtons[i].Rect = new Rectangle(tx + pad, y, b, b);
            y += b;
        }

        var actW = _actionButtons.Count * b + pad * 2;
        var actH = b + pad * 2;
        int ax = Math.Clamp(sel.Right - actW, mon.Left, Math.Max(mon.Left, mon.Right - actW));
        int ay = sel.Bottom + gap;
        if (ay + actH > mon.Bottom) ay = sel.Top - gap - actH;
        if (ay < mon.Top) ay = sel.Bottom - actH - gap;
        _actionBar = new Rectangle(ax, ay, actW, actH);
        for (int i = 0; i < _actionButtons.Count; i++)
            _actionButtons[i].Rect = new Rectangle(ax + pad + i * b, ay + pad, b, b);

        var colorBtn = _toolButtons.First(x => x.Kind == ButtonKind.Color).Rect;
        int sw = S(24), rows = (Palette.Colors.Length + Palette.Columns - 1) / Palette.Columns;
        var pw = Palette.Columns * sw + pad * 2;
        var ph = rows * sw + pad * 2;
        int px = _toolBar.Left - gap - pw;
        if (px < mon.Left) px = _toolBar.Right + gap;
        int py = Math.Clamp(colorBtn.Top, mon.Top, Math.Max(mon.Top, mon.Bottom - ph));
        _paletteRect = new Rectangle(px, py, pw, ph);
    }

    private Rectangle SwatchRect(int i)
    {
        int sw = S(24), pad = S(3);
        return new Rectangle(_paletteRect.X + pad + (i % Palette.Columns) * sw, _paletteRect.Y + pad + (i / Palette.Columns) * sw, sw, sw);
    }

    private Rectangle LabelRect(Rectangle sel)
    {
        var size = TextRenderer.MeasureText(LabelText(sel), LabelFont);
        var r = new Rectangle(sel.X, sel.Y - size.Height - S(6), size.Width + S(10), size.Height + S(2));
        if (r.Y < MonitorOf(sel).Top) r.Y = sel.Y + S(4);
        return r;
    }

    private static string LabelText(Rectangle sel) => $"{sel.Width} × {sel.Height}";

    private Font? _labelFont;
    private Font LabelFont => _labelFont ??= new Font("Segoe UI", 12f * _scale, GraphicsUnit.Pixel);

    private Rectangle ChromeRect()
    {
        if (_selection is not Rectangle sel) return Rectangle.Empty;
        var r = Rectangle.Inflate(sel, S(8), S(8));
        r = Rectangle.Union(r, LabelRect(sel));
        r = Rectangle.Union(r, Rectangle.Inflate(_toolBar, 2, 2));
        r = Rectangle.Union(r, Rectangle.Inflate(_actionBar, 2, 2));
        if (_paletteOpen) r = Rectangle.Union(r, Rectangle.Inflate(_paletteRect, 2, 2));
        if (!_tipRect.IsEmpty) r = Rectangle.Union(r, _tipRect);
        return r;
    }

    private void InvalidateChrome()
    {
        if (_selection is Rectangle sel) LayoutChrome(sel);
        UpdateTipRect();
        var now = ChromeRect();
        var dirty = _lastChrome.IsEmpty ? now : now.IsEmpty ? _lastChrome : Rectangle.Union(_lastChrome, now);
        _lastChrome = now;
        if (!dirty.IsEmpty) Invalidate(dirty);
    }

    private void UpdateTipRect()
    {
        if (_hoverButton == null) { _tipRect = Rectangle.Empty; return; }
        var size = TextRenderer.MeasureText(_hoverButton.Tip, LabelFont);
        var w = size.Width + S(12);
        var h = size.Height + S(6);
        var b = _hoverButton.Rect;
        var mon = _selection is Rectangle sel ? MonitorOf(sel) : ClientRectangle;
        Rectangle r;
        if (_toolButtons.Contains(_hoverButton))
        {
            r = new Rectangle(_toolBar.Right + S(6), b.Y + (b.Height - h) / 2, w, h);
            if (r.Right > mon.Right) r.X = _toolBar.Left - S(6) - w;
        }
        else
        {
            r = new Rectangle(b.X + (b.Width - w) / 2, _actionBar.Bottom + S(4), w, h);
            if (r.Bottom > mon.Bottom) r.Y = _actionBar.Top - S(4) - h;
        }
        _tipRect = r;
    }

    // ---------- Painting ----------

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, e.ClipRectangle);

    internal void Render(Graphics g, Rectangle clip)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.DrawImage(_dimmed, clip, clip, GraphicsUnit.Pixel);

        var bright = _selection ?? (_drag is Drag.None or Drag.Pending ? _hoverWindow : null);
        if (bright is Rectangle br)
        {
            var r = Rectangle.Intersect(br, clip);
            if (!r.IsEmpty) g.DrawImage(_capture, r, r, GraphicsUnit.Pixel);
        }
        g.CompositingMode = CompositingMode.SourceOver;

        if (_selection is Rectangle sel)
        {
            var state = g.Save();
            g.SetClip(sel);
            Renderer.Prepare(g);
            foreach (var a in _annotations) a.Draw(g);
            _current?.Draw(g);
            if (_editing != null) DrawEditing(g, _editing);
            g.Restore(state);
            DrawChrome(g, sel);
        }
        else if (_hoverWindow is Rectangle hw && _drag is Drag.None or Drag.Pending)
        {
            using var pen = new Pen(Accent, S(2));
            g.DrawRectangle(pen, hw.X + 1, hw.Y + 1, hw.Width - 2, hw.Height - 2);
        }

        if (_bubble != null) DrawBubble(g);
    }

    private void DrawEditing(Graphics g, TextNote note)
    {
        note.Draw(g);
        var size = note.Measure(g);
        using var font = note.MakeFont();
        var lines = note.Text.Split('\n');
        var last = lines[^1];
        var lastW = last.Length == 0 ? 0 : g.MeasureString(last, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        var lineH = font.GetHeight(g);
        var caretX = note.Origin.X + lastW + 1;
        var caretY = note.Origin.Y + lineH * (lines.Length - 1);
        using (var caret = new Pen(note.Color, Math.Max(1.5f, note.Size / 3)))
            g.DrawLine(caret, caretX, caretY, caretX, caretY + lineH);
        using var box = new Pen(Color.FromArgb(160, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(box, note.Origin.X - 4, note.Origin.Y - 3, size.Width + 10, size.Height + 6);
    }

    private void DrawChrome(Graphics g, Rectangle sel)
    {
        g.SmoothingMode = SmoothingMode.None;
        using (var border = new Pen(Accent, 1))
            g.DrawRectangle(border, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
        var hs = S(7);
        using (var hb = new SolidBrush(Color.White))
        using (var hp = new Pen(Accent, 1))
        {
            foreach (var h in HandlePoints(sel))
            {
                var r = new Rectangle(h.X - hs / 2, h.Y - hs / 2, hs, hs);
                g.FillRectangle(hb, r);
                g.DrawRectangle(hp, r);
            }
        }

        if (_drag != Drag.Draw)
        {
            var lr = LabelRect(sel);
            using (var lb = new SolidBrush(BarBack)) g.FillRectangle(lb, lr);
            TextRenderer.DrawText(g, LabelText(sel), LabelFont, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (_drag is Drag.NewSelection or Drag.Resize) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawBar(g, _toolBar, _toolButtons);
        DrawBar(g, _actionBar, _actionButtons);

        if (_paletteOpen)
        {
            using (var pb = new SolidBrush(BarBack)) FillRound(g, pb, _paletteRect, S(5));
            for (int i = 0; i < Palette.Colors.Length; i++)
            {
                var r = Rectangle.Inflate(SwatchRect(i), -S(3), -S(3));
                using var b = new SolidBrush(Palette.Colors[i]);
                g.FillRectangle(b, r);
                var selected = Palette.Colors[i].ToArgb() == _color.ToArgb();
                using var p = new Pen(selected ? Color.White : Color.FromArgb(90, 255, 255, 255), selected ? S(2) : 1);
                g.DrawRectangle(p, r);
            }
        }

        if (_hoverButton != null && !_tipRect.IsEmpty)
        {
            using (var tb = new SolidBrush(BarBack)) FillRound(g, tb, _tipRect, S(4));
            TextRenderer.DrawText(g, _hoverButton.Tip, LabelFont, _tipRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void DrawBar(Graphics g, Rectangle bar, List<ToolButton> buttons)
    {
        using (var bb = new SolidBrush(BarBack)) FillRound(g, bb, bar, S(5));
        foreach (var b in buttons)
        {
            var active = b.Kind == ButtonKind.Tool && b.Tool == _tool || b.Kind == ButtonKind.Color && _paletteOpen;
            if (active || b == _hoverButton)
            {
                using var hb = new SolidBrush(active ? Accent : Color.FromArgb(70, 255, 255, 255));
                FillRound(g, hb, Rectangle.Inflate(b.Rect, -S(1), -S(1)), S(4));
            }
            var enabled = b.Kind switch
            {
                ButtonKind.Undo => _annotations.Count > 0 || _editing != null,
                ButtonKind.Redo => _redo.Count > 0,
                _ => true,
            };
            Icons.Draw(g, b, _color, enabled);
        }
    }

    private void DrawBubble(Graphics g)
    {
        using (var b = new SolidBrush(BarBack)) FillRound(g, b, _bubbleRect, S(4));
        var dot = Math.Min(_size * 2 + 2, _bubbleRect.Height - S(6));
        using (var db = new SolidBrush(_color))
            g.FillEllipse(db, _bubbleRect.X + S(6), _bubbleRect.Y + (_bubbleRect.Height - dot) / 2f, dot, dot);
        var textRect = Rectangle.FromLTRB(_bubbleRect.X + S(10) + dot, _bubbleRect.Y, _bubbleRect.Right, _bubbleRect.Bottom);
        TextRenderer.DrawText(g, _bubble, LabelFont, textRect, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private static void FillRound(Graphics g, Brush b, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    private static Point[] HandlePoints(Rectangle r)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new[]
        {
            new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
            new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy),
        };
    }

    // Puts the overlay in a given state without input, for rendering the README screenshot.
    internal void SetDemoState(Rectangle selection, IEnumerable<Annotation> annotations, Tool tool, bool hoverActiveTool)
    {
        _selection = selection;
        _annotations.Clear();
        _annotations.AddRange(annotations);
        _tool = tool;
        LayoutChrome(selection);
        _hoverButton = hoverActiveTool ? _toolButtons.First(b => b.Kind == ButtonKind.Tool && b.Tool == tool) : null;
        UpdateTipRect();
    }

    // ---------- Mouse ----------

    private int HitHandle(Point p)
    {
        if (_selection is not Rectangle sel) return -1;
        var pts = HandlePoints(sel);
        var hit = S(6);
        for (int i = 0; i < pts.Length; i++)
            if (Math.Abs(p.X - pts[i].X) <= hit && Math.Abs(p.Y - pts[i].Y) <= hit) return i;
        return -1;
    }

    private ToolButton? HitButton(Point p)
    {
        if (_selection == null) return null;
        foreach (var b in _toolButtons) if (b.Rect.Contains(p)) return b;
        foreach (var b in _actionButtons) if (b.Rect.Contains(p)) return b;
        return null;
    }

    private bool OnChrome(Point p) =>
        _selection != null && (_toolBar.Contains(p) || _actionBar.Contains(p) || _paletteOpen && _paletteRect.Contains(p));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = e.Location;

        if (e.Button == MouseButtons.Right)
        {
            if (_drag != Drag.None) CancelDrag();
            else if (_editing != null) CommitText();
            else if (_selection == null) Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_paletteOpen)
        {
            if (_paletteRect.Contains(p))
            {
                for (int i = 0; i < Palette.Colors.Length; i++)
                    if (SwatchRect(i).Contains(p)) SetColor(Palette.Colors[i]);
                _paletteOpen = false;
                InvalidateChrome();
                return;
            }
            _paletteOpen = false;
            InvalidateChrome();
        }

        var button = HitButton(p);
        if (button != null) { Press(button); return; }
        if (OnChrome(p)) return;

        if (_editing != null)
        {
            var wasOn = _editing.Bounds.Contains(p);
            CommitText();
            if (wasOn) return;
        }

        _dragStart = p;
        var handle = HitHandle(p);
        if (_selection is Rectangle sel)
        {
            if (handle >= 0)
            {
                _drag = Drag.Resize;
                _resizeHandle = handle;
                _dragOrigSel = sel;
                InvalidateChrome();
                return;
            }
            if (sel.Contains(p))
            {
                if (_tool != Tool.None) { BeginDraw(p); return; }
                _drag = Drag.Move;
                _dragOrigSel = sel;
                return;
            }
            if (_tool != Tool.None) return;
        }
        _selectionBeforeDrag = _selection;
        _drag = Drag.Pending;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.Location;
        switch (_drag)
        {
            case Drag.None:
                UpdateHover(p);
                return;
            case Drag.Pending:
                if (Math.Abs(p.X - _dragStart.X) > 3 || Math.Abs(p.Y - _dragStart.Y) > 3)
                {
                    _drag = Drag.NewSelection;
                    if (_hoverWindow is Rectangle hw) { _hoverWindow = null; Invalidate(Rectangle.Inflate(hw, 3, 3)); }
                    SetSelection(Normalize(_dragStart, p));
                }
                return;
            case Drag.NewSelection:
                SetSelection(Normalize(_dragStart, p));
                return;
            case Drag.Move:
            {
                var r = _dragOrigSel;
                r.Offset(p.X - _dragStart.X, p.Y - _dragStart.Y);
                r.X = Math.Clamp(r.X, 0, Math.Max(0, ClientSize.Width - r.Width));
                r.Y = Math.Clamp(r.Y, 0, Math.Max(0, ClientSize.Height - r.Height));
                SetSelection(r);
                return;
            }
            case Drag.Resize:
                SetSelection(ResizeRect(_dragOrigSel, _resizeHandle, p.X - _dragStart.X, p.Y - _dragStart.Y));
                return;
            case Drag.Draw:
                UpdateDraw(p);
                return;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (_drag)
        {
            case Drag.Pending:
                _drag = Drag.None;
                var w = WindowFinder.Hit(_windows, e.Location);
                if (w is Rectangle hw)
                {
                    _hoverWindow = null;
                    SetSelection(hw);
                }
                break;
            case Drag.Draw:
                EndDraw();
                break;
            default:
                _drag = Drag.None;
                break;
        }
        if (_selection is Rectangle s && (s.Width < 2 || s.Height < 2)) { _selection = null; InvalidateChrome(); Invalidate(); }
        InvalidateChrome();
        UpdateHover(e.Location);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // A redact box is always solid black, so a size change mid-drag would only confuse.
        if (_drag == Drag.Draw && _current is BoxShape { Kind: BoxKind.Redact }) return;
        var next = Math.Clamp(_size + Math.Sign(e.Delta), MinSize, MaxSize);
        if (next == _size && _bubble != null) return;
        _size = next;
        _settings.Size = _size;
        if (_editing != null)
        {
            var before = _editing.Bounds;
            _editing.Size = _size;
            InvalidateF(RectangleF.Union(before, _editing.Bounds));
        }
        if (_drag == Drag.Draw && _current != null)
        {
            _current.Size = _size;
            var now = _current.Bounds;
            InvalidateF(RectangleF.Union(_currentBounds, now));
            _currentBounds = now;
        }
        ShowBubble($"Size {_size}", e.Location);
        InvalidateChrome();
    }

    private void UpdateHover(Point p)
    {
        if (_selection == null)
        {
            var w = WindowFinder.Hit(_windows, p);
            if (w != _hoverWindow)
            {
                if (_hoverWindow is Rectangle old) Invalidate(Rectangle.Inflate(old, 3, 3));
                _hoverWindow = w;
                if (w is Rectangle now) Invalidate(Rectangle.Inflate(now, 3, 3));
            }
            Cursor = Cursors.Cross;
            return;
        }

        var button = HitButton(p);
        if (button != _hoverButton)
        {
            _hoverButton = button;
            InvalidateChrome();
        }
        Cursor = CursorFor(p, button);
    }

    private Cursor CursorFor(Point p, ToolButton? button)
    {
        if (button != null || OnChrome(p)) return Cursors.Hand;
        var h = HitHandle(p);
        if (h >= 0) return h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, _ => Cursors.SizeWE };
        if (_selection is Rectangle sel && sel.Contains(p))
            return _tool switch { Tool.None => Cursors.SizeAll, Tool.Text => Cursors.IBeam, _ => Cursors.Cross };
        return _tool == Tool.None ? Cursors.Cross : Cursors.Default;
    }

    private void CancelDrag()
    {
        if (_drag == Drag.Draw && _current != null)
        {
            InvalidateF(_currentBounds);
            _current = null;
        }
        else if (_drag is Drag.Move or Drag.Resize) SetSelection(_dragOrigSel);
        else if (_drag == Drag.NewSelection)
        {
            var dragged = _selection;
            _selection = _selectionBeforeDrag;
            if (dragged is Rectangle d) Invalidate(Rectangle.Inflate(d, 2, 2));
            if (_selection is Rectangle s) Invalidate(Rectangle.Inflate(s, 2, 2));
        }
        _drag = Drag.None;
        InvalidateChrome();
    }

    private static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    internal static Rectangle ResizeRect(Rectangle r, int handle, int dx, int dy)
    {
        int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (handle is 0 or 6 or 7) l += dx;
        if (handle is 2 or 3 or 4) rt += dx;
        if (handle is 0 or 1 or 2) t += dy;
        if (handle is 4 or 5 or 6) b += dy;
        return Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b));
    }

    private void SetSelection(Rectangle r)
    {
        r.Intersect(ClientRectangle);
        var old = _selection;
        _selection = r;
        if (old is Rectangle o) Invalidate(Rectangle.Inflate(o, 2, 2));
        Invalidate(Rectangle.Inflate(r, 2, 2));
        InvalidateChrome();
    }

    // ---------- Drawing ----------

    private void BeginDraw(Point p)
    {
        var pt = (PointF)p;
        switch (_tool)
        {
            case Tool.Text:
                _editing = new TextNote { Color = _color, Size = _size, Origin = new PointF(pt.X, pt.Y - (8 + _size * 3) * 0.6f) };
                InvalidateF(_editing.Bounds);
                InvalidateChrome();
                return;
            case Tool.Number:
                // Held until mouse-up, so scrolling can resize it and moving can reposition it before it's placed.
                var n = _annotations.Count(a => a is NumberMark) + 1;
                _current = new NumberMark { Color = _color, Size = _size, Center = pt, Number = n };
                break;
            case Tool.Pen:
            case Tool.Highlighter:
                var s = new Stroke { Color = _color, Size = _size, Highlighter = _tool == Tool.Highlighter };
                s.Points.Add(pt);
                _current = s;
                break;
            case Tool.Line:
            case Tool.Arrow:
                _current = new LineShape { Color = _color, Size = _size, Start = pt, End = pt, Arrow = _tool == Tool.Arrow };
                break;
            case Tool.Rectangle:
            case Tool.Ellipse:
            case Tool.Redact:
                var kind = _tool switch { Tool.Rectangle => BoxKind.Rectangle, Tool.Ellipse => BoxKind.Ellipse, _ => BoxKind.Redact };
                _current = new BoxShape { Color = _color, Size = _size, Rect = new RectangleF(pt, SizeF.Empty), Kind = kind };
                break;
        }
        _drag = Drag.Draw;
        _currentBounds = _current!.Bounds;
        InvalidateF(_currentBounds);
        InvalidateChrome();
    }

    private void UpdateDraw(Point p)
    {
        if (_current == null) return;
        var shift = (ModifierKeys & Keys.Shift) != 0;
        PointF pt = p;
        switch (_current)
        {
            case Stroke s:
                var last = s.Points[^1];
                if (Math.Abs(last.X - pt.X) < 1 && Math.Abs(last.Y - pt.Y) < 1) return;
                s.Points.Add(pt);
                break;
            case LineShape l:
                l.End = shift ? Renderer.Snap45(l.Start, pt) : pt;
                break;
            case BoxShape b:
                b.Rect = Renderer.Box(_dragStart, pt, shift);
                break;
            case NumberMark m:
                m.Center = pt;
                break;
        }
        var now = _current.Bounds;
        InvalidateF(RectangleF.Union(_currentBounds, now));
        _currentBounds = now;
    }

    private void EndDraw()
    {
        _drag = Drag.None;
        var c = _current;
        _current = null;
        if (c == null) return;
        var keep = c switch
        {
            LineShape l => Math.Abs(l.End.X - l.Start.X) + Math.Abs(l.End.Y - l.Start.Y) >= 2,
            BoxShape b => b.Rect.Width >= 2 && b.Rect.Height >= 2,
            _ => true,
        };
        if (keep) Push(c);
        else InvalidateF(_currentBounds);
    }

    private void Push(Annotation a)
    {
        _annotations.Add(a);
        _redo.Clear();
        InvalidateF(a.Bounds);
        InvalidateChrome();
    }

    private void CommitText()
    {
        var note = _editing;
        if (note == null) return;
        _editing = null;
        InvalidateF(note.Bounds);
        if (note.Text.Trim().Length > 0) Push(note);
        else InvalidateChrome();
    }

    private void Undo()
    {
        if (_editing != null)
        {
            var b = _editing.Bounds;
            _editing = null;
            InvalidateF(b);
        }
        else if (_annotations.Count > 0)
        {
            var a = _annotations[^1];
            _annotations.RemoveAt(_annotations.Count - 1);
            _redo.Push(a);
            InvalidateF(a.Bounds);
        }
        InvalidateChrome();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var a = _redo.Pop();
        _annotations.Add(a);
        InvalidateF(a.Bounds);
        InvalidateChrome();
    }

    private void InvalidateF(RectangleF r)
    {
        var rect = Rectangle.Inflate(Rectangle.Round(r), 3, 3);
        if (_selection is Rectangle sel) rect.Intersect(Rectangle.Inflate(sel, 2, 2));
        if (!rect.IsEmpty) Invalidate(rect);
    }

    private void SetTool(Tool t)
    {
        CommitText();
        _tool = t;
        InvalidateChrome();
        UpdateHover(PointToClient(MousePosition));
    }

    private void SetColor(Color c)
    {
        _color = c;
        _settings.ColorArgb = c.ToArgb();
        if (_editing != null)
        {
            var note = new TextNote { Color = c, Size = _editing.Size, Origin = _editing.Origin, Text = _editing.Text };
            _editing = note;
            InvalidateF(note.Bounds);
        }
    }

    private void Press(ToolButton b)
    {
        switch (b.Kind)
        {
            case ButtonKind.Tool: SetTool(_tool == b.Tool ? Tool.None : b.Tool); break;
            case ButtonKind.Color: _paletteOpen = !_paletteOpen; InvalidateChrome(); break;
            case ButtonKind.Undo: Undo(); break;
            case ButtonKind.Redo: Redo(); break;
            case ButtonKind.Copy: Copy(); break;
            case ButtonKind.Save: SaveAs(); break;
            case ButtonKind.Close: Close(); break;
        }
    }

    private void ShowBubble(string text, Point at)
    {
        if (!_bubbleRect.IsEmpty) Invalidate(_bubbleRect);
        _bubble = text;
        var size = TextRenderer.MeasureText(text, LabelFont);
        var h = Math.Max(size.Height + S(8), S(28));
        _bubbleRect = new Rectangle(at.X + S(18), at.Y + S(18), size.Width + S(22) + Math.Min(_size * 2 + 2, h - S(6)), h);
        Invalidate(_bubbleRect);
        _bubbleTimer ??= new System.Windows.Forms.Timer { Interval = 900 };
        _bubbleTimer.Stop();
        _bubbleTimer.Tick -= HideBubble;
        _bubbleTimer.Tick += HideBubble;
        _bubbleTimer.Start();
    }

    private void HideBubble(object? sender, EventArgs e)
    {
        _bubbleTimer?.Stop();
        _bubble = null;
        Invalidate(_bubbleRect);
        _bubbleRect = Rectangle.Empty;
    }

    // ---------- Keyboard ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var alt = (keyData & Keys.Alt) != 0;
        // AltGr arrives as Ctrl+Alt (ś, ż, ć...), so a shortcut needs Ctrl without Alt.
        var ctrl = (keyData & Keys.Control) != 0 && !alt;
        var shift = (keyData & Keys.Shift) != 0;

        if (_editing != null)
        {
            if (ctrl && key == Keys.C) { CommitText(); Copy(); return true; }
            if (ctrl && key == Keys.S) { CommitText(); SaveAs(); return true; }
            if (ctrl && key == Keys.Z)
            {
                if (_editing.Text.Length == 0) Undo();
                else SetEditingText(TrimWord(_editing.Text));
                return true;
            }
            if (ctrl && key == Keys.V)
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n');
                    AppendText(text.Length > MaxPaste ? text[..MaxPaste] : text);
                }
                return true;
            }
            if (key == Keys.Escape || key == Keys.Enter && !shift) { CommitText(); return true; }
            if (key == Keys.Enter && shift) { AppendText("\n"); return true; }
            if (key == Keys.Back)
            {
                if (_editing.Text.Length > 0)
                    SetEditingText(ctrl ? TrimWord(_editing.Text) : RemoveLastCharacter(_editing.Text));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (key == Keys.Escape)
        {
            if (_drag != Drag.None) CancelDrag();
            else if (_paletteOpen) { _paletteOpen = false; InvalidateChrome(); }
            else Close();
            return true;
        }
        if (ctrl && key == Keys.C || key == Keys.Enter && !ctrl && !alt) { Copy(); return true; }
        if (ctrl && key == Keys.S) { SaveAs(); return true; }
        if (ctrl && key == Keys.Z && !shift) { Undo(); return true; }
        if (ctrl && (key == Keys.Y || key == Keys.Z && shift)) { Redo(); return true; }
        if (ctrl && key == Keys.A)
        {
            var mon = Screen.FromPoint(MousePosition).Bounds;
            mon.Offset(-Left, -Top);
            _hoverWindow = null;
            SetSelection(mon);
            Invalidate();
            return true;
        }

        if (!ctrl && !alt && _selection is Rectangle sel)
        {
            Tool? t = key switch
            {
                Keys.P => Tool.Pen, Keys.L => Tool.Line, Keys.A => Tool.Arrow, Keys.R => Tool.Rectangle,
                Keys.E => Tool.Ellipse, Keys.H => Tool.Highlighter, Keys.T => Tool.Text, Keys.N => Tool.Number,
                Keys.B => Tool.Redact, _ => null,
            };
            if (t is Tool tool) { SetTool(_tool == tool ? Tool.None : tool); return true; }

            int dx = key == Keys.Left ? -1 : key == Keys.Right ? 1 : 0;
            int dy = key == Keys.Up ? -1 : key == Keys.Down ? 1 : 0;
            if (dx != 0 || dy != 0)
            {
                var r = sel;
                if (shift) { r.Width = Math.Max(2, r.Width + dx); r.Height = Math.Max(2, r.Height + dy); }
                else
                {
                    // Clamp like a mouse move; otherwise the edge intersect shrinks the selection.
                    r.X = Math.Clamp(r.X + dx, 0, Math.Max(0, ClientSize.Width - r.Width));
                    r.Y = Math.Clamp(r.Y + dy, 0, Math.Max(0, ClientSize.Height - r.Height));
                }
                SetSelection(r);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_editing != null && !char.IsControl(e.KeyChar))
        {
            AppendText(e.KeyChar.ToString());
            e.Handled = true;
        }
    }

    private const int MaxPaste = 2000, MaxNoteLength = 5000;

    private void AppendText(string s)
    {
        if (_editing == null) return;
        var room = MaxNoteLength - _editing.Text.Length;
        if (room <= 0) return;
        SetEditingText(_editing.Text + (s.Length > room ? s[..room] : s));
    }

    private void SetEditingText(string text)
    {
        if (_editing == null) return;
        var before = _editing.Bounds;
        _editing.Text = text;
        InvalidateF(RectangleF.Union(before, _editing.Bounds));
    }

    // Removes one user-visible character, so emoji and other surrogate pairs aren't split.
    internal static string RemoveLastCharacter(string s)
    {
        if (s.Length == 0) return s;
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(s);
        return s[..starts[^1]];
    }

    internal static string TrimWord(string s)
    {
        var i = s.Length;
        while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(s[i - 1])) i--;
        return s[..i];
    }

    // ---------- Output ----------

    private Bitmap? Compose() =>
        _selection is Rectangle sel && sel.Width > 0 && sel.Height > 0 ? Renderer.Compose(_capture, sel, _annotations) : null;

    private void Copy()
    {
        using var img = Compose();
        if (img == null) return;
        try
        {
            ClipboardWriter.SetImage(img);
        }
        catch (Exception ex)
        {
            Log.Write($"copy failed: {ex}");
            MessageBox.Show(this, $"Couldn't copy to the clipboard. Another app may be holding it.\n\n{ex.Message}",
                "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Close();
    }

    private void SaveAs()
    {
        using var img = Compose();
        if (img == null) return;
        var dir = _settings.LastSaveDirectory;
        if (string.IsNullOrEmpty(dir) || !DirectoryExistsQuickly(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        using var dlg = new SaveFileDialog
        {
            Title = "Save screenshot",
            InitialDirectory = dir,
            FileName = $"Snip {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|Bitmap (*.bmp)|*.bmp",
            DefaultExt = "png",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ImageSaver.Save(img, dlg.FileName);
        }
        catch (Exception ex)
        {
            Log.Write($"save failed: {ex}");
            MessageBox.Show(this, $"Couldn't save {dlg.FileName}.\n\n{ex.Message}", "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.LastSaveDirectory = Path.GetDirectoryName(dlg.FileName);
        Close();
    }

    // An offline network share can block Directory.Exists for tens of seconds; give up after a moment.
    private static bool DirectoryExistsQuickly(string dir)
    {
        var check = Task.Run(() => System.IO.Directory.Exists(dir));
        return check.Wait(TimeSpan.FromMilliseconds(400)) && check.Result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmed.Dispose();
            _capture.Dispose();
            _labelFont?.Dispose();
            _bubbleTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class ClipboardWriter
{
    public static void SetImage(Bitmap img)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, true, img);
        var png = new MemoryStream();
        img.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, png);
        Clipboard.SetDataObject(data, true, 10, 50);
    }
}

internal static class ImageSaver
{
    public static void Save(Bitmap img, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
            img.Save(path, codec, p);
        }
        else if (ext == ".bmp") img.Save(path, ImageFormat.Bmp);
        else img.Save(path, ImageFormat.Png);
    }
}
