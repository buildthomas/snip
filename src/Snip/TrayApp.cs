using System.Diagnostics;
using Microsoft.Win32;

namespace Snip;

internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly KeyboardHook _hook;
    private readonly RegisteredWaitHandle _signalWait;
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _printScreenItem;
    private OverlayForm? _overlay;

    public TrayApp(EventWaitHandle captureSignal, bool captureNow)
    {
        // A hidden control forces creation of the WinForms synchronization context on this thread.
        using (var _ = new Control()) { }
        _ui = SynchronizationContext.Current!;

#if !DEBUG
        // Only the installed copy claims autostart; a copy run from Downloads or a build folder never does.
        if (Startup.IsInstalledCopy)
        {
            try { Startup.Set(_settings.StartWithWindows); }
            catch (Exception ex) { Log.Write($"startup registration failed: {ex.Message}"); }
        }
#endif

        _icon = LoadIcon();
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _printScreenItem = new ToolStripMenuItem("Use PrintScreen key", null, (_, _) => TogglePrintScreen());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Capture  (Win+Shift+S / PrtSc)", null, (_, _) => Capture()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_printScreenItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Open settings folder", null, (_, _) => OpenSettingsFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = Startup.IsEnabled;
            _printScreenItem.Checked = _settings.PrintScreenEnabled;
        };

        _tray = new NotifyIcon { Icon = _icon, Text = "Snip - Win+Shift+S or PrtSc", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Capture(); };

        _hook = new KeyboardHook(Capture) { PrintScreenEnabled = _settings.PrintScreenEnabled };
        _signalWait = ThreadPool.RegisterWaitForSingleObject(captureSignal, (_, _) => _ui.Post(_ => Capture(), null), null, -1, false);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (captureNow) _ui.Post(_ => Capture(), null);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _hook.Reinstall();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _hook.Reinstall();
    }

    private static Icon LoadIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("snip.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : (Icon)SystemIcons.Application.Clone();
    }

    public void Capture()
    {
        if (_overlay != null)
        {
            _overlay.Activate();
            return;
        }

        Log.Trace("capture start");
        // Grab pixels first, before any window of ours appears, so menus and hover states are preserved.
        var sw = Stopwatch.StartNew();
        var previousForeground = Native.GetForegroundWindow();
        Bitmap? bmp = null;
        long tCapture, tWindows, tForm;
        try
        {
            bmp = ScreenCapture.CaptureVirtualScreen(out var bounds);
            tCapture = sw.ElapsedMilliseconds;
            Log.Trace($"blit {ScreenCapture.LastBlitMs} ms");
            var windows = WindowFinder.VisibleWindows(bounds);
            tWindows = sw.ElapsedMilliseconds;
            _overlay = new OverlayForm(bmp, bounds, windows, _settings);
            tForm = sw.ElapsedMilliseconds;
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            Log.Write($"capture failed: {ex}");
            _tray.ShowBalloonTip(4000, "Snip", $"Capture failed: {ex.Message}", ToolTipIcon.Warning);
            return;
        }
        _overlay.Shown += (_, _) => Log.Trace($"overlay shown: capture {tCapture} ms, windows {tWindows} ms, form {tForm} ms, shown {sw.ElapsedMilliseconds} ms");
        _overlay.FormClosed += (_, e) =>
        {
            Log.Trace($"overlay closed: {e.CloseReason}");
            _overlay.Dispose();
            _overlay = null;
            _settings.Save();
            if (previousForeground != IntPtr.Zero) Native.SetForegroundWindow(previousForeground);
            // Give back capture-sized buffers right away. The working set is deliberately not trimmed:
            // paging the hook's code back in on the next keystroke risks Windows dropping the hook.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _hook.Reinstall();
        };
        _overlay.Show();
    }

    private void ToggleStartup()
    {
        try
        {
            _settings.StartWithWindows = !Startup.IsEnabled;
            Startup.Set(_settings.StartWithWindows);
            _settings.Save();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Snip", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void TogglePrintScreen()
    {
        _settings.PrintScreenEnabled = !_settings.PrintScreenEnabled;
        _hook.PrintScreenEnabled = _settings.PrintScreenEnabled;
        _settings.Save();
    }

    private static void OpenSettingsFolder()
    {
        Directory.CreateDirectory(Settings.Directory);
        Process.Start(new ProcessStartInfo { FileName = Settings.Directory, UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _signalWait.Unregister(null);
            _hook.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            _overlay?.Dispose();
        }
        base.Dispose(disposing);
    }
}
