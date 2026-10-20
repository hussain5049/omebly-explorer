// Undo and redo (Ctrl+Z / Ctrl+Y) for what Omebly Explorer did to files: rename, new, delete to the Recycle Bin,
// copy, move and shortcuts. Like Explorer, deleting for good can't be undone.
namespace OmeblyExplorer.Core;

public sealed class UndoStep
{
    public required string What { get; init; }         // "Rename “a.txt” to “b.txt”"
    public required Func<Task<bool>> Undo { get; init; }
    public required Func<Task<bool>> Redo { get; init; }
}

public static class UndoLog
{
    private static readonly Stack<UndoStep> Done = new(), Undone = new();
    private static readonly SemaphoreSlim One = new(1);
    public static event Action? Changed;
    public static string? NextUndo { get { lock (Done) return Done.Count > 0 ? Done.Peek().What : null; } }
    public static string? NextRedo { get { lock (Done) return Undone.Count > 0 ? Undone.Peek().What : null; } }

    public static void Add(UndoStep step)
    {
        lock (Done) { Done.Push(step); Undone.Clear(); while (Done.Count > 200) TrimOldest(); }
        Changed?.Invoke();
    }
    private static void TrimOldest() { var keep = Done.Reverse().Skip(1).ToList(); Done.Clear(); foreach (var s in keep) Done.Push(s); }

    /// <summary>Takes back the last step. Returns what was undone (null if nothing, or it failed: then the reason).</summary>
    public static async Task<(string? what, bool ok)> UndoLast()
    {
        await One.WaitAsync();
        try
        {
            UndoStep? s; lock (Done) s = Done.Count > 0 ? Done.Pop() : null;
            if (s == null) return (null, true);
            bool ok; try { ok = await s.Undo(); } catch { ok = false; }
            lock (Done) { if (ok) Undone.Push(s); }
            Changed?.Invoke();
            return (s.What, ok);
        }
        finally { One.Release(); }
    }

    public static async Task<(string? what, bool ok)> RedoLast()
    {
        await One.WaitAsync();
        try
        {
            UndoStep? s; lock (Done) s = Undone.Count > 0 ? Undone.Pop() : null;
            if (s == null) return (null, true);
            bool ok; try { ok = await s.Redo(); } catch { ok = false; }
            lock (Done) { if (ok) Done.Push(s); }
            Changed?.Invoke();
            return (s.What, ok);
        }
        finally { One.Release(); }
    }

    // ---------- The steps ----------
    public static void Renamed(string oldPath, string newPath) => Add(new UndoStep
    {
        What = $"Rename “{Path.GetFileName(oldPath)}” to “{Path.GetFileName(newPath)}”",
        Undo = () => Exists(newPath) && !Exists(oldPath) ? FileOps.Rename(newPath, Path.GetFileName(oldPath)) : Task.FromResult(false),
        Redo = () => Exists(oldPath) && !Exists(newPath) ? FileOps.Rename(oldPath, Path.GetFileName(newPath)) : Task.FromResult(false),
    });

    /// <summary>Sent to the Recycle Bin: undo brings them back from it.</summary>
    public static void Recycled(IReadOnlyList<string> paths, DateTime when, string? what = null) => Add(new UndoStep
    {
        What = what ?? (paths.Count == 1 ? $"Delete “{Path.GetFileName(paths[0].TrimEnd('\\'))}”" : $"Delete {paths.Count} items"),
        Undo = () => Task.Run(() =>
        {
            var found = RecycleBin.Find(paths, when);
            return found.Count > 0 && found.All((i) => RecycleBin.Restore(i) != null);
        }),
        Redo = () => { when = DateTime.Now; return FileOps.Recycle(paths.Where(Exists)); },
    });

    /// <summary>Something new was made (folder, file, shortcut): undo sends it to the Recycle Bin, redo brings it back.</summary>
    public static void Made(string path, string what)
    {
        DateTime when = default;
        var born = Born(path);
        Add(new UndoStep
        {
            What = what,
            // Only the very thing that was made: if something else has taken its place since, it is left alone.
            Undo = async () => { if (!Exists(path) || Math.Abs((Born(path) - born).TotalSeconds) > 2) return false; when = DateTime.Now; return await FileOps.Recycle(new[] { path }); },
            Redo = () => Task.Run(() => RecycleBin.Find(new[] { path }, when) is { Count: > 0 } f && RecycleBin.Restore(f[0]) != null),
        });
    }

    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
    private static DateTime Born(string p) { try { return Directory.Exists(p) ? Directory.GetCreationTimeUtc(p) : File.GetCreationTimeUtc(p); } catch { return default; } }
}
