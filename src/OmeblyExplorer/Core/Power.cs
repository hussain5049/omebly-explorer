// Power tools (stage 8): bulk rename, duplicates, disk map sizes, clean-up finders, folder compare and sync,
// "what is using this file", shred, checksums, links, permissions in plain words, photo location removal,
// lock with a password, hide, scan with Windows Security, temporary-file cleaner, start-up apps and fonts.
// Everything runs on this PC; anything that deletes goes to the Recycle Bin unless it says otherwise and asks.
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OmeblyExplorer.Core;

// ---------- Bulk rename ----------
public sealed class RenameRule
{
    public string Find = "", Replace = "", Prefix = "", Suffix = "", Pattern = ""; // Pattern: "Holiday {n}" / "{date} {name}"
    public bool Regex, KeepExtension = true; public int Start = 1, Digits = 2; public string Case = "keep"; // keep, lower, upper, title
}
public static class BulkRename
{
    /// <summary>The new names, in order; a name Windows can't use, or two the same, is reported instead.</summary>
    public static List<(string from, string to, string? problem)> Preview(IReadOnlyList<FileItem> items, RenameRule r)
    {
        var list = new List<(string, string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = r.Start;
        foreach (var it in items)
        {
            var ext = it.IsFolder || !r.KeepExtension ? "" : System.IO.Path.GetExtension(it.Name);
            var stem = ext.Length > 0 ? it.Name[..^ext.Length] : it.Name;
            string name = stem;
            if (r.Pattern.Length > 0)
                name = r.Pattern.Replace("{n}", n.ToString().PadLeft(r.Digits, '0')).Replace("{name}", stem)
                    .Replace("{date}", Taken(it).ToString("yyyy-MM-dd")).Replace("{time}", Taken(it).ToString("HH.mm.ss"))
                    .Replace("{folder}", System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(it.Path)) ?? "");
            if (r.Find.Length > 0)
            {
                try { name = r.Regex ? System.Text.RegularExpressions.Regex.Replace(name, r.Find, r.Replace, RegexOptions.None, TimeSpan.FromMilliseconds(200)) : name.Replace(r.Find, r.Replace, StringComparison.OrdinalIgnoreCase); }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { list.Add((it.Name, it.Name, "That pattern isn't valid")); n++; continue; }
            }
            name = r.Prefix + name + r.Suffix;
            name = r.Case switch { "lower" => name.ToLowerInvariant(), "upper" => name.ToUpperInvariant(), "title" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()), _ => name };
            var full = name.Trim() + ext;
            var problem = FileOps.NameProblem(full) ?? (!seen.Add(full) ? "Two files would get this name" : null);
            if (problem == null && !full.Equals(it.Name, StringComparison.OrdinalIgnoreCase) && (File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(it.Path)!, full)) || Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(it.Path)!, full))) && !items.Any((x) => x.Name.Equals(full, StringComparison.OrdinalIgnoreCase)))
                problem = "A file with this name is already there";
            list.Add((it.Name, full, problem));
            n++;
        }
        return list;
    }
    /// <summary>When a photo was taken (from the picture), or else when the file changed.</summary>
    private static DateTime Taken(FileItem f)
    {
        if (f.Kind == FileKind.Picture)
            try { if (Props.Read(f.Path, new[] { ("t", "System.Photo.DateTaken") }).TryGetValue("t", out var s) && DateTime.TryParse(s.Replace("‎", "").Replace("‏", ""), out var d)) return d; } catch { }
        return f.Modified;
    }
    /// <summary>Renames in two steps (through temporary names), so swaps like a→b, b→a work. Returns what was done (for undo).</summary>
    public static List<(string from, string to)> Apply(IReadOnlyList<FileItem> items, List<(string from, string to, string? problem)> plan)
    {
        var done = new List<(string, string)>();
        var temp = new List<(string tmp, string final, string original)>();
        for (int i = 0; i < items.Count; i++)
        {
            var (from, to, problem) = plan[i];
            if (problem != null || from == to) continue;
            var dir = System.IO.Path.GetDirectoryName(items[i].Path)!;
            var tmp = System.IO.Path.Combine(dir, $".~omebly-rename-{Guid.NewGuid():N}");
            try { if (items[i].IsFolder) Directory.Move(items[i].Path, tmp); else File.Move(items[i].Path, tmp); temp.Add((tmp, System.IO.Path.Combine(dir, to), items[i].Path)); } catch { }
        }
        foreach (var (tmp, final, original) in temp)
        {
            try { if (Directory.Exists(tmp)) Directory.Move(tmp, final); else File.Move(tmp, final); done.Add((original, final)); Library.Current.Moved(original, final); }
            catch { try { if (Directory.Exists(tmp)) Directory.Move(tmp, original); else File.Move(tmp, original); } catch { } }
        }
        return done;
    }
}

