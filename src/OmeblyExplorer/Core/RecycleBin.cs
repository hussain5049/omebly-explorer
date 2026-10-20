// The Recycle Bin read straight from disk, like Windows does it: every drive keeps the person's deleted things in
// \$Recycle.Bin\<their account id>\, a "$R…" file or folder with the content and a "$I…" file saying where it came
// from, when, and how big it was. Restoring moves the $R item back (same drive, so instant) and removes its $I.
using System.Security.Principal;
using System.Text;

namespace OmeblyExplorer.Core;

public sealed class BinItem
{
    public required string Name { get; init; }        // the original name
    public required string OriginalPath { get; init; } // where it was
    public required string DataPath { get; init; }     // the $R… item holding it
    public required string InfoPath { get; init; }     // the $I… file
    public DateTime Deleted { get; init; }
    public long Size { get; init; }
    public bool IsFolder { get; init; }
    public string From => Path.GetDirectoryName(OriginalPath) ?? "";
    public override string ToString() => Name;
}

public static class RecycleBin
{
    private static string? _sid;
    private static string Sid => _sid ??= WindowsIdentity.GetCurrent().User?.Value ?? "";

    /// <summary>This person's bin folder on each drive that has one.</summary>
    private static IEnumerable<string> Folders()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            // Fixed drives only: a USB stick or a disk image could carry a made-up bin (Security 339).
            if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
            var f = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", Sid);
            if (!Directory.Exists(f)) continue;
            try { if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; } // a link to somewhere else: not a real bin
            yield return f;
        }
    }

    public static List<BinItem> Items()
    {
        var list = new List<BinItem>();
        foreach (var folder in Folders())
        {
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(folder, "$I*").ToList(); } catch { continue; }
            foreach (var info in infos)
            {
                var item = Read(info);
                if (item != null) list.Add(item);
            }
        }
        return list.OrderByDescending((i) => i.Deleted).ToList();
    }

    /// <summary>A $I file: version, size, time deleted, then the original path (fixed 260 characters in version 1,
    /// counted in version 2).</summary>
    public static BinItem? Read(string info)
    {
        try
        {
            var b = File.ReadAllBytes(info);
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0), size = BitConverter.ToInt64(b, 8), time = BitConverter.ToInt64(b, 16);
            string original;
            if (version == 1) original = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            else if (version == 2 && b.Length >= 28) { int chars = BitConverter.ToInt32(b, 24); original = Encoding.Unicode.GetString(b, 28, Math.Min(chars * 2, b.Length - 28)); }
            else return null;
            original = original.TrimEnd('\0');
            int nul = original.IndexOf('\0'); if (nul >= 0) original = original[..nul];
            if (!SafeOriginal(original, info)) return null;
            var data = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
            bool folder = Directory.Exists(data);
            if (!folder && !File.Exists(data)) return null; // the content is gone: nothing to show or restore
            if ((File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0) return null; // a link, not deleted content
            return new BinItem
            {
                Name = Path.GetFileName(original.TrimEnd('\\')), OriginalPath = original, DataPath = data, InfoPath = info,
                Deleted = time > 0 ? DateTime.FromFileTime(time) : File.GetCreationTime(info), Size = size, IsFolder = folder,
            };
        }
        catch { return null; }
    }

    /// <summary>Where a bin entry says it came from must be a plain local path on the bin's own drive ("C:\\...",
    /// no network or \\?\\ paths, no ".."), so a made-up entry can't put things anywhere else (Security 339).</summary>
    public static bool SafeOriginal(string original, string infoPath)
    {
        if (original.Length < 4 || !char.IsAsciiLetter(original[0]) || original[1] != ':' || original[2] != '\\') return false;
        if (original.StartsWith(@"\\") || original.Contains('/') || original.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (original.Split('\\').Any((part) => part is ".." or ".")) return false;
        if (!Path.IsPathFullyQualified(original)) return false;
        return string.Equals(Path.GetPathRoot(original), Path.GetPathRoot(infoPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Puts it back where it was (with "(2)" if that name is taken now). Returns where it went, or null.</summary>
    public static string? Restore(BinItem item)
    {
        try
        {
            var dir = Path.GetDirectoryName(item.OriginalPath)!;
            Directory.CreateDirectory(dir);
            var target = item.OriginalPath;
            if (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(dir, FileOps.FreeName(dir, Path.GetFileNameWithoutExtension(item.Name), item.IsFolder ? "" : Path.GetExtension(item.Name)));
            if (item.IsFolder) Directory.Move(item.DataPath, target); else File.Move(item.DataPath, target);
            try { File.Delete(item.InfoPath); } catch { }
            Native.NotifyChanged(item.InfoPath);
            return target;
        }
        catch { return null; }
    }

    /// <summary>Deleted for good: the content and its $I file.</summary>
    public static bool DeleteForever(BinItem item)
    {
        try
        {
            if (item.IsFolder)
            {
                // Never step through links inside it (they would lead outside the bin).
                var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                foreach (var f in Directory.EnumerateFiles(item.DataPath, "*", opts)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(item.DataPath, true); // removes links themselves, not what they point to
            }
            else { File.SetAttributes(item.DataPath, FileAttributes.Normal); File.Delete(item.DataPath); }
            File.Delete(item.InfoPath);
            Native.NotifyChanged(item.InfoPath);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The newest bin entries for these original paths, deleted at or after <paramref name="since"/> (for Undo).</summary>
    public static List<BinItem> Find(IEnumerable<string> originals, DateTime since)
    {
        var want = new HashSet<string>(originals.Select((p) => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        return Items().Where((i) => want.Contains(i.OriginalPath.TrimEnd('\\')) && i.Deleted >= since.AddSeconds(-5))
            .GroupBy((i) => i.OriginalPath, StringComparer.OrdinalIgnoreCase).Select((g) => g.First()).ToList();
    }
}
