// The whole of stage 2, tried on the real app: Home, the path bar, search, views, rename, copy, cut, paste,
// new folder, delete, live updates, the clipboard with other apps, tabs, pins, themes, drag and drop, Windows'
// own menu and a 100,000-file folder. Works only inside D:\oxtest\ui and with its own settings folder.
// Run: dotnet run --project tests/UiTests -- "<path to OmeblyExplorer.exe>"
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

Native.SetProcessDPIAware();
var exe = args.Length > 0 ? args[0] : @"D:\my latest project\Omebly Explorer\publish\test-x64\OmeblyExplorer.exe"; // its own copy, so a copy that is open on the desktop is never touched
const string Root = @"D:\oxtest\ui";
var settingsDir = Path.Combine(Path.GetTempPath(), "omebly-explorer-test-settings");
int pass = 0, fail = 0;
var shots = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "shots"); Directory.CreateDirectory(shots);
void Ok(string name, bool cond, string info = "") { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info == "" ? "" : " â€” " + info)}"); if (cond) pass++; else fail++; }
bool Until(Func<bool> cond, int ms = 6000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { try { if (cond()) return true; } catch { } Thread.Sleep(100); } try { return cond(); } catch { return false; } }

// A fresh test folder.
if (Directory.Exists(Root)) Directory.Delete(Root, true);
Directory.CreateDirectory(Path.Combine(Root, "Sub"));
Directory.CreateDirectory(Path.Combine(Root, "odd; name"));
File.WriteAllText(Path.Combine(Root, "a.txt"), "aaa");
File.WriteAllText(Path.Combine(Root, "b.txt"), "bbb");
File.WriteAllText(Path.Combine(Root, "forever.txt"), "zzz");
var hidden = Path.Combine(Root, "hidden.txt"); File.WriteAllText(hidden, "h"); File.SetAttributes(hidden, FileAttributes.Hidden);
var outside = Path.Combine(Path.GetTempPath(), "omebly-from-explorer.txt"); File.WriteAllText(outside, "from another app");
if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);

