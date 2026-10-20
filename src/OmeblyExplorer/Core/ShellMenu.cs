// "Show more options": Windows' own right-click menu for the selected files, with everything other apps add to it
// (7-Zip, Photoshop, antivirus, Send to, Open with). Same menu File Explorer shows.
using System.Runtime.InteropServices;

namespace OmeblyExplorer.Core;

public static class ShellMenu
{
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, IntPtr pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters; [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow; public uint dwHotKey; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle; public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW; [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW; public POINT ptInvoke;
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder ppshf);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
    private const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint FirstId = 1, LastId = 0x7FFF;
    private static IContextMenu3? _current; private static SubclassProc? _proc;

    /// <summary>Shows Windows' menu for <paramref name="paths"/> (all in one folder) at the mouse.</summary>
    public static void Show(IntPtr hwnd, IReadOnlyList<string> paths, bool extended = false)
    {
        if (paths.Count == 0) return;
        var pidls = new List<IntPtr>(); IntPtr parentPtr = IntPtr.Zero, cmPtr = IntPtr.Zero, menu = IntPtr.Zero, host = IntPtr.Zero;
        try
        {
            var children = new List<IntPtr>();
            foreach (var p in paths)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0) continue;
                pidls.Add(pidl);
                if (parentPtr == IntPtr.Zero) { if (SHBindToParent(pidl, IID_IShellFolder, out parentPtr, out var last) != 0) return; children.Add(last); }
                else if (SHBindToParent(pidl, IID_IShellFolder, out var other, out var last2) == 0) { Marshal.Release(other); children.Add(last2); }
            }
            if (parentPtr == IntPtr.Zero || children.Count == 0) return;
            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            if (parent.GetUIObjectOf(hwnd, (uint)children.Count, children.ToArray(), IID_IContextMenu, IntPtr.Zero, out cmPtr) != 0) return;
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
            menu = CreatePopupMenu();
            cm.QueryContextMenu(menu, 0, FirstId, LastId, CMF_NORMAL | (extended ? CMF_EXTENDEDVERBS : 0));
            _current = cm as IContextMenu3;
            _proc = Hook;
            // The menu belongs to a small hidden window of its own, so the Explorer window's own mouse and keyboard
            // handling is never touched by Windows' menu loop (it keeps working normally afterwards).
            host = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, "STATIC", "", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var owner = host != IntPtr.Zero ? host : hwnd;
            if (_current != null) SetWindowSubclass(owner, _proc, (UIntPtr)0x4F4D, UIntPtr.Zero); // Send to, Open with: their submenus fill in
            GetCursorPos(out var pt);
            SetForegroundWindow(owner); // Windows' rule for menus: the owner is in front, then WM_NULL after, so the menu closes cleanly
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, owner, IntPtr.Zero);
            PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);
            if (_current != null) RemoveWindowSubclass(owner, _proc, (UIntPtr)0x4F4D);
            SetForegroundWindow(hwnd);
            if (cmd >= FirstId)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(), fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE, hwnd = hwnd,
                    lpVerb = (IntPtr)(cmd - FirstId), lpVerbW = (IntPtr)(cmd - FirstId), nShow = 1, ptInvoke = pt,
                    lpDirectoryW = System.IO.Path.GetDirectoryName(paths[0]),
                };
                cm.InvokeCommand(ref info);
            }
        }
        catch { /* a broken shell extension can't take the Explorer down */ }
        finally
        {
            _current = null;
            if (host != IntPtr.Zero) DestroyWindow(host);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) CoTaskMemFree(p);
        }
    }

    private const uint WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    private static IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (_current != null && msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            try { if (_current.HandleMenuMsg2(msg, wParam, lParam, out var r) == 0) return r; } catch { }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
