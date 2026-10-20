// Taking over from File Explorer (a switch in Folder options; off gives Windows back at once):
//  - folders and drives opened anywhere (the desktop, Start, "Show in folder" from Chrome, other apps) and Win+E
//    open in Omebly Explorer, through the same per-user registry entries the open-source Files app uses. Only this
//    person's settings (HKCU) change; nothing for other users or the whole PC.
//  - if Omebly Explorer fails to start twice in a row while it is the default, it hands folders back to Windows by
//    itself, so a broken install can never leave folders unopenable.
using Microsoft.Win32;

namespace OmeblyExplorer.Core;

public static class Takeover
{
    private const string Verb = "openinomebly";
    private const string WinE = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow";
    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OmeblyExplorer.exe");

    public static bool IsOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\" + Verb + @"\command"); return k?.GetValue(null) is string s && s.Contains("OmeblyExplorer", StringComparison.OrdinalIgnoreCase); }
    }

    public static void TurnOn()
    {
        var cmd = $"\"{Exe}\" --folder \"%1\"";
        foreach (var cls in new[] { "Directory", "Drive" })
        {
            using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell");
            shell.SetValue(null, Verb); // the default action for double-click / "Open"
            using var v = shell.CreateSubKey(Verb);
            v.SetValue(null, "Open in Omebly Explorer");
            v.SetValue("Icon", $"\"{Exe}\",0");
            using var c = v.CreateSubKey("command");
            c.SetValue(null, cmd);
        }
        // Win+E and "This PC" from the taskbar.
        using (var w = Registry.CurrentUser.CreateSubKey(WinE + @"\command"))
        {
            w.SetValue(null, $"\"{Exe}\"");
            w.SetValue("DelegateExecute", "");
        }
        Settings.Current.Takeover = true; Settings.Save();
        Native.NotifyAssociations();
    }

    public static void TurnOff()
    {
        foreach (var cls in new[] { "Directory", "Drive" })
        {
            try
            {
                using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell", writable: true);
                if (shell == null) continue;
                if (shell.GetValue(null) as string == Verb) shell.DeleteValue("", false);
                shell.DeleteSubKeyTree(Verb, false);
            }
            catch { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(WinE, false); } catch { }
        Settings.Current.Takeover = false; Settings.Save();
        Native.NotifyAssociations();
    }

    // ---------- Crash safety ----------
    private static readonly string Marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer", "starting.txt");

    /// <summary>Called first thing at start. Two starts in a row that never reached a working window: give folders back.</summary>
    public static void Starting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            int failed = File.Exists(Marker) && int.TryParse(File.ReadAllText(Marker), out var n) ? n : 0;
            if (failed >= 2 && IsOn) { TurnOff(); CrashLog.Write(new Exception("Omebly Explorer didn't start twice, so folders were handed back to Windows' File Explorer.")); failed = 0; }
            File.WriteAllText(Marker, (failed + 1).ToString());
        }
        catch { }
    }
    /// <summary>Called once the window is up and working.</summary>
    public static void Started() { try { File.Delete(Marker); } catch { } }
}
