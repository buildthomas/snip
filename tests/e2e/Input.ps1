# Shared input/window helpers for the e2e scripts.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class In {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    public static void Key(byte vk, bool up) { keybd_event(vk, 0, up ? 2u : 0u, UIntPtr.Zero); }
    public static void Tap(byte vk) { Key(vk, false); Key(vk, true); }
    public static void Drag(int x1, int y1, int x2, int y2) {
        SetCursorPos(x1, y1); System.Threading.Thread.Sleep(40);
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        for (int i = 1; i <= 12; i++) { SetCursorPos(x1 + (x2 - x1) * i / 12, y1 + (y2 - y1) * i / 12); System.Threading.Thread.Sleep(12); }
        mouse_event(4, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    }
    public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(30); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60); }
}
'@

function Overlay {
    $h = [In]::FindWindow([NullString]::Value, 'Snip')
    if ($h -ne [IntPtr]::Zero -and [In]::IsWindowVisible($h)) { $h } else { [IntPtr]::Zero }
}
function Shot($name) {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($vs.Location, [System.Drawing.Point]::Empty, $vs.Size)
    $primary = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $crop = $bmp.Clone([System.Drawing.Rectangle]::new($primary.X - $vs.X, $primary.Y - $vs.Y, $primary.Width, $primary.Height), $bmp.PixelFormat)
    $crop.Save((Join-Path $OutDir $name)); $crop.Dispose(); $g.Dispose(); $bmp.Dispose()
}

function Fail($msg) { Write-Host "FAIL: $msg"; Cleanup; exit 1 }
function ForegroundClass {
    $sb = New-Object System.Text.StringBuilder 256
    [void][In]::GetClassName([In]::GetForegroundWindow(), $sb, 256); $sb.ToString()
}
function Trigger($hotkey) {
    if ($hotkey -eq 'PrtSc') { [In]::Tap(0x2C) }
    else { [In]::Key(0x5B, $false); [In]::Key(0x10, $false); [In]::Key(0x53, $false); [In]::Key(0x53, $true); [In]::Key(0x10, $true); [In]::Key(0x5B, $true) }
}
function WaitOverlay([int] $ms = 3000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Overlay) -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt $ms) { Start-Sleep -Milliseconds 5 }
    $h = Overlay
    if ($h -eq [IntPtr]::Zero) { Fail 'overlay did not appear' }
    $sw.ElapsedMilliseconds
}
function Ctrl([byte] $vk) { [In]::Key(0x11, $false); [In]::Tap($vk); [In]::Key(0x11, $true) }
