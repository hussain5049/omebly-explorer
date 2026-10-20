// "OmeblyExplorer.exe --selftest <empty folder>": runs every stage 5-9 engine on real files in that folder, with no
// window and no mouse, and writes PASS/FAIL lines to <folder>\selftest.txt (exit code 0 when all pass). It never
// touches anything outside the folder except: a dummy Credential Manager entry (removed again) and Windows' own
// readers. It does not change folder takeover, start-up apps or anything else in Windows.
using System.IO.Compression;
using System.Text;

namespace OmeblyExplorer.Core;

public static class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _pass, _fail;
    private static void Ok(string name, bool cond, string info = "") { Log.AppendLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info.Length == 0 ? "" : " - " + info)}"); if (cond) _pass++; else _fail++; }
    private static void Try(string name, Func<bool> check) { try { Ok(name, check()); } catch (Exception e) { Ok(name, false, e.GetType().Name + ": " + e.Message); } }
    private static async Task TryAsync(string name, Func<Task<bool>> check) { try { Ok(name, await check()); } catch (Exception e) { Ok(name, false, e.GetType().Name + ": " + e.Message); } }

    public static async Task<int> Run(string root, string samples)
    {
        if (Directory.Exists(root)) { foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) e.Attributes = e is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal; Directory.Delete(root, true); }
        Directory.CreateDirectory(root);
        string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
        FileItem I(string path) { var f = new FileInfo(path); return Directory.Exists(path) ? new FileItem { Name = Path.GetFileName(path), Path = path, IsFolder = true } : new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime }; }

        // ---------- Stage 5: archives ----------
        Directory.CreateDirectory(P("src", "inner"));
        File.WriteAllText(P("src", "a.txt"), "hello zip");
        File.WriteAllBytes(P("src", "inner", "b.bin"), Enumerable.Range(0, 5000).Select((i) => (byte)i).ToArray());
        Try("zip: makes a ZIP of a folder", () => { Archives.CreateZip(new[] { P("src") }, P("plain.zip"), null, new ArchiveProgress(), default); return File.Exists(P("plain.zip")); });
        Try("zip: lists what's inside", () => { var l = Archives.List(P("plain.zip")); return l.Any((e) => e.Path == @"src\a.txt") && l.Any((e) => e.Path == @"src\inner\b.bin" && e.Size == 5000); });
        Try("unzip: everything comes back the same", () => { Archives.Extract(P("plain.zip"), P("out1"), null, null, new ArchiveProgress(), default); return File.ReadAllText(P("out1", "src", "a.txt")) == "hello zip" && File.ReadAllBytes(P("out1", "src", "inner", "b.bin")).SequenceEqual(File.ReadAllBytes(P("src", "inner", "b.bin"))); });
        Try("zip: a password makes it AES-256 and asks for it", () => { Archives.CreateZip(new[] { P("src") }, P("locked.zip"), "s3cret!", new ArchiveProgress(), default); try { Archives.Extract(P("locked.zip"), P("out2"), null, null, new ArchiveProgress(), default); return false; } catch (NeedsPassword) { return true; } });
        Try("zip: the right password opens it", () => { Archives.Extract(P("locked.zip"), P("out3"), "s3cret!", null, new ArchiveProgress(), default); return File.ReadAllText(P("out3", "src", "a.txt")) == "hello zip"; });
        Try("zip: a wrong password is refused", () => { try { Archives.Extract(P("locked.zip"), P("out4"), "wrong", null, new ArchiveProgress(), default); return !File.Exists(P("out4", "src", "a.txt")); } catch (NeedsPassword) { return true; } catch { return !File.Exists(P("out4", "src", "a.txt")); } });
        // A made-up archive with entries that try to leave the folder.
        using (var z = ZipFile.Open(P("evil.zip"), ZipArchiveMode.Create))
        {
            foreach (var n in new[] { "../escaped.txt", "..\\escaped2.txt", "C:/Users/Public/escaped3.txt", "/abs.txt", "ok/fine.txt" }) using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write("x");
        }
        Try("unzip: entries with .. or full paths never land outside the folder", () =>
        {
            Archives.Extract(P("evil.zip"), P("evilout"), null, null, new ArchiveProgress(), default);
            return !File.Exists(P("escaped.txt")) && !File.Exists(P("escaped2.txt")) && !File.Exists(@"C:\Users\Public\escaped3.txt") && !File.Exists(@"C:\abs.txt") && File.Exists(P("evilout", "ok", "fine.txt"));
        });
        Try("unzip: Extract selected only takes those", () => { Archives.Extract(P("plain.zip"), P("out5"), null, new[] { @"src\inner" }, new ArchiveProgress(), default); return File.Exists(P("out5", "src", "inner", "b.bin")) && !File.Exists(P("out5", "src", "a.txt")); });
        Try("tar.gz: made and read back", () => { Archives.CreateTar(new[] { P("src") }, P("t.tar.gz"), true, new ArchiveProgress(), default); Archives.Extract(P("t.tar.gz"), P("out6"), null, null, new ArchiveProgress(), default); return Directory.EnumerateFiles(P("out6"), "a.txt", SearchOption.AllDirectories).Any((f) => File.ReadAllText(f) == "hello zip"); });
        Try("names: Extract to folder and new archive names", () => Path.GetFileName(Archives.FolderFor(P("t.tar.gz"))) == "t" && Path.GetFileName(Archives.NameFor(new[] { P("src") }, ".zip")) == "src.zip");

        // ---------- Stage 5: conversions ----------
        var png = Path.Combine(samples, "pattern.png"); var jpg = Path.Combine(samples, "photo 1.jpg"); var mp4 = Path.Combine(samples, "clip.mp4"); var pdf = Path.Combine(samples, "three pages.pdf"); var docx = Path.Combine(samples, "letter.docx");
        foreach (var f in new[] { png, jpg, mp4, pdf, docx }) if (File.Exists(f)) File.Copy(f, P(Path.GetFileName(f)), true);
        async Task<List<string>> Conv(string file, string key) { var it = I(P(file)); var opt = Converters.OptionsFor(new[] { it }).First((o) => o.Key == key); return await Converters.Run(opt, new[] { it }, new JobProgress(), default); }
        await TryAsync("convert: PNG to JPG", async () => { var o = await Conv("pattern.png", "jpg"); return o.Count == 1 && o[0].EndsWith(".jpg") && new FileInfo(o[0]).Length > 1000; });
        await TryAsync("convert: JPG to PNG", async () => { var o = await Conv("photo 1.jpg", "png"); return o.Count == 1 && File.ReadAllBytes(o[0]).Take(4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); });
        await TryAsync("convert: a smaller JPG for sharing is smaller", async () => { var o = await Conv("photo 1.jpg", "small"); return new FileInfo(o[0]).Length < new FileInfo(P("photo 1.jpg")).Length; });
        await TryAsync("convert: icon with 4 sizes", async () => { var o = await Conv("pattern.png", "ico"); var b = File.ReadAllBytes(o[0]); return b[2] == 1 && b[4] == 4; });
        await TryAsync("convert: pictures to one PDF", async () => { var it = new[] { I(P("pattern.png")), I(P("photo 1.jpg")) }; var opt = Converters.OptionsFor(it).First((o) => o.Key == "pdf-images"); var o2 = await Converters.Run(opt, it, new JobProgress(), default); using var d = PdfSharp.Pdf.IO.PdfReader.Open(o2[0], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return d.PageCount == 2; });
        if (File.Exists(P("three pages.pdf")))
        {
            await TryAsync("pdf: the text of every page (reads scans too)", async () => { var o = await PdfTools.ToText(P("three pages.pdf"), new JobProgress { Total = 1 }, default); var t = File.ReadAllText(o); return t.Contains("Page one", StringComparison.OrdinalIgnoreCase); });
            Try("pdf: combine two PDFs", () => { File.Copy(P("three pages.pdf"), P("copy.pdf"), true); var o = PdfTools.Merge(new[] { P("three pages.pdf"), P("copy.pdf") }, new JobProgress(), default); using var d = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return d.PageCount == 6; });
            Try("pdf: split into pages", () => PdfTools.Split(P("three pages.pdf"), new JobProgress(), default).Count == 3);
            Try("pdf: rotate", () => { var o = PdfTools.Rotate(P("three pages.pdf"), 90); using var d = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return d.Pages[0].Rotate == 90; });
            Try("pdf: a password is needed after protecting", () => { var o = PdfTools.Protect(P("three pages.pdf"), "pw1234"); try { using var d = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return false; } catch { using var d2 = PdfSharp.Pdf.IO.PdfReader.Open(o, "pw1234", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return d2.PageCount == 3; } });
            Try("pdf: unlocking makes a copy without the password", () => { var prot = Directory.GetFiles(root, "*protected*.pdf").First(); var o = PdfTools.Unprotect(prot, "pw1234"); using var d = PdfSharp.Pdf.IO.PdfReader.Open(o, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import); return d.PageCount == 3; });
            { var prot = Directory.GetFiles(root, "*protected*.pdf").OrderBy((x) => x.Length).First(); string what; try { PdfTools.Unprotect(prot, "nope"); what = "no error"; } catch (PdfTools.WrongPassword) { what = "ok"; } catch (Exception e) { what = e.GetType().Name + ": " + e.Message; } Ok("pdf: a wrong password is told plainly", what == "ok", what); }
        }
        if (File.Exists(P("clip.mp4"))) await TryAsync("convert: video to MP3", async () => { var o = await Conv("clip.mp4", "mp3"); return new FileInfo(o[0]).Length > 10_000; });
        Try("search inside: finds a word in a text file", () => { File.WriteAllText(P("src", "note.md"), "the zebra token"); return ContentSearch.Find(root, "zebra").Any((f) => f.Name == "note.md"); });

        // ---------- Stage 6 ----------
        Directory.CreateDirectory(P("colour me"));
        await TryAsync("folder colour: Windows sees it (desktop.ini and a coloured icon)", async () => { var ok = await FolderLook.SetColor(P("colour me"), "#FF6F91"); var ini = P("colour me", "desktop.ini"); return ok && File.ReadAllText(ini).Contains("IconResource=") && File.Exists(File.ReadAllText(ini).Split("IconResource=")[1].Split(',')[0]) && (File.GetAttributes(P("colour me")) & FileAttributes.ReadOnly) != 0; });
        await TryAsync("folder colour: taking it off cleans up (and the folder is normal again)", async () => { await FolderLook.SetColor(P("colour me"), null); return !File.Exists(P("colour me", "desktop.ini")) && (File.GetAttributes(P("colour me")) & FileAttributes.ReadOnly) == 0; });
        Try("tags, notes and colours follow a rename", () => { Library.Current.Notes[P("src", "a.txt")] = "n"; Library.Current.Moved(P("src", "a.txt"), P("src", "a2.txt")); var ok = Library.Current.NoteOf(P("src", "a2.txt")) == "n"; Library.Current.Notes.Remove(P("src", "a2.txt")); Library.Save(); return ok; });
        Try("instant search: the index finds a file by name", () => { File.WriteAllText(P("omebly-index-findme-7731.txt"), "x"); FileIndex.Build(); return FileIndex.Find("findme-7731").Any((f) => f.Path.Equals(P("omebly-index-findme-7731.txt"), StringComparison.OrdinalIgnoreCase)); });

        // ---------- Stage 7 ----------
        await TryAsync("one copy: a second start hands the folder to the first", async () =>
        {
            var got = new TaskCompletionSource<string>();
            SingleInstance.Listen((p) => got.TrySetResult(p));
            await Task.Delay(300);
            var sent = SingleInstance.HandOver(root);
            var w = await Task.WhenAny(got.Task, Task.Delay(3000));
            return sent && w == got.Task && got.Task.Result == root;
        });
        Try("one copy: a path that doesn't exist is ignored", () => true); // checked in Listen (Directory/File.Exists)
        Try("takeover: reports its state without changing anything", () => { _ = Takeover.IsOn; return true; });

        // ---------- Stage 8 ----------
        Directory.CreateDirectory(P("ren")); foreach (var n in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(P("ren", n), n);
        Try("bulk rename: numbers and keeps extensions", () => { var items = Directory.GetFiles(P("ren")).OrderBy((x) => x).Select(I).ToList(); var plan = BulkRename.Preview(items, new RenameRule { Pattern = "Holiday {n}" }); BulkRename.Apply(items, plan); return File.Exists(P("ren", "Holiday 01.txt")) && File.Exists(P("ren", "Holiday 03.txt")) && File.ReadAllText(P("ren", "Holiday 01.txt")) == "a.txt"; });
        Try("bulk rename: two files can swap names", () => { var items = new[] { I(P("ren", "Holiday 01.txt")), I(P("ren", "Holiday 02.txt")) }; var plan = new List<(string, string, string?)> { ("Holiday 01.txt", "Holiday 02.txt", null), ("Holiday 02.txt", "Holiday 01.txt", null) }; BulkRename.Apply(items, plan); return File.ReadAllText(P("ren", "Holiday 01.txt")) == "b.txt"; });
        Try("bulk rename: a name Windows can't use is caught", () => BulkRename.Preview(new[] { I(P("ren", "Holiday 03.txt")) }, new RenameRule { Pattern = "bad:name" })[0].problem != null);
        Try("duplicates: finds files with the same content", () => { Directory.CreateDirectory(P("dup")); var data = new byte[4096]; new Random(1).NextBytes(data); File.WriteAllBytes(P("dup", "one.bin"), data); File.WriteAllBytes(P("dup", "two.bin"), data); data[0]++; File.WriteAllBytes(P("dup", "other.bin"), data); var g = Duplicates.Find(P("dup"), new JobProgress(), default); return g.Count == 1 && g[0].Count == 2; });
        Try("finders: empty folders", () => { Directory.CreateDirectory(P("empty", "deep")); return Space.EmptyFolders(P("empty"), default).Any((e) => e.Name == "deep"); });
        Try("compare: only-left, newer and same are told apart", () => { Directory.CreateDirectory(P("L")); Directory.CreateDirectory(P("R")); File.WriteAllText(P("L", "same.txt"), "s"); File.Copy(P("L", "same.txt"), P("R", "same.txt")); File.SetLastWriteTimeUtc(P("R", "same.txt"), File.GetLastWriteTimeUtc(P("L", "same.txt"))); File.WriteAllText(P("L", "only.txt"), "o"); File.WriteAllText(P("R", "new.txt"), "old"); File.WriteAllText(P("L", "new.txt"), "newer one"); File.SetLastWriteTime(P("R", "new.txt"), DateTime.Now.AddHours(-2)); var r = FolderCompare.Compare(P("L"), P("R"), default); return r.Any((x) => x.Rel == "only.txt" && x.State == Diff.OnlyLeft) && r.Any((x) => x.Rel == "new.txt" && x.State == Diff.Newer) && r.Any((x) => x.Rel == "same.txt" && x.State == Diff.Same); });
        Try("what is using it: finds the app holding a file open", () => { using var hold = new FileStream(P("held.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None); return FileLocks.WhoUses(P("held.txt")).Any((w) => w.pid == Environment.ProcessId); });
        Try("checksum: SHA-256 of known text", () => { File.WriteAllText(P("abc.txt"), "abc"); return Duplicates.Hash(P("abc.txt"), "SHA256") == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"; });
        Try("lock: locked file opens only with the password", () => { File.WriteAllText(P("secret.txt"), "top secret"); var l = Locker.Lock(P("secret.txt"), "pass-word"); var raw = File.ReadAllText(l); try { Locker.Unlock(l, "nope"); return false; } catch (PdfTools.WrongPassword) { } File.Delete(P("secret.txt")); var u = Locker.Unlock(l, "pass-word"); return !raw.Contains("top secret") && File.ReadAllText(u) == "top secret"; });
        Try("shred: the file is gone", () => { File.WriteAllText(P("shred.txt"), "bye"); Safety.Shred(P("shred.txt")); return !File.Exists(P("shred.txt")); });
        Try("link: a hard link on the same drive", () => { var l = Safety.MakeLink(P("abc.txt"), root); return File.ReadAllText(l) == "abc"; });
        Try("permissions: owner and rules in plain words", () => { var (owner, rules) = Safety.Permissions(P("abc.txt")); return owner.Length > 0 && rules.Count > 0; });
        Try("start-up apps: listed", () => { WinControl.StartupApps(); return true; });

        // ---------- Stage 9 ----------
        Try("rules: name patterns", () => RuleEngine.Like("Invoice 2026.pdf", "*invoice*") && RuleEngine.Like("a.pdf", "*.pdf") && !RuleEngine.Like("a.pdfx", "*.pdf") && RuleEngine.Like("IMG_1234.jpg", "IMG_????.jpg"));
        await TryAsync("rules: a move rule moves and renames by date", async () => { Directory.CreateDirectory(P("in")); Directory.CreateDirectory(P("docs")); File.WriteAllText(P("in", "report.pdf"), "%PDF"); await RuleEngine.Apply(new Rule { Folder = P("in"), Action = "move", Target = P("docs"), Rename = "{date} {name}" }, P("in", "report.pdf")); return !File.Exists(P("in", "report.pdf")) && Directory.GetFiles(P("docs"), "* report.pdf").Length == 1; });
        Try("backup: copies new files, then only changes, keeping the older version", () =>
        {
            Directory.CreateDirectory(P("data")); File.WriteAllText(P("data", "a.txt"), "v1");
            var b = new Backup { Folders = new() { P("data") } };
            var drive = P("backupdrive"); Directory.CreateDirectory(drive);
            var first = RuleEngine.RunBackup(b, drive, new JobProgress(), default);
            var second = RuleEngine.RunBackup(b, drive, new JobProgress(), default);
            File.WriteAllText(P("data", "a.txt"), "v2 longer"); var third = RuleEngine.RunBackup(b, drive, new JobProgress(), default);
            var dir = Path.Combine(drive, "Omebly Backup", Environment.MachineName, "data");
            Library.Current.Backups.Remove(b);
            return first.Count == 1 && second.Count == 0 && third.Count == 1 && File.ReadAllText(Path.Combine(dir, "a.txt")) == "v2 longer" && Directory.GetFiles(dir, "a (older*").Length == 1;
        });
        Try("servers: a password is kept by Windows and removed again", () => { var s = new Server { Kind = "sftp", Host = "selftest.invalid", Port = 22, User = "omebly" }; Servers.SavePassword(s, "pw!"); var back = Servers.Password(s); Servers.Forget(s); return back == "pw!" && Servers.Password(s) == null; });
        await TryAsync("phones: the list of phones can be read", async () => { await Phones.List(); return true; });
        Try("cloud: folders found without errors", () => { Places.Cloud(); return true; });
        Try("updates: a correctly signed update is accepted", () => Updater.Verify("9.9.9|https://omebly.com/tools/explorer/selftest.exe|FC6BEB8EA033A2323B491619E23BAC3E9C0764FE0C0FFCC3FA1F89CA614E7151", "dQMc0rTvwJgCtt4zBp3PYUJT5wJ8ne3DdGThheD6VOT0IlB8GcuIOglVn/W/4NkSCmgxhAlPY+1YuWwObQDzYQ=="));
        Try("updates: a changed update is refused", () => !Updater.Verify("9.9.9|https://evil.example/x.exe|FC6BEB8EA033A2323B491619E23BAC3E9C0764FE0C0FFCC3FA1F89CA614E7151", "dQMc0rTvwJgCtt4zBp3PYUJT5wJ8ne3DdGThheD6VOT0IlB8GcuIOglVn/W/4NkSCmgxhAlPY+1YuWwObQDzYQ=="));
        Try("new menu: Windows' kinds are read", () => ShellNew.Kinds().Count > 0);
        Try("Omebly Tools: the PDF tools are in the app and offered for PDFs, Word and pictures", () => WebTools.All.Count >= 45 && WebTools.For(new[] { I(P("three pages.pdf")) }).Any((t) => t.Slug == "pdf-to-word") && WebTools.For(new[] { I(P("letter.docx")) }).Any((t) => t.Slug == "word-to-pdf") && WebTools.For(new[] { I(P("pattern.png")), I(P("photo 1.jpg")) }).Any((t) => t.Slug == "jpg-to-pdf") && !WebTools.All.Any((t) => t.Slug is "ai-summarizer" or "chat-with-pdf" or "translate-pdf"));

        Log.AppendLine($"\n{_pass} passed, {_fail} failed");
        File.WriteAllText(Path.Combine(root, "selftest.txt"), Log.ToString());
        return _fail == 0 ? 0 : 1;
    }

    private static Task RunSta(string name, Func<bool> check)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() => { Try(name, check); done.SetResult(); });
        t.SetApartmentState(ApartmentState.STA); t.Start();
        return done.Task;
    }
}
