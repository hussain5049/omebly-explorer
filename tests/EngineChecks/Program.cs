// Copy engine checks with real files in D:\oxtest\eng (and a little in the temp folder on C: for moves between
// drives). Run: dotnet run -c Release --project tests/EngineChecks
using System.Diagnostics;
using System.Security.Cryptography;
using OmeblyExplorer.Core;

const string Root = @"D:\oxtest\eng";
var other = Path.Combine(Path.GetTempPath(), "omebly-eng"); // another drive (C:)
int pass = 0, fail = 0;
void Ok(string name, bool cond, string info = "") { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info == "" ? "" : " — " + info)}"); if (cond) pass++; else fail++; }
void Fresh(string d) { if (Directory.Exists(d)) { foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); } Directory.CreateDirectory(d); }
static string Hash(string f) { using var s = File.OpenRead(f); return Convert.ToHexString(SHA256.HashData(s)); }
bool Same(string a, string b) => Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select((f) => Path.GetRelativePath(a, f)).OrderBy((x) => x).SequenceEqual(Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select((f) => Path.GetRelativePath(b, f)).OrderBy((x) => x))
    && Directory.GetFiles(a, "*", SearchOption.AllDirectories).All((f) => new FileInfo(f).Length == new FileInfo(Path.Combine(b, Path.GetRelativePath(a, f))).Length);
async Task<CopyJob> Run(IReadOnlyList<string> src, string target, bool move = false, SameName answer = SameName.KeepBoth, Action<CopyJob>? during = null)
{
    var job = new CopyJob { Sources = src, Target = target, Move = move };
    job.Changed += () => { if (job.State == JobState.Asking) job.Answer(answer); };
    var t = CopyEngine.Run(job);
    during?.Invoke(job);
    await t;
    return job;
}

// ---------- Test data: a project folder with many small files, and two big videos ----------
var rnd = new Random(42);
var data = Path.Combine(Root, "data"); var small = Path.Combine(data, "Project"); var big = Path.Combine(data, "Videos");
if (!Directory.Exists(small) || Directory.GetFiles(small, "*", SearchOption.AllDirectories).Length != 3000)
{
    Fresh(data); Directory.CreateDirectory(big);
    for (int i = 0; i < 3000; i++)
    {
        var d = Path.Combine(small, $"part {i / 100:00}", $"sub {i % 7}"); Directory.CreateDirectory(d);
        var buf = new byte[rnd.Next(4_000, 200_000)]; rnd.NextBytes(buf); File.WriteAllBytes(Path.Combine(d, $"file {i:0000}.dat"), buf);
    }
    for (int i = 0; i < 2; i++)
    {
        using var f = File.Create(Path.Combine(big, $"video {i}.mp4")); var buf = new byte[4 << 20];
        for (int k = 0; k < 125; k++) { rnd.NextBytes(buf); f.Write(buf); } // 500 MB each
    }
}
long smallBytes = Directory.GetFiles(small, "*", SearchOption.AllDirectories).Sum((f) => new FileInfo(f).Length);
Console.WriteLine($"test data: 3,000 small files ({smallBytes / 1048576} MB) and 2 videos (1,000 MB)\n");

// ---------- Speed: Omebly against Windows' own copy (the one File Explorer uses) ----------
async Task<double> Ours(string src, string dstRoot) { Fresh(dstRoot); var sw = Stopwatch.StartNew(); var j = await Run(new[] { src }, dstRoot); sw.Stop(); Ok($"speed: our copy of {Path.GetFileName(src)} is complete and identical", j.State == JobState.Done && j.Problems.Count == 0 && Same(src, Path.Combine(dstRoot, Path.GetFileName(src))), string.Join("; ", j.Problems.Take(3))); return sw.Elapsed.TotalSeconds; }
double Windows(string src, string dstRoot) { Fresh(dstRoot); var sw = Stopwatch.StartNew(); FileOps.WindowsCopy(src, dstRoot); sw.Stop(); return sw.Elapsed.TotalSeconds; }
foreach (var (src, label) in new[] { (small, "3,000 small files"), (big, "2 big files") })
{
    // Each one twice, taking turns, best time counts (so neither gets an unfair warm disk cache).
    var w = new List<double>(); var o = new List<double>();
    for (int r = 0; r < 2; r++) { w.Add(Windows(src, Path.Combine(Root, "win"))); o.Add(await Ours(src, Path.Combine(Root, "ours"))); }
    Console.WriteLine($"     {label}: Windows {w.Min():0.00} s, Omebly {o.Min():0.00} s ({w.Min() / o.Min():0.0}x)");
    Ok($"speed: {label} copy at least as fast as Windows (within 15%)", o.Min() <= w.Min() * 1.15, $"Windows {w.Min():0.00} s vs ours {o.Min():0.00} s");
}

