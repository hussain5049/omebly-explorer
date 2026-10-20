// Windows shortcuts (.lnk), made with Windows' own shell link object, like "Create shortcut" and Alt-drag in Explorer.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace OmeblyExplorer.Core;

public static class Shortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>Makes "&lt;name&gt; - Shortcut.lnk" in <paramref name="folder"/> pointing at <paramref name="target"/>.
    /// Returns the new shortcut's path.</summary>
    public static string Create(string target, string folder)
    {
        var name = Path.GetFileName(target.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = target.TrimEnd('\\').Replace(":", "");
        if (!Directory.Exists(target)) name = Path.GetFileNameWithoutExtension(name) is { Length: > 0 } n ? n : name;
        var file = Path.Combine(folder, FileOps.FreeName(folder, name + " - Shortcut", ".lnk"));
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            var dir = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
            if (dir != null) link.SetWorkingDirectory(dir);
            ((IPersistFile)link).Save(file, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
        return file;
    }
}
