// What you add to your files to organise them: folder colours (also seen in Windows, through desktop.ini and a
// coloured folder icon), colour tags, notes, favourite files, the shelf, folder templates and workspaces. Kept in one
// small file on this PC (%LOCALAPPDATA%\Omebly\Explorer\library.json); nothing is uploaded.
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OmeblyExplorer.Core;

public sealed class Library
{
    public Dictionary<string, string> FolderColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // path -> tag colours
    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Favorites { get; set; } = new();
    public List<string> Shelf { get; set; } = new();
    public List<FolderTemplate> Templates { get; set; } = new();
    public List<Workspace> Workspaces { get; set; } = new();
    public List<Server> Servers { get; set; } = new();
    public Dictionary<string, string> HostKeys { get; set; } = new();  // SFTP servers' keys, remembered the first time
    public List<Rule> Rules { get; set; } = new();
    public List<Backup> Backups { get; set; } = new();

    public static readonly (string color, string name)[] TagColors = { ("#FF5A5F", "Red"), ("#FF9F43", "Orange"), ("#F4C04E", "Yellow"), ("#2EF2B3", "Green"), ("#4C9BFF", "Blue"), ("#9B7BFF", "Purple"), ("#B9C4C0", "Grey") };
    public static readonly FolderTemplate[] BuiltIn =
    {
        new() { Name = "Client project", Folders = new() { "Brief", "Designs", "Invoices", "Final" } },
        new() { Name = "College", Folders = new() { "Notes", "Assignments", "Exams", "Projects" } },
        new() { Name = "Photo shoot", Folders = new() { "Raw", "Edited", "Exports", "Shared" } },
        new() { Name = "App or website", Folders = new() { "Design", "Code", "Docs", "Releases" } },
        new() { Name = "Video", Folders = new() { "Footage", "Audio", "Graphics", "Exports" } },
    };

    private static readonly string Dir = Environment.GetEnvironmentVariable("OMEBLY_SETTINGS_DIR") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer");
    private static readonly string FilePath = Path.Combine(Dir, "library.json");
    public static Library Current { get; private set; } = Load();
    public static event Action? Changed;