// ---------- Duplicates ----------
public static class Duplicates
{
    /// <summary>Groups of files with exactly the same content (same size, then the same SHA-256), biggest waste first.</summary>
    public static List<List<FileItem>> Find(string folder, JobProgress p, CancellationToken ct, long minSize = 1024)
    {
        var bySize = new Dictionary<long, List<FileInfo>>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | (FileAttributes)0x400000 };
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Length < minSize) continue;
            if (!bySize.TryGetValue(f.Length, out var l)) bySize[f.Length] = l = new();
            l.Add(f); p.Current = f.Name;
        }
        var candidates = bySize.Values.Where((l) => l.Count > 1).ToList();
        p.Total = candidates.Sum((l) => l.Count);
        var groups = new List<List<FileItem>>();
        foreach (var same in candidates)
        {
            var byHash = new Dictionary<string, List<FileInfo>>();
            foreach (var f in same)
            {
                ct.ThrowIfCancellationRequested();
                p.Current = f.Name;
                string h; try { h = Hash(f.FullName, "SHA256", ct); } catch { continue; }
                if (!byHash.TryGetValue(h, out var l)) byHash[h] = l = new();
                l.Add(f); p.Done++; p.Fraction = (double)p.Done / Math.Max(1, p.Total);
            }
            foreach (var g in byHash.Values.Where((l) => l.Count > 1))
                groups.Add(g.OrderBy((f) => f.CreationTime).Select((f) => new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime, Created = f.CreationTime }).ToList());
        }
        return groups.OrderByDescending((g) => g[0].Size * (g.Count - 1)).ToList();
    }
    public static string Hash(string path, string kind, CancellationToken ct = default)
    {
        using HashAlgorithm a = kind switch { "MD5" => MD5.Create(), "SHA1" => SHA1.Create(), _ => SHA256.Create() };
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        var buf = new byte[1 << 20]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); a.TransformBlock(buf, 0, n, null, 0); }
        a.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(a.Hash!);
    }
}

