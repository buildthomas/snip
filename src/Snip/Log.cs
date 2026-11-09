namespace Snip;

// Records lifecycle events and errors only, never key data.
internal static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Settings.Directory, "snip.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 256 * 1024) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }

    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("SNIP_TRACE") == "1";

    public static void Trace(string message)
    {
        if (TraceEnabled) Write(message);
    }
}
