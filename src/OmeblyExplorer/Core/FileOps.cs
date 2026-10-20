// Copy, move, delete, rename and new folders, through Windows' own shell operations: deletes go to the Recycle
// Bin, name clashes get "(2)" like Explorer, and Windows' progress window shows for big jobs. (Stage 3 replaces the
// copy and move with Omebly's own faster engine and progress card.) Clipboard and drag-and-drop work with Explorer.
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace OmeblyExplorer.Core;

public static class FileOps
{
    public static IntPtr Owner;

    private static Task<bool> Shell(uint func, IEnumerable<string> from, string? to, ushort flags) => Task.Run(() =>
    {
        var (r, aborted) = Native.FileOperation(Owner, func, Native.DoubleNul(from), to == null ? null : to + "\0\0", flags);
        return r == 0 && !aborted;
    });

    public static Task<bool> Copy(IEnumerable<string> paths, string target) => Shell(Native.FO_COPY, paths, target, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | RenameIfSameFolder(paths, target)));
    public static Task<bool> Move(IEnumerable<string> paths, string target)
    {
        var list = paths.Where((p) => !string.Equals(System.IO.Path.GetDirectoryName(p.TrimEnd('\\')), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
        return list.Count == 0 ? Task.FromResult(true) : Shell(Native.FO_MOVE, list, target, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR));
    }
    /// <summary>To the Recycle Bin (Windows asks before deleting forever only if the bin can't take it).</summary>
    public static Task<bool> Recycle(IEnumerable<string> paths) => Shell(Native.FO_DELETE, paths, null, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_WANTNUKEWARNING));
    public static Task<bool> DeleteForever(IEnumerable<string> paths) => Shell(Native.FO_DELETE, paths, null, Native.FOF_NOCONFIRMATION);
    public static Task<bool> Rename(string path, string newName) => Shell(Native.FO_RENAME, new[] { path }, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path.TrimEnd('\\'))!, newName), Native.FOF_ALLOWUNDO);

    // Copying into the same folder (Ctrl+C, Ctrl+V) makes "name - Copy", never overwrites.
    private static ushort RenameIfSameFolder(IEnumerable<string> paths, string target) =>
        paths.Any((p) => string.Equals(System.IO.Path.GetDirectoryName(p.TrimEnd('\\')), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) ? Native.FOF_RENAMEONCOLLISION : (ushort)0;

    /// <summary>A name that isn't taken yet in <paramref name="folder"/>: "New folder", "New folder (2)" ...</summary>
    public static string FreeName(string folder, string name, string ext = "")
    {
        var candidate = name + ext;
        for (int n = 2; Directory.Exists(System.IO.Path.Combine(folder, candidate)) || File.Exists(System.IO.Path.Combine(folder, candidate)); n++) candidate = $"{name} ({n}){ext}";
        return candidate;
    }

    /// <summary>Windows file names can't contain these, and can't be these names.</summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Please type a name.";
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain any of these: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "A name can't end with a dot or a space.";
        var bare = System.IO.Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (bare is "CON" or "PRN" or "AUX" or "NUL" || (bare.Length == 4 && (bare.StartsWith("COM") || bare.StartsWith("LPT")) && char.IsDigit(bare[3]))) return "Windows keeps that name for itself. Please choose another.";
        return null;
    }
}

public static class Clip
{
    public static async Task<List<IStorageItem>> ToStorageItems(IEnumerable<(string path, bool folder)> items)
    {
        var list = new List<IStorageItem>();
        foreach (var (path, folder) in items)
        {
            try { list.Add(folder ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path)); }
            catch { /* gone or not reachable: left out */ }
        }
        return list;
    }

    /// <summary>Puts files on the clipboard, so they paste in Explorer, Chrome, WhatsApp and any app too.</summary>
    public static async Task Put(IReadOnlyList<FileItem> items, bool cut)
    {
        var dp = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        dp.SetStorageItems(await ToStorageItems(items.Select((i) => (i.Path, i.IsFolder))), readOnly: false);
        dp.SetText(string.Join(Environment.NewLine, items.Select((i) => i.Path)));
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }

    /// <summary>The files on the clipboard (from Omebly or any app), and whether they were cut.</summary>
    public static async Task<(List<string> paths, bool cut)> Get()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.StorageItems)) return (new(), false);
        var items = await view.GetStorageItemsAsync();
        return (items.Select((i) => i.Path).Where((p) => !string.IsNullOrEmpty(p)).ToList(), view.RequestedOperation.HasFlag(DataPackageOperation.Move));
    }

    public static bool HasFiles() { try { return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems); } catch { return false; } }
}
