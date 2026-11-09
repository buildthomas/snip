using System.Text.Json;
using Microsoft.Win32;

namespace Snip;

internal sealed class Settings
{
    public string? LastSaveDirectory { get; set; }
    public int ColorArgb { get; set; } = Color.FromArgb(255, 230, 40, 40).ToArgb();
    public int Size { get; set; } = 4;
    public bool PrintScreenEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public RedactStyle RedactStyle { get; set; } = RedactStyle.Pixelate;
    public RedactShape RedactShape { get; set; } = RedactShape.Brush;
    public int RedactCell { get; set; } = 12;
    public int EmojiIndex { get; set; }
    public bool RedactSecure { get; set; } = true;

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snip");
    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            // Corrupt settings fall back to defaults rather than blocking startup.
            Log.Write($"settings load failed, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // Write-then-rename so a crash mid-save can't leave a truncated file behind.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Snip";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Snip");

    public static bool IsInstalledCopy =>
        string.Equals(Path.GetDirectoryName(Environment.ProcessPath), InstallDirectory, StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s == Command;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }
}
