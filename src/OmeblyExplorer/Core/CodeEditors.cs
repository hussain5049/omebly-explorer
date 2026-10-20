// "Open with Code" and friends: the code editors and IDEs installed on this PC (VS Code, Cursor, Antigravity,
// Windsurf, JetBrains IDEs, Android Studio, Sublime Text, Notepad++ and more), plus the classic "open here" entries
// other apps add to File Explorer's folder background menu (Visual Studio, Git Bash, Git GUI and so on).
// Editors are found in Windows' list of installed apps; nothing is downloaded and nothing is changed.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OmeblyExplorer.Core;

/// <summary>One way to open a folder: the app, and its arguments with {0} for the folder.</summary>
public sealed record OpenWith(string Name, string Exe, string Args, string IconSource, bool Classic = false);

public static partial class CodeEditors
{
    private static List<OpenWith>? _cache;
    private static DateTime _at;
    private static readonly object Gate = new();

    [GeneratedRegex(@"(Visual Studio Code|VSCodium|^Cursor\b|Antigravity|Windsurf|^Trae\b|^Kiro\b|^Zed\b|PyCharm|IntelliJ IDEA|WebStorm|JetBrains Rider|CLion|GoLand|PhpStorm|RubyMine|DataGrip|DataSpell|RustRover|JetBrains Fleet|Android Studio|Sublime Text|Notepad\+\+|Eclipse IDE|Apache NetBeans|Positron)", RegexOptions.IgnoreCase)]
    private static partial Regex EditorName();
    [GeneratedRegex(@"\s+(\(User\)|\(x64\)|\(64-bit\)|\(Insiders?\)|v?\d+(\.\d+)+.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameTail();

    /// <summary>What is installed now (looked up again every two minutes, so a new editor shows without a restart).</summary>
    public static IReadOnlyList<OpenWith> All()
    {
        lock (Gate)
        {
            if (_cache != null && DateTime.Now - _at < TimeSpan.FromMinutes(2)) return _cache;
            try { _cache = Find(); } catch { _cache ??= new(); }
            _at = DateTime.Now;
            return _cache;
        }
    }

    /// <summary>Characters a command line could read as commands if another app's template passed them on (Security 342).</summary>
    public static bool RiskyForClassic(string folder) => folder.IndexOfAny(Risky) >= 0;
    private static readonly char[] Risky = ['&', '|', '^', ';', '%', '!', '"', '(', ')', '<', '>', '`'];

    /// <summary>Opens the folder in it. The folder is passed as one quoted argument; nothing goes through a shell.</summary>
    public static void Open(OpenWith o, string folder)
    {
        // Other apps' "open here" commands are their templates, not ours: a folder whose name has command
        // characters is never put into one.
        if (o.Classic && RiskyForClassic(folder))
            throw new InvalidOperationException($"This folder's name has characters ({string.Concat(folder.Where((c) => Risky.Contains(c)).Distinct())}) that {o.Name} could read as commands, so it isn't opened this way. Use one of the editors, or rename the folder.");
        // "D:\" would end in \" and swallow the quote: "D:\." means the same folder.
        var f = folder.EndsWith('\\') ? folder + "." : folder;
        Process.Start(new ProcessStartInfo(o.Exe, o.Args.Replace("{0}", f)) { UseShellExecute = false, WorkingDirectory = Directory.Exists(folder) ? folder : "" });
    }

    private static List<OpenWith> Find()
    {
        var found = new List<OpenWith>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1. Code editors from the installed apps list.
        foreach (var (hive, key) in new[] { (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"), (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            using var root = hive.OpenSubKey(key);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || !EditorName().IsMatch(name) || k.GetValue("SystemComponent") is 1) continue;
                var exe = ExeOf(k.GetValue("DisplayIcon") as string, k.GetValue("InstallLocation") as string, name);
                if (exe == null || !seen.Add(exe)) continue;
                found.Add(new OpenWith(Clean(name), exe, "\"{0}\"", exe));
            }
        }
        // 2. Editors that don't always list themselves (portable or per-user installs).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var (name, path) in new[]
        {
            ("VS Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe")), ("VS Code", Path.Combine(pf, "Microsoft VS Code", "Code.exe")),
            ("Cursor", Path.Combine(local, "Programs", "cursor", "Cursor.exe")), ("Windsurf", Path.Combine(local, "Programs", "Windsurf", "Windsurf.exe")),
            ("Zed", Path.Combine(local, "Programs", "Zed", "Zed.exe")), ("Sublime Text", Path.Combine(pf, "Sublime Text", "sublime_text.exe")),
            ("Notepad++", Path.Combine(pf, "Notepad++", "notepad++.exe")),
        })
            if (File.Exists(path) && seen.Add(path)) found.Add(new OpenWith(name, path, "\"{0}\"", path));
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        // 3. The classic folder-background entries other apps add for File Explorer.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var root = hive.OpenSubKey(@"Software\Classes\Directory\Background\shell");
            if (root == null) continue;
            foreach (var verb in root.GetSubKeyNames())
            {
                if (verb.Equals("cmd", StringComparison.OrdinalIgnoreCase) || verb.Equals("Powershell", StringComparison.OrdinalIgnoreCase) || verb.Equals("WSL", StringComparison.OrdinalIgnoreCase)) continue; // "Open terminal here" covers these
                using var k = root.OpenSubKey(verb);
                using var c = root.OpenSubKey(verb + @"\command");
                if (k == null || c?.GetValue(null) is not string cmd) continue;
                var names = k.GetValueNames();
                if (names.Any((n) => n is "Extended" or "LegacyDisable" or "ProgrammaticAccessOnly")) continue; // hidden in Explorer too (Shift-only or off)
                var label = Label(k.GetValue("MUIVerb") as string ?? k.GetValue(null) as string ?? verb);
                var (exe, args) = Split(Environment.ExpandEnvironmentVariables(cmd));
                if (exe == null || !Path.IsPathRooted(exe) || !File.Exists(exe) || exe.EndsWith("OmeblyExplorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Shells.Contains(Path.GetFileName(exe))) continue; // a shell would read the folder name as a command line (Security 342); Open terminal here covers these
                if (!seen.Add(exe + "|" + args)) continue;
                if (found.Any((f) => f.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))) continue; // the editor is already there
                // %V, %v, %1, %L and %W are the folder in Explorer's own commands.
                args = Regex.Replace(args, "%[VvLlWw1]", "{0}");
                if (!args.Contains("{0}")) continue;
                if (!QuotedFolder().IsMatch(args)) continue; // the folder must sit inside quotes, or a space or symbol in its name would split it
                var icon = k.GetValue("Icon") is string ic ? IconPath(ic) ?? exe : exe;
                found.Add(new OpenWith(label, exe, args, icon, Classic: true));
            }
        }
        return found;
    }

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "powershell.exe", "pwsh.exe", "wt.exe", "bash.exe", "sh.exe", "wsl.exe", "conhost.exe", "openconsole.exe", "mshta.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "msiexec.exe", "regsvr32.exe" };
    [GeneratedRegex(@"""[^""]*\{0\}[^""]*""")]
    private static partial Regex QuotedFolder();

    private static string? ExeOf(string? displayIcon, string? installLocation, string name)
    {
        var icon = IconPath(displayIcon);
        // The listed file can be gone while its 64-bit twin is there (Android Studio lists studio.exe, ships studio64.exe).
        if (icon == null && IconPath(displayIcon, mustExist: false) is { } listed && listed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var twin = Path.Combine(Path.GetDirectoryName(listed) ?? "", Path.GetFileNameWithoutExtension(listed) + "64.exe");
            if (File.Exists(twin)) return twin;
        }
        if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) && !Path.GetFileName(icon).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            // JetBrains-style launchers: the 64-bit one next to it opens faster.
            var x64 = Path.Combine(Path.GetDirectoryName(icon)!, Path.GetFileNameWithoutExtension(icon) + "64.exe");
            return File.Exists(x64) ? x64 : icon;
        }
        if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation.Trim('"')))
        {
            var dir = installLocation.Trim('"');
            var bin = Path.Combine(dir, "bin");
            foreach (var d in new[] { dir, bin })
                if (Directory.Exists(d) && Directory.GetFiles(d, "*.exe").Where((f) => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("elevat", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("helper", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending((f) => name.Contains(Path.GetFileNameWithoutExtension(f).TrimEnd('6', '4'), StringComparison.OrdinalIgnoreCase)) // named like the app first
                    .ThenByDescending((f) => Path.GetFileNameWithoutExtension(f).EndsWith("64")).ThenBy((f) => f.Length).FirstOrDefault() is { } exe) return exe;
        }
        return null;
    }

    private static string? IconPath(string? v, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim();
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); s = end > 0 ? s[1..end] : s.Trim('"'); }
        else { var comma = s.LastIndexOf(','); if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma]; }
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        return !mustExist || File.Exists(s) ? s : null;
    }

    private static (string? exe, string args) Split(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); return end < 0 ? (null, "") : (cmd[1..end], cmd[(end + 1)..].Trim()); }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    private static string Clean(string name)
    {
        var n = NameTail().Replace(name.Trim(), "").Trim();
        if (n.Equals("Microsoft Visual Studio Code", StringComparison.OrdinalIgnoreCase) || n.Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        return n;
    }

    /// <summary>A menu label as Explorer shows it: "@file.dll,-123" read from the file, "&amp;" marks dropped.</summary>
    private static string Label(string raw)
    {
        var s = raw;
        if (s.StartsWith('@'))
        {
            var sb = new StringBuilder(512);
            s = SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s.TrimStart('@');
        }
        return s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