var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
psi.Environment["OMEBLY_SETTINGS_DIR"] = settingsDir;
psi.Environment["OMEBLY_PERF"] = Path.Combine(settingsDir, "perf.txt");
using var app = FlaUI.Core.Application.Launch(psi);
using var ui = new UIA3Automation();
var win = app.GetMainWindow(ui, TimeSpan.FromSeconds(20))!;
win.SetForeground();
AutomationElement? Id(string id) => win.FindFirstDescendant((c) => c.ByAutomationId(id));
AutomationElement? Named(string name)
{
    try { return win.FindFirstDescendant((c) => c.ByName(name)); }
    catch (System.Runtime.InteropServices.COMException)
    {
        // Busy or hung? Say which, so a slow moment is told apart from a stuck window.
        var p = Process.GetProcessById(app.ProcessId); p.Refresh();
        var sw = Stopwatch.StartNew(); while (!p.Responding && sw.ElapsedMilliseconds < 60000) { Thread.Sleep(250); p.Refresh(); }
        Console.WriteLine($"     looking for '{name}' timed out; the window answered again after {sw.ElapsedMilliseconds} ms (responding: {p.Responding}), {win.FindAllDescendants().Length} elements");
        return win.FindFirstDescendant((c) => c.ByName(name));
    }
}
string Status() => Id("StatusLeft")?.Name ?? "";
AutomationElement? FileList() => Id("FileList") is { IsOffscreen: false } l ? l : Id("FileTiles");
List<string> Items() => FileList()?.FindAllChildren().Select((e) => e.Name).ToList() ?? new();
AutomationElement? Item(string name) => FileList()?.FindFirstChild((c) => c.ByName(name));
void Keys(params VirtualKeyShort[] k) { win.SetForeground(); Keyboard.TypeSimultaneously(k); Thread.Sleep(250); }
void Type(string text) { Keyboard.Type(text); Thread.Sleep(150); }
void Shot(string name) { try { FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(shots, name + ".png")); } catch { } }
void Go(string path) { Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L); Type(path); Keys(VirtualKeyShort.ENTER); Thread.Sleep(600); }
AutomationElement? MenuItem(string name) => ui.GetDesktop().FindAllDescendants((c) => c.ByName(name).And(c.ByControlType(ControlType.MenuItem))).FirstOrDefault((e) => !e.IsOffscreen);
void More(string item) { Keys(VirtualKeyShort.ESCAPE); Keys(VirtualKeyShort.ESCAPE); if (!Until(() => Id("MoreButton") != null, 5000)) { Shot("no-more-button"); throw new Exception("More button not found"); } Id("MoreButton")!.Click(); if (!Until(() => MenuItem(item) != null, 3000)) { Shot("missing-" + item); throw new Exception("menu item not shown: " + item); } MenuItem(item)!.Click(); Thread.Sleep(500); }
void Sub(string item) { if (!Until(() => MenuItem(item) != null, 3000)) { Shot("missing-" + item); throw new Exception("menu item not shown: " + item); } MenuItem(item)!.Click(); Thread.Sleep(500); }
void ClickNamed(string name) { if (!Until(() => Named(name) != null, 3000)) { Shot("missing-" + name); throw new Exception("not found: " + name); } var el = Named(name)!; if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); else if (el.Patterns.ExpandCollapse.IsSupported) el.Patterns.ExpandCollapse.Pattern.Expand(); else el.Click(); Thread.Sleep(400); }
// Shift held down while Delete is pressed, as a person does it.
void ShiftDelete() { win.SetForeground(); Keyboard.Press(VirtualKeyShort.SHIFT); Thread.Sleep(80); Keyboard.Type(VirtualKeyShort.DELETE); Thread.Sleep(80); Keyboard.Release(VirtualKeyShort.SHIFT); Thread.Sleep(300); }
// A real mouse drag: button down, small relative moves (like a hand), button up.
void HandDrag(System.Drawing.Point from, System.Drawing.Point to)
{
    Native.SetCursorPos(from.X, from.Y); Thread.Sleep(150);
    Native.Mouse(0x0002, 0, 0); Thread.Sleep(250); // left down
    int steps = 30;
    for (int k = 1; k <= steps; k++) { Native.MoveAbs(from.X + (to.X - from.X) * k / steps, from.Y + (to.Y - from.Y) * k / steps); Thread.Sleep(25); }
    Native.SetCursorPos(to.X, to.Y); Native.Mouse(0x0001, 1, 0); Thread.Sleep(60); Native.Mouse(0x0001, -1, 0); // settle exactly on the target
    Thread.Sleep(400);
    Native.Mouse(0x0004, 0, 0); Thread.Sleep(300); // left up
}
void Select(string name) { var i = Item(name); i?.Click(); Thread.Sleep(200); }

