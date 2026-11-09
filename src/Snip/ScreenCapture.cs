using System.Drawing.Imaging;
using static Snip.Native;

namespace Snip;

internal static class ScreenCapture
{
    public static Rectangle VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static long LastBlitMs;

    // BitBlt into a device-compatible bitmap (fastest path under DWM), then GetDIBits straight into the GDI+ buffer.
    // CAPTUREBLT is left out on purpose: DWM already composes menus/tooltips, and the flag only adds a sync.
    public static unsafe Bitmap CaptureVirtualScreen(out Rectangle bounds)
    {
        bounds = VirtualScreen;
        int w = bounds.Width, h = bounds.Height;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var ddb = CreateCompatibleBitmap(screen, w, h);
        try
        {
            if (ddb == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleBitmap failed");
            var old = SelectObject(mem, ddb);
            var t = System.Diagnostics.Stopwatch.StartNew();
            var ok = BitBlt(mem, 0, 0, w, h, screen, bounds.X, bounds.Y, SRCCOPY);
            LastBlitMs = t.ElapsedMilliseconds;
            SelectObject(mem, old);
            // Fails e.g. on the secure desktop or a disconnected remote session; better than a black overlay.
            if (!ok) throw new InvalidOperationException("Could not read the screen (BitBlt failed).");

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var success = false;
            try
            {
                var bmi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                if (GetDIBits(mem, ddb, 0, (uint)h, data.Scan0, ref bmi, 0) == 0)
                    throw new InvalidOperationException("GetDIBits failed");
                var dst = (byte*)data.Scan0;
                var stride = data.Stride;
                Parallel.For(0, h, y =>
                {
                    var d = (uint*)(dst + (long)y * stride);
                    for (int x = 0; x < w; x++) d[x] |= 0xFF000000;
                });
                success = true;
            }
            finally
            {
                bmp.UnlockBits(data);
                if (!success) bmp.Dispose();
            }
            return bmp;
        }
        finally
        {
            if (ddb != IntPtr.Zero) DeleteObject(ddb);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Darkened copy used as the frozen background outside the selection.
    public static unsafe Bitmap Dim(Bitmap source, int keep256)
    {
        int w = source.Width, h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var sd = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var rd = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte* sp = (byte*)sd.Scan0, rp = (byte*)rd.Scan0;
            int ss = sd.Stride, rs = rd.Stride;
            Parallel.For(0, h, y =>
            {
                var s = (uint*)(sp + (long)y * ss);
                var d = (uint*)(rp + (long)y * rs);
                for (int x = 0; x < w; x++)
                {
                    var p = s[x];
                    var rb = ((p & 0x00FF00FF) * (uint)keep256 >> 8) & 0x00FF00FF;
                    var g = ((p & 0x0000FF00) * (uint)keep256 >> 8) & 0x0000FF00;
                    d[x] = 0xFF000000 | rb | g;
                }
            });
        }
        finally
        {
            source.UnlockBits(sd);
            result.UnlockBits(rd);
        }
        return result;
    }
}
