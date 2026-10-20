// Instant search by name across the whole PC, like the "Everything" tool: Omebly keeps a compact list of every
// folder and file on the fixed drives (Windows' own system folders, program folders and caches are left out) and
// searches it in memory in milliseconds. The list is built in the background at low priority, saved on this PC
// (%LOCALAPPDATA%\Omebly\Explorer\index.bin) so it is ready at once next time, and refreshed every half hour.
// Reading the NTFS change journal directly (faster, always current) needs admin rights; it comes with the
// optional search service in stage 9.
namespace OmeblyExplorer.Core;

public static class FileIndex
{
    // One entry per file or folder: its name, its parent's index, and whether it is a folder. Roots have parent -1.
    private static string[] _names = Array.Empty<string>();
    private static int[] _parents = Array.Empty<int>();
    private static bool[] _folders = Array.Empty<bool>();
    private static readonly object Gate = new();
    private static DateTime _built;
    private static bool _building;
    public static int Count { get { lock (Gate) return _names.Length; } }
    public static bool Ready => Count > 0;
    public static event Action? Updated;

    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer", "index.bin");
    private static readonly string[] Skip = { "$Recycle.Bin", "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs", "$WinREAgent", "node_modules", ".git", "AppData", "WinSxS", "__pycache__", ".gradle", ".nuget", "obj" };

    /// <summary>Loads the saved list and starts a refresh if it is old.</summary>
    public static void Start()
    {
        _ = Task.Run(() =>
        {
            try { Load(); } catch { }
            if (DateTime.Now - _built > TimeSpan.FromMinutes(30)) Build();
        });
    }

    public static void Build()
    {
        lock (Gate) { if (_building) return; _building = true; }
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Lowest;
            var names = new List<string>(500_000); var parents = new List<int>(500_000); var folders = new List<bool>(500_000);
            foreach (var d in DriveInfo.GetDrives().Where((d) => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                int root = names.Count; names.Add(d.RootDirectory.FullName.TrimEnd('\\')); parents.Add(-1); folders.Add(true);
                var stack = new Stack<(string path, int index)>(); stack.Push((d.RootDirectory.FullName, root));
                while (stack.Count > 0)
                {
                    var (dir, at) = stack.Pop();
                    IEnumerable<FileSystemInfo> kids;
                    try { kids = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, RecurseSubdirectories = false }); }
                    catch { continue; }
                    try
                    {
                        foreach (var k in kids)
                        {
                            bool isDir = (k.Attributes & FileAttributes.Directory) != 0;
                            if (isDir && Skip.Contains(k.Name, StringComparer.OrdinalIgnoreCase)) continue;
                            names.Add(k.Name); parents.Add(at); folders.Add(isDir);
                            if (isDir) stack.Push((k.FullName, names.Count - 1));
                            if (names.Count > 5_000_000) break; // a safety limit for memory
                        }
                    }
                    catch { }
                }
            }
            lock (Gate) { _names = names.ToArray(); _parents = parents.ToArray(); _folders = folders.ToArray(); _built = DateTime.Now; }
            Save();
            Updated?.Invoke();
        }
        finally { lock (Gate) _building = false; }
    }

    public static string PathOf(int i)
    {
        lock (Gate)
        {
            var parts = new Stack<string>();
            for (int j = i; j >= 0; j = _parents[j]) parts.Push(_names[j]);
            var root = parts.Pop();
            return parts.Count == 0 ? root + "\\" : root + "\\" + string.Join('\\', parts);
        }
    }

    /// <summary>Names containing every word typed (in any order), best matches first: exact, then starting with it.</summary>
    public static List<FileItem> Find(string query, int max = 1000)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        string[] names; bool[] folders;
        lock (Gate) { names = _names; folders = _folders; }
        var hits = new List<(int i, int rank)>();
        for (int i = 0; i < names.Length; i++)
        {
            var n = names[i];
            bool all = true;
            foreach (var w in words) if (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
            if (!all) continue;
            int rank = n.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : n.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            hits.Add((i, rank));
            if (hits.Count > max * 20) break;
        }
        return hits.OrderBy((h) => h.rank).ThenBy((h) => names[h.i].Length).Take(max).Select((h) =>
        {
            var p = PathOf(h.i);
            try
            {
                if (folders[h.i]) return new FileItem { Name = names[h.i], Path = p, IsFolder = true, Modified = Directory.GetLastWriteTime(p) };
                var fi = new FileInfo(p);
                return new FileItem { Name = names[h.i], Path = p, Size = fi.Exists ? fi.Length : 0, Modified = fi.Exists ? fi.LastWriteTime : default, Attributes = fi.Exists ? fi.Attributes : default };
            }
            catch { return new FileItem { Name = names[h.i], Path = p, IsFolder = folders[h.i] }; }
        }).Where((f) => File.Exists(f.Path) || Directory.Exists(f.Path)).ToList();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string[] n; int[] p; bool[] f;
            lock (Gate) { n = _names; p = _parents; f = _folders; }
            using var fs = new FileStream(FilePath + ".tmp", FileMode.Create);
            using (var w = new BinaryWriter(fs))
            {
                w.Write(1); w.Write(_built.ToBinary()); w.Write(n.Length);
                for (int i = 0; i < n.Length; i++) { w.Write(n[i]); w.Write(p[i]); w.Write(f[i]); }
            }
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { }
    }
    private static void Load()
    {
        if (!File.Exists(FilePath)) return;
        using var r = new BinaryReader(File.OpenRead(FilePath));
        if (r.ReadInt32() != 1) return;
        var built = DateTime.FromBinary(r.ReadInt64()); int count = r.ReadInt32();
        if (count < 0 || count > 5_000_000) return;
        var n = new string[count]; var p = new int[count]; var f = new bool[count];
        for (int i = 0; i < count; i++) { n[i] = r.ReadString(); p[i] = r.ReadInt32(); f[i] = r.ReadBoolean(); if (p[i] >= i) return; }
        lock (Gate) { _names = n; _parents = p; _folders = f; _built = built; }
        Updated?.Invoke();
    }
}
