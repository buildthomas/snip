using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Snip.Tests;

// Drives the real overlay with synthetic mouse/keyboard events (no real input involved).
public class OverlayInteractionTests
{
    private const int W = 400, H = 300;

    private sealed class Harness : IDisposable
    {
        public readonly OverlayForm Form;

        public Harness(Tool tool)
        {
            var capture = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(capture)) g.Clear(Color.White);
            Form = new OverlayForm(capture, new Rectangle(0, 0, W, H), new List<Rectangle>(),
                new Settings { ColorArgb = Color.FromArgb(255, 0, 0).ToArgb(), Size = 6 });
            Form.SetDemoState(new Rectangle(0, 0, W, H), Array.Empty<Annotation>(), tool, hoverActiveTool: false);
        }

        private void Call(string method, params object[] args) =>
            typeof(OverlayForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Form, args);

        public void Down(int x, int y) => Call("OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        public void Move(int x, int y) => Call("OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, x, y, 0));
        public void Up(int x, int y) => Call("OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        public void RightDown(int x, int y) => Call("OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, x, y, 0));
        public void Type(string text) { foreach (var c in text) Call("OnKeyPress", new KeyPressEventArgs(c)); }

        public void Key(Keys key)
        {
            var msg = new Message();
            typeof(OverlayForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Form, new object[] { msg, key });
        }

        public Rectangle Ink()
        {
            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) Form.Render(g, new Rectangle(0, 0, W, H));
            int l = W, t = H, r = -1, b = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R > 180 && p.G < 90 && p.B < 90) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
                }
            return r < 0 ? Rectangle.Empty : Rectangle.FromLTRB(l, t, r + 1, b + 1);
        }

        public void Dispose() => Form.Dispose();
    }

    private static Rectangle Reference(string text)
    {
        using var h = new Harness(Tool.Text);
        h.Down(40, 40);
        h.Up(40, 40);
        h.Type(text);
        h.Key(Keys.Enter);
        return h.Ink();
    }

    [Fact]
    public void TextBeingTyped_CanBeDraggedByItsBox_ThenCommitted()
    {
        var reference = Reference("Hello!");
        using var h = new Harness(Tool.Text);
        h.Down(40, 40);
        h.Up(40, 40);
        h.Type("Hello");
        var editing = h.Ink();
        var grab = new Point(editing.X + 5, editing.Y + editing.Height / 2);
        h.Down(grab.X, grab.Y);
        h.Move(grab.X + 100, grab.Y + 60);
        h.Move(grab.X + 200, grab.Y + 120);
        h.Up(grab.X + 200, grab.Y + 120);
        h.Type("!");
        h.Key(Keys.Enter);

        var after = h.Ink();
        Assert.Equal(reference.Size, after.Size);
        Assert.Equal(new Point(reference.X + 200, reference.Y + 120), after.Location);
    }

    [Fact]
    public void RightClickWhileDraggingText_PutsItBack()
    {
        var reference = Reference("Note");
        using var h = new Harness(Tool.Text);
        h.Down(40, 40);
        h.Up(40, 40);
        h.Type("Note");
        var editing = h.Ink();
        var grab = new Point(editing.X + 5, editing.Y + editing.Height / 2);
        h.Down(grab.X, grab.Y);
        h.Move(grab.X + 150, grab.Y + 100);
        h.RightDown(grab.X + 150, grab.Y + 100);
        h.Key(Keys.Enter);
        Assert.Equal(reference, h.Ink());
    }

    [Fact]
    public void ClickingOutsideTheText_FinishesIt_WhereItIs()
    {
        var reference = Reference("Stay");
        using var h = new Harness(Tool.None);
        h.Form.SetDemoState(new Rectangle(0, 0, W, H), Array.Empty<Annotation>(), Tool.Text, hoverActiveTool: false);
        h.Down(40, 40);
        h.Up(40, 40);
        h.Type("Stay");
        h.Down(300, 250);
        h.Up(300, 250);
        h.Key(Keys.Escape);
        Assert.Equal(reference, h.Ink());
    }
}
