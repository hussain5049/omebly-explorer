// The few Windows functions Omebly Explorer calls directly: fast folder listing, shell thumbnails and icons,
// known folders, and the shell's own file operations (Recycle Bin, undo in Windows, progress for big jobs).
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OmeblyExplorer.Core;

internal static partial class Native
{
    // ---------- Fast folder listing ----------
    public const int FindExInfoBasic = 1, FindExSearchNameMatch = 0, FIND_FIRST_EX_LARGE_FETCH = 2;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATA
    {
        public FileAttributes dwFileAttributes;
        public FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    /// <summary>Windows' own "natural" order: file2 before file10, like Explorer.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string psz1, string psz2);

    // ---------- Known folders (Desktop, Downloads ...) ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    public static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken);
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    // ---------- Thumbnails and icons ----------
    [Flags] public enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10, CropToSquare = 0x20, WideThumbnails = 0x40, IconBackground = 0x80, ScaleUp = 0x100 }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int c, out BITMAP pv);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>A file's thumbnail or icon as top-down BGRA pixels (premultiplied), or null.</summary>
    public static (byte[] pixels, int w, int h)? GetImage(string path, int size, SIIGBF flags)
    {
        object? o = null; IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out o);
            var f = (IShellItemImageFactory)o;
            if (f.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;
            var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            var dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            // Some icons come without alpha (all zero): make them opaque so they don't disappear.
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return (px, w, h);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (o != null) Marshal.ReleaseComObject(o);
        }
    }

    // ---------- The shell's file operations (copy/move/delete with the Recycle Bin and Windows' progress) ----------
    // shellapi.h packs this to 1 byte in 32-bit Windows only; 64-bit uses normal alignment. Both shapes are here and
    // the right one is used at run time (x86 builds run on 32-bit layouts, x64 and ARM64 on 64-bit).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings; [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct SHFILEOPSTRUCT32
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings; [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    public const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3, FO_RENAME = 4;
    public const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMMKDIR = 0x200, FOF_WANTNUKEWARNING = 0x4000;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation64(ref SHFILEOPSTRUCT lpFileOp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 lpFileOp);

    /// <summary>Runs a shell file operation; returns (ok, cancelled by the person).</summary>
    public static (int result, bool aborted) FileOperation(IntPtr hwnd, uint func, string from, string? to, ushort flags)
    {
        if (IntPtr.Size == 8)
        {
            var op = new SHFILEOPSTRUCT { hwnd = hwnd, wFunc = func, pFrom = from, pTo = to, fFlags = flags };
            var r = SHFileOperation64(ref op); return (r, op.fAnyOperationsAborted);
        }
        var op32 = new SHFILEOPSTRUCT32 { hwnd = hwnd, wFunc = func, pFrom = from, pTo = to, fFlags = flags };
        var r32 = SHFileOperation32(ref op32); return (r32, op32.fAnyOperationsAborted);
    }

    public static string DoubleNul(IEnumerable<string> paths) => string.Join('\0', paths) + "\0\0";

    // ---------- Recycle Bin ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    // ---------- Opening files with their app, and the "Open with" / properties windows ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow; public IntPtr hInstApp, lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
    }
    public const uint SEE_MASK_INVOKEIDLIST = 0x0C;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);
    /// <summary>Tells Windows (and File Explorer windows) that something in this file's folder changed.</summary>
    /// <summary>Tells Windows that which app opens what has changed (after taking over or giving back folders).</summary>
    public static void NotifyAssociations() { try { SHChangeNotify(0x08000000 /* ASSOCCHANGED */, 0, null, IntPtr.Zero); } catch { } }
    public static void NotifyChanged(string path) { try { SHChangeNotify(0x00001000 /* UPDATEDIR */, 0x0005 /* PATHW */, System.IO.Path.GetDirectoryName(path), IntPtr.Zero); } catch { } }

    public static void ShellRun(string path, string? verb = null, IntPtr hwnd = default)
    {
        var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), lpFile = path, lpVerb = verb, nShow = 1, hwnd = hwnd, fMask = verb is "properties" or "openas" ? SEE_MASK_INVOKEIDLIST : 0 };
        ShellExecuteExW(ref info);
    }
}
