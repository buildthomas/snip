using static Snip.Native;

namespace Snip;

// Snapshot of visible top-level window bounds in z-order, taken at capture time for click-to-select.
internal static class WindowFinder
{
    public static List<Rectangle> VisibleWindows(Rectangle virtualScreen)
    {
        var result = new List<Rectangle>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_NOACTIVATE) != 0) return true;

            Rectangle r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
                r = rect.ToRectangle();
            else if (GetWindowRect(hwnd, out rect))
                r = rect.ToRectangle();
            else
                return true;

            r.Intersect(virtualScreen);
            if (r.Width >= 8 && r.Height >= 8) result.Add(r);
            return true;
        }, IntPtr.Zero);

        foreach (var s in Screen.AllScreens) result.Add(s.Bounds);
        return result;
    }

    public static Rectangle? Hit(IReadOnlyList<Rectangle> windows, Point p)
    {
        foreach (var r in windows)
            if (r.Contains(p)) return r;
        return null;
    }
}
