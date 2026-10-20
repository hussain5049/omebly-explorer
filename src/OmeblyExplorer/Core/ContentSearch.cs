// Search inside files (and names) in a folder and everything under it. Windows Search's own index answers first
// (it reads inside PDFs, Word, Excel, mail and more); places Windows doesn't index are read directly for text-type
// files. Nothing leaves the PC.
using System.Data.OleDb;

namespace OmeblyExplorer.Core;

public static class ContentSearch
{
    public static List<FileItem> Find(string folder, string query, int max = 2000)
    {
        var found = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        query = query.Trim();
        if (query.Length == 0) return new();
        try { foreach (var f in FromIndex(folder, query, max)) found.TryAdd(f.Path, f); } catch { /* Windows Search is off or not reachable */ }
        if (found.Count < max) foreach (var f in ByReading(folder, query, max - found.Count)) found.TryAdd(f.Path, f);
        return found.Values.OrderBy((f) => f.Name, StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
    }

    private static IEnumerable<FileItem> FromIndex(string folder, string query, int max)
    {
        // Words and quotes are cleaned so the text can only ever be a search term, never part of the command.
        var term = new string(query.Where((c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        if (term.Length == 0) yield break;
        var scope = "file:" + folder.TrimEnd('\\').Replace('\\', '/').Replace("'", "''");
        var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.Size, System.DateModified FROM SystemIndex WHERE SCOPE='{scope}' AND (CONTAINS(*, '\"{term}*\"') OR System.FileName LIKE '%{term.Replace("'", "''")}%')";
        using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        conn.Open();
        using var cmd = new OleDbCommand(sql, conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r[0] is not string path || path.Length == 0) continue;
            var dir = Directory.Exists(path);
            yield return new FileItem { Name = System.IO.Path.GetFileName(path), Path = path, IsFolder = dir, Size = r[1] is DBNull ? 0 : Convert.ToInt64(r[1]), Modified = r[2] is DateTime d ? d.ToLocalTime() : default };
        }
    }

    private static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".java", ".c", ".cpp", ".h", ".ini", ".yaml", ".yml", ".sql", ".srt", ".ps1", ".bat", ".dart", ".kt", ".go", ".rs", ".php", ".rb" };

    /// <summary>Names everywhere, and the text inside text-type files (up to 5 MB each, 20,000 files in all).</summary>
    private static IEnumerable<FileItem> ByReading(string folder, string query, int max)
    {
        int seen = 0, hits = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        IEnumerable<string> all;
        try { all = Directory.EnumerateFileSystemEntries(folder, "*", opts); } catch { yield break; }
        foreach (var p in all)
        {
            if (hits >= max || ++seen > 20000) yield break;
            var name = System.IO.Path.GetFileName(p);
            bool match = name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            FileInfo? fi = null;
            if (!match && TextKinds.Contains(System.IO.Path.GetExtension(p)))
            {
                try { fi = new FileInfo(p); if (fi.Length <= 5 << 20 && (fi.Attributes & (FileAttributes)0x400000) == 0) match = File.ReadAllText(p).Contains(query, StringComparison.CurrentCultureIgnoreCase); } catch { }
            }
            if (!match) continue;
            hits++;
            bool dir = Directory.Exists(p);
            fi ??= dir ? null : new FileInfo(p);
            yield return new FileItem { Name = name, Path = p, IsFolder = dir, Size = fi?.Length ?? 0, Modified = dir ? Directory.GetLastWriteTime(p) : fi!.LastWriteTime };
        }
    }
}
