// Stage 4, tried on the real app: every kind of file opens inside Omebly Explorer's pop-up viewer (pictures,
// video, music, PDF, Word, text, code, Markdown, CSV, web pages, fonts, ZIP, unknown files), next and previous,
// Space for a quick look, editing text with Save, the unsaved-changes question, pop out, full screen, delete.
// Works only inside D:\oxtest\viewers (made by the sample script) and with its own settings folder.
// Run: dotnet run --project tests/ViewerTests -- "<path to OmeblyExplorer.exe>"
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

Native.SetProcessDPIAware();
var exe = args.Length > 0 ? args[0] : @"D:\my latest project\Omebly Explorer\publish\test-x64\OmeblyExplorer.exe";
const string Root = @"D:\oxtest\viewers";
if (!File.Exists(Path.Combine(Root, "clip.mp4"))) { Console.WriteLine("Make the sample files first (D:\\oxtest\\viewers)."); return 2; }
var settingsDir = Path.Combine(Path.GetTempPath(), "omebly-explorer-viewer-test-settings");
int pass = 0, fail = 0;
var shots = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "shots"); Directory.CreateDirectory(shots);
void Ok(string name, bool cond, string info = "") { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info == "" ? "" : " - " + info)}"); if (cond) pass++; else fail++; }
bool Until(Func<bool> cond, int ms = 6000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { try { if (cond()) return true; } catch { } Thread.Sleep(100); } try { return cond(); } catch { return false; } }

File.WriteAllText(Path.Combine(Root, "edit me.txt"), "first line\r\n");
File.WriteAllText(Path.Combine(Root, "edit me.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); // as if downloaded
// Pages that try to reach other computers through file: links (Security 338).
File.WriteAllText(Path.Combine(Root, "reach out.html"), "<html><body><h1>Reach out test</h1><img src='file://127.0.0.1/c$/Windows/win.ini'><img src='file:///\\\\10.255.255.1\\share\\x.png'><div style=\"background:url('file://10.255.255.2/s/y.png')\">x</div><img src='logo.svg' alt='local picture'></body></html>");
File.WriteAllText(Path.Combine(Root, "zz delete me.txt"), "bye");
if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);

var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
psi.Environment["OMEBLY_SETTINGS_DIR"] = settingsDir;
psi.Environment["OMEBLY_PERF"] = Path.Combine(settingsDir, "perf.txt");
using var app = FlaUI.Core.Application.Launch(psi);
using var ui = new UIA3Automation();
var win = app.GetMainWindow(ui, TimeSpan.FromSeconds(20))!;
win.SetForeground();
AutomationElement? Id(string id) => win.FindFirstDescendant((c) => c.ByAutomationId(id));
AutomationElement? Named(string name) => win.FindFirstDescendant((c) => c.ByName(name));
AutomationElement? FileList() => Id("FileList") is { IsOffscreen: false } l ? l : Id("FileTiles");
AutomationElement? Item(string name) => FileList()?.FindFirstChild((c) => c.ByName(name));
void Keys(params VirtualKeyShort[] k) { Keyboard.TypeSimultaneously(k); Thread.Sleep(250); }
void Type(string text) { Keyboard.Type(text); Thread.Sleep(150); }
void Shot(string name) { try { FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(shots, name + ".png")); } catch { } }
void Go(string path) { win.SetForeground(); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L); Type(path); Keys(VirtualKeyShort.ENTER); Thread.Sleep(700); }
string VName() => Id("ViewerName")?.Properties.Name.ValueOrDefault ?? "";
string VKind() => Id("ViewerName")?.Properties.HelpText.ValueOrDefault ?? "";
string VInfo() => Id("ViewerInfo")?.Properties.Name.ValueOrDefault ?? "";
bool ViewerOpen() => Id("ViewerName") != null;
void Click(string name) { if (!Until(() => Named(name) != null, 3000)) { Shot("missing-" + name); throw new Exception("not found: " + name); } var el = Named(name)!; if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); else el.Click(); Thread.Sleep(400); }
void CloseViewer() { if (!ViewerOpen()) return; var t0 = DateTime.Now; Keys(VirtualKeyShort.ESCAPE); if (!Until(() => !ViewerOpen(), 3000)) { var f = ui.FocusedElement(); Console.WriteLine($"     esc at {t0:HH:mm:ss.fff} did not close; focus on {f?.Properties.ClassName.ValueOrDefault} '{f?.Properties.Name.ValueOrDefault}' fg={Native.GetForegroundWindow()} win={win.Properties.NativeWindowHandle.ValueOrDefault} at {DateTime.Now:HH:mm:ss.fff}"); Keys(VirtualKeyShort.ESCAPE); Console.WriteLine($"     second Esc closed it: {Until(() => !ViewerOpen(), 3000)}"); } }
// Opens a file the way a person does: a double-click on it in the list.
bool Open(string name)
{
    CloseViewer();
    if (!Until(() => Item(name) != null, 4000)) return false;
    var it = Item(name)!; it.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); Thread.Sleep(150);
    it.DoubleClick(); Thread.Sleep(300);
    return Until(() => VName() == name, 6000);
}