    private static Library Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var l = JsonSerializer.Deserialize<Library>(File.ReadAllText(FilePath)) ?? new();
            l.FolderColors = new(l.FolderColors.Where((c) => Launch.IsColor(c.Value)), StringComparer.OrdinalIgnoreCase);
            l.Tags = new(l.Tags.Where((t) => t.Value != null).ToDictionary((t) => t.Key, (t) => t.Value.Where(Launch.IsColor).Distinct().ToList()), StringComparer.OrdinalIgnoreCase);
            l.Notes = new(l.Notes, StringComparer.OrdinalIgnoreCase);
            return l;
        }
        catch { return new(); }
    }
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
        Changed?.Invoke();
    }

    public string? ColorOf(string folder) => FolderColors.TryGetValue(folder.TrimEnd('\\'), out var c) ? c : null;
    public IReadOnlyList<string> TagsOf(string path) => Tags.TryGetValue(path, out var t) ? t : Array.Empty<string>();
    public string? NoteOf(string path) => Notes.TryGetValue(path, out var n) ? n : null;

    /// <summary>When files are renamed or moved inside Omebly, what was added to them goes with them.</summary>
    public void Moved(string from, string to)
    {
        bool changed = false;
        void Move<T>(Dictionary<string, T> d)
        {
            foreach (var k in d.Keys.Where((k) => k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            { var v = d[k]; d.Remove(k); d[to + k[from.Length..]] = v; changed = true; }
        }
        Move(FolderColors); Move(Tags); Move(Notes);
        for (int i = 0; i < Favorites.Count; i++) if (Favorites[i].Equals(from, StringComparison.OrdinalIgnoreCase)) { Favorites[i] = to; changed = true; }
        for (int i = 0; i < Shelf.Count; i++) if (Shelf[i].Equals(from, StringComparison.OrdinalIgnoreCase)) { Shelf[i] = to; changed = true; }
        if (changed) Save();
    }
}

public sealed class FolderTemplate { public string Name { get; set; } = ""; public List<string> Folders { get; set; } = new(); }
public sealed class Workspace { public string Name { get; set; } = ""; public List<string> Tabs { get; set; } = new(); }

/// <summary>Coloured folders that Windows shows too: a folder icon in that colour and a desktop.ini that points to it.</summary>
public static class FolderLook
{
    private static readonly string IconDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer", "FolderIcons");

    public static async Task<bool> SetColor(string folder, string? color)
    {
        folder = folder.TrimEnd('\\');
        if (color == null) Library.Current.FolderColors.Remove(folder); else Library.Current.FolderColors[folder] = color;
        Library.Save();
        try
        {
            var ini = Path.Combine(folder, "desktop.ini");
            if (color == null)
            {
                // Only our own desktop.ini lines go; a desktop.ini that was there before keeps everything else.
                if (File.Exists(ini)) { var lines = File.ReadAllLines(ini).Where((l) => !l.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) || !l.Contains("Omebly", StringComparison.OrdinalIgnoreCase)).ToList(); File.SetAttributes(ini, FileAttributes.Normal); if (lines.All((l) => l.Trim().Length == 0 || l.Trim().Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase))) { File.Delete(ini); File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.ReadOnly); /* nothing left to show: the folder goes back to normal */ } else { File.WriteAllLines(ini, lines); File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System); } }
            }
            else
            {
                var icon = await IconFor(color);
                var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : new List<string>();
                if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
                lines.RemoveAll((l) => l.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase));
                int at = lines.FindIndex((l) => l.Trim().Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase));
                if (at < 0) { lines.Insert(0, "[.ShellClassInfo]"); at = 0; }
                lines.Insert(at + 1, $"IconResource={icon},0");
                File.WriteAllLines(ini, lines);
                File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
                // Windows only reads desktop.ini in folders marked read-only or system (this doesn't make the files read-only).
                File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
            }
            Native.NotifyChanged(Path.Combine(folder, "x"));
            return true;
        }
        catch { return false; } // the colour still shows in Omebly Explorer
    }

    /// <summary>A folder icon (16 to 256 pixels) in this colour, drawn once and kept.</summary>
    public static async Task<string> IconFor(string color)
    {
        Directory.CreateDirectory(IconDir);
        var path = Path.Combine(IconDir, $"Omebly folder {color.TrimStart('#')}.ico");
        if (File.Exists(path)) return path;
        var c = Convert.ToUInt32(color.TrimStart('#'), 16);
        byte r = (byte)(c >> 16), g = (byte)(c >> 8), b = (byte)c;
        var sizes = new[] { 16, 24, 32, 48, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes) pngs.Add(await Png(DrawFolder(s, r, g, b), s));
        using var f = new FileStream(path + ".tmp", FileMode.Create);
        using (var bw = new BinaryWriter(f))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) { bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((short)0); bw.Write((short)1); bw.Write((short)32); bw.Write(pngs[i].Length); bw.Write(offset); offset += pngs[i].Length; }
            foreach (var p in pngs) bw.Write(p);
        }
        File.Move(path + ".tmp", path, true);
        return path;
    }

    /// <summary>A rounded folder shape: a darker back with the tab, a lighter front (BGRA, premultiplied).</summary>
    private static byte[] DrawFolder(int s, byte r, byte g, byte b)
    {
        var px = new byte[s * s * 4];
        double k = s / 32.0;
        bool In(double x, double y, double x0, double y0, double x1, double y1, double rad)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1) return false;
            double cx = Math.Clamp(x, x0 + rad, x1 - rad), cy = Math.Clamp(y, y0 + rad, y1 - rad);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad;
        }
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                double fx = x + 0.5, fy = y + 0.5;
                bool back = In(fx, fy, 2 * k, 6 * k, 30 * k, 27 * k, 2.5 * k) || In(fx, fy, 2 * k, 4 * k, 13 * k, 9 * k, 1.8 * k);
                bool front = In(fx, fy, 2 * k, 10 * k, 30 * k, 27 * k, 2.5 * k);
                if (!back && !front) continue;
                double shade = front ? 1.0 : 0.78;
                int i = (y * s + x) * 4;
                px[i] = (byte)(b * shade); px[i + 1] = (byte)(g * shade); px[i + 2] = (byte)(r * shade); px[i + 3] = 255;
            }
        return px;
    }
    private static async Task<byte[]> Png(byte[] bgra, int s)
    {
        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)s, (uint)s, 96, 96, bgra);
        await enc.FlushAsync();
        var bytes = new byte[ms.Size];
        await ms.AsStreamForRead().ReadExactlyAsync(bytes);
        return bytes;
    }
}