// ---------- Sizes for the disk map, and clean-up finders ----------
public sealed record SizeNode(string Path, string Name, long Size, bool IsFolder);
public static class Space
{
    /// <summary>The folders and files directly inside, each with its full size (folders counted all the way down).</summary>
    public static List<SizeNode> Children(string folder, CancellationToken ct)
    {
        var list = new List<SizeNode>();
        var di = new DirectoryInfo(folder);
        foreach (var e in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            if (e is FileInfo f) list.Add(new SizeNode(f.FullName, f.Name, f.Length, false));
            else list.Add(new SizeNode(e.FullName, e.Name, FolderSize(e.FullName, ct), true));
        }
        return list.OrderByDescending((n) => n.Size).ToList();
    }
    public static long FolderSize(string folder, CancellationToken ct)
    {
        long total = 0;
        try { foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })) { total += f.Length; if ((total & 0xFFF) == 0) ct.ThrowIfCancellationRequested(); } } catch (OperationCanceledException) { throw; } catch { }
        return total;
    }
    public static List<FileItem> EmptyFolders(string folder, CancellationToken ct)
    {
        var list = new List<FileItem>();
        foreach (var d in Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
        {
            ct.ThrowIfCancellationRequested();
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) list.Add(new FileItem { Name = System.IO.Path.GetFileName(d), Path = d, IsFolder = true, Modified = Directory.GetLastWriteTime(d) }); } catch { }
        }
        return list;
    }
    public static List<FileItem> Large(string folder, long atLeast, CancellationToken ct) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .Where((f) => { ct.ThrowIfCancellationRequested(); return f.Length >= atLeast; }).OrderByDescending((f) => f.Length).Take(2000)
            .Select((f) => new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime }).ToList();
    public static List<FileItem> Older(string folder, TimeSpan age, CancellationToken ct) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            .Where((f) => { ct.ThrowIfCancellationRequested(); return f.LastWriteTime < DateTime.Now - age; }).OrderBy((f) => f.LastWriteTime)
            .Select((f) => new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime }).ToList();
    /// <summary>Temporary files of this user older than a day (Windows and apps leave them behind).</summary>
    public static List<FileItem> TempFiles(CancellationToken ct)
    {
        var t = System.IO.Path.GetTempPath();
        return new DirectoryInfo(t).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where((f) => { ct.ThrowIfCancellationRequested(); return f.LastWriteTime < DateTime.Now.AddDays(-1) && !f.FullName.Contains("Omebly Explorer"); })
            .Select((f) => new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime }).ToList();
    }
}

