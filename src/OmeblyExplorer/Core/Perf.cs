// Timings for the speed tests: written only when OMEBLY_PERF is set (to a file path).
namespace OmeblyExplorer.Core;

public static class Perf
{
    private static readonly string? File = Environment.GetEnvironmentVariable("OMEBLY_PERF");
    public static void Log(string line) { if (File != null) try { Directory.CreateDirectory(Path.GetDirectoryName(File)!); System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { } }
}
