// The "Jump to" bar: when any app shows Windows' Open or Save window (Photoshop, Chrome uploads, Word...), a small
// Omebly bar sits on top of it with your pinned folders, the folder open in Omebly, and the usual places. One click
// takes that window there. Nothing is injected into other apps: Omebly only watches for the window (an accessibility
// event) and types the folder into its file-name box, as a person would, then puts back what was there.
using System.Runtime.InteropServices;
using System.Text;

namespace OmeblyExplorer.Core;

public static class DialogWatcher
{
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, StringBuilder? l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    private static WinEventProc? _proc; // kept alive while hooked
    private static IntPtr _show, _gone, _move;
    public static event Action<IntPtr>? DialogShown;
    public static event Action<IntPtr>? DialogGone;
    public static event Action<IntPtr>? DialogMoved;
    public static bool Running => _show != IntPtr.Zero;

    public static void Start()
    {
        if (Running) return;
        _proc = OnEvent;
        const uint OUTOFCONTEXT = 0x0000, SKIPOWN = 0x0002;
        _show = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _proc, 0, 0, OUTOFCONTEXT | SKIPOWN);        // shown
        _gone = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, _proc, 0, 0, OUTOFCONTEXT | SKIPOWN);        // destroyed
        _move = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _proc, 0, 0, OUTOFCONTEXT | SKIPOWN);        // moved or sized
    }
    public static void Stop()
    {
        foreach (var h in new[] { _show, _gone, _move }) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _show = _gone = _move = IntPtr.Zero;
    }

    private static readonly HashSet<IntPtr> Dialogs = new();
    private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || hwnd == IntPtr.Zero) return; // whole windows only
        if (ev == 0x8001) { if (Dialogs.Remove(hwnd)) DialogGone?.Invoke(hwnd); return; }
        if (ev == 0x800B) { if (Dialogs.Contains(hwnd)) DialogMoved?.Invoke(hwnd); return; }
        if (!IsFileDialog(hwnd) || !Dialogs.Add(hwnd)) return;
        DialogShown?.Invoke(hwnd);
    }

    /// <summary>Windows' own Open/Save window (the modern one): a dialog box holding the folder view and a file-name box.</summary>
    public static bool IsFileDialog(IntPtr h)
    {
        if (ClassOf(h) != "#32770" || !IsWindowVisible(h)) return false;
        return Find(h, "DUIViewWndClassName") != IntPtr.Zero && FileNameBox(h) != IntPtr.Zero;
    }
    private static string ClassOf(IntPtr h) { var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString(); }
    private static IntPtr Find(IntPtr parent, string cls)
    {
        for (var c = FindWindowExW(parent, IntPtr.Zero, null, null); c != IntPtr.Zero; c = FindWindowExW(parent, c, null, null))
        {
            if (ClassOf(c) == cls) return c;
            var deeper = Find(c, cls); if (deeper != IntPtr.Zero) return deeper;
        }
        return IntPtr.Zero;
    }
    /// <summary>The "File name" box: an Edit inside a ComboBox inside a ComboBoxEx32.</summary>
    private static IntPtr FileNameBox(IntPtr dialog)
    {
        var ex = Find(dialog, "ComboBoxEx32"); if (ex == IntPtr.Zero) return IntPtr.Zero;
        var combo = Find(ex, "ComboBox"); if (combo == IntPtr.Zero) return IntPtr.Zero;
        return Find(combo, "Edit");
    }

    /// <summary>Takes the Open/Save window to this folder, then puts the file name back (so a Save keeps its name).</summary>
    public static async Task GoTo(IntPtr dialog, string folder)
    {
        if (!IsWindow(dialog) || !Directory.Exists(folder)) return;
        var edit = FileNameBox(dialog); if (edit == IntPtr.Zero) return;
        const uint WM_GETTEXT = 0x000D, WM_SETTEXT = 0x000C, WM_COMMAND = 0x0111;
        var old = new StringBuilder(1024); SendMessageW(edit, WM_GETTEXT, (IntPtr)1024, old);
        SetForegroundWindow(dialog);
        SendMessageW(edit, WM_SETTEXT, IntPtr.Zero, folder.TrimEnd('\\') + "\\");
        PostMessageW(dialog, WM_COMMAND, (IntPtr)1 /* IDOK: with a folder in the box, the window goes there */, IntPtr.Zero);
        await Task.Delay(350);
        if (IsWindow(dialog)) SendMessageW(edit, WM_SETTEXT, IntPtr.Zero, old.ToString());
    }
}
