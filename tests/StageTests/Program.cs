// Stages 5-9 on screen, in the real app: zip and unzip, archives as folders, convert, PDF tools, search inside
// files; colours, tags, notes, favourites, the shelf, templates, tidy-up, Ctrl+K, filters, split view, instant
// search; Folder options' Windows switches (not turned on here); the Tools page and its tools; rules and backup.
// Works inside D:\oxtest\stages with its own settings folder. Folder takeover is never switched on by this test.
// Run: dotnet run --project tests/StageTests -- "<path to OmeblyExplorer.exe>"
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

Native.SetProcessDPIAware();
var exe = args.Length > 0 ? args[0] : @"D:\my latest project\Omebly Explorer\publish\test-x64\OmeblyExplorer.exe";
const string Root = @"D:\oxtest\stages";
var settingsDir = Path.Combine(Path.GetTempPath(), "omebly-explorer-stage-test-settings");
int pass = 0, fail = 0;
var shots = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "shots"); Directory.CreateDirectory(shots);
void Ok(string name, bool cond, string info = "") { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info == "" ? "" : " - " + info)}"); if (cond) pass++; else fail++; }
bool Until(Func<bool> cond, int ms = 6000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { try { if (cond()) return true; } catch { } Thread.Sleep(150); } try { return cond(); } catch { return false; } }
string F(params string[] p) => Path.Combine(new[] { Root }.Concat(p).ToArray());

void Clean(string dir) { if (!Directory.Exists(dir)) return; foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) e.Attributes = e is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal; Directory.Delete(dir, true); }
Clean(Root);
Directory.CreateDirectory(F("Project", "inner"));
File.WriteAllText(F("Project", "readme.txt"), "zebra inside a file");
File.WriteAllText(F("Project", "inner", "data.csv"), "a,b\n1,2");
File.Copy(@"D:\oxtest\viewers\pattern.png", F("pattern.png"));
File.Copy(@"D:\oxtest\viewers\three pages.pdf", F("doc.pdf"));
File.Copy(@"D:\oxtest\viewers\photo 1.jpg", F("holiday.jpg"));
foreach (var n in new[] { "one.txt", "two.txt", "three.txt" }) File.WriteAllText(F(n), n);
File.WriteAllText(F("same1.bin"), new string('x', 5000)); File.WriteAllText(F("same2.bin"), new string('x', 5000));
Directory.CreateDirectory(F("Messy")); File.Copy(F("pattern.png"), F("Messy", "pic.png")); File.WriteAllText(F("Messy", "letter.txt"), "t"); File.Copy(F("doc.pdf"), F("Messy", "paper.pdf"));
Directory.CreateDirectory(F("Watched")); Directory.CreateDirectory(F("Sorted"));
Directory.CreateDirectory(F("Empty", "nothing"));
if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);