// ---------- Compare and sync two folders ----------
public enum Diff { OnlyLeft, OnlyRight, Newer, Older, Same, Different }
public sealed record DiffRow(string Rel, Diff State, long LeftSize, long RightSize);
public static class FolderCompare
{
    public static List<DiffRow> Compare(string left, string right, CancellationToken ct)
    {
        Dictionary<string, FileInfo> Map(string root) => new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .ToDictionary((f) => System.IO.Path.GetRelativePath(root, f.FullName), (f) => f, StringComparer.OrdinalIgnoreCase);
        var a = Map(left); var b = Map(right);
        var rows = new List<DiffRow>();
        foreach (var (rel, fa) in a)
        {
            ct.ThrowIfCancellationRequested();
            if (!b.TryGetValue(rel, out var fb)) { rows.Add(new(rel, Diff.OnlyLeft, fa.Length, 0)); continue; }
            Diff d = fa.Length == fb.Length && Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) < 2 ? Diff.Same
                : fa.LastWriteTimeUtc > fb.LastWriteTimeUtc.AddSeconds(2) ? Diff.Newer : fb.LastWriteTimeUtc > fa.LastWriteTimeUtc.AddSeconds(2) ? Diff.Older : Diff.Different;
            rows.Add(new(rel, d, fa.Length, fb.Length));
        }
        foreach (var (rel, fb) in b) if (!a.ContainsKey(rel)) rows.Add(new(rel, Diff.OnlyRight, 0, fb.Length));
        return rows.OrderBy((r) => r.State == Diff.Same).ThenBy((r) => r.Rel, StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static bool SameFile(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && Duplicates.Hash(a, "SHA256") == Duplicates.Hash(b, "SHA256");
}

// ---------- What is using this file (Windows' Restart Manager) ----------
public static class FileLocks
{
    [StructLayout(LayoutKind.Sequential)] private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO { public RM_UNIQUE_PROCESS Process; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName; public int ApplicationType; public uint AppStatus; public uint TSSessionId; [MarshalAs(UnmanagedType.Bool)] public bool bRestartable; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, IntPtr services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? list, ref uint reasons);

    /// <summary>The programs that have this file open: name and process id.</summary>
    public static List<(string app, int pid)> WhoUses(string path)
    {
        var result = new List<(string, int)>();
        var key = new StringBuilder(64);
        if (RmStartSession(out var h, 0, key) != 0) return result;
        try
        {
            if (RmRegisterResources(h, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return result;
            uint count = 0, reasons = 0;
            int r = RmGetList(h, out var needed, ref count, null, ref reasons);
            if (r == 234 /* more data */ && needed > 0)
            {
                var list = new RM_PROCESS_INFO[needed]; count = needed;
                if (RmGetList(h, out needed, ref count, list, ref reasons) == 0)
                    for (int i = 0; i < count; i++) result.Add((list[i].strAppName, list[i].Process.dwProcessId));
            }
        }
        finally { RmEndSession(h); }
        return result;
    }
}

// ---------- Shred, links, permissions ----------
public static class Safety
{
    /// <summary>Overwrites the file's content with random bytes, then deletes it (it can't be brought back). On SSDs
    /// the drive may keep old copies of blocks; for full safety use BitLocker, as the window explains.</summary>
    public static void Shred(string path)
    {
        if (Directory.Exists(path)) { foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) Shred(f); Directory.Delete(path, true); return; }
        var fi = new FileInfo(path);
        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { fi.Delete(); return; } // a link: only the link goes
        fi.Attributes = FileAttributes.Normal;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
        {
            var buf = new byte[1 << 20]; long left = s.Length;
            while (left > 0) { RandomNumberGenerator.Fill(buf); int n = (int)Math.Min(buf.Length, left); s.Write(buf, 0, n); left -= n; }
            s.Flush(true);
        }
        var renamed = System.IO.Path.Combine(fi.DirectoryName!, Guid.NewGuid().ToString("N")); // the old name goes too
        File.Move(path, renamed); File.Delete(renamed);
    }

    /// <summary>A link: for a folder a symbolic link (needs Developer Mode or admin), for a file a hard link when on the same drive.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateSymbolicLinkW(string link, string target, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string target, IntPtr sec);
    public static string MakeLink(string target, string folder)
    {
        var name = FileOps.FreeName(folder, System.IO.Path.GetFileNameWithoutExtension(target.TrimEnd('\\')) + " (link)", Directory.Exists(target) ? "" : System.IO.Path.GetExtension(target));
        var link = System.IO.Path.Combine(folder, name);
        bool dir = Directory.Exists(target);
        if (!dir && string.Equals(System.IO.Path.GetPathRoot(target), System.IO.Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase) && CreateHardLinkW(link, target, IntPtr.Zero)) return link;
        if (CreateSymbolicLinkW(link, target, (dir ? 1 : 0) | 2 /* ALLOW_UNPRIVILEGED_CREATE */)) return link;
        throw new InvalidOperationException("Windows only lets you make this kind of link with Developer Mode on (Settings, System, For developers) or as an administrator.");
    }

    /// <summary>Who owns it, and who can do what with it, in plain words.</summary>
    public static (string owner, List<(string who, string can)> rules) Permissions(string path)
    {
        FileSystemSecurity sec = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        string Name(IdentityReference id) { try { return id.Translate(typeof(NTAccount)).Value; } catch { return id.Value; } }
        var owner = sec.GetOwner(typeof(SecurityIdentifier)) is { } o ? Name(o) : "Unknown";
        var rules = new List<(string, string)>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var rights = r.FileSystemRights;
            string can = (rights & FileSystemRights.FullControl) == FileSystemRights.FullControl ? "Can do everything (full control)"
                : (rights & FileSystemRights.Modify) == FileSystemRights.Modify ? "Can open, change and delete"
                : (rights & FileSystemRights.Write) != 0 ? "Can open and change"
                : (rights & FileSystemRights.ReadAndExecute) != 0 || (rights & FileSystemRights.Read) != 0 ? "Can open and read"
                : "Special permissions";
            if (r.AccessControlType == AccessControlType.Deny) can = "Is blocked: " + can.ToLowerInvariant().Replace("can ", "can't ");
            rules.Add((Name(r.IdentityReference), can));
        }
        return (owner, rules.Distinct().ToList());
    }
}

// ---------- Photos ----------
public static class PhotoPrivacy
{
    /// <summary>A copy of the photo with where it was taken (GPS) removed, for sharing. The picture itself isn't re-saved.</summary>
    public static async Task<string> RemoveLocation(string path)
    {
        var outPath = Converters.OutFor(path, System.IO.Path.GetExtension(path), System.IO.Path.GetFileNameWithoutExtension(path) + " no location");
        using (var input = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.Read))
        using (var output = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(outPath, Windows.Storage.FileAccessMode.ReadWrite, Windows.Storage.StorageOpenOptions.None, Windows.Storage.Streams.FileOpenDisposition.CreateNew))
        {
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateForTranscodingAsync(output, dec);
            var gps = new[] { "System.GPS.Latitude", "System.GPS.Longitude", "System.GPS.Altitude", "System.GPS.LatitudeRef", "System.GPS.LongitudeRef", "System.GPS.AltitudeRef", "System.GPS.LatitudeNumerator", "System.GPS.LongitudeNumerator", "/app1/ifd/gps", "/xmp/exif:GPSLatitude", "/xmp/exif:GPSLongitude" };
            foreach (var key in gps) { try { await enc.BitmapProperties.SetPropertiesAsync(new[] { new KeyValuePair<string, Windows.Graphics.Imaging.BitmapTypedValue>(key, new Windows.Graphics.Imaging.BitmapTypedValue(null, Windows.Foundation.PropertyType.Empty)) }); } catch { } }
            await enc.FlushAsync();
        }
        return outPath;
    }
}

// ---------- Lock with a password (AES-256-GCM) ----------
public static class Locker
{
    // File: "OMBLK1" + salt(16) + nonce(12) + tag(16) + ciphertext. The key comes from the password (PBKDF2-SHA256, 600,000 rounds).
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OMBLK1");
    public static bool IsLocked(string path) => path.EndsWith(".omeblylock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Locks a file (or a folder, zipped first) into "name.omeblylock"; the original goes to the Recycle Bin after.</summary>
    public static string Lock(string path, string password)
    {
        byte[] plain;
        string inner;
        if (Directory.Exists(path))
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"omebly-lock-{Guid.NewGuid():N}.zip");
            try { Archives.CreateZip(new[] { path }, tmp, null, new ArchiveProgress(), CancellationToken.None); plain = File.ReadAllBytes(tmp); } finally { try { File.Delete(tmp); } catch { } }
            inner = System.IO.Path.GetFileName(path.TrimEnd('\\')) + ".zip";
        }
        else { plain = File.ReadAllBytes(path); inner = System.IO.Path.GetFileName(path); }
        var nameBytes = Encoding.UTF8.GetBytes(inner);
        var payload = new byte[2 + nameBytes.Length + plain.Length];
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(payload, 0); nameBytes.CopyTo(payload, 2); plain.CopyTo(payload, 2 + nameBytes.Length);
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[payload.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, payload, cipher, tag);
        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(plain);
        var dir = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'))!;
        var outPath = System.IO.Path.Combine(dir, FileOps.FreeName(dir, System.IO.Path.GetFileName(path.TrimEnd('\\')), ".omeblylock"));
        using (var f = new FileStream(outPath, FileMode.CreateNew)) { f.Write(Magic); f.Write(salt); f.Write(nonce); f.Write(tag); f.Write(cipher); }
        return outPath;
    }

