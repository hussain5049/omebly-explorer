// Automatic rules and backup (stage 9). While Omebly Explorer is open, it watches the folders you chose:
//  - "When a file like this arrives in Downloads, move it to Documents (and rename it by date)", or copy it,
//    convert it (smaller picture, PDF, MP3...) or zip it;
//  - "Tidy this folder every day";
//  - backup: copy new and changed files from your folders to a backup drive whenever it is plugged in (nothing is
//    ever deleted from the backup), and restore from it.
// A rule only moves, copies, renames or converts; it never deletes.
namespace OmeblyExplorer.Core;

public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool On { get; set; } = true;
    public string Folder { get; set; } = "";
    public string Match { get; set; } = "*";            // "*.pdf", "*invoice*", "*" ...
    public string Kind { get; set; } = "any";           // any, pictures, videos, music, documents, archives
    public string Action { get; set; } = "move";        // move, copy, convert, zip, tidy
    public string Target { get; set; } = "";            // folder for move / copy
    public string Rename { get; set; } = "";            // "" or a pattern like "{date} {name}"
    public string Convert { get; set; } = "";           // a Converters option key (small, pdf-images, mp3...)
    public DateTime LastRun { get; set; }
    public string Describe() => (Action switch
    {
        "tidy" => $"Every day, tidy {TabState.NameOf(Folder)}",
        "convert" => $"When {What()} arrives in {TabState.NameOf(Folder)}, convert it ({Convert})",
        "zip" => $"When {What()} arrives in {TabState.NameOf(Folder)}, zip it",
        "copy" => $"When {What()} arrives in {TabState.NameOf(Folder)}, copy it to {TabState.NameOf(Target)}",
        _ => $"When {What()} arrives in {TabState.NameOf(Folder)}, move it to {TabState.NameOf(Target)}",
    }) + (Rename.Length > 0 && Action is "move" or "copy" ? $", named “{Rename}”" : "");
    private string What() => Kind == "any" ? (Match is "*" or "" ? "a file" : $"“{Match}”") : Kind.TrimEnd('s') + (Match is "*" or "" ? "" : $" “{Match}”");
}

public sealed class Backup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> Folders { get; set; } = new();
    public string DriveLabel { get; set; } = "";        // the backup drive, found by its name (letters change)
    public string DriveSerial { get; set; } = "";
    public DateTime LastRun { get; set; }
    public string LastResult { get; set; } = "";
}

public static class RuleEngine
{
    private static readonly Dictionary<string, FileSystemWatcher> Watchers = new();
    public static event Action<string>? Note;             // something done ("Moved 3 files")
    private static Timer? _daily;