var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
psi.Environment["OMEBLY_SETTINGS_DIR"] = settingsDir;
using var app = FlaUI.Core.Application.Launch(psi);
using var ui = new UIA3Automation();
var win = app.GetMainWindow(ui, TimeSpan.FromSeconds(20))!;
win.SetForeground();
AutomationElement? Id(string id) => win.FindFirstDescendant((c) => c.ByAutomationId(id));
AutomationElement? Named(string name) => win.FindFirstDescendant((c) => c.ByName(name));
AutomationElement? FileList() => Id("FileList") is { IsOffscreen: false } l ? l : Id("FileTiles");
List<string> Items() => FileList()?.FindAllChildren().Select((e) => e.Properties.Name.ValueOrDefault ?? "").ToList() ?? new();
AutomationElement? Item(string name) => FileList()?.FindFirstChild((c) => c.ByName(name));
void Keys(params VirtualKeyShort[] k) { win.SetForeground(); Keyboard.TypeSimultaneously(k); Thread.Sleep(300); }
void Type(string text) { Keyboard.Type(text); Thread.Sleep(150); }
void Shot(string name) { try { FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(shots, name + ".png")); } catch { } }
void Go(string path) { Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L); Type(path); Keys(VirtualKeyShort.ENTER); Thread.Sleep(800); }
void Select(params string[] names) { Item(names[0])?.Click(); for (int i = 1; i < names.Length; i++) { Keyboard.Press(VirtualKeyShort.CONTROL); Item(names[i])?.Click(); Keyboard.Release(VirtualKeyShort.CONTROL); } Thread.Sleep(250); }
AutomationElement? MenuItem(string name) => ui.GetDesktop().FindAllDescendants((c) => c.ByControlType(ControlType.MenuItem)).FirstOrDefault((e) => !e.IsOffscreen && (e.Properties.Name.ValueOrDefault ?? "") == name);
AutomationElement? MenuStarts(string start) => ui.GetDesktop().FindAllDescendants((c) => c.ByControlType(ControlType.MenuItem)).FirstOrDefault((e) => !e.IsOffscreen && (e.Properties.Name.ValueOrDefault ?? "").StartsWith(start));
void Invoke(AutomationElement el) { if (el.Patterns.ExpandCollapse.IsSupported && el.Patterns.Invoke.IsSupported == false) el.Patterns.ExpandCollapse.Pattern.Expand(); else if (el.Patterns.Toggle.IsSupported) el.Patterns.Toggle.Pattern.Toggle(); else if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); else el.Click(); Thread.Sleep(600); }
// Right-click an item, then go through the menu path ("Compress to", "ZIP file").
void Menu(string item, params string[] path)
{
    Keys(VirtualKeyShort.ESCAPE);
    Until(() => Item(item) != null, 4000);
    Item(item)!.RightClick(); Thread.Sleep(600);
    for (int i = 0; i < path.Length; i++)
    {
        var step = path[i];
        if (!Until(() => (step.EndsWith("…*") ? MenuStarts(step[..^2]) : MenuItem(step)) != null, 4000)) { Shot("missing-" + step.Replace("…", "").Replace("*", "")); throw new Exception("menu item not shown: " + step); }
        var mi = step.EndsWith("…*") ? MenuStarts(step[..^2])! : MenuItem(step)!;
        if (i < path.Length - 1 && mi.Patterns.ExpandCollapse.IsSupported) { mi.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(600); }
        else Invoke(mi);
    }
}
void Click(string name) { if (!Until(() => Named(name) != null, 5000)) { Shot("missing-" + name); throw new Exception("not found: " + name); } var el = Named(name)!; if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); else el.Click(); Thread.Sleep(600); }
string Status() => Id("StatusLeft")?.Properties.Name.ValueOrDefault ?? "";
void DialogOk(string button) { var b = win.FindAllDescendants((c) => c.ByName(button).And(c.ByControlType(ControlType.Button))).LastOrDefault(); b?.Patterns.Invoke.PatternOrDefault?.Invoke(); Thread.Sleep(700); }

