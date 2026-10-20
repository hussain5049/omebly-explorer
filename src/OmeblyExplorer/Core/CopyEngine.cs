// Omebly's own copy and move. Faster than Windows on big jobs: many small files are copied at the same time, big
// files go straight to the disk without the system cache, and moves on the same drive are instant. Pause, resume,
// cancel and undo; files with the same name are asked about once (or one by one). Runs off the UI thread.
using System.Runtime.InteropServices;

namespace OmeblyExplorer.Core;

public enum JobState { Preparing, Asking, Running, Paused, Done, Cancelled, Failed }
public enum SameName { Ask, Replace, Skip, KeepBoth, EachFile }

public sealed class CopyJob
{
    public required IReadOnlyList<string> Sources { get; init; }
    public required string Target { get; init; }
    public bool Move { get; init; }

    public JobState State { get; internal set; } = JobState.Preparing;
    public long TotalBytes { get; internal set; }
    public int TotalItems { get; internal set; }
    public long DoneBytes => Interlocked.Read(ref _doneBytes);
    public int DoneItems => _doneItems;
    public string CurrentFile { get; internal set; } = "";
    public double BytesPerSecond { get; internal set; }
    public readonly List<double> SpeedHistory = new(); // for the graph, one sample every 250 ms
    public readonly List<(string path, string reason)> Problems = new();
    public int Skipped => _skipped;
    public TimeSpan Elapsed => _clock.Elapsed;
    public List<string> Conflicts { get; } = new();
    /// <summary>Called (on a background thread) for each file with the same name when the choice is "each file".</summary>
    public Func<string, string, Task<(SameName choice, bool forAll)>>? AskEach;
    public event Action? Changed;

    internal long _doneBytes, _lastBytes; internal int _doneItems, _skipped;
    internal readonly System.Diagnostics.Stopwatch _clock = new();
    internal readonly ManualResetEventSlim _running = new(true);
    internal readonly CancellationTokenSource _cancel = new();
    internal TaskCompletionSource<SameName>? _sameNameAnswer;
    internal readonly List<(string src, string dst, bool dir)> _made = new(); // for Undo
    internal readonly List<(string src, string dst)> _copied = new(); // every file that safely reached its target
    internal readonly List<string> _backups = new(); // replaced files' old versions, sent to the Recycle Bin at the end
    internal void Raise() => Changed?.Invoke();
    internal void AddProblem(string path, string reason) { lock (Problems) Problems.Add((path, reason)); }

    public void Pause() { if (State == JobState.Running) { _running.Reset(); State = JobState.Paused; _clock.Stop(); Raise(); } }
    public void Resume() { if (State == JobState.Paused) { State = JobState.Running; _clock.Start(); _running.Set(); Raise(); } }
    public void Cancel() { _cancel.Cancel(); _running.Set(); _sameNameAnswer?.TrySetResult(SameName.Skip); }
    /// <summary>The person's answer about files with the same name.</summary>
    public void Answer(SameName choice) => _sameNameAnswer?.TrySetResult(choice);
    public string TargetName => Path.GetFileName(Target.TrimEnd('\\')) is { Length: > 0 } n ? n : Target;
}

public static class CopyEngine
{
    private const long Big = 64L * 1024 * 1024;   // bigger files skip the system cache and go one at a time
    private const int SmallWorkers = 6;          // small files copied at the same time
    private const long NoCacheAbove = 4L << 30; // only huge files skip the system cache (so RAM isn't flooded)

    public static Task Run(CopyJob job) => Task.Run(() => RunAsync(job));

    private sealed record Item(string Src, string Dst, long Size, bool Dir);