    /// <summary>Unlocks next to the locked file. A wrong password (or a changed file) is refused.</summary>
    public static string Unlock(string locked, string password)
    {
        var all = File.ReadAllBytes(locked);
        if (all.Length < 50 || !all.AsSpan(0, 6).SequenceEqual(Magic)) throw new InvalidOperationException("This isn't a file locked by Omebly Explorer.");
        var salt = all[6..22]; var nonce = all[22..34]; var tag = all[34..50]; var cipher = all[50..];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        var payload = new byte[cipher.Length];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(nonce, cipher, tag, payload); }
        catch (AuthenticationTagMismatchException) { throw new PdfTools.WrongPassword(); }
        finally { CryptographicOperations.ZeroMemory(key); }
        int nl = BitConverter.ToUInt16(payload, 0);
        var name = System.IO.Path.GetFileName(Encoding.UTF8.GetString(payload, 2, nl)); // only a name, never a path
        if (FileOps.NameProblem(name) != null) name = "Unlocked file";
        var dir = System.IO.Path.GetDirectoryName(locked)!;
        var outPath = System.IO.Path.Combine(dir, FileOps.FreeName(dir, System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetExtension(name)));
        using (var f = new FileStream(outPath, FileMode.CreateNew)) f.Write(payload, 2 + nl, payload.Length - 2 - nl);
        CryptographicOperations.ZeroMemory(payload);
        return outPath;
    }
}

