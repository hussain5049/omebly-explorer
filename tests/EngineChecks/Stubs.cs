// What the copy engine needs from the rest of the app, for testing it on its own.
using System.Runtime.InteropServices;
namespace OmeblyExplorer.Core;

public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Op { public IntPtr hwnd; public uint wFunc; [MarshalAs(UnmanagedType.LPWStr)] public string pFrom; [MarshalAs(UnmanagedType.LPWStr)] public string? pTo; public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool aborted; public IntPtr map; [MarshalAs(UnmanagedType.LPWStr)] public string? title; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref Op op);
    public static Task<bool> Recycle(IEnumerable<string> paths) => Task.Run(() => { var o = new Op { wFunc = 3, pFrom = string.Join('\0', paths) + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 }; return SHFileOperationW(ref o) == 0; });
    /// <summary>Windows' own copy (what File Explorer uses), silent, for the speed comparison.</summary>
    public static bool WindowsCopy(string from, string to) { var o = new Op { wFunc = 2, pFrom = from + "\0\0", pTo = to + "\0\0", fFlags = 0x10 | 0x4 | 0x200 | 0x400 }; return SHFileOperationW(ref o) == 0; }
}