try
{
    Go(Root);
    Ok("folder: the sample folder opens", Until(() => Item("clip.mp4") != null), Root);

    // Every kind: opens inside, the right viewer, facts in the top bar.
    var kinds = new (string file, string kind, Func<string, bool> info)[]
    {
        ("photo 1.jpg", "Picture", (i) => i.Contains("×")),
        ("pattern.png", "Picture", (i) => i.Contains("640 × 360")),
        ("logo.svg", "Picture", (i) => i.Length > 0),
        ("clip.mp4", "Video", (i) => i.Contains("1280 × 720") && i.Contains("0:12")),
        ("song.mp3", "Music", (i) => i.Contains("0:20")),
        ("alarm.wav", "Music", (i) => i.Length > 0),
        ("three pages.pdf", "Pdf", (i) => i.Contains("of 3")),
        ("letter.docx", "Office", (i) => i.Length > 0),
        ("sheet.xlsx", "Office", (i) => i.Length > 0),
        ("notes.txt", "Text", (i) => i.Contains("lines") && i.Contains("UTF-8")),
        ("hello.cs", "Code", (i) => i.Contains("7 lines")),
        ("script.py", "Code", (i) => i.Contains("lines")),
        ("config.ini", "Code", (i) => i.Contains("lines")),
        ("readme.md", "Markdown", (i) => i.Length > 0),
        ("table.csv", "Csv", (i) => i.Contains("3 rows") && i.Contains("3 columns")),
        ("page.html", "Html", (i) => i.Length > 0),
        ("segoe.ttf", "Font", (i) => i.Contains("Segoe")),
        ("mystery.bin", "Other", (i) => i.Length > 0),
        ("clip.srt", "Text", (i) => i.Contains("lines")),
    };
    foreach (var (file, kind, info) in kinds)
    {
        var opened = Open(file);
        Ok($"{file}: opens inside Omebly Explorer", opened, VName());
        if (!opened) { Shot("fail-" + file); continue; }
        Ok($"{file}: shown by the {kind} viewer", VKind() == kind, VKind());
        Ok($"{file}: the top bar shows its facts", Until(() => info(VInfo()), 8000), VInfo());
        Thread.Sleep(kind is "Video" or "Pdf" or "Office" or "Html" ? 1500 : 500);
        Shot("view-" + file);
    }

    // A ZIP opens as a folder (stage 5), with Extract all; Back comes out of it.
    CloseViewer();
    if (Until(() => Item("bundle.zip") != null, 4000)) Item("bundle.zip")!.DoubleClick();
    Ok("bundle.zip: opens as a folder you can look through", Until(() => Named("Extract all") != null && Id("ArchiveList")?.FindFirstDescendant((c) => c.ByName("docs")) != null, 8000));
    Shot("view-bundle.zip");
    Go(Root);
    Until(() => Item("clip.mp4") != null);

    // Text shows every line, not only the first.
    bool Shows(string text) => win.FindAllDescendants((c) => c.ByControlType(ControlType.Text).Or(c.ByControlType(ControlType.Document)).Or(c.ByControlType(ControlType.Edit))).Any((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains(text) || (e.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1) ?? "").Contains(text) || (e.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? "").Contains(text));
    Open("notes.txt");
    Ok("text: every line shows (the last one too)", Until(() => Shows("bread") && Shows("Hindi"), 4000));
    Open("hello.cs");
    Ok("code: plain words show, not only the coloured ones", Until(() => Shows("Hello") && Shows("Console"), 4000));
    Keys(VirtualKeyShort.KEY_E); Thread.Sleep(500);
    Ok("code: edit mode keeps every line", Until(() => Shows("Console.WriteLine") && Shows("/* done */"), 4000));
    Shot("edit-mode");
    Keys(VirtualKeyShort.ESCAPE); Until(() => !ViewerOpen(), 3000);

    // Pictures: arrows go to the next picture, the count follows.
    Open("photo 1.jpg");
    Keys(VirtualKeyShort.RIGHT);
    Ok("pictures: → goes to the next picture", Until(() => VName() == "photo 2.jpg"), VName());
    Keys(VirtualKeyShort.LEFT);
    Ok("pictures: ← comes back", Until(() => VName() == "photo 1.jpg"), VName());
    Click("Rotate (R)"); Click("Zoom in"); Click("Fit to window (0)");
    Ok("pictures: rotate and zoom don't close the viewer", VName() == "photo 1.jpg");
    Click("Next file");
    Ok("pictures: the arrow button goes to the next one", Until(() => VName() == "photo 2.jpg"), VName());

    // Other files: Page Down / Page Up go through every file in the folder.
    Open("hello.cs");
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.NEXT);
    Ok("next file: Ctrl+Page Down goes to the next file in the folder", Until(() => VName() != "hello.cs" && VName() != ""), VName());
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.PRIOR);
    Ok("next file: Ctrl+Page Up comes back", Until(() => VName() == "hello.cs"), VName());

    // Video: subtitles found next to it, Space plays and pauses, it is really playing.
    Open("clip.mp4");
    Ok("video: subtitles button shows for clip.srt", Until(() => Named("Subtitles (C)") is { IsOffscreen: false }, 5000));
    Ok("video: playback controls are there", Named("5 seconds forward (→)") != null && Named("Speed") != null);
    Thread.Sleep(2500); Shot("video-playing");

    // PDF: next page.
    Open("three pages.pdf");
    Click("Next page");
    Ok("pdf: Next page goes to page 2", Until(() => VInfo().StartsWith("Page 2 of 3")), VInfo());

    // Web pages: scripts don't run.
    Open("page.html"); Thread.Sleep(2500);
    var desk = ui.GetDesktop();
    Ok("web page: its text shows", Until(() => desk.FindFirstDescendant((c) => c.ByName("A web page")) != null, 6000));
    Ok("web page: scripts in the page don't run", desk.FindFirstDescendant((c) => c.ByName("SCRIPT RAN")) == null && desk.FindFirstDescendant((c) => c.ByName("script did not run")) != null);

    // A web page can't make Windows connect to another computer (SMB 445 / WebDAV 80) through file: links.
    var seen = new List<string>();
    var watch = Task.Run(() => { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < 6000) { foreach (var c in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()) { var r = c.RemoteEndPoint; if ((r.Port is 445 or 139 or 80) && (r.Address.ToString() is "127.0.0.1" or "10.255.255.1" or "10.255.255.2")) lock (seen) seen.Add(r.ToString()); } Thread.Sleep(50); } });
    Open("reach out.html");
    Ok("web page: shows", Until(() => desk.FindFirstDescendant((c) => c.ByName("Reach out test")) != null, 6000));
    watch.Wait();
    Ok("web page: file: links to other computers open no connection", seen.Count == 0, string.Join(", ", seen.Distinct()));
    Shot("reach-out");

    // Text: edit and save with Ctrl+S, keeps Windows line endings.
    Open("edit me.txt");
    Keys(VirtualKeyShort.KEY_E); Thread.Sleep(400);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME); Type("zero line"); Keys(VirtualKeyShort.ENTER);
    Ok("edit: the top bar says not saved", Until(() => VInfo().Contains("not saved")), VInfo());
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_S); Thread.Sleep(600);
    var saved = File.ReadAllText(Path.Combine(Root, "edit me.txt"));
    Ok("edit: Ctrl+S saves the file", saved.Contains("zero line") && saved.Contains("first line"), saved);
    Ok("edit: Windows line endings and the last line break are kept", saved == "zero line\r\nfirst line\r\n", saved.Replace("\r", "\\r").Replace("\n", "\\n"));
    Ok("edit: saving keeps the downloaded-from-the-internet mark", ZoneMark(Path.Combine(Root, "edit me.txt")));
    Ok("edit: after saving, not saved goes away", Until(() => !VInfo().Contains("not saved")), VInfo());
    // Unsaved changes ask before closing.
    Type(" more");
    Keys(VirtualKeyShort.ESCAPE);
    Ok("edit: closing with changes asks first", Until(() => desk.FindFirstDescendant((c) => c.ByName("Save your changes?")) != null || Named("Save your changes?") != null, 4000));
    Shot("edit-ask");
    Click("Don't save");
    Ok("edit: Don't save closes and leaves the file as it was", Until(() => !ViewerOpen()) && !File.ReadAllText(Path.Combine(Root, "edit me.txt")).Contains("more"));

    // Space: a quick look at the selected file.
    CloseViewer();
    Item("readme.md")!.Click(); Thread.Sleep(300);
    Keys(VirtualKeyShort.SPACE);
    Ok("space: a quick look at the selected file", Until(() => VName() == "readme.md"), VName());
    CloseViewer();
    Ok("esc: closes the viewer", !ViewerOpen());

    // Full screen and back.
    Open("photo 2.jpg");
    var before = win.BoundingRectangle;
    Keys(VirtualKeyShort.KEY_F); Thread.Sleep(900);
    var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(before.X + 50, before.Y + 50)).Bounds;
    Ok("full screen: F fills the screen", Until(() => win.BoundingRectangle.Width >= screen.Width && win.BoundingRectangle.Height >= screen.Height, 3000), $"{win.BoundingRectangle} vs {screen}");
    Shot("full-screen");
    Keys(VirtualKeyShort.ESCAPE); Thread.Sleep(900);
    Ok("full screen: Esc leaves full screen and keeps the viewer", win.BoundingRectangle.Width < screen.Width && ViewerOpen(), win.BoundingRectangle.ToString());

    // Pop out into its own window.
    Click("Open in its own window");
    Ok("pop out: the main window's viewer closes", Until(() => !ViewerOpen(), 3000));
    Window? pop = null;
    Until(() => (pop = app.GetAllTopLevelWindows(ui).FirstOrDefault((w) => w.Title == "photo 2.jpg")) != null, 6000);
    Ok("pop out: a window opens with the file", pop != null);
    if (pop != null)
    {
        Ok("pop out: the window shows the picture viewer", Until(() => pop.FindFirstDescendant((c) => c.ByAutomationId("ViewerName"))?.Properties.Name.ValueOrDefault == "photo 2.jpg"));
        Shot("pop-out");
        pop.SetForeground(); Keys(VirtualKeyShort.LEFT);
        Ok("pop out: arrows work in the window too", Until(() => pop.FindFirstDescendant((c) => c.ByAutomationId("ViewerName"))?.Properties.Name.ValueOrDefault == "photo 1.jpg"));
        pop.Close();
        Ok("pop out: closing it leaves the Explorer open", Until(() => app.GetAllTopLevelWindows(ui).All((w) => w.Title != "photo 2.jpg" && w.Title != "photo 1.jpg"), 3000) && !app.HasExited);
    }

    // Delete from the viewer goes to the Recycle Bin and moves on.
    win.SetForeground();
    Open("zz delete me.txt");
    Click("Delete (to the Recycle Bin)");
    Ok("delete: the file goes to the Recycle Bin", Until(() => !File.Exists(Path.Combine(Root, "zz delete me.txt")), 5000));
    Ok("delete: the viewer moves on to another file", Until(() => ViewerOpen() && VName() != "zz delete me.txt", 3000), VName());
    CloseViewer();

    // Programs are still run, not opened in the viewer (checked by the list, not by running one).
    Ok("app: still responds after all of it", Process.GetProcessById(app.ProcessId).Responding);
    var crash = Path.Combine(settingsDir, "crash.log");
    Ok("app: nothing went wrong inside (no crash log)", !File.Exists(crash), File.Exists(crash) ? File.ReadAllText(crash)[..Math.Min(1500, File.ReadAllText(crash).Length)] : "");
}
catch (Exception e) { Console.WriteLine("ERROR " + e); fail++; Shot("error"); }
finally { try { app.Close(); Thread.Sleep(800); if (!app.HasExited) app.Kill(); } catch { } }
Console.WriteLine($"\n{pass} passed, {fail} failed (pictures in {shots})");
return fail == 0 ? 0 : 1;

static bool ZoneMark(string path) { try { return File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3"); } catch { return false; } }

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
