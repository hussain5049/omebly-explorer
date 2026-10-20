// Stage 9: cloud folders (OneDrive, Google Drive, Dropbox, iCloud) with their sync state and "Always keep on this
// device" / "Free up space"; phones and cameras over USB; network drives; and FTP / SFTP servers as folders.
// Passwords for servers are kept in Windows Credential Manager (encrypted by Windows for this user), never in a file.
using System.Runtime.InteropServices;
using System.Text;
using FluentFTP;
using Renci.SshNet;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;

namespace OmeblyExplorer.Core;

public static class CloudFiles
{
    public const FileAttributes OnlineOnly = (FileAttributes)0x400000; // RECALL_ON_DATA_ACCESS
    public const FileAttributes Pinned = (FileAttributes)0x80000, Unpinned = (FileAttributes)0x100000;
    public static string StateOf(FileItem f) =>
        (f.Attributes & OnlineOnly) != 0 || (f.Attributes & FileAttributes.Offline) != 0 ? "Online only"
        : (f.Attributes & Pinned) != 0 ? "Always on this PC"
        : (f.Attributes & Unpinned) != 0 ? "Online only (after use)"
        : InCloud(f.Path) ? "On this PC" : "";
    public static bool InCloud(string path) => Places.Cloud().Any((c) => path.StartsWith(c.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    [DllImport("cldapi.dll")] private static extern int CfSetPinState(Microsoft.Win32.SafeHandles.SafeFileHandle h, int state, int flags, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    /// <summary>"Always keep on this device" (pinned) or "Free up space" (unpinned: the cloud app removes the local copy).</summary>
    public static bool SetKeep(string path, bool keep)
    {
        const uint FILE_WRITE_ATTRIBUTES = 0x100, SHARE_ALL = 7, OPEN_EXISTING = 3, BACKUP_SEMANTICS = 0x02000000;
        using var h = CreateFileW(path, FILE_WRITE_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return false;
        return CfSetPinState(h, keep ? 1 : 2, Directory.Exists(path) ? 1 /* RECURSE */ : 0, IntPtr.Zero) == 0;
    }
}

// ---------- Phones and cameras over USB ----------
public sealed record PhoneEntry(string Name, bool IsFolder, ulong Size, DateTimeOffset Modified, IStorageItem Item);
public static class Phones
{
    /// <summary>Phones, cameras and media players plugged in by USB (they don't have drive letters).</summary>
    public static async Task<List<(string name, string id)>> List()
    {
        var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Portable.StorageDevice.GetDeviceSelector());
        return found.Select((d) => (d.Name, d.Id)).ToList();
    }
    public static StorageFolder Open(string id) => Windows.Devices.Portable.StorageDevice.FromId(id);

    public static async Task<StorageFolder> FolderAt(string id, string inside)
    {
        StorageFolder f = Open(id);
        foreach (var part in inside.Split('\\', StringSplitOptions.RemoveEmptyEntries)) f = await f.GetFolderAsync(part);
        return f;
    }
    public static async Task<List<PhoneEntry>> Items(StorageFolder f)
    {
        var list = new List<PhoneEntry>();
        foreach (var it in await f.GetItemsAsync())
        {
            ulong size = 0; DateTimeOffset when = it.DateCreated;
            if (it is StorageFile file) { try { var p = await file.GetBasicPropertiesAsync(); size = p.Size; when = p.DateModified; } catch { } }
            list.Add(new PhoneEntry(it.Name, it is StorageFolder, size, when, it));
        }
        return list.OrderBy((e) => !e.IsFolder).ThenBy((e) => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    /// <summary>Copies files and folders from the phone to a folder on the PC ("(2)" if a name is taken).</summary>
    public static async Task<List<string>> CopyToPc(IEnumerable<IStorageItem> items, string target, JobProgress p, CancellationToken ct)
    {
        var made = new List<string>();
        var dest = await StorageFolder.GetFolderFromPathAsync(target);
        async Task One(IStorageItem it, StorageFolder into)
        {
            ct.ThrowIfCancellationRequested();
            p.Current = it.Name;
            if (it is StorageFile f) { var c = await f.CopyAsync(into, f.Name, NameCollisionOption.GenerateUniqueName); made.Add(c.Path); p.Done++; }
            else if (it is StorageFolder d)
            {
                var sub = await into.CreateFolderAsync(d.Name, CreationCollisionOption.GenerateUniqueName);
                if (into.Path == target) made.Add(sub.Path);
                foreach (var child in await d.GetItemsAsync()) await One(child, sub);
            }
        }
        foreach (var it in items) await One(it, dest);
        return made;
    }
    /// <summary>Copies PC files onto the phone folder.</summary>
    public static async Task CopyToPhone(IEnumerable<string> paths, StorageFolder into, JobProgress p, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            p.Current = System.IO.Path.GetFileName(path);
            if (File.Exists(path)) { var f = await StorageFile.GetFileFromPathAsync(path); await f.CopyAsync(into, f.Name, NameCollisionOption.GenerateUniqueName); }
            p.Done++;
        }
    }
}

// ---------- FTP and SFTP ----------
public sealed class Server
{
    public string Name { get; set; } = ""; public string Kind { get; set; } = "sftp"; // ftp, ftps, sftp
    public string Host { get; set; } = ""; public int Port { get; set; } = 22; public string User { get; set; } = "";
    public string Key => $"Omebly Explorer:{Kind}://{User}@{Host}:{Port}";
}
public sealed record RemoteEntry(string Name, string Path, bool IsFolder, long Size, DateTime Modified);

public static class Servers
{
    // Windows Credential Manager: the password stays encrypted by Windows for this user.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL { public int Flags, Type; public string TargetName, Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist, AttributeCount; public IntPtr Attributes; public string? TargetAlias, UserName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWriteW(ref CREDENTIAL c, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, int type, int flags, out IntPtr c);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr c);

    public static void SavePassword(Server s, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var c = new CREDENTIAL { Type = 1 /* GENERIC */, TargetName = s.Key, UserName = s.User, CredentialBlobSize = blob.Length, CredentialBlob = Marshal.AllocCoTaskMem(blob.Length), Persist = 2 /* LOCAL_MACHINE: this user, this PC */ };
        try { Marshal.Copy(blob, 0, c.CredentialBlob, blob.Length); if (!CredWriteW(ref c, 0)) throw new InvalidOperationException("Windows couldn't keep the password."); }
        finally { Marshal.FreeCoTaskMem(c.CredentialBlob); Array.Clear(blob); }
    }
    public static string? Password(Server s)
    {
        if (!CredReadW(s.Key, 1, 0, out var p)) return null;
        try { var c = Marshal.PtrToStructure<CREDENTIAL>(p); return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2); }
        finally { CredFree(p); }
    }
    public static void Forget(Server s) => CredDeleteW(s.Key, 1, 0);

    /// <summary>A connection to one server; open, list, download and upload, then close.</summary>
    public sealed class Session : IDisposable
    {
        private readonly AsyncFtpClient? _ftp; private readonly SftpClient? _sftp;
        public Session(Server s, string password)
        {
            if (s.Kind == "sftp") { _sftp = new SftpClient(s.Host, s.Port, s.User, password); _sftp.HostKeyReceived += (_, e) => e.CanTrust = TrustHost(s, e.FingerPrintSHA256); }
            else { _ftp = new AsyncFtpClient(s.Host, s.User, password, s.Port); _ftp.Config.EncryptionMode = s.Kind == "ftps" ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None; _ftp.Config.ValidateAnyCertificate = false; }
        }
        public async Task Connect(CancellationToken ct) { if (_sftp != null) await Task.Run(() => _sftp.Connect(), ct); else await _ftp!.Connect(ct); }
        public async Task<List<RemoteEntry>> List(string path, CancellationToken ct)
        {
            if (_sftp != null)
                return (await Task.Run(() => _sftp.ListDirectory(path).Where((f) => f.Name is not "." and not "..").Select((f) => new RemoteEntry(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTime)).ToList(), ct))
                    .OrderBy((e) => !e.IsFolder).ThenBy((e) => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var items = await _ftp!.GetListing(path, ct);
            return items.Select((f) => new RemoteEntry(f.Name, f.FullName, f.Type == FtpObjectType.Directory, f.Size, f.Modified)).OrderBy((e) => !e.IsFolder).ThenBy((e) => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public async Task<string> Download(RemoteEntry e, string folder, CancellationToken ct)
        {
            var name = System.IO.Path.GetFileName(e.Name); if (FileOps.NameProblem(name) != null) name = "download";
            var local = System.IO.Path.Combine(folder, FileOps.FreeName(folder, System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetExtension(name)));
            if (_sftp != null) { await using var f = File.Create(local); await Task.Run(() => _sftp.DownloadFile(e.Path, f), ct); }
            else await _ftp!.DownloadFile(local, e.Path, FtpLocalExists.Overwrite, FtpVerify.None, null, ct);
            return local;
        }
        public async Task Upload(string local, string remoteFolder, CancellationToken ct)
        {
            var remote = remoteFolder.TrimEnd('/') + "/" + System.IO.Path.GetFileName(local);
            if (_sftp != null) { await using var f = File.OpenRead(local); await Task.Run(() => _sftp.UploadFile(f, remote, false), ct); }
            else await _ftp!.UploadFile(local, remote, FtpRemoteExists.Skip, false, FtpVerify.None, null, ct);
        }
        public void Dispose() { try { _sftp?.Disconnect(); _sftp?.Dispose(); _ftp?.Dispose(); } catch { } }
    }

    /// <summary>The first time, a server's key is remembered; after that a different key is refused (someone may be in between).</summary>
    private static bool TrustHost(Server s, string fingerprint)
    {
        var known = Library.Current.HostKeys;
        var id = $"{s.Host}:{s.Port}";
        if (known.TryGetValue(id, out var k)) return k == fingerprint;
        known[id] = fingerprint; Library.Save();
        return true;
    }
}
