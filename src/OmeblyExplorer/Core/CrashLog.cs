// When something goes wrong, the details are added to crash.log next to the settings, on this PC only.
namespace OmeblyExplorer.Core;

public static class CrashLog
{
    public static void Write(Exception? e)
    {
        if (e == null) return;
        try
        {
            var dir = Environment.GetEnvironmentVariable("OMEBLY_SETTINGS_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "crash.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
            File.AppendAllText(file, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{typeof(CrashLog).Assembly.GetName().Version}\n{e}\n\n");
        }
        catch { }
    }
}