try
{
    // ---------- Home ----------
    Ok("home: the window opens with the Omebly Explorer title", win.Title == "Omebly Explorer");
    Ok("home: Home shows drives, quick actions and recent files", Until(() => Named("Drives") != null && Named("Quick actions") != null && Named("Recent files") != null));
    Ok("home: the status bar counts the drives", Until(() => Status().Contains("drives")), Status());
    Shot("01-home");

    // ---------- The path bar ----------
    Go(Root);
    Ok("path bar: typing a path opens it", Until(() => Items().Contains("a.txt") && Items().Contains("Sub")), string.Join(", ", Items()));
    Ok("path bar: folders come first, then files by name", Items().Take(2).OrderBy((x) => x).SequenceEqual(new[] { "odd; name", "Sub" }) && Items()[2] == "a.txt", string.Join(", ", Items()));
    Ok("path bar: hidden files are not shown", !Items().Contains("hidden.txt"));
    Ok("path bar: the breadcrumb shows the folders", Until(() => Named("Path ui") != null && Named("Path oxtest") != null));
    Ok("status: the item count is right", Until(() => Status().StartsWith("5 items")), Status());
    Go(@"D:\oxtest\does-not-exist");
    Ok("path bar: a wrong path says so, kindly", Until(() => Named("Can't find this place") != null));
    Keys(VirtualKeyShort.ENTER);

    // ---------- Search ----------
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F); Type("b.t");
    Ok("search: typing narrows the folder to matches", Until(() => Items().SequenceEqual(new[] { "b.txt" })), string.Join(", ", Items()));
    Keys(VirtualKeyShort.ESCAPE);
    Ok("search: Escape shows everything again", Until(() => Items().Count == 5));

    // ---------- Views ----------
    Id("ViewTiles")!.Click(); Thread.Sleep(400);
    Ok("views: large icons", Until(() => Id("FileTiles") is { IsOffscreen: false }));
    Shot("02-tiles");
    Id("ViewGallery")!.Click(); Thread.Sleep(400); Shot("03-gallery");
    Id("ViewDetails")!.Click(); Thread.Sleep(400);
    Ok("views: back to details", Until(() => Id("FileList") is { IsOffscreen: false }));

    // ---------- Rename ----------
    Select("b.txt"); Keys(VirtualKeyShort.F2); Thread.Sleep(500);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Type("renamed.txt"); Keys(VirtualKeyShort.ENTER);
    Ok("rename: F2 renames the file on disk", Until(() => File.Exists(Path.Combine(Root, "renamed.txt")) && !File.Exists(Path.Combine(Root, "b.txt"))));
    Ok("rename: the list shows the new name", Until(() => Items().Contains("renamed.txt") && !Items().Contains("b.txt")), string.Join(", ", Items()));
    Select("renamed.txt"); Keys(VirtualKeyShort.F2); Thread.Sleep(500);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Type("bad:name"); Keys(VirtualKeyShort.ENTER); Thread.Sleep(400);
    Ok("rename: a name Windows can't use is explained, not tried", Until(() => Named("A name can't contain any of these: \\ / : * ? \" < > |") != null) && File.Exists(Path.Combine(Root, "renamed.txt")));
    Keys(VirtualKeyShort.ESCAPE);

    // ---------- Copy, paste, cut ----------
    Select("a.txt"); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    Ok("copy: Ctrl+C, Ctrl+V in the same folder makes a copy", Until(() => Directory.GetFiles(Root, "a*.txt").Length == 2, 8000), string.Join(", ", Directory.GetFiles(Root).Select(Path.GetFileName)));
    Ok("copy: the copy appears in the list by itself", Until(() => Items().Count((n) => n.StartsWith("a")) == 2), string.Join(", ", Items()));
    var copyName = Directory.GetFiles(Root, "a*.txt").Select(Path.GetFileName).First((n) => n != "a.txt")!;
    Select(copyName); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_X);
    Item("Sub")!.DoubleClick();
    Ok("open: double-clicking a folder opens it", Until(() => Named("Path Sub") != null));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    Ok("cut: Ctrl+X then Ctrl+V moves the file", Until(() => File.Exists(Path.Combine(Root, "Sub", copyName)) && !File.Exists(Path.Combine(Root, copyName)), 8000));

    // ---------- Back, forward, up ----------
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.LEFT);
    Ok("back: Alt+Left goes back", Until(() => Items().Contains("a.txt")), string.Join(", ", Items()));
    Shot("back");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.RIGHT);
    Ok("forward: Alt+Right goes forward", Until(() => Items().Contains(copyName)));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP);
    Ok("up: Alt+Up goes to the parent folder", Until(() => Items().Contains("a.txt")));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP);
    Ok("up: and again", Until(() => Items().Contains("ui")), string.Join(", ", Items()));
    Go(Root);

    // ---------- New folder ----------
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_N); Thread.Sleep(900);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Type("Made by test"); Keys(VirtualKeyShort.ENTER);
    Ok("new folder: Ctrl+Shift+N makes a folder and lets you name it", Until(() => Directory.Exists(Path.Combine(Root, "Made by test")), 8000));

    // ---------- Delete ----------
    Select("renamed.txt"); Keys(VirtualKeyShort.DELETE);
    Ok("delete: Delete sends the file to the Recycle Bin", Until(() => !File.Exists(Path.Combine(Root, "renamed.txt")), 8000));
    Select("forever.txt"); ShiftDelete();
    Ok("delete for good: Shift+Delete asks first", Until(() => Named("Delete for good?") != null));
    Keys(VirtualKeyShort.ESCAPE); Thread.Sleep(400);
    Ok("delete for good: Cancel keeps the file", File.Exists(Path.Combine(Root, "forever.txt")));
    Select("forever.txt"); ShiftDelete(); Until(() => Named("Delete for good?") != null);
    Named("Delete for good")?.Click();
    Ok("delete for good: confirming deletes it", Until(() => !File.Exists(Path.Combine(Root, "forever.txt")), 8000));

    // ---------- Live updates ----------
    File.WriteAllText(Path.Combine(Root, "made outside.txt"), "x");
    Ok("live: a file made by another app appears by itself", Until(() => Items().Contains("made outside.txt"), 5000));
    File.Delete(Path.Combine(Root, "made outside.txt"));
    Ok("live: and disappears when it's deleted elsewhere", Until(() => !Items().Contains("made outside.txt"), 5000));

    // ---------- Clipboard with other apps ----------
    var t = new Thread(() => { var list = new System.Collections.Specialized.StringCollection { outside }; System.Windows.Forms.Clipboard.SetFileDropList(list); });
    t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
    win.SetForeground(); FileList()?.Focus(); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    Ok("clipboard: a file copied in another app pastes here", Until(() => File.Exists(Path.Combine(Root, Path.GetFileName(outside))), 8000));
    Select("a.txt"); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C); Thread.Sleep(500);
    string[] got = Array.Empty<string>();
    var t2 = new Thread(() => { var l = System.Windows.Forms.Clipboard.GetFileDropList(); got = l.Cast<string>().ToArray(); });
    t2.SetApartmentState(ApartmentState.STA); t2.Start(); t2.Join();
    Ok("clipboard: a file copied here can be pasted in other apps", got.Any((g) => g.Equals(Path.Combine(Root, "a.txt"), StringComparison.OrdinalIgnoreCase)), string.Join(", ", got));

    // ---------- Drag and drop ----------
    var src = Item("a.txt")!; var dst = Item("Sub")!;
    // Ctrl held: copy (without it, a drop on the same drive moves, like Explorer; the parity test checks that).
    Keyboard.Press(VirtualKeyShort.CONTROL);
    try { HandDrag(src.GetClickablePoint(), dst.GetClickablePoint()); } finally { Keyboard.Release(VirtualKeyShort.CONTROL); }
    Ok("drag and drop: Ctrl-dropping a file on a folder copies it there", Until(() => File.Exists(Path.Combine(Root, "Sub", "a.txt")), 8000) && File.Exists(Path.Combine(Root, "a.txt")));

    // ---------- Tabs ----------
    int Tabs() => win.FindAllDescendants((c) => c.ByAutomationId("Tab")).Length;
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
    Ok("tabs: Ctrl+T opens a new tab on Home", Until(() => Tabs() == 2 && Named("Drives") != null), Tabs().ToString());
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
    Ok("tabs: Ctrl+Tab goes back to the first tab, still in its folder", Until(() => Items().Contains("a.txt")));
    Ok("tabs: the tabs can be clicked too", Named("Tab Home") != null);
    Named("Tab Home")!.Click(); Thread.Sleep(300);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_W);
    Ok("tabs: Ctrl+W closes the tab", Until(() => Tabs() == 1 && Items().Contains("a.txt")));

    // ---------- Pins (right-click menu) ----------
    Item("Sub")!.RightClick(); Thread.Sleep(500);
    Ok("menu: right-click shows Open, Copy, Rename, Delete, Pin", Named("Open in new tab") != null && Named("Rename") != null && Named("Pin to Home") != null && Named("Show more options") != null);
    Named("Pin to Home")!.Click();
    Ok("pins: the folder appears in the sidebar under Pinned", Until(() => Named("Pinned") != null && win.FindAllDescendants((c) => c.ByName("Sub")).Length >= 2));
    Ok("pins: it is remembered", Until(() => File.ReadAllText(Path.Combine(settingsDir, "settings.json")).Contains(@"D:\\oxtest\\ui\\Sub")));
    Named("Home")?.Click();
    Ok("pins: Home shows the pinned folder", Until(() => Named("Pinned folders") != null && win.FindAllDescendants((c) => c.ByName("Sub")).Length >= 2));
    Shot("04-home-pinned");
    Go(Root);

    // ---------- Windows' own menu ----------
    Item("a.txt")!.RightClick(); Thread.Sleep(400); Named("Show more options")!.Click(); Thread.Sleep(1500);
    var menu = ui.GetDesktop().FindFirstDescendant((c) => c.ByControlType(ControlType.Menu));
    Ok("menu: Show more options opens Windows' full menu", menu != null);
    Keyboard.Type(VirtualKeyShort.ESCAPE); Thread.Sleep(400); // pressed and released
    var menuGone = Until(() => ui.GetDesktop().FindFirstDescendant((c) => c.ByControlType(ControlType.Menu)) == null, 3000);
    Ok("menu: Escape closes Windows' menu", menuGone);
    if (!menuGone) { Shot("menu-stuck"); Keyboard.Type(VirtualKeyShort.ESCAPE); Thread.Sleep(400); }
    Ok("menu: closing it leaves the Explorer working", Until(() => Items().Contains("a.txt")));

    // ---------- A folder with ';' in its name ----------
    Keys(VirtualKeyShort.ESCAPE); Keys(VirtualKeyShort.ESCAPE); Thread.Sleep(800);
    Until(() => Item("Made by test") != null); Item("Made by test")!.DoubleClick();
    Ok("probe: after Windows' menu, double-click opens a folder", Until(() => Named("Path Made by test") != null)); Go(Root);
    Until(() => Item("odd; name") != null); Item("odd; name")!.DoubleClick();
    Ok("odd names: a folder with ';' in its name opens", Until(() => Named("Path odd; name") != null));
    if (Named("Path odd; name") == null) Shot("odd-names");
    Go(Root);

    // ---------- Hidden files and themes ----------
    More("Show hidden files");
    Ok("hidden files: can be shown", Until(() => Items().Contains("hidden.txt")));
    More("Theme"); Sub("Windows Light");
    Ok("themes: Windows Light is chosen and remembered", Until(() => File.ReadAllText(Path.Combine(settingsDir, "settings.json")).Contains("\"light\"")));
    Shot("05-light");
    Ok("themes: the path bar is readable in the light theme", Named("Path ui") != null);
    Thread.Sleep(500); More("Theme"); Thread.Sleep(400); Keys(VirtualKeyShort.HOME); Keys(VirtualKeyShort.ENTER);
    Ok("themes: back to Omebly Glass", Until(() => File.ReadAllText(Path.Combine(settingsDir, "settings.json")).Contains("\"glass\"")));
    Thread.Sleep(500); More("Show hidden files");


    // After changing themes, folders still open.
    Keys(VirtualKeyShort.ESCAPE); Keys(VirtualKeyShort.ESCAPE); Thread.Sleep(600); Until(() => Item("Sub") != null); Item("Sub")!.DoubleClick();
    Ok("themes: after switching themes, double-click still opens a folder", Until(() => Named("Path Sub") != null));
    if (Named("Path Sub") == null) Shot("after-theme-open");
    Go(Root);

    // ---------- Stage 3: the copy card ----------
    var proj = @"D:\oxtest\eng\data\Project";
    if (Directory.Exists(proj))
    {
        var dest = Path.Combine(Root, "Copies"); Directory.CreateDirectory(dest);
        Until(() => Item("Copies") is { } c && c.TryGetClickablePoint(out _)); Thread.Sleep(500); Item("Copies")!.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); Item("Copies")!.DoubleClick(); Ok("copy card: a folder made outside the app opens on double-click", Until(() => Named("Path Copies") != null)); if (Named("Path Copies") == null) { Shot("copies-not-open"); Go(dest); }
        var tt = new Thread(() => System.Windows.Forms.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { proj }));
        tt.SetApartmentState(ApartmentState.STA); tt.Start(); tt.Join();
        win.SetForeground(); FileList()?.Focus(); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        Ok("copy card: pasting 3,000 files shows the copy card", Until(() => Named("Done") != null || win.FindFirstDescendant((c) => c.ByText("Copying 3,000 items")) != null, 8000));
        Ok("copy card: it finishes with Done", Until(() => Named("Done") != null, 20000));
        Ok("copy card: everything arrived", Directory.GetFiles(Path.Combine(dest, "Project"), "*", SearchOption.AllDirectories).Length == 3000);
        Shot("07-copy-done");
        Ok("copy card: Open and Undo are offered", Named("Open Copies") != null && Named("Undo") != null);
        Named("Undo")!.Click();
        Ok("copy card: Undo takes the copies away", Until(() => !Directory.Exists(Path.Combine(dest, "Project")), 15000));
        // Same names: paste again twice; the second time the card asks.
        Thread.Sleep(5000);
        Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V); Until(() => Directory.Exists(Path.Combine(dest, "Project")) && Directory.GetFiles(Path.Combine(dest, "Project"), "*", SearchOption.AllDirectories).Length == 3000, 20000);
        Thread.Sleep(5000);
        Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        Ok("same names: the card asks what to do with 3,000 files that are already there", Until(() => win.FindFirstDescendant((c) => c.ByText("3,000 files are already in Copies")) != null, 10000));
        Ok("same names: four choices, as designed", Named("Replace them") != null && Named("Skip them") != null && Named("Keep both") != null && Named("Let me decide for each file") != null);
        Shot("08-same-names");
        Named("Skip them")!.Click(); Thread.Sleep(200); Named("Continue")!.Click();
        Ok("same names: Skip copies nothing new", Until(() => Named("Done") != null, 20000) && Directory.GetFiles(Path.Combine(dest, "Project"), "*", SearchOption.AllDirectories).Length == 3000);
        Ok("copy card: after a few seconds it tucks into a chip in the status bar", Until(() => Named("Copy finished, open") != null, 15000));
        Shot("09-chip");
        Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP);
        try { Directory.Delete(dest, true); } catch { }
    }

    // ---------- 100,000 files ----------
    if (Directory.Exists(@"D:\oxtest\big"))
    {
        var sw = Stopwatch.StartNew(); Go(@"D:\oxtest\big");
        Ok("big folder: 100,050 items listed", Until(() => Status().Replace(",", "").StartsWith("100050 items"), 10000), Status());
        Console.WriteLine($"     (opened and counted in {sw.ElapsedMilliseconds} ms, including typing the path)");
        FileList()?.Focus(); Keys(VirtualKeyShort.END);
        Ok("big folder: End jumps to the last file at once", Until(() => Item("file 099999.txt") != null, 4000));
        Keys(VirtualKeyShort.HOME);
        Ok("big folder: Home jumps back to the top", Until(() => Item("folder 0") != null, 4000));
        Shot("06-big");
        app.WaitWhileBusy();
        var p = Process.GetProcessById(app.ProcessId); p.Refresh();
        Console.WriteLine($"     memory with 100,050 items: {p.WorkingSet64 / 1048576} MB");
        Ok("big folder: the window still responds", p.Responding);
    }
}
catch (Exception e) { Console.WriteLine("ERROR " + e); fail++; Shot("error"); }
finally { try { app.Close(); Thread.Sleep(800); if (!app.HasExited) app.Kill(); } catch { } }
Console.WriteLine($"\n{pass} passed, {fail} failed (pictures in {shots})");
return fail == 0 ? 0 : 1;

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    public static void Mouse(uint flags, int dx, int dy) => mouse_event(flags, dx, dy, 0, UIntPtr.Zero);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    /// <summary>A mouse move to a screen point, as a real move event (works across several screens).</summary>
    public static void MoveAbs(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        mouse_event(0x0001 | 0x8000 | 0x4000, (x - vx) * 65535 / (vw - 1), (y - vy) * 65535 / (vh - 1), 0, UIntPtr.Zero);
    }
}
