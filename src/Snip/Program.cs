namespace Snip;

internal static class Program
{
    private const string MutexName = @"Local\Snip.SingleInstance.7f3c";
    private const string CaptureEventName = @"Local\Snip.Capture.7f3c";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // A second launch (double-click, shortcut, `Snip.exe --capture`) asks the running instance to capture.
            if (EventWaitHandle.TryOpenExisting(CaptureEventName, out var signal))
                using (signal) signal.Set();
            return;
        }

        using var captureSignal = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName);
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