    private static async Task RunAsync(CopyJob job)
    {
        var ct = job._cancel.Token;
        try
        {
            job._clock.Start();
            // 1. Same-drive moves are just renames: instant, whatever the size.
            bool sameDrive = job.Move && job.Sources.All((s) => string.Equals(Path.GetPathRoot(s), Path.GetPathRoot(job.Target), StringComparison.OrdinalIgnoreCase));
            // 2. Count everything ("Getting ready").
            var items = new List<Item>();
            var tops = new List<(string src, string dst, bool dir)>();
            foreach (var s in job.Sources)
            {
                ct.ThrowIfCancellationRequested();
                var src = s.TrimEnd('\\');
                bool dir = Directory.Exists(src);
                if (!dir && !File.Exists(src)) { job.AddProblem(src, "It's no longer there"); continue; }
                // A link to another folder (junction) is left alone: following it could copy, or on a move delete, the real files behind it.
                if (dir && (File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0) { job.AddProblem(src, "This is a link to another folder; it was left as it is"); continue; }
                var dst = Path.Combine(job.Target, Path.GetFileName(src));
                if (dir && (job.Target.TrimEnd('\\') + "\\").StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) { job.AddProblem(src, "A folder can't go inside itself"); continue; }
                if (string.Equals(Path.GetDirectoryName(src), job.Target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    if (job.Move) continue; // moving to where it already is: nothing to do
                    dst = FreeCopyName(job.Target, Path.GetFileName(src), dir); // copy in the same folder: "name - Copy"
                }
                tops.Add((src, dst, dir));
                if (!sameDrive) Collect(src, dst, dir, items, job, ct);
            }
            if (sameDrive) { job.TotalItems = tops.Count; foreach (var t in tops) items.Add(new Item(t.src, t.dst, 0, t.dir)); }
            job.TotalBytes = items.Sum((i) => i.Size);
            job.TotalItems = items.Count((i) => !i.Dir) + (sameDrive ? items.Count((i) => i.Dir) : 0);
            job.Raise();

            // 3. Files that already exist at the target: one question for all of them.
            var policy = SameName.KeepBoth;
            var clashes = (sameDrive ? items : items.Where((i) => !i.Dir)).Where((i) => File.Exists(i.Dst) || (sameDrive && Directory.Exists(i.Dst))).ToList();
            if (clashes.Count > 0)
            {
                job.Conflicts.AddRange(clashes.Select((c) => c.Dst));
                job._sameNameAnswer = new TaskCompletionSource<SameName>(TaskCreationOptions.RunContinuationsAsynchronously);
                job.State = JobState.Asking; job.Raise();
                policy = await job._sameNameAnswer.Task;
                ct.ThrowIfCancellationRequested();
            }
            job.State = JobState.Running; job.Raise();

            using var ticker = new Timer((_) => Sample(job), null, 250, 250);
            if (sameDrive) await MoveSameDrive(job, items, policy, ct);
            else await CopyAll(job, items, policy, ct);

            // 4. A move between drives: the originals go once every copy is safely there.
            if (job.Move && !sameDrive && !ct.IsCancellationRequested)
            {
                // Only files that were really copied (to wherever they went, "(2)" names too) lose their original; a
                // skipped file, or one that had a problem, stays exactly where it was.
                List<(string src, string dst)> copied; lock (job._copied) copied = job._copied.ToList();
                foreach (var (src, dst) in copied)
                {
                    if (!File.Exists(dst)) continue;
                    try { File.SetAttributes(src, FileAttributes.Normal); File.Delete(src); }
                    catch (Exception e) { job.AddProblem(src, "Copied, but the original couldn't be removed: " + e.Message); }
                }
                foreach (var it in items.Where((i) => i.Dir).OrderByDescending((i) => i.Src.Length)) { try { if (!Directory.EnumerateFileSystemEntries(it.Src).Any()) Directory.Delete(it.Src); } catch { } }
            }
            Sample(job);
            await RecycleBackups(job);
            job.State = ct.IsCancellationRequested ? JobState.Cancelled : JobState.Done;
        }
        catch (OperationCanceledException) { await RecycleBackups(job); job.State = JobState.Cancelled; }
        catch (Exception e) { job.AddProblem(job.Target, e.Message); job.State = JobState.Failed; }
        finally { job._clock.Stop(); job.CurrentFile = ""; job.Raise(); }
    }

    private static void Collect(string src, string dst, bool dir, List<Item> items, CopyJob job, CancellationToken ct)
    {
        if (!dir) { items.Add(new Item(src, dst, SafeLength(src), false)); return; }
        items.Add(new Item(src, dst, 0, true));
        var stack = new Stack<(string s, string d)>(); stack.Push((src, dst));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (s, d) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(s).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }); }
            catch (Exception e) { job.AddProblem(s, e.Message); continue; }
            try
            {
                foreach (var e in entries)
                {
                    var nd = Path.Combine(d, e.Name);
                    if (e is DirectoryInfo di)
                    {
                        // A link to another folder is copied as a folder link would be in Explorer: not followed.
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { job.AddProblem(di.FullName, "A link to another folder was skipped"); continue; }
                        items.Add(new Item(di.FullName, nd, 0, true)); stack.Push((di.FullName, nd));
                    }
                    else items.Add(new Item(e.FullName, nd, ((FileInfo)e).Length, false));
                    if (items.Count % 2000 == 0) { job.TotalItems = items.Count; job.Raise(); }
                }
            }
            catch (Exception e) { job.AddProblem(s, e.Message); }
        }
    }

    private static async Task CopyAll(CopyJob job, List<Item> items, SameName policy, CancellationToken ct)
    {
        foreach (var d in items.Where((i) => i.Dir))
        {
            try { if (!Directory.Exists(d.Dst)) { Directory.CreateDirectory(Long(d.Dst)); lock (job._made) job._made.Add((d.Src, d.Dst, true)); } }
            catch (Exception e) { job.AddProblem(d.Src, e.Message); }
        }
        var files = items.Where((i) => !i.Dir).ToList();
        var big = files.Where((f) => f.Size >= Big).ToList();
        var small = new System.Collections.Concurrent.ConcurrentQueue<Item>(files.Where((f) => f.Size < Big));
        var asking = new SemaphoreSlim(1); // one same-name question at a time
        var forAll = (SameName?)null;
        async Task<SameName> Decide(Item f)
        {
            if (policy != SameName.EachFile) return policy;
            if (forAll is SameName fa) return fa;
            await asking.WaitAsync(ct);
            try
            {
                if (forAll is SameName fa2) return fa2;
                if (job.AskEach == null) return SameName.KeepBoth;
                var (choice, all) = await job.AskEach(f.Src, f.Dst);
                if (all) forAll = choice;
                return choice;
            }
            finally { asking.Release(); }
        }
        async Task CopyOne(Item f)
        {
            job._running.Wait(ct);
            ct.ThrowIfCancellationRequested();
            var dst = f.Dst;
            if (File.Exists(dst))
            {
                var choice = await Decide(f);
                if (choice == SameName.Skip) { Interlocked.Increment(ref job._skipped); Interlocked.Add(ref job._doneBytes, f.Size); Interlocked.Increment(ref job._doneItems); return; }
                if (choice == SameName.KeepBoth) dst = FreeName(dst);
            }
            job.CurrentFile = f.Src;
            bool replaced = File.Exists(dst);
            try
            {
                if (replaced) ReplaceSafely(f, dst, job, ct);
                else { CopyFile(f.Src, dst, f.Size, job, ct); lock (job._made) job._made.Add((f.Src, dst, false)); }
                lock (job._copied) job._copied.Add((f.Src, dst));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { job.AddProblem(f.Src, e.Message); }
            Interlocked.Increment(ref job._doneItems);
        }
        // Big files two at a time; small files several at once.
        var bigQueue = new System.Collections.Concurrent.ConcurrentQueue<Item>(big);
        var bigTask = Task.WhenAll(Enumerable.Range(0, 2).Select((_) => Task.Run(async () => { while (bigQueue.TryDequeue(out var f)) await CopyOne(f); }, ct))); // two at a time suits SSDs
        var workers = Enumerable.Range(0, SmallWorkers).Select((_) => Task.Run(async () => { while (small.TryDequeue(out var f)) await CopyOne(f); }, ct)).ToList();
        await Task.WhenAll(workers.Append(bigTask));
        // Folders keep their dates, like Explorer.
        foreach (var d in items.Where((i) => i.Dir)) { try { Directory.SetLastWriteTimeUtc(d.Dst, Directory.GetLastWriteTimeUtc(d.Src)); } catch { } }
    }

    private static Task MoveSameDrive(CopyJob job, List<Item> items, SameName policy, CancellationToken ct)
    {
        foreach (var it in items)
        {
            job._running.Wait(ct); ct.ThrowIfCancellationRequested();
            var dst = it.Dst;
            bool exists = File.Exists(dst) || Directory.Exists(dst);
            if (exists)
            {
                var choice = policy == SameName.EachFile ? SameName.KeepBoth : policy;
                if (choice == SameName.Skip) { job._skipped++; job._doneItems++; continue; }
                if (choice == SameName.KeepBoth) dst = it.Dir ? FreeFolderName(dst) : FreeName(dst);
                else if (choice == SameName.Replace && !it.Dir)
                {
                    // Swapped in one step by Windows, so the old file is never lost halfway.
                    job.CurrentFile = it.Src;
                    try { File.SetAttributes(dst, FileAttributes.Normal); File.Move(it.Src, dst, overwrite: true); lock (job._made) job._made.Add((it.Src, dst, false)); }
                    catch (Exception e) { job.AddProblem(it.Src, e.Message); }
                    job._doneItems++; job.Raise(); continue;
                }
                else if (choice == SameName.Replace && it.Dir) { job.AddProblem(it.Src, "A folder with this name is already there; it was left as it is"); continue; }
            }
            job.CurrentFile = it.Src;
            try
            {
                if (it.Dir) Directory.Move(it.Src, dst); else File.Move(it.Src, dst);
                lock (job._made) job._made.Add((it.Src, dst, it.Dir));
            }
            catch (Exception e) { job.AddProblem(it.Src, e.Message); }
            job._doneItems++; job.Raise();
        }
        return Task.CompletedTask;
    }

    /// <summary>Takes back what the job did: copies go to the Recycle Bin, moves go back where they were.</summary>
    public static async Task Undo(CopyJob job)
    {
        List<(string src, string dst, bool dir)> made; lock (job._made) made = job._made.ToList();
        if (job.Move)
        {
            await Task.Run(() =>
            {
                foreach (var (src, dst, dir) in made.Where((m) => !m.dir || m.src.Length > 0).OrderBy((m) => m.dir).ThenByDescending((m) => m.dst.Length))
                {
                    try
                    {
                        if (dir && !Directory.Exists(src) && Directory.Exists(dst)) { Directory.Move(dst, src); continue; }
                        if (!dir && File.Exists(dst) && !File.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(src)!); File.Move(dst, src); }
                    }
                    catch { }
                }
            });
        }
        else
        {
            // Only the top items need to go: inside folders came with them.
            var tops = made.Where((m) => !made.Any((o) => o.dir && m.dst.StartsWith(o.dst + "\\", StringComparison.OrdinalIgnoreCase))).Select((m) => m.dst).ToList();
            if (tops.Count > 0) await FileOps.Recycle(tops);
        }
    }

    // ---------- One file ----------
    private delegate uint ProgressRoutine(long total, long transferred, long streamSize, long streamTransferred, uint streamNumber, uint reason, IntPtr src, IntPtr dst, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileExW(string src, string dst, ProgressRoutine? progress, IntPtr data, ref int cancel, uint flags);
    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1, COPY_FILE_NO_BUFFERING = 0x1000;
    private const uint PROGRESS_CONTINUE = 0, PROGRESS_CANCEL = 1;

    /// <summary>
    /// Replace: the new file is copied next to the old one under a temporary name first, then swapped in by Windows in
    /// one step. A cancel or a problem never leaves a damaged file, and the old version goes to the Recycle Bin.
    /// </summary>
    private static void ReplaceSafely(Item f, string dst, CopyJob job, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dst)!;
        var tmp = Path.Combine(dir, $".~omebly-{Guid.NewGuid():N}.tmp");
        try
        {
            CopyFile(f.Src, tmp, f.Size, job, ct);
            File.SetAttributes(dst, FileAttributes.Normal);
            var old = OldName(dst);
            File.Replace(Long(tmp), Long(dst), Long(old), ignoreMetadataErrors: true);
            lock (job._backups) job._backups.Add(old);
        }
        catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } throw; }
    }
    private static string OldName(string path)
    {
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        var c = Path.Combine(dir, $"{name} (old){ext}");
        for (int n = 2; File.Exists(c) || Directory.Exists(c); n++) c = Path.Combine(dir, $"{name} (old {n}){ext}");
        return c;
    }
    private static async Task RecycleBackups(CopyJob job)
    {
        List<string> list; lock (job._backups) { list = job._backups.Where(File.Exists).ToList(); job._backups.Clear(); }
        if (list.Count > 0) await FileOps.Recycle(list);
    }

    private static void CopyFile(string src, string dst, long size, CopyJob job, CancellationToken ct)
    {
        long last = 0;
        int cancel = 0;
        ProgressRoutine progress = (total, done, _, _, _, _, _, _, _) =>
        {
            Interlocked.Add(ref job._doneBytes, done - last); last = done;
            if (!job._running.IsSet) job._running.Wait(); // paused: wait here, mid-file
            return ct.IsCancellationRequested ? PROGRESS_CANCEL : PROGRESS_CONTINUE;
        };
        // Never overwrites (Replace goes through ReplaceSafely), and encrypted files are never quietly decrypted.
        uint flags = COPY_FILE_FAIL_IF_EXISTS | (size >= NoCacheAbove ? COPY_FILE_NO_BUFFERING : 0);
        bool ok = CopyFileExW(Long(src), Long(dst), progress, IntPtr.Zero, ref cancel, flags);
        GC.KeepAlive(progress);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            if (ct.IsCancellationRequested || err == 1235 /* cancelled */) throw new OperationCanceledException();
            Interlocked.Add(ref job._doneBytes, size - last);
            throw new IOException(err == 6000 ? "This file is encrypted, and the place it's going can't keep it encrypted (so it wasn't copied)" : new System.ComponentModel.Win32Exception(err).Message);
        }
        Interlocked.Add(ref job._doneBytes, size - last); // small files may not report every byte
    }

    // ---------- Helpers ----------
    private static void Sample(CopyJob job)
    {
        var s = job.Elapsed.TotalSeconds;
        lock (job.SpeedHistory)
        {
            var prev = job.SpeedHistory.Count > 0 ? job._lastBytes : 0;
            var speed = (job.DoneBytes - prev) / 0.25;
            job._lastBytes = job.DoneBytes;
            if (job.State == JobState.Running) { job.SpeedHistory.Add(Math.Max(0, speed)); if (job.SpeedHistory.Count > 80) job.SpeedHistory.RemoveAt(0); }
            // A smooth speed for "time left": the average of the last 2 seconds.
            var recent = job.SpeedHistory.TakeLast(8).ToList();
            job.BytesPerSecond = recent.Count > 0 ? recent.Average() : (s > 0 ? job.DoneBytes / s : 0);
        }
        job.Raise();
    }
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    /// <summary>Paths longer than Windows' old 260-character limit still work.</summary>
    private static string Long(string p) => p.Length >= 248 && !p.StartsWith(@"\\?\") ? (p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p) : p;
    public static string FreeName(string path)
    {
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int n = 2; ; n++) { var c = Path.Combine(dir, $"{name} ({n}){ext}"); if (!File.Exists(c) && !Directory.Exists(c)) return c; }
    }
    private static string FreeFolderName(string path) { for (int n = 2; ; n++) { var c = $"{path} ({n})"; if (!File.Exists(c) && !Directory.Exists(c)) return c; } }
    private static string FreeCopyName(string folder, string name, bool dir)
    {
        var bare = dir ? name : Path.GetFileNameWithoutExtension(name); var ext = dir ? "" : Path.GetExtension(name);
        var c = Path.Combine(folder, $"{bare} - Copy{ext}");
        for (int n = 2; File.Exists(c) || Directory.Exists(c); n++) c = Path.Combine(folder, $"{bare} - Copy ({n}){ext}");
        return c;
    }
}
