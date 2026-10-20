// Starting other programs for a folder. A folder's path is never put into another program's command line (folder
// names can hold characters such as ';' that some programs, like Windows Terminal, read as their own commands):
// the folder is given as the working directory instead.
using System.Diagnostics;

namespace OmeblyExplorer.Core;

public static class Launch
{
    // Programs by their full path, so a program with the same name inside the folder is never the one started.
    public static string TerminalExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
    public static string PowerShellExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static ProcessStartInfo TerminalInfo(string folder) => new(TerminalExe, "-d .") { WorkingDirectory = folder, UseShellExecute = false };
    public static ProcessStartInfo PowerShellInfo(string folder) => new(PowerShellExe) { WorkingDirectory = folder, UseShellExecute = false };

    public static void Terminal(string folder)
    {
        if (File.Exists(TerminalExe)) { try { Process.Start(TerminalInfo(folder)); return; } catch { /* use PowerShell */ } }
        Process.Start(PowerShellInfo(folder));
    }

    /// <summary>Only colours written as #RRGGBB are kept; anything else in the settings file gets the default.</summary>
    public static bool IsColor(string? c) => c is { Length: 7 } && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit);
}