// ---------- Start-up apps, scan, fonts ----------
public sealed record StartupApp(string Name, string Command, string Where, bool Enabled, bool CanChange);
public static class WinControl
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static List<StartupApp> StartupApps()
    {
        var list = new List<StartupApp>();
        using var approved = Registry.CurrentUser.OpenSubKey(Approved);
        bool On(string name) => approved?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0; // odd first byte = turned off (as Task Manager stores it)
        using (var k = Registry.CurrentUser.OpenSubKey(Run)) if (k != null) foreach (var n in k.GetValueNames()) list.Add(new(n, k.GetValue(n) as string ?? "", "Your account", On(n), true));
        using (var k = Registry.LocalMachine.OpenSubKey(Run)) if (k != null) foreach (var n in k.GetValueNames()) list.Add(new(n, k.GetValue(n) as string ?? "", "All users (needs admin to change)", true, false));
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(startup)) foreach (var f in Directory.GetFiles(startup).Where((f) => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))) list.Add(new(System.IO.Path.GetFileNameWithoutExtension(f), f, "Start-up folder", true, false));
        return list.OrderBy((a) => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    /// <summary>Turns a start-up app of this account on or off the same way Task Manager does (nothing is deleted).</summary>
    public static void SetStartup(string name, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Approved);
        var data = new byte[12]; data[0] = on ? (byte)2 : (byte)3;
        if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(name, data, RegistryValueKind.Binary);
    }

    /// <summary>Windows Security scans the file or folder (its own scanner, started by its full path).</summary>
    public static void Scan(string path)
    {
        var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("Windows Security's scanner isn't on this PC (another antivirus may be in charge).");
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-Scan"); psi.ArgumentList.Add("-ScanType"); psi.ArgumentList.Add("3"); psi.ArgumentList.Add("-File"); psi.ArgumentList.Add(path); psi.ArgumentList.Add("-DisableRemediation");
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        LastScan = p.ExitCode == 0 ? "No threats found." : p.ExitCode == 2 ? "Windows Security found a threat. Open Windows Security to deal with it." : "The scan didn't finish: " + output.Trim().Split('\n').LastOrDefault();
    }
    public static string LastScan = "";

    /// <summary>Installs a font for this account only (no admin), as Windows does with "Install".</summary>
    public static string InstallFont(string fontFile, string familyName)
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        Directory.CreateDirectory(dir);
        var dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(fontFile));
        File.Copy(fontFile, dest, true);
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
        k.SetValue($"{familyName} ({(fontFile.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "OpenType" : "TrueType")})", dest);
        AddFontResourceW(dest);
        SendMessageTimeoutW((IntPtr)0xFFFF, 0x001D /* WM_FONTCHANGE */, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _);
        return dest;
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResourceW(string file);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
}
