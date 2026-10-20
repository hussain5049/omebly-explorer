// Zip and unzip. Reads ZIP, 7Z, RAR, TAR, GZ, BZ2, XZ (SharpCompress, MIT); makes ZIP (with an optional AES-256
// password, SharpZipLib, MIT), TAR and TAR.GZ. Extracting is safe by design: nothing is written outside the chosen
// folder (no "..", no absolute paths, no links), and a "zip bomb" that would unpack to far more than it looks is
// stopped before it fills the disk.
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace OmeblyExplorer.Core;

public sealed record ArchiveEntry(string Path, bool IsFolder, long Size, long Packed, DateTime Modified, bool Encrypted);

public sealed class ArchiveProblem : Exception { public ArchiveProblem(string m) : base(m) { } }
public sealed class NeedsPassword : Exception { public NeedsPassword() : base("This archive needs a password.") { } }

public sealed class ArchiveProgress
{
    public long Done, Total; public int Files, TotalFiles; public string Current = "";
}

public static class Archives
{
    public static readonly string[] Kinds = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".cab", ".iso_not" };
    public static bool IsArchive(string path)
    {
        var n = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return n.EndsWith(".zip") || n.EndsWith(".7z") || n.EndsWith(".rar") || n.EndsWith(".tar") || n.EndsWith(".tar.gz") || n.EndsWith(".tgz") || n.EndsWith(".gz") || n.EndsWith(".tar.bz2") || n.EndsWith(".tbz2") || n.EndsWith(".bz2") || n.EndsWith(".tar.xz") || n.EndsWith(".txz") || n.EndsWith(".xz");
    }

    /// <summary>One thing inside an archive, as each reader sees it.</summary>
    private sealed record Src(string? Key, bool IsDir, long Size, long Packed, DateTime? Modified, bool Encrypted, bool IsLink, Func<Stream> Open);

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    private static bool IsRandomAccess(string path) => path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Walks an archive: ZIP through SharpZipLib (it reads WinZip AES passwords), 7Z and RAR through
    /// SharpCompress's archive reader, TAR and the compressed streams (GZ, BZ2, XZ) through its forward reader. A
    /// stream from Open must be read before moving to the next entry.</summary>
    private static IEnumerable<Src> Walk(string path, string? password)
    {
        if (IsZip(path))
        {
            ZipFile zf;
            try { zf = new ZipFile(path); } catch (ZipException) { throw new ArchiveProblem("This file isn't a ZIP Omebly can read, or it is damaged."); }
            using (zf)
            {
                if (!string.IsNullOrEmpty(password)) zf.Password = password;
                foreach (ZipEntry e in zf)
                {
                    var entry = e;
                    bool link = (e.HostSystem == 3 /* Unix */ && ((e.ExternalFileAttributes >> 16) & 0xF000) == 0xA000);
                    yield return new Src(e.Name, e.IsDirectory, e.Size, e.CompressedSize, e.DateTime, e.IsCrypted, link, () =>
                    {
                        if (entry.IsCrypted && string.IsNullOrEmpty(password)) throw new NeedsPassword();
                        try { return zf.GetInputStream(entry); }
                        catch (ZipException ze) when (ze.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ze.Message.Contains("AES", StringComparison.OrdinalIgnoreCase)) { throw new NeedsPassword(); }
                    });
                }
            }
            yield break;
        }
        var opts = new ReaderOptions { Password = password, LookForHeader = true };
        if (IsRandomAccess(path))
        {
            IArchive a;
            try { a = ArchiveFactory.OpenArchive(path, opts); }
            catch (CryptographicException) { throw new NeedsPassword(); }
            catch (Exception e) when (e is InvalidFormatException or ArchiveOperationException) { throw new ArchiveProblem("This file isn't an archive Omebly can read, or it is damaged."); }
            using (a)
                foreach (var e in a.Entries)
                {
                    var entry = e;
                    yield return new Src(e.Key, e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime, e.IsEncrypted, e.LinkTarget != null, () =>
                    {
                        try { return entry.OpenEntryStream(); } catch (CryptographicException) { throw new NeedsPassword(); }
                    });
                }
            yield break;
        }
        using var fs = File.OpenRead(path);
        IReader r;
        try { r = ReaderFactory.OpenReader(fs, opts); }
        catch (Exception e) when (e is InvalidFormatException or ArchiveOperationException) { throw new ArchiveProblem("This file isn't an archive Omebly can read, or it is damaged."); }
        using (r)
            while (r.MoveToNextEntry())
            {
                var e = r.Entry;
                yield return new Src(e.Key, e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime, e.IsEncrypted, e.LinkTarget != null, () => r.OpenEntryStream());
            }
    }

    /// <summary>Everything inside (folders implied by file paths are added).</summary>
    public static List<ArchiveEntry> List(string path, string? password = null)
    {
        var list = new List<ArchiveEntry>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0;
        foreach (var e in Walk(path, password))
        {
            var key = e.Key is null or "" && !e.IsDir ? System.IO.Path.GetFileNameWithoutExtension(path) : Clean(e.Key); // a .gz holds one unnamed file
            if (key == null) continue;
            if (e.IsDir) { if (folders.Add(key)) list.Add(new ArchiveEntry(key, true, 0, 0, e.Modified ?? default, false)); continue; }
            if (e.IsLink) continue;
            files++;
            list.Add(new ArchiveEntry(key, false, e.Size, e.Packed, e.Modified ?? default, e.Encrypted));
            for (var d = System.IO.Path.GetDirectoryName(key); !string.IsNullOrEmpty(d); d = System.IO.Path.GetDirectoryName(d))
                if (folders.Add(d)) list.Add(new ArchiveEntry(d, true, 0, 0, default, false));
        }
        return list;
    }

    /// <summary>An archive's inside path in Windows form, or null if it tries to leave the folder (absolute, "..", drive).</summary>
    public static string? Clean(string? key)
    {
        if (key == null) return "";
        var k = key.Replace('/', '\\').Trim('\\');
        if (k.Length == 0) return null;
        if (k.Contains(':') || k.StartsWith("\\\\")) return null;
        var parts = k.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any((p) => p is ".." or "." || p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)) return null;
        return string.Join('\\', parts);
    }

    /// <summary>Extracts everything (or only <paramref name="only"/>: inside paths, folders include what's in them) into <paramref name="target"/>.
    /// Same-name files get "(2)". Returns what was written.</summary>
    public static List<string> Extract(string path, string target, string? password, IReadOnlyCollection<string>? only, ArchiveProgress progress, CancellationToken ct)
    {
        var written = new List<string>();
        var root = System.IO.Path.GetFullPath(target).TrimEnd('\\') + "\\";
        Directory.CreateDirectory(root);
        bool Wanted(string key) => only == null || only.Any((o) => key.Equals(o, StringComparison.OrdinalIgnoreCase) || key.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase));
        string? KeyOf(Src e) => e.Key is null or "" ? System.IO.Path.GetFileNameWithoutExtension(path) : Clean(e.Key);
        var pick = Walk(path, password).Where((e) => !e.IsDir && !e.IsLink).Select((e) => (e, key: KeyOf(e))).Where((x) => x.key != null && Wanted(x.key!)).Select((x) => (size: x.e.Size, x.key)).ToList();
        progress.TotalFiles = pick.Count; progress.Total = pick.Sum((x) => Math.Max(0, x.size));
        // A zip bomb: far more inside than the file's size suggests. Stop before it fills the disk.
        long packed = new FileInfo(path).Length;
        var free = new DriveInfo(System.IO.Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (progress.Total > free - (256L << 20)) throw new ArchiveProblem($"There isn't enough free space: this needs {Format.Size(progress.Total)} and the drive has {Format.Size(free)}.");
        if (progress.Total > 4L << 30 && progress.Total > packed * 200) throw new ArchiveProblem("This archive would unpack to far more than its size (a \"zip bomb\"), so it was not extracted.");
        long limit = Math.Max(progress.Total, 1) * 2 + (64L << 20); // sizes inside can lie: never write more than twice what it says
        long writtenBytes = 0;
        var wanted = new HashSet<string>(pick.Select((x) => x.key!), StringComparer.OrdinalIgnoreCase);
        foreach (var e in Walk(path, password))
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDir || e.IsLink) continue; // links inside archives are never made
            var key = KeyOf(e);
            if (key == null || !wanted.Contains(key)) continue;
            var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, key!));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // would land outside the folder
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) || Directory.Exists(dest)) dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, FileOps.FreeName(System.IO.Path.GetDirectoryName(dest)!, System.IO.Path.GetFileNameWithoutExtension(dest), System.IO.Path.GetExtension(dest)));
            progress.Current = key!;
            var tmp = dest + ".~omebly-unzip";
            try
            {
                using (var input = e.Open())
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buf = new byte[1 << 16]; int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        writtenBytes += n;
                        if (writtenBytes > limit) throw new ArchiveProblem("This archive unpacks to more than it says (a \"zip bomb\"), so extracting was stopped.");
                        output.Write(buf, 0, n);
                        Interlocked.Add(ref progress.Done, n);
                    }
                }
                File.Move(tmp, dest);
                if (e.Modified is { } t && t.Year > 1980) try { File.SetLastWriteTime(dest, t); } catch { }
                written.Add(dest);
                progress.Files++;
            }
            catch (CryptographicException) { TryDelete(tmp); throw new NeedsPassword(); }
            catch (InvalidFormatException f) when (f.Message.Contains("AES", StringComparison.OrdinalIgnoreCase) || f.Message.Contains("password", StringComparison.OrdinalIgnoreCase)) { TryDelete(tmp); throw new NeedsPassword(); }
            catch (ZipException z) when (z.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || z.Message.Contains("AES", StringComparison.OrdinalIgnoreCase)) { TryDelete(tmp); throw new NeedsPassword(); }
            catch { TryDelete(tmp); throw; }
        }
        return written;
    }
    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    /// <summary>Makes a ZIP of these files and folders (links are not followed). A password makes it AES-256.</summary>
    public static void CreateZip(IReadOnlyList<string> sources, string zipPath, string? password, ArchiveProgress progress, CancellationToken ct)
    {
        var files = Collect(sources);
        progress.TotalFiles = files.Count(f => !f.dir); progress.Total = files.Sum((f) => f.size);
        var tmp = zipPath + ".~omebly-zip";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var zip = new ZipOutputStream(fs) { IsStreamOwner = true })
            {
                zip.SetLevel(6);
                if (!string.IsNullOrEmpty(password)) zip.Password = password;
                var buf = new byte[1 << 16];
                foreach (var (full, name, dir, size) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = new ZipEntry(ZipEntry.CleanName(name) + (dir ? "/" : "")) { DateTime = dir ? Directory.GetLastWriteTime(full) : File.GetLastWriteTime(full), IsUnicodeText = true };
                    if (!dir) { entry.Size = size; if (!string.IsNullOrEmpty(password)) entry.AESKeySize = 256; }
                    zip.PutNextEntry(entry);
                    if (!dir)
                    {
                        progress.Current = name;
                        using var input = File.OpenRead(full);
                        int n; while ((n = input.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); zip.Write(buf, 0, n); Interlocked.Add(ref progress.Done, n); }
                        progress.Files++;
                    }
                    zip.CloseEntry();
                }
            }
            File.Move(tmp, zipPath);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>TAR (or TAR.GZ) of these files and folders.</summary>
    public static void CreateTar(IReadOnlyList<string> sources, string tarPath, bool gzip, ArchiveProgress progress, CancellationToken ct)
    {
        var files = Collect(sources);
        progress.TotalFiles = files.Count(f => !f.dir); progress.Total = files.Sum((f) => f.size);
        var tmp = tarPath + ".~omebly-tar";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew))
            using (var w = SharpCompress.Writers.WriterFactory.OpenWriter(fs, ArchiveType.Tar, new SharpCompress.Writers.WriterOptions(gzip ? CompressionType.GZip : CompressionType.None)))
            {
                foreach (var (full, name, dir, _) in files.Where((f) => !f.dir))
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Current = name;
                    using var input = File.OpenRead(full);
                    w.Write(name.Replace('\\', '/'), input, File.GetLastWriteTime(full));
                    Interlocked.Add(ref progress.Done, input.Length); progress.Files++;
                }
            }
            File.Move(tmp, tarPath);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>Every file and folder under the sources, with names relative to the sources' folder. Links are skipped.</summary>
    private static List<(string full, string name, bool dir, long size)> Collect(IReadOnlyList<string> sources)
    {
        var list = new List<(string, string, bool, long)>();
        foreach (var s in sources)
        {
            var baseDir = System.IO.Path.GetDirectoryName(s.TrimEnd('\\'))!;
            string Rel(string p) => System.IO.Path.GetRelativePath(baseDir, p);
            if (File.Exists(s)) { list.Add((s, Rel(s), false, new FileInfo(s).Length)); continue; }
            if (!Directory.Exists(s)) continue;
            if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
            list.Add((s, Rel(s), true, 0));
            var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var d in Directory.EnumerateDirectories(s, "*", opts)) list.Add((d, Rel(d), true, 0));
            foreach (var f in Directory.EnumerateFiles(s, "*", opts)) { long len = 0; try { len = new FileInfo(f).Length; } catch { } list.Add((f, Rel(f), false, len)); }
        }
        return list;
    }

    /// <summary>The name for a new archive next to the sources: "Holiday.zip", or "Archive.zip" for several.</summary>
    public static string NameFor(IReadOnlyList<string> sources, string ext)
    {
        var folder = System.IO.Path.GetDirectoryName(sources[0].TrimEnd('\\'))!;
        var stem = sources.Count == 1 ? (Directory.Exists(sources[0]) ? System.IO.Path.GetFileName(sources[0].TrimEnd('\\')) : System.IO.Path.GetFileNameWithoutExtension(sources[0])) : "Archive";
        return System.IO.Path.Combine(folder, FileOps.FreeName(folder, stem, ext));
    }

    /// <summary>Where "Extract to <name>\" goes: a folder named after the archive (without .tar.gz etc.).</summary>
    public static string FolderFor(string archive)
    {
        var n = System.IO.Path.GetFileName(archive);
        foreach (var e in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz", ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" })
            if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) { n = n[..^e.Length]; break; }
        var dir = System.IO.Path.GetDirectoryName(archive)!;
        return System.IO.Path.Combine(dir, FileOps.FreeName(dir, n.Length > 0 ? n : "Extracted"));
    }
}