try
{
    Go(Root);
    Ok("folder opens", Until(() => Items().Contains("Project")), string.Join(", ", Items()));

    // ================= Stage 5 =================
    Menu("Project", "Compress to", "ZIP file");
    Ok("zip: Compress to ZIP makes Project.zip", Until(() => File.Exists(F("Project.zip")), 10000));
    Until(() => Item("Project.zip") != null);
    Item("Project.zip")!.DoubleClick();
    Ok("archive: double-click opens it like a folder", Until(() => Id("ArchiveList") != null && Id("ArchiveList")!.FindAllChildren().Any((e) => e.Properties.Name.ValueOrDefault == "Project"), 6000));
    Shot("archive");
    Click("Extract all");
    Ok("archive: Extract all makes a folder next to it", Until(() => File.Exists(F("Project (2)", "Project", "readme.txt")) || Directory.GetDirectories(Root).Any((d) => File.Exists(Path.Combine(d, "Project", "readme.txt")) && d != F("Project")), 10000), string.Join(", ", Directory.GetDirectories(Root).Select(Path.GetFileName)));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("Project.zip") != null, 5000);
    Menu("Project.zip", "Extract here");
    // The ZIP holds "Project", which is already here: nothing is overwritten, the files come in as "(2)".
    Ok("archive: Extract here (never overwrites)", Until(() => File.Exists(F("Project", "readme (2).txt")) && File.ReadAllText(F("Project", "readme.txt")) == "zebra inside a file", 10000), string.Join(", ", Directory.GetFiles(F("Project")).Select(Path.GetFileName)));
    Menu("pattern.png", "Convert to", "JPG picture");
    Ok("convert: PNG to JPG", Until(() => File.Exists(F("pattern.jpg")), 10000));
    Menu("pattern.png", "Convert to", "PDF");
    Ok("convert: picture to PDF", Until(() => File.Exists(F("pattern.pdf")), 10000));
    // The Omebly Tools PDF tools: a tool window opens with the file already in it.
    Menu("doc.pdf", "PDF tools", "Organise", "Rotate PDF");
    Window? tw = null;
    Ok("pdf tools: Rotate PDF opens its window", Until(() => (tw = app.GetAllTopLevelWindows(ui).FirstOrDefault((w) => w.Title.StartsWith("Rotate PDF"))) != null, 10000));
    if (tw != null)
    {
        Ok("pdf tools: the file is already loaded in the tool", Until(() => tw.FindAllDescendants().Any((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains("doc.pdf")), 20000));
        Thread.Sleep(1500); Shot("tool-rotate");
        tw.FindAllDescendants((c) => c.ByControlType(ControlType.Button)).FirstOrDefault((b) => (b.Properties.Name.ValueOrDefault ?? "").Contains("Right") && !b.IsOffscreen)?.Patterns.Invoke.PatternOrDefault?.Invoke(); Thread.Sleep(500);
        var run = tw.FindAllDescendants((c) => c.ByControlType(ControlType.Button)).FirstOrDefault((b) => (b.Properties.Name.ValueOrDefault ?? "").StartsWith("Save rotated", StringComparison.OrdinalIgnoreCase) && !b.IsOffscreen);
        Console.WriteLine("     run button: " + (run?.Properties.Name.ValueOrDefault ?? "none") + " | buttons: " + string.Join(" / ", tw.FindAllDescendants((c) => c.ByControlType(ControlType.Button)).Select((b) => b.Properties.Name.ValueOrDefault).Where((n) => !string.IsNullOrEmpty(n)).Take(25)));
        run?.Patterns.Invoke.PatternOrDefault?.Invoke(); Thread.Sleep(3000); Shot("tool-rotate-done");
        var dl = tw.FindAllDescendants((c) => c.ByControlType(ControlType.Button).Or(c.ByControlType(ControlType.Hyperlink))).FirstOrDefault((b) => (b.Properties.Name.ValueOrDefault ?? "").StartsWith("Download", StringComparison.OrdinalIgnoreCase) && !b.IsOffscreen);
        dl?.Patterns.Invoke.PatternOrDefault?.Invoke();
        Ok("pdf tools: the result is saved next to the original", Until(() => Directory.GetFiles(Root, "*.pdf").Length > 2, 15000), string.Join(", ", Directory.GetFiles(Root, "*.pdf").Select(Path.GetFileName)));
        tw.Close(); Thread.Sleep(800);
    }
    win.SetForeground();
    // Search inside files: Enter in the search box.
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F); Type("zebra"); Keys(VirtualKeyShort.ENTER);
    Ok("search inside files: finds the file with the word in it", Until(() => Items().Contains("readme.txt"), 10000), string.Join(", ", Items()));
    Shot("search-inside");
    Go(Root);

    // ================= Stage 6 =================
    Menu("Project", "Folder colour", "Pink");
    Ok("folder colour: Windows' desktop.ini is written", Until(() => File.Exists(F("Project", "desktop.ini")), 5000));
    Menu("one.txt", "Tag", "Red");
    Ok("tags: the sidebar shows the Red tag", Until(() => Named("Red") != null, 4000));
    Click("Red");
    Ok("tags: the tag lists the file", Until(() => Items().Contains("one.txt"), 5000));
    Go(Root);
    Menu("two.txt", "Add a note…");
    Until(() => win.FindFirstDescendant((c) => c.ByName("Note").And(c.ByControlType(ControlType.Edit))) != null);
    win.FindFirstDescendant((c) => c.ByName("Note").And(c.ByControlType(ControlType.Edit)))!.Click(); Type("call the client");
    DialogOk("Save");
    Ok("notes: saved", Until(() => File.ReadAllText(Path.Combine(settingsDir, "library.json")).Contains("call the client"), 4000));
    Menu("three.txt", "Add to favourites");
    Ok("favourites: in the sidebar", Until(() => Named("Favourites") != null && Named("three.txt") != null, 4000));
    Menu("holiday.jpg", "Add to shelf");
    Ok("shelf: in the sidebar", Until(() => Named("Shelf") != null, 4000));
    Click("Shelf");
    Ok("shelf: lists what was added", Until(() => Items().Contains("holiday.jpg"), 5000));
    Go(Root);
    // Template (right-click the empty space).
    var list = FileList()!.BoundingRectangle; Mouse.MoveTo(list.X + list.Width / 2, list.Bottom - 30); Mouse.Click(MouseButton.Right); Thread.Sleep(700);
    var tpl = MenuItem("New from template"); if (tpl != null) { tpl.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(600); }
    MenuStarts("Client project")?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(900); Keys(VirtualKeyShort.ESCAPE);
    Ok("templates: a project folder with its folders inside", Until(() => Directory.Exists(F("Client project", "Invoices")) && Directory.Exists(F("Client project", "Final")), 5000));
    // Tidy up the messy folder.
    Item("Messy")!.DoubleClick(); Until(() => Named("Path Messy") != null);
    list = FileList()!.BoundingRectangle; Mouse.MoveTo(list.X + list.Width / 2, list.Bottom - 30); Mouse.Click(MouseButton.Right); Thread.Sleep(700);
    MenuStarts("Tidy up this folder")?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(900);
    DialogOk("Tidy up");
    Ok("tidy-up: files go into Pictures and Documents folders", Until(() => File.Exists(F("Messy", "Pictures", "pic.png")) && File.Exists(F("Messy", "Documents", "paper.pdf")), 10000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("Messy") != null);
    // Command bar.
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K); Thread.Sleep(700);
    Type("new tab"); Keys(VirtualKeyShort.ENTER);
    Ok("command bar: Ctrl+K, type, Enter", Until(() => win.FindAllDescendants((c) => c.ByAutomationId("Tab")).Length == 2, 4000));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
    // Filters.
    Id("ViewMore")!.Click(); Thread.Sleep(600); MenuItem("Show only")!.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(500); Invoke(MenuItem("Pictures")!); Keys(VirtualKeyShort.ESCAPE);
    Ok("filters: Show only pictures", Until(() => Items().Contains("holiday.jpg") && !Items().Contains("one.txt"), 4000), string.Join(", ", Items()));
    Id("ViewMore")!.Click(); Thread.Sleep(600); MenuItem("Show only")!.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(500); Invoke(MenuItem("Everything")!); Keys(VirtualKeyShort.ESCAPE);
    // Instant search from Home.
    Click("Home"); Thread.Sleep(600);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F); Type("holiday.jpg"); Keys(VirtualKeyShort.ENTER);
    Ok("instant search: the whole PC by name", Until(() => Items().Contains("holiday.jpg"), 240000), string.Join(", ", Items().Take(5)));
    Shot("instant-search");
    Go(Root);

    // ================= Stage 7 =================
    Id("MoreButton")!.Click(); Thread.Sleep(500); MenuItem("Folder options…")!.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(800);
    Ok("folder options: the switch for folders and Win+E is there (left off)", Named("Open folders and Win+E in Omebly Explorer (instead of File Explorer)") != null);
    Ok("folder options: the Jump-to bar switch is there", Named("Show a Jump-to bar on other apps' Open and Save windows") != null);
    Shot("folder-options-windows");
    DialogOk("Cancel");

    // ================= Stage 8 =================
    Id("MoreButton")!.Click(); Thread.Sleep(500); MenuItem("Tools")!.Patterns.Invoke.Pattern.Invoke();
    Ok("tools: the Tools page opens", Until(() => Named("Find duplicates") != null && Named("Disk space map") != null, 5000));
    Shot("tools");
    Click("Find duplicates");
    Ok("duplicates: finds the two same files", Until(() => win.FindAllDescendants((c) => c.ByControlType(ControlType.CheckBox)).Count((b) => (b.Properties.Name.ValueOrDefault ?? "").Contains("same")) == 2, 15000));
    Shot("duplicates");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Thread.Sleep(600);
    Click("Disk space map");
    Ok("disk map: blocks for the folders", Until(() => Named("Project") != null || Named("Messy") != null, 15000));
    Shot("disk-map");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Thread.Sleep(600);
    Click("Empty folders");
    Ok("finders: empty folders", Until(() => Items().Contains("nothing"), 10000), string.Join(", ", Items()));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Thread.Sleep(600);
    Click("Start-up apps");
    Ok("start-up apps: the page opens", Until(() => Named("Start-up apps") != null, 5000));
    Shot("startup");
    Go(Root);
    // Bulk rename.
    Select("one.txt", "two.txt", "three.txt"); Keys(VirtualKeyShort.F2); Thread.Sleep(900);
    var newName = win.FindAllDescendants((c) => c.ByControlType(ControlType.Edit)).FirstOrDefault((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains("New name"));
    newName?.Click(); Type("Note {n}"); Thread.Sleep(500);
    Shot("bulk-rename");
    DialogOk("Rename");
    Ok("bulk rename: three files renamed with numbers", Until(() => File.Exists(F("Note 01.txt")) && File.Exists(F("Note 03.txt")), 6000), string.Join(", ", Directory.GetFiles(Root, "*.txt").Select(Path.GetFileName)));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("bulk rename: Ctrl+Z puts the names back", Until(() => File.Exists(F("one.txt")) && File.Exists(F("three.txt")), 6000));
    // Checksum.
    Menu("holiday.jpg", "More tools", "Checksum (SHA-256, SHA-1, MD5)…");
    Ok("checksum: shows the SHA-256", Until(() => win.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => (t.Properties.Name.ValueOrDefault ?? "").StartsWith("SHA-256")), 8000));
    Keys(VirtualKeyShort.ESCAPE); // (not "Close": that name is also the window's own close button)
    // Lock and unlock.
    Menu("one.txt", "More tools", "Lock with a password…");
    Until(() => win.FindFirstDescendant((c) => c.ByName("Password").And(c.ByControlType(ControlType.Edit))) != null);
    win.FindFirstDescendant((c) => c.ByName("Password").And(c.ByControlType(ControlType.Edit)))!.Click(); Type("lock-1234");
    win.FindFirstDescendant((c) => c.ByName("Password again").And(c.ByControlType(ControlType.Edit)))!.Click(); Type("lock-1234");
    DialogOk("OK");
    Ok("lock: a locked copy is made", Until(() => File.Exists(F("one.txt.omeblylock")) || Directory.GetFiles(Root, "one*.omeblylock").Length == 1, 15000));
    // Shred.
    File.WriteAllText(F("shred me.txt"), "bye"); Until(() => Item("shred me.txt") != null);
    Menu("shred me.txt", "More tools", "Shred (delete for good, can't be recovered)…");
    DialogOk("Shred"); DialogOk("Yes, shred");
    Ok("shred: asks twice, then the file is gone", Until(() => !File.Exists(F("shred me.txt")), 8000));

    // ================= Stage 9 =================
    Id("MoreButton")!.Click(); Thread.Sleep(500); MenuItem("Tools")!.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(700);
    Click("Rules"); Thread.Sleep(600);
    Click("Add a rule…"); Thread.Sleep(900);
    var boxes = win.FindAllDescendants((c) => c.ByControlType(ControlType.Edit)).ToList();
    var watch = boxes.FirstOrDefault((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains("Watch this folder"));
    var to = boxes.FirstOrDefault((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains("To this folder"));
    if (watch != null && to != null)
    {
        watch.Patterns.Value.Pattern.SetValue(F("Watched")); to.Patterns.Value.Pattern.SetValue(F("Sorted"));
        DialogOk("Add");
        Ok("rules: the rule is listed", Until(() => win.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => (t.Properties.Name.ValueOrDefault ?? "").StartsWith("When a file arrives in Watched")), 4000));
        File.WriteAllText(F("Watched", "incoming.txt"), "new");
        Ok("rules: a file that arrives is moved by itself", Until(() => File.Exists(F("Sorted", "incoming.txt")) && !File.Exists(F("Watched", "incoming.txt")), 20000));
    }
    else Ok("rules: the Add a rule window has its boxes", false, string.Join(" | ", boxes.Select((b) => b.Properties.Name.ValueOrDefault)));
    Shot("rules");

    Ok("app: still responds", Process.GetProcessById(app.ProcessId).Responding);
    var crash = Path.Combine(settingsDir, "crash.log");
    Ok("app: nothing went wrong inside (no crash log)", !File.Exists(crash), File.Exists(crash) ? File.ReadAllText(crash)[..Math.Min(2000, File.ReadAllText(crash).Length)] : "");
}
catch (Exception e) { Console.WriteLine("ERROR " + e); fail++; Shot("error"); }
finally { try { app.Close(); Thread.Sleep(800); if (!app.HasExited) app.Kill(); } catch { } }
Console.WriteLine($"\n{pass} passed, {fail} failed (pictures in {shots})");
return fail == 0 ? 0 : 1;

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