    public static void Start()
    {
        Stop();
        foreach (var r in Library.Current.Rules.Where((r) => r.On && Directory.Exists(r.Folder) && r.Action != "tidy"))
        {
            var w = new FileSystemWatcher(r.Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
            var rule = r;
            void Arrived(string path) => _ = Task.Run(async () => { await WaitUntilDone(path); if (Matches(rule, path)) await Apply(rule, path); });
            w.Created += (_, e) => Arrived(e.FullPath);
            w.Renamed += (_, e) => Arrived(e.FullPath); // browsers download to "x.crdownload" then rename
            w.EnableRaisingEvents = true;
            Watchers[r.Id] = w;
        }
        _daily = new Timer((_) => Daily(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
    }
    public static void Stop() { foreach (var w in Watchers.Values) w.Dispose(); Watchers.Clear(); _daily?.Dispose(); _daily = null; }

    private static bool Matches(Rule r, string path)
    {
        if (!File.Exists(path)) return false;
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(".crdownload") || name.EndsWith(".part") || name.EndsWith(".tmp") || name.StartsWith(".~omebly") || name.StartsWith("~$")) return false;
        var kind = FileKinds.Of(System.IO.Path.GetExtension(path).ToLowerInvariant());
        bool kindOk = r.Kind switch
        {
            "pictures" => kind == FileKind.Picture, "videos" => kind == FileKind.Video, "music" => kind == FileKind.Music,
            "documents" => kind is FileKind.Pdf or FileKind.Document or FileKind.Sheet or FileKind.Slides or FileKind.Text, "archives" => kind == FileKind.Archive, _ => true,
        };
        return kindOk && Like(name, r.Match.Length == 0 ? "*" : r.Match);
    }
    /// <summary>"*.pdf", "*invoice*", "IMG_????.jpg": * is any text, ? one letter, no other special meaning.</summary>
    public static bool Like(string name, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    /// <summary>A file still being written (a download) is left alone until it stops growing.</summary>
    private static async Task WaitUntilDone(string path)
    {
        long last = -1;
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            try
            {
                var len = new FileInfo(path).Length;
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                if (len == last) return;
                last = len;
            }
            catch (FileNotFoundException) { return; }
            catch { /* still open in the app writing it */ }
        }
    }

    public static async Task Apply(Rule r, string path)
    {
        try
        {
            var name = System.IO.Path.GetFileName(path);
            if (r.Rename.Length > 0)
            {
                var fi = new FileInfo(path);
                var stem = System.IO.Path.GetFileNameWithoutExtension(name);
                var n = r.Rename.Replace("{date}", fi.LastWriteTime.ToString("yyyy-MM-dd")).Replace("{time}", fi.LastWriteTime.ToString("HH.mm")).Replace("{name}", stem);
                if (FileOps.NameProblem(n + fi.Extension) == null) name = n + fi.Extension;
            }
            switch (r.Action)
            {
                case "move" or "copy":
                    if (!Directory.Exists(r.Target)) return;
                    var dest = System.IO.Path.Combine(r.Target, FileOps.FreeName(r.Target, System.IO.Path.GetFileNameWithoutExtension(name), System.IO.Path.GetExtension(name)));
                    if (r.Action == "move") File.Move(path, dest); else File.Copy(path, dest);
                    Note?.Invoke($"{(r.Action == "move" ? "Moved" : "Copied")} {System.IO.Path.GetFileName(path)} to {TabState.NameOf(r.Target)}");
                    break;
                case "zip":
                    Archives.CreateZip(new[] { path }, Archives.NameFor(new[] { path }, ".zip"), null, new ArchiveProgress(), CancellationToken.None);
                    Note?.Invoke($"Zipped {name}");
                    break;
                case "convert":
                    var item = new FileItem { Name = System.IO.Path.GetFileName(path), Path = path, Size = new FileInfo(path).Length };
                    var opt = Converters.OptionsFor(new[] { item }).FirstOrDefault((o) => o.Key == r.Convert);
                    if (opt == null) return;
                    await Converters.Run(opt, new[] { item }, new JobProgress(), CancellationToken.None);
                    Note?.Invoke($"Converted {name}");
                    break;
            }
        }
        catch (Exception e) { CrashLog.Write(e); }
    }

    // ---------- Daily jobs and backups ----------
    public static Action<Rule>? TidyNow;   // the window does tidy-ups (it shows the move cards)
    private static void Daily()
    {
        foreach (var r in Library.Current.Rules.Where((r) => r.On && r.Action == "tidy" && DateTime.Now - r.LastRun > TimeSpan.FromHours(23)))
        { r.LastRun = DateTime.Now; Library.Save(); TidyNow?.Invoke(r); }
        foreach (var b in Library.Current.Backups)
            if (DateTime.Now - b.LastRun > TimeSpan.FromHours(20) && DriveFor(b) is { } root) _ = Task.Run(() => RunBackup(b, root, new JobProgress(), CancellationToken.None));
    }

    public static string? DriveFor(Backup b) => DriveInfo.GetDrives().FirstOrDefault((d) => d.IsReady && (Serial(d.RootDirectory.FullName) == b.DriveSerial || (b.DriveSerial.Length == 0 && d.VolumeLabel == b.DriveLabel)))?.RootDirectory.FullName;
    public static string Serial(string root)
    {
        uint serial = 0;
        try { GetVolumeInformationW(root, null, 0, out serial, out _, out _, null, 0); } catch { }
        return serial.ToString("X8");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(string root, System.Text.StringBuilder? name, int nameSize, out uint serial, out uint maxLen, out uint flags, System.Text.StringBuilder? fs, int fsSize);

    /// <summary>Copies new and changed files to "&lt;drive&gt;\Omebly Backup\&lt;PC name&gt;\&lt;folder&gt;". Never deletes anything there.</summary>
    public static List<string> RunBackup(Backup b, string driveRoot, JobProgress p, CancellationToken ct)
    {
        var copied = new List<string>();
        var baseDir = System.IO.Path.Combine(driveRoot, "Omebly Backup", Environment.MachineName);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | CloudFiles.OnlineOnly };
        int failed = 0;
        foreach (var src in b.Folders.Where(Directory.Exists))
        {
            var dst = System.IO.Path.Combine(baseDir, System.IO.Path.GetFileName(src.TrimEnd('\\')));
            foreach (var f in new DirectoryInfo(src).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var target = System.IO.Path.Combine(dst, System.IO.Path.GetRelativePath(src, f.FullName));
                try
                {
                    var t = new FileInfo(target);
                    if (t.Exists && t.Length == f.Length && Math.Abs((t.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) < 2) continue;
                    Directory.CreateDirectory(t.DirectoryName!);
                    // An older version already in the backup is kept beside the new one, never overwritten.
                    if (t.Exists) File.Move(target, System.IO.Path.Combine(t.DirectoryName!, FileOps.FreeName(t.DirectoryName!, System.IO.Path.GetFileNameWithoutExtension(target) + " (older " + t.LastWriteTime.ToString("yyyy-MM-dd") + ")", t.Extension)));
                    p.Current = f.Name;
                    File.Copy(f.FullName, target);
                    File.SetLastWriteTimeUtc(target, f.LastWriteTimeUtc);
                    copied.Add(target); p.Done++;
                }
                catch { failed++; }
            }
        }
        b.LastRun = DateTime.Now; b.LastResult = $"{copied.Count:N0} files backed up{(failed > 0 ? $", {failed} couldn't be read" : "")} on {DateTime.Now:d MMM, HH:mm}";
        Library.Save();
        Note?.Invoke("Backup: " + b.LastResult);
        return copied;
    }
}
