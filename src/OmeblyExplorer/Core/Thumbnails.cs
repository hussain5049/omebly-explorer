// Thumbnails and icons from Windows' own thumbnail cache, made on two background threads so scrolling never
// waits. Items ask when they come into view; the newest requests go first, so the screen you look at fills first.
using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OmeblyExplorer.Core;

public static class Thumbnails
{
    private static DispatcherQueue? _ui;
    private static readonly object Gate = new();
    private static readonly LinkedList<(FileItem item, int size)> Queue = new(); // newest first
    private static readonly SemaphoreSlim Work = new(0);
    // Same-looking icons (all .txt files, all folders) are made once.
    private static readonly ConcurrentDictionary<string, ImageSource> Shared = new();
    private static readonly HashSet<string> Unique = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msi" };

    public static void Start(DispatcherQueue ui)
    {
        _ui = ui;
        for (int i = 0; i < 2; i++)
        {
            var t = new Thread(Loop) { IsBackground = true, Name = "thumbnails", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA); // some thumbnail makers (Office, PDF) need it
            t.Start();
        }
    }

    /// <summary>Ask for an item's picture at <paramref name="size"/> px (16-32: icon; bigger: thumbnail).</summary>
    public static void Request(FileItem item, int size)
    {
        if (item.IconSizeLoaded >= size) return;
        var key = SharedKey(item, size);
        if (key != null && Shared.TryGetValue(key, out var img)) { item.Icon = img; item.IconSizeLoaded = size; return; }
        lock (Gate)
        {
            for (var n = Queue.First; n != null; n = n.Next) if (n.Value.item == item) { Queue.Remove(n); break; }
            Queue.AddFirst((item, size));
            if (Queue.Count > 600) Queue.RemoveLast(); // scrolled far away: forget the oldest asks
        }
        Work.Release();
    }

    /// <summary>Items that scrolled out of view no longer need a picture.</summary>
    public static void Cancel(FileItem item) { lock (Gate) for (var n = Queue.First; n != null; n = n.Next) if (n.Value.item == item) { Queue.Remove(n); break; } }

    private static string? SharedKey(FileItem item, int size)
    {
        bool small = size <= 32;
        if (item.IsFolder) return null; // folders can have their own icon (desktop.ini)
        if (Unique.Contains(item.Extension)) return null;
        if (!small && item.Kind is FileKind.Picture or FileKind.Video or FileKind.Pdf or FileKind.Document or FileKind.Slides or FileKind.Sheet) return null; // real thumbnails
        return $"{item.Extension}|{size}";
    }

    private static void Loop()
    {
        while (true)
        {
            Work.Wait();
            (FileItem item, int size) job;
            lock (Gate) { if (Queue.First == null) continue; job = Queue.First.Value; Queue.RemoveFirst(); }
            var (item, size) = job;
            if (item.IconSizeLoaded >= size) continue;
            var flags = size <= 32 ? Native.SIIGBF.IconOnly | Native.SIIGBF.BiggerSizeOk : Native.SIIGBF.BiggerSizeOk;
            var scale = 1.5; // sharp on high-DPI screens
            var img = Native.GetImage(item.Path, (int)(size * scale), flags);
            if (img is null) continue;
            var key = SharedKey(item, size);
            _ui?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var (px, w, h) = img.Value;
                var wb = new WriteableBitmap(w, h);
                using (var s = wb.PixelBuffer.AsStream()) s.Write(px, 0, px.Length);
                wb.Invalidate();
                item.Icon = wb; item.IconSizeLoaded = size;
                if (key != null) Shared.TryAdd(key, wb);
            });
        }
    }
}
