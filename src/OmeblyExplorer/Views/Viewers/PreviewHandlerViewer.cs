// Word, Excel, PowerPoint, Outlook mail and other files whose app installed a Windows "preview handler" (the same
// previews File Explorer's preview pane shows). Windows runs them in its own separate preview process (prevhost),
// so a damaged file can't take Omebly Explorer down. They draw in a small window placed over the viewer area.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.UI.Xaml;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views.Viewers;

public sealed class PreviewHandlerViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
    }
    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryStringW(uint flags, int str, string assoc, string extra, StringBuilder? outStr, ref uint len);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, IntPtr outer, uint ctx, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attrs, bool create, IntPtr template, out IStream stream);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    private const string IID_PreviewHandler = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private IPreviewHandler? _handler;
    private IntPtr _child;
    private IStream? _stream;

    /// <summary>The preview handler Windows knows for this kind of file, if any.</summary>
    public static Guid? HandlerFor(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        uint len = 64; var sb = new StringBuilder((int)len);
        return AssocQueryStringW(0x4 /* ASSOCF_INIT_DEFAULTTOSTAR */, 16 /* ASSOCSTR_SHELLEXTENSION */, ext, IID_PreviewHandler, sb, ref len) == 0 && Guid.TryParse(sb.ToString(), out var g) ? g : null;
    }
    public static bool HasHandler(string ext) => HandlerFor(ext) != null;

    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        var clsid = HandlerFor(file.Extension);
        if (clsid == null) { ShowMessage("No preview for this file on this PC", file.Kind is FileKind.Document or FileKind.Sheet or FileKind.Slides ? "Word, Excel and PowerPoint previews need Microsoft Office (or another app that adds previews). Try Open in its usual app." : "Try Open in its usual app."); return; }
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        object? obj = null;
        // Only in Windows' separate preview process: another app's preview code is never loaded into Omebly itself.
        if (CoCreateInstance(clsid.Value, IntPtr.Zero, 0x4 /* LOCAL_SERVER */, typeof(IPreviewHandler).GUID, out obj) != 0)
        { ShowMessage("This preview can't be shown here", "Try Open in its usual app."); return; }
        try
        {
            if (obj is IInitializeWithStream ws)
            {
                if (SHCreateStreamOnFileEx(file.Path, 0x40 /* STGM_SHARE_DENY_NONE */, 0, false, IntPtr.Zero, out var stream) != 0) throw new IOException("can't read");
                _stream = stream; ws.Initialize(stream, 0);
            }
            else if (obj is IInitializeWithFile wf) wf.Initialize(file.Path, 0);
            else throw new InvalidOperationException("no way to give it the file");
            _handler = (IPreviewHandler)obj;
            var host = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            _child = CreateWindowExW(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x02000000 /* CHILD | VISIBLE | CLIPCHILDREN */, 0, 0, 10, 10, host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            SetWindowPos(_child, IntPtr.Zero /* top */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */); // above the window's own content
            var r = Place();
            _handler.SetWindow(_child, ref r);
            _handler.DoPreview();
            SizeChanged += (_, _) => Reposition();
            LayoutUpdated += (_, _) => Reposition();
        }
        catch (Exception)
        {
            Close();
            ShowMessage("This preview couldn't open the file", "Try Open in its usual app.");
        }
    }

    private RECT _last;
    private RECT Place()
    {
        var scale = XamlRoot.RasterizationScale;
        var b = TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
        int x = (int)(b.X * scale), y = (int)(b.Y * scale), w = Math.Max(1, (int)(b.Width * scale)), h = Math.Max(1, (int)(b.Height * scale));
        MoveWindow(_child, x, y, w, h, true);
        return new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
    }
    private void Reposition()
    {
        if (_handler == null || _child == IntPtr.Zero || XamlRoot == null) return;
        var r = Place();
        if (r.Right == _last.Right && r.Bottom == _last.Bottom) return;
        _last = r;
        try { _handler.SetRect(ref r); } catch { }
    }

    public override void Close()
    {
        try { _handler?.Unload(); } catch { }
        if (_handler != null) { try { Marshal.FinalReleaseComObject(_handler); } catch { } _handler = null; }
        if (_stream != null) { try { Marshal.FinalReleaseComObject(_stream); } catch { } _stream = null; }
        if (_child != IntPtr.Zero) { DestroyWindow(_child); _child = IntPtr.Zero; }
    }
}