// ---------- Copy in the same folder ----------
var work = Path.Combine(Root, "work"); Fresh(work);
File.WriteAllText(Path.Combine(work, "note.txt"), "hello");
var j1 = await Run(new[] { Path.Combine(work, "note.txt") }, work);
Ok("same folder: a copy is made as \"note - Copy.txt\"", File.Exists(Path.Combine(work, "note - Copy.txt")) && File.ReadAllText(Path.Combine(work, "note - Copy.txt")) == "hello");

// ---------- Same names ----------
var a = Path.Combine(work, "a"); var b = Path.Combine(work, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
File.WriteAllText(Path.Combine(a, "x.txt"), "new"); File.WriteAllText(Path.Combine(b, "x.txt"), "old");
var jr = await Run(new[] { Path.Combine(a, "x.txt") }, b, answer: SameName.Replace);
Ok("same name: the question is asked", jr.Conflicts.Count == 1);
Ok("same name: Replace replaces", File.ReadAllText(Path.Combine(b, "x.txt")) == "new");
File.WriteAllText(Path.Combine(b, "x.txt"), "old");
await Run(new[] { Path.Combine(a, "x.txt") }, b, answer: SameName.Skip);
Ok("same name: Skip leaves the old file", File.ReadAllText(Path.Combine(b, "x.txt")) == "old");
await Run(new[] { Path.Combine(a, "x.txt") }, b, answer: SameName.KeepBoth);
Ok("same name: Keep both adds \"(2)\"", File.ReadAllText(Path.Combine(b, "x.txt")) == "old" && File.ReadAllText(Path.Combine(b, "x (2).txt")) == "new");
var each = new CopyJob { Sources = new[] { Path.Combine(a, "x.txt") }, Target = b };
int asked = 0;
each.AskEach = (s, d) => { asked++; return Task.FromResult((SameName.Replace, true)); };
each.Changed += () => { if (each.State == JobState.Asking) each.Answer(SameName.EachFile); };
await CopyEngine.Run(each);
Ok("same name: \"decide for each file\" asks about the file", asked == 1 && File.ReadAllText(Path.Combine(b, "x.txt")) == "new");
var jc = await Run(new[] { Path.Combine(a, "x.txt") }, b, during: (j) => { j.Changed += () => { if (j.State == JobState.Asking) j.Cancel(); }; });

// ---------- Pause, resume, cancel ----------
var pr = Path.Combine(Root, "pause"); Fresh(pr);
var pj = new CopyJob { Sources = new[] { big }, Target = pr };
var pt = CopyEngine.Run(pj);
while (pj.DoneBytes < 100L << 20) await Task.Delay(20);
pj.Pause(); await Task.Delay(300); var at = pj.DoneBytes; await Task.Delay(1000);
Ok("pause: nothing more is copied while paused", pj.DoneBytes - at < 8 << 20 && pj.State == JobState.Paused, $"{(pj.DoneBytes - at) >> 20} MB moved while paused");
pj.Resume(); await pt;
Ok("resume: the copy finishes complete and identical", pj.State == JobState.Done && Same(big, Path.Combine(pr, "Videos")) && Hash(Path.Combine(big, "video 1.mp4")) == Hash(Path.Combine(pr, "Videos", "video 1.mp4")));
Fresh(pr);
var cj = new CopyJob { Sources = new[] { big }, Target = pr };
var ct = CopyEngine.Run(cj);
while (cj.DoneBytes < 200L << 20) await Task.Delay(20);
cj.Cancel(); await ct;
var leftover = Directory.Exists(Path.Combine(pr, "Videos")) ? Directory.GetFiles(Path.Combine(pr, "Videos")).Where((f) => new FileInfo(f).Length != new FileInfo(Path.Combine(big, Path.GetFileName(f))).Length).ToList() : new();
Ok("cancel: stops and leaves no half-copied file", cj.State == JobState.Cancelled && leftover.Count == 0, string.Join(", ", leftover));

// ---------- Undo ----------
var ud = Path.Combine(Root, "undo"); Fresh(ud);
var uj = await Run(new[] { small }, ud);
await CopyEngine.Undo(uj);
Ok("undo copy: the copies go (to the Recycle Bin)", !Directory.Exists(Path.Combine(ud, "Project")));

// ---------- Moves ----------
var m1 = Path.Combine(work, "move me"); Directory.CreateDirectory(Path.Combine(m1, "inner")); File.WriteAllText(Path.Combine(m1, "inner", "f.txt"), "f");
var mdst = Path.Combine(work, "moved here"); Directory.CreateDirectory(mdst);
var sw2 = Stopwatch.StartNew(); var mj = await Run(new[] { m1 }, mdst, move: true); sw2.Stop();
Ok("move on the same drive: instant and complete", !Directory.Exists(m1) && File.Exists(Path.Combine(mdst, "move me", "inner", "f.txt")) && sw2.ElapsedMilliseconds < 500, $"{sw2.ElapsedMilliseconds} ms");
await CopyEngine.Undo(mj);
Ok("undo move: it goes back where it was", Directory.Exists(m1) && File.Exists(Path.Combine(m1, "inner", "f.txt")) && !Directory.Exists(Path.Combine(mdst, "move me")));
Fresh(other);
var xj = await Run(new[] { m1 }, other, move: true);
Ok("move to another drive: copied there, then removed here", !Directory.Exists(m1) && File.ReadAllText(Path.Combine(other, "move me", "inner", "f.txt")) == "f" && xj.Problems.Count == 0, string.Join("; ", xj.Problems));

// ---------- Moves between drives with files that are already there (Security 337) ----------
async Task CrossMove(SameName answer, bool eachThenSkip, string label, Func<string, string, bool> check)
{
    var srcDir = Path.Combine(work, "xsrc " + label); Fresh(srcDir);
    var dstDir = Path.Combine(other, "xdst " + label); Fresh(dstDir);
    File.WriteAllText(Path.Combine(srcDir, "same.txt"), "mine"); File.WriteAllText(Path.Combine(srcDir, "only.txt"), "only");
    File.WriteAllText(Path.Combine(dstDir, "same.txt"), "theirs");
    var j = new CopyJob { Sources = new[] { Path.Combine(srcDir, "same.txt"), Path.Combine(srcDir, "only.txt") }, Target = dstDir, Move = true };
    j.Changed += () => { if (j.State == JobState.Asking) j.Answer(eachThenSkip ? SameName.EachFile : answer); };
    if (eachThenSkip) j.AskEach = (_, _) => Task.FromResult((SameName.Skip, false));
    await CopyEngine.Run(j);
    Ok($"move between drives, {label}", check(srcDir, dstDir), $"src: {string.Join(",", Directory.GetFiles(srcDir).Select(Path.GetFileName))}; dst: {string.Join(",", Directory.GetFiles(dstDir).Select((f) => Path.GetFileName(f) + "=" + File.ReadAllText(f)))}");
}
await CrossMove(SameName.Skip, false, "Skip keeps the skipped original", (s, d) => File.ReadAllText(Path.Combine(s, "same.txt")) == "mine" && File.ReadAllText(Path.Combine(d, "same.txt")) == "theirs" && !File.Exists(Path.Combine(s, "only.txt")) && File.Exists(Path.Combine(d, "only.txt")));
await CrossMove(SameName.Skip, true, "Each file then Skip keeps the skipped original", (s, d) => File.ReadAllText(Path.Combine(s, "same.txt")) == "mine" && File.ReadAllText(Path.Combine(d, "same.txt")) == "theirs");
await CrossMove(SameName.KeepBoth, false, "Keep both moves it as (2)", (s, d) => !File.Exists(Path.Combine(s, "same.txt")) && File.ReadAllText(Path.Combine(d, "same (2).txt")) == "mine" && File.ReadAllText(Path.Combine(d, "same.txt")) == "theirs");
await CrossMove(SameName.Replace, false, "Replace swaps in the new file", (s, d) => !File.Exists(Path.Combine(s, "same.txt")) && File.ReadAllText(Path.Combine(d, "same.txt")) == "mine" && !Directory.GetFiles(d, "*.tmp").Any() && !File.Exists(Path.Combine(d, "same (old).txt")));

// Replace that is cancelled halfway keeps the old file whole.
var rbig = Path.Combine(other, "rbig"); Fresh(rbig);
File.Copy(Path.Combine(big, "video 0.mp4"), Path.Combine(rbig, "video 1.mp4")); // the "old" one at the target
var oldHash = Hash(Path.Combine(rbig, "video 1.mp4"));
var rj = new CopyJob { Sources = new[] { Path.Combine(big, "video 1.mp4") }, Target = rbig };
rj.Changed += () => { if (rj.State == JobState.Asking) rj.Answer(SameName.Replace); };
var rt = CopyEngine.Run(rj);
while (rj.DoneBytes < 100L << 20 && !rt.IsCompleted) await Task.Delay(5);
rj.Cancel(); await rt;
Ok("replace cancelled halfway: the old file is untouched and no temporary file is left", (Hash(Path.Combine(rbig, "video 1.mp4")) == oldHash || rj.State == JobState.Done) && !Directory.GetFiles(rbig, "*.tmp", new EnumerationOptions { AttributesToSkip = 0 }).Any(), rj.State.ToString());

// A link to another folder is left alone (never followed, never deleted).
var real = Path.Combine(work, "real folder"); Fresh(real); File.WriteAllText(Path.Combine(real, "keep.txt"), "keep");
var link = Path.Combine(work, "link to real");
if (!Directory.Exists(link)) Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
if (Directory.Exists(link))
{
    var lnj = await Run(new[] { link }, other, move: true);
    Ok("a folder link moved to another drive: left as it is, the real files stay", File.Exists(Path.Combine(real, "keep.txt")) && lnj.Problems.Any((p) => p.reason.Contains("link")));
}

// ---------- Hard cases ----------
var self = await Run(new[] { work }, Path.Combine(work, "a"));
Ok("a folder can't be copied into itself", self.Problems.Any((p) => p.reason.Contains("inside itself")) && !Directory.Exists(Path.Combine(work, "a", "work")));
var deep = Path.Combine(work, string.Join("\\", Enumerable.Repeat("a very long folder name to pass the old limit", 7)));
Directory.CreateDirectory(@"\\?\" + deep); File.WriteAllText(@"\\?\" + Path.Combine(deep, "deep.txt"), "deep");
var lj = await Run(new[] { Path.Combine(work, "a very long folder name to pass the old limit") }, b);
Ok("paths longer than 260 characters copy fine", lj.Problems.Count == 0 && File.Exists(@"\\?\" + Path.Combine(b, Path.GetRelativePath(work, deep), "deep.txt")), string.Join("; ", lj.Problems));
var locked = Path.Combine(work, "locked.txt"); File.WriteAllText(locked, "x"); File.WriteAllText(Path.Combine(work, "free.txt"), "y");
var dst3 = Path.Combine(work, "c"); Directory.CreateDirectory(dst3);
using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    var kj = await Run(new[] { locked, Path.Combine(work, "free.txt") }, dst3);
    Ok("a file in use is reported, and the others still copy", kj.Problems.Count == 1 && File.Exists(Path.Combine(dst3, "free.txt")), string.Join("; ", kj.Problems));
}
var gone = await Run(new[] { Path.Combine(work, "not there.txt") }, dst3);
Ok("a file that disappeared is reported kindly", gone.Problems.Any((p) => p.reason.Contains("no longer there")));
var ro = Path.Combine(work, "readonly.txt"); File.WriteAllText(ro, "ro"); File.SetAttributes(ro, FileAttributes.ReadOnly);
File.WriteAllText(Path.Combine(dst3, "readonly.txt"), "old"); File.SetAttributes(Path.Combine(dst3, "readonly.txt"), FileAttributes.ReadOnly);
await Run(new[] { ro }, dst3, answer: SameName.Replace);
Ok("replacing a read-only file works", File.ReadAllText(Path.Combine(dst3, "readonly.txt")) == "ro");

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
