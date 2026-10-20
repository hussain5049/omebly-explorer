// Lists a folder as fast as Windows allows (FindFirstFileEx with large fetch, no short names), in the background,
// handing items over in batches so the first screen of a huge folder shows at once.
namespace OmeblyExplorer.Core;

public static class DirectoryReader
{
    /// <summary>Reads <paramref name="folder"/>; <paramref name="onBatch"/> gets items in batches (on the calling thread pool).</summary>
    public static async Task<int> ReadAsync(string folder, bool showHidden, Action<List<FileItem>> onBatch, CancellationToken ct, int firstBatch = 400, int batch = 4000)
    {
        return await Task.Run(() =>
        {
            int total = 0;
            var list = new List<FileItem>(firstBatch);
            var pattern = System.IO.Path.Combine(folder, "*");
            var h = Native.FindFirstFileExW(pattern.Length >= 248 ? @"\\?\" + pattern : pattern, Native.FindExInfoBasic, out var fd, Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h == Native.INVALID_HANDLE_VALUE) throw new IOException(new System.ComponentModel.Win32Exception().Message);
            try
            {
                int limit = firstBatch;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    var name = fd.cFileName;
                    if (name is "." or "..") continue;
                    var a = fd.dwFileAttributes;
                    if (!showHidden && ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0 && (a & FileAttributes.Directory) == 0)) continue;
                    long modified = ((long)fd.ftLastWriteTime.dwHighDateTime << 32) | (uint)fd.ftLastWriteTime.dwLowDateTime;
                    long created = ((long)fd.ftCreationTime.dwHighDateTime << 32) | (uint)fd.ftCreationTime.dwLowDateTime;
                    long accessed = ((long)fd.ftLastAccessTime.dwHighDateTime << 32) | (uint)fd.ftLastAccessTime.dwLowDateTime;
                    list.Add(new FileItem
                    {
                        Name = name,
                        Path = System.IO.Path.Combine(folder, name),
                        IsFolder = (a & FileAttributes.Directory) != 0,
                        Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
                        Modified = modified > 0 ? DateTime.FromFileTime(modified) : default,
                        Created = created > 0 ? DateTime.FromFileTime(created) : default,
                        Accessed = accessed > 0 ? DateTime.FromFileTime(accessed) : default,
                        Attributes = a,
                    });
                    if (list.Count >= limit) { total += list.Count; onBatch(list); list = new List<FileItem>(batch); limit = batch; }
                } while (Native.FindNextFileW(h, out fd));
            }
            finally { Native.FindClose(h); }
            if (list.Count > 0) { total += list.Count; onBatch(list); }
            return total;
        }, ct);
    }

    /// <summary>Folders first, then Windows' natural name order (like File Explorer).</summary>
    public static int CompareByName(FileItem a, FileItem b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : Native.StrCmpLogicalW(a.Name, b.Name);
}
