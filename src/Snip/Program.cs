namespace Snip;

internal static class Program
{
    private const string MutexName = @"Local\Snip.SingleInstance.7f3c";
    private const string CaptureEventName = @"Local\Snip.Capture.7f3c";

    [STAThread]
    private static void Main(string[] args)
    {
        EventWaitHandle captureSignal;
        Mutex mutex;
        bool first;
        try
        {
            // The event exists before the mutex, so a second launch can always find it.
            captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName);
            mutex = new Mutex(true, MutexName, out first);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Snip is already running as administrator. Exit that copy from its tray icon first.",
                "Snip", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var _ = mutex;
        using var signal = captureSignal;
        if (!first)
        {
            // A second launch (double-click, shortcut, `Snip.exe --capture`) asks the running instance to capture.
            signal.Set();
            return;
        }

        DisableEfficiencyThrottling();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");
        using var app = new TrayApp(captureSignal, captureNow: args.Contains("--capture"));
        Application.Run(app);
    }

    // Windows slows down window-less background processes; captures must run at full speed. Idle cost is unaffected.
    private static void DisableEfficiencyThrottling()
    {
        var state = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };
        Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref state,
            System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>());
    }
}
