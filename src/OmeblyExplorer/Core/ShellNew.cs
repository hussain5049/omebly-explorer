// Windows' "New" menu: every kind of file the installed apps add (Word document, Excel sheet, bitmap, rich text,
// compressed folder...), read from the same registry entries File Explorer uses. Only empty files, built-in data or
// template files are made; nothing in the registry is ever run.
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace OmeblyExplorer.Core;

public sealed record NewKind(string Extension, string Name, byte[]? Data, string? Template);

public static class ShellNew
{
    private static List<NewKind>? _cache;
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    public static List<NewKind> Kinds()
    {
        if (_cache != null) return _cache;
        var list = new List<NewKind>();
        IEnumerable<string> exts;
        // Explorer keeps its own list of extensions with a New entry; fall back to looking at every extension.
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew"))
            exts = (k?.GetValue("Classes") as string[])?.Where((e) => e.StartsWith('.')).ToList() ?? Registry.ClassesRoot.GetSubKeyNames().Where((n) => n.StartsWith('.')).ToList();
        foreach (var ext in exts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ext is ".lnk" or ".library-ms") continue; // shortcuts and libraries have their own ways
            try
            {
                using var ek = Registry.ClassesRoot.OpenSubKey(ext);
                if (ek == null) continue;
                var progId = ek.GetValue(null) as string;
                RegistryKey? sn = ek.OpenSubKey("ShellNew");
                if (sn == null && !string.IsNullOrEmpty(progId)) sn = ek.OpenSubKey(progId + @"\ShellNew");
                if (sn == null) continue;
                using (sn)
                {
                    if (sn.GetValue("Command") != null && sn.GetValue("NullFile") == null && sn.GetValue("Data") == null && sn.GetValue("FileName") == null) continue; // a program would have to run
                    byte[]? data = sn.GetValue("Data") switch { byte[] b => b, string s => Encoding.ASCII.GetBytes(s), _ => null };
                    string? template = sn.GetValue("FileName") as string;
                    if (template != null)
                    {
                        template = Environment.ExpandEnvironmentVariables(template);
                        if (!Path.IsPathRooted(template)) template = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew", template);
                        if (!File.Exists(template)) { template = null; if (data == null && sn.GetValue("NullFile") == null) continue; }
                    }
                    var name = Indirect(sn.GetValue("ItemName") as string) ?? FriendlyName(progId) ?? FileKinds.Describe(ext.ToLowerInvariant());
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new NewKind(ext.ToLowerInvariant(), name, data, template));
                }
            }
            catch { /* an odd entry: left out */ }
        }
        return _cache = list.GroupBy((k) => k.Extension).Select((g) => g.First()).OrderBy((k) => k.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? FriendlyName(string? progId)
    {
        if (string.IsNullOrEmpty(progId)) return null;
        using var k = Registry.ClassesRoot.OpenSubKey(progId);
        return Indirect(k?.GetValue("FriendlyTypeName") as string) ?? k?.GetValue(null) as string;
    }
    private static string? Indirect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (!s.StartsWith('@')) return s;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    /// <summary>Makes "New &lt;kind&gt;.ext" in the folder (empty, from built-in data, or a copy of the template). Returns its path.</summary>
    public static string Create(NewKind kind, string folder)
    {
        var name = FileOps.FreeName(folder, "New " + kind.Name, kind.Extension);
        var path = Path.Combine(folder, name);
        if (kind.Template != null) { File.Copy(kind.Template, path, overwrite: false); File.SetAttributes(path, FileAttributes.Normal); } // templates are often hidden or read-only
        else using (var f = new FileStream(path, FileMode.CreateNew)) if (kind.Data != null) f.Write(kind.Data);
        return path;
    }
}
