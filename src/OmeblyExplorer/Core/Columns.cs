// The columns of the details view: the everyday ones come from the folder listing itself (fast); the rest are
// Windows properties (dimensions, length, author, date taken, rating...) read only for rows on screen.
using System.Runtime.InteropServices;

namespace OmeblyExplorer.Core;

public sealed record Column(string Key, string Header, double Width, bool Right = false, string? Property = null, bool Sortable = false)
{
    public bool Builtin => Property == null;
}

public static class Columns
{
    public static readonly Column[] All =
    {
        new("name", "Name", 320, Sortable: true),
        new("modified", "Date modified", 170, Sortable: true),
        new("type", "Type", 170, Sortable: true),
        new("size", "Size", 100, Right: true, Sortable: true),
        new("created", "Date created", 170, Sortable: true),
        new("accessed", "Date accessed", 170),
        new("attributes", "Attributes", 90),
        new("folder", "Folder", 240),
        new("status", "Cloud status", 150),
        new("dimensions", "Dimensions", 120, Property: "System.Image.Dimensions"),
        new("length", "Length", 90, Property: "System.Media.Duration"),
        new("taken", "Date taken", 170, Property: "System.Photo.DateTaken"),
        new("camera", "Camera model", 150, Property: "System.Photo.CameraModel"),
        new("title", "Title", 200, Property: "System.Title"),
        new("authors", "Authors", 160, Property: "System.Author"),
        new("artist", "Contributing artists", 170, Property: "System.Music.Artist"),
        new("album", "Album", 170, Property: "System.Music.AlbumTitle"),
        new("rating", "Rating", 110, Property: "System.Rating"),
        new("tags", "Tags", 160, Property: "System.Keywords"),
        new("pages", "Pages", 70, Right: true, Property: "System.Document.PageCount"),
        new("bitrate", "Bit rate", 100, Property: "System.Audio.EncodingBitrate"),
        new("framerate", "Frame rate", 100, Property: "System.Video.FrameRate"),
        new("company", "Company", 150, Property: "System.Company"),
        new("version", "File version", 120, Property: "System.FileVersion"),
        new("owner", "Owner", 160, Property: "System.FileOwner"),
    };
    public static Column Get(string key) => All.FirstOrDefault((c) => c.Key == key) ?? All[0];

    /// <summary>The text for a built-in column (extended ones come from <see cref="Props"/>).</summary>
    public static string Text(FileItem f, string key) => key switch
    {
        "name" => f.DisplayName,
        "modified" => f.ModifiedText,
        "type" => f.TypeText,
        "size" => f.IsFolder ? (f.FolderSize is { } fs ? Format.Size(fs) : "") : f.SizeText,
        "created" => f.Created == default ? "" : Format.When(f.Created),
        "accessed" => f.Accessed == default ? "" : Format.When(f.Accessed),
        "attributes" => Attr(f.Attributes),
        "folder" => System.IO.Path.GetDirectoryName(f.Path) ?? "",
        "status" => CloudFiles.StateOf(f),
        _ => f.Props != null && f.Props.TryGetValue(key, out var v) ? v : "",
    };
    private static string Attr(FileAttributes a) => string.Concat(
        (a & FileAttributes.ReadOnly) != 0 ? "R" : "", (a & FileAttributes.Hidden) != 0 ? "H" : "", (a & FileAttributes.System) != 0 ? "S" : "",
        (a & FileAttributes.Archive) != 0 ? "A" : "", (a & FileAttributes.Compressed) != 0 ? "C" : "", (a & FileAttributes.Encrypted) != 0 ? "E" : "",
        (a & FileAttributes.Offline) != 0 || (a & (FileAttributes)0x400000) != 0 ? "O" : "");
}

/// <summary>Windows properties for a file, formatted the way Explorer shows them ("1920 x 1080", "00:03:12").</summary>
public static class Props
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p1, p2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out PROPERTYKEY key);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSFormatForDisplayAlloc(ref PROPERTYKEY key, ref PROPVARIANT pv, int flags, out IntPtr text);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int InitPropVariantFromStringAsVector(string text, out PROPVARIANT pv);
    [DllImport("propsys.dll")] private static extern int PSCoerceToCanonicalValue(ref PROPERTYKEY key, ref PROPVARIANT pv);

    private static readonly Dictionary<string, PROPERTYKEY> Keys = new();
    private static bool KeyFor(string name, out PROPERTYKEY k) { lock (Keys) { if (Keys.TryGetValue(name, out k)) return true; if (PSGetPropertyKeyFromName(name, out k) != 0) return false; Keys[name] = k; return true; } }

    /// <summary>Writes one property into the file (title, tags, authors, comments, rating), like Explorer's details
    /// pane. Lists (tags, authors) are separated by ";". Returns false if the file type can't hold it.</summary>
    public static bool Write(string path, string prop, string value, bool list = false)
    {
        if (!KeyFor(prop, out var key)) return false;
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0x2 /* READWRITE */, ref iid, out var store) != 0) return false;
        var pv = new PROPVARIANT();
        try
        {
            if (list) { if (InitPropVariantFromStringAsVector(value, out pv) != 0) return false; }
            else { pv.vt = 31; /* VT_LPWSTR */ pv.p1 = Marshal.StringToCoTaskMemUni(value); }
            if (PSCoerceToCanonicalValue(ref key, ref pv) < 0) return false;
            store.SetValue(ref key, ref pv);
            store.Commit();
            return true;
        }
        catch { return false; }
        finally { PropVariantClear(ref pv); Marshal.ReleaseComObject(store); }
    }

    /// <summary>Reads these properties (canonical Windows names) for one file. Background threads only.</summary>
    public static Dictionary<string, string> Read(string path, IEnumerable<(string col, string prop)> wanted)
    {
        var result = new Dictionary<string, string>();
        var iid = typeof(IPropertyStore).GUID;
        // GPS_BESTEFFORT | GPS_OPENSLOWITEM-free: never wakes cloud-only files (no download just to show a column).
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0x40 /* BESTEFFORT */, ref iid, out var store) != 0) return result;
        try
        {
            foreach (var (col, prop) in wanted)
            {
                if (!KeyFor(prop, out var key)) continue;
                if (store.GetValue(ref key, out var pv) != 0) continue;
                try
                {
                    if (pv.vt == 0) continue; // empty
                    if (PSFormatForDisplayAlloc(ref key, ref pv, 0, out var text) == 0 && text != IntPtr.Zero)
                    {
                        var s = Marshal.PtrToStringUni(text) ?? ""; Marshal.FreeCoTaskMem(text);
                        if (s.Length > 0) result[col] = s.Replace("‎", "").Replace("‏", "");
                    }
                }
                finally { PropVariantClear(ref pv); }
            }
        }
        finally { Marshal.ReleaseComObject(store); }
        return result;
    }
}
