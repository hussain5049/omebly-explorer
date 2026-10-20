// Windows File Explorer features in Omebly Explorer, tried on the real app: Undo and Redo (delete, rename, new
// folder, copy), the Recycle Bin page (restore, delete for good), create shortcut, select none and invert, typing
// to jump to a name, copy as path and hiding file extensions.
// Works only inside D:\oxtest\parity and with its own settings folder. Items it sends to the Recycle Bin are
// named "omebly-parity-*" and are removed from the bin at the end.
// Run: dotnet run --project tests/ParityTests -- "<path to OmeblyExplorer.exe>"
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

Native.SetProcessDPIAware();
var exe = args.Length > 0 ? args[0] : @"D:\my latest project\Omebly Explorer\publish\test-x64\OmeblyExplorer.exe";
const string Root = @"D:\oxtest\parity";
var settingsDir = Path.Combine(Path.GetTempPath(), "omebly-explorer-parity-test-settings");
int pass = 0, fail = 0;
var shots = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "shots"); Directory.CreateDirectory(shots);
void Ok(string name, bool cond, string info = "") { Console.WriteLine($"{(cond ? "PASS" : "FAIL")} {name}{(cond || info == "" ? "" : " - " + info)}"); if (cond) pass++; else fail++; }
bool Until(Func<bool> cond, int ms = 6000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { try { if (cond()) return true; } catch { } Thread.Sleep(100); } try { return cond(); } catch { return false; } }

var tag = DateTime.Now.ToString("HHmmss");
string F(string n) => Path.Combine(Root, n);
if (Directory.Exists(Root)) Directory.Delete(Root, true);
Directory.CreateDirectory(F("Sub"));
var binMe = $"omebly-parity-{tag}-a.txt"; var forever = $"omebly-parity-{tag}-b.txt";
File.WriteAllText(F(binMe), "a"); File.WriteAllText(F(forever), "b");
File.WriteAllText(F("photo.jpg"), "not really a picture");
File.WriteAllText(F("zeta.txt"), "z"); File.WriteAllText(F("zulu.txt"), "z");
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
List<string> Selected() => FileList()?.FindAllChildren().Where((e) => e.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true).Select((e) => e.Properties.Name.ValueOrDefault ?? "").ToList() ?? new();
void Keys(params VirtualKeyShort[] k) { win.SetForeground(); Keyboard.TypeSimultaneously(k); Thread.Sleep(300); }
void Type(string text) { Keyboard.Type(text); Thread.Sleep(150); }
void Shot(string name) { try { FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(shots, name + ".png")); } catch { } }
void Go(string path) { Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L); Type(path); Keys(VirtualKeyShort.ENTER); Thread.Sleep(700); }
void Select(string name) { Item(name)?.Click(); Thread.Sleep(250); }
string Note() => Id("Note")?.Properties.Name.ValueOrDefault ?? "";
AutomationElement? MenuItem(string name) => ui.GetDesktop().FindAllDescendants((c) => c.ByName(name).And(c.ByControlType(ControlType.MenuItem))).FirstOrDefault((e) => !e.IsOffscreen);
void More(string item) { Keys(VirtualKeyShort.ESCAPE); Id("MoreButton")!.Click(); if (!Until(() => MenuItem(item) != null, 3000)) { Shot("missing-" + item); throw new Exception("menu item not shown: " + item); } Thread.Sleep(500); var mi = MenuItem(item)!; if (mi.Patterns.Toggle.IsSupported) mi.Patterns.Toggle.Pattern.Toggle(); else if (mi.Patterns.Invoke.IsSupported) mi.Patterns.Invoke.Pattern.Invoke(); else mi.Click(); Thread.Sleep(600); Keys(VirtualKeyShort.ESCAPE); }
void Click(string name) { if (!Until(() => Named(name) != null, 4000)) { Shot("missing-" + name); throw new Exception("not found: " + name); } var el = Named(name)!; if (el.Patterns.Invoke.IsSupported) el.Patterns.Invoke.Pattern.Invoke(); else el.Click(); Thread.Sleep(500); }
bool InBin(string name) => RecycleBinHas(name);

try
{
    Go(Root);
    Ok("folder opens", Until(() => Items().Contains(binMe)), string.Join(", ", Items()));
    // ---------- Undo a delete ----------
    Select(binMe); Keys(VirtualKeyShort.DELETE);
    Ok("delete: the file goes to the Recycle Bin", Until(() => !File.Exists(F(binMe)) && InBin(binMe), 8000));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("undo: Ctrl+Z brings the deleted file back", Until(() => File.Exists(F(binMe)) && File.ReadAllText(F(binMe)) == "a", 8000));
    Ok("undo: the status bar says what was undone", Until(() => Note().StartsWith("Undone: Delete"), 3000), Note());
    Ok("undo: it is out of the Recycle Bin again", !InBin(binMe));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Y);
    Ok("redo: Ctrl+Y deletes it again", Until(() => !File.Exists(F(binMe)) && InBin(binMe), 8000));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("undo again: back again", Until(() => File.Exists(F(binMe)), 8000));

    // ---------- Undo a rename ----------
    Until(() => Item("zeta.txt") != null);
    Select("zeta.txt"); Keys(VirtualKeyShort.F2); Thread.Sleep(500);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Type("omega.txt"); Keys(VirtualKeyShort.ENTER);
    Ok("rename: done", Until(() => File.Exists(F("omega.txt")) && !File.Exists(F("zeta.txt"))));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("undo: the rename is taken back", Until(() => File.Exists(F("zeta.txt")) && !File.Exists(F("omega.txt"))));

    // ---------- Undo a new folder ----------
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_N); Thread.Sleep(900);
    Keys(VirtualKeyShort.ESCAPE); // keep the name "New folder"
    Ok("new folder: made", Until(() => Directory.Exists(F("New folder"))));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("undo: the new folder goes away", Until(() => !Directory.Exists(F("New folder")), 8000));

    // ---------- Undo a copy ----------
    Select("zulu.txt"); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
    Item("Sub")!.DoubleClick(); Until(() => Named("Path Sub") != null);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    Ok("copy: pasted into Sub", Until(() => File.Exists(F(@"Sub\zulu.txt")), 8000));
    Thread.Sleep(1200);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("undo: the copy goes away, the original stays", Until(() => !File.Exists(F(@"Sub\zulu.txt")), 8000) && File.Exists(F("zulu.txt")));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("zulu.txt") != null);

    // ---------- The Recycle Bin page ----------
    Select(forever); Keys(VirtualKeyShort.DELETE);
    Until(() => InBin(forever), 8000);
    Select(binMe); Keys(VirtualKeyShort.DELETE);
    Until(() => InBin(binMe), 8000);
    Click("Recycle Bin");
    Ok("bin: the Recycle Bin opens inside Omebly", Until(() => Named("Path Recycle Bin") != null && Id("BinList") != null));
    AutomationElement? BinRow(string n) => Id("BinList")?.FindAllChildren().FirstOrDefault((e) => e.Properties.Name.ValueOrDefault == n);
    Ok("bin: deleted files are listed", Until(() => BinRow(binMe) != null && BinRow(forever) != null, 6000));
    Ok("bin: it shows where they came from", Until(() => win.FindAllDescendants((c) => c.ByName(Root)).Length > 0));
    Shot("bin");
    // Made-up bin entries that point somewhere else are never listed (Security 339).
    var binDir = Path.Combine(@"D:\$Recycle.Bin", System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
    var fakes = new[] { ($"$IOMB{tag}1.txt", @"C:\Users\Public\omebly-evil.txt", "other drive"), ($"$IOMB{tag}2.txt", @"\\127.0.0.1\c$\omebly-evil.txt", "network"), ($"$IOMB{tag}3.txt", @"D:\oxtest\parity\..\omebly-evil.txt", "dot-dot"), ($"$IOMB{tag}4.txt", F("omebly-parity-fine.txt"), "fine") };
    foreach (var (iname, original, _) in fakes)
    {
        var body = new List<byte>(); body.AddRange(BitConverter.GetBytes(2L)); body.AddRange(BitConverter.GetBytes(5L)); body.AddRange(BitConverter.GetBytes(DateTime.Now.ToFileTime()));
        body.AddRange(BitConverter.GetBytes(original.Length + 1)); body.AddRange(System.Text.Encoding.Unicode.GetBytes(original + "\0"));
        File.WriteAllBytes(Path.Combine(binDir, iname), body.ToArray());
        File.WriteAllText(Path.Combine(binDir, "$R" + iname[2..]), "fake");
    }
    Go(Root); Click("Recycle Bin"); Until(() => Id("BinList") != null); Thread.Sleep(800);
    Ok("bin: a made-up entry for another drive, a network path or .. is not listed", BinRow("omebly-evil.txt") == null);
    Ok("bin: a normal entry is listed", Until(() => BinRow("omebly-parity-fine.txt") != null, 4000));
    foreach (var (iname, _, _) in fakes) { try { File.Delete(Path.Combine(binDir, iname)); File.Delete(Path.Combine(binDir, "$R" + iname[2..])); } catch { } }
    Go(Root); Click("Recycle Bin"); Until(() => BinRow(binMe) != null);
    BinRow(binMe)!.Click(); Thread.Sleep(300);
    Click("Restore");
    Ok("bin: Restore puts the file back where it was", Until(() => File.Exists(F(binMe)) && !InBin(binMe), 8000));
    Ok("bin: and opens its folder with it", Until(() => Named("Path parity") != null && Item(binMe) != null, 6000));
    Click("Recycle Bin");
    Until(() => BinRow(forever) != null);
    BinRow(forever)!.Click(); Thread.Sleep(300);
    Click("Delete for good");
    Ok("bin: Delete for good asks first", Until(() => Named("Delete for good?") != null, 4000));
    Keys(VirtualKeyShort.ENTER); // the dialog's own "Delete for good" (its default button)
    Ok("bin: it is gone from the bin for good", Until(() => !InBin(forever) && BinRow(forever) == null, 6000));
    Go(Root);

    // ---------- Create shortcut ----------
    Until(() => Item("zulu.txt") != null);
    Item("zulu.txt")!.RightClick(); Thread.Sleep(500);
    Click("Create shortcut");
    Ok("shortcut: Create shortcut makes a .lnk next to it", Until(() => File.Exists(F("zulu - Shortcut.lnk")), 5000), string.Join(", ", Directory.GetFiles(Root).Select(Path.GetFileName)));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("shortcut: Ctrl+Z removes it", Until(() => !File.Exists(F("zulu - Shortcut.lnk")), 8000));

    // ---------- Select none, invert ----------
    Select("zulu.txt");
    More("Invert selection");
    Ok("invert: everything but zulu.txt is selected", Until(() => Selected().Count == Items().Count - 1 && !Selected().Contains("zulu.txt")), string.Join(", ", Selected()));
    More("Select none");
    Ok("select none: nothing is selected", Until(() => Selected().Count == 0));

    // ---------- Type to jump ----------
    Select("Sub"); Thread.Sleep(200);
    Type("zu");
    Ok("type to jump: typing zu selects zulu.txt", Until(() => Selected().SequenceEqual(new[] { "zulu.txt" }), 3000), string.Join(", ", Selected()));
    Thread.Sleep(1200);
    Type("p");
    Ok("type to jump: then p selects photo.jpg", Until(() => Selected().SequenceEqual(new[] { "photo.jpg" }), 3000), string.Join(", ", Selected()));

    // ---------- Copy as path ----------
    Select("photo.jpg");
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_C); Thread.Sleep(400);
    var clip = ""; var th = new Thread(() => { try { clip = System.Windows.Forms.Clipboard.GetText(); } catch { } }); th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
    Ok("copy as path: Ctrl+Shift+C puts the quoted path on the clipboard", clip == $"\"{F("photo.jpg")}\"", clip);

    // ---------- File extensions ----------
    More("Show file extensions");
    Ok("extensions: hidden, photo.jpg shows as photo", Until(() => FileList()!.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => t.Properties.Name.ValueOrDefault == "photo"), 4000));
    Shot("no-extensions");
    More("Show file extensions");
    Ok("extensions: shown again", Until(() => FileList()!.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => t.Properties.Name.ValueOrDefault == "photo.jpg"), 4000));

    // ================= Batch 2: views, Ctrl+wheel, group by, columns, per-folder views =================
    File.Copy(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg", F("wall.jpg"), true);
    Until(() => Item("wall.jpg") != null);
    void PickFrom(string button, params string[] path)
    {
        Keys(VirtualKeyShort.ESCAPE);
        Id(button)!.Click(); Thread.Sleep(500);
        foreach (var step in path)
        {
            if (!Until(() => MenuItem(step) != null, 3000)) { Shot("missing-" + step); throw new Exception("menu item not shown: " + step); }
            var mi = MenuItem(step)!;
            if (mi.Patterns.ExpandCollapse.IsSupported && step != path[^1]) { mi.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(500); continue; }
            if (mi.Patterns.Toggle.IsSupported) mi.Patterns.Toggle.Pattern.Toggle(); else if (mi.Patterns.Invoke.IsSupported) mi.Patterns.Invoke.Pattern.Invoke(); else mi.Click();
            Thread.Sleep(600);
        }
        Keys(VirtualKeyShort.ESCAPE);
    }
    // Right-click on the column headers (on the Type header: a plain right-click there opens the column chooser).
    void HeaderMenu() { var t = Named("Column Type")!.BoundingRectangle; Mouse.MoveTo(t.X + t.Width / 2, t.Y + t.Height / 2); Mouse.Click(MouseButton.Right); Thread.Sleep(600); }
    bool TilesShown() => Id("FileTiles") is { IsOffscreen: false };
    bool ListShown() => Id("FileList") is { IsOffscreen: false };
    foreach (var (label, grid) in new[] { ("Extra large icons", true), ("Large icons", true), ("Medium icons", true), ("Small icons", true), ("List", true), ("Tiles", true), ("Content", false), ("Gallery", true), ("Details", false) })
    {
        PickFrom("ViewMore", label);
        Ok($"views: {label}", Until(() => grid ? TilesShown() : ListShown(), 3000) && Until(() => Items().Contains("wall.jpg"), 3000));
        Shot("view-" + label.Replace(' ', '-'));
    }
    // Ctrl + mouse wheel: bigger icons, then back to details.
    var mid = FileList()!.BoundingRectangle; Mouse.MoveTo(mid.X + mid.Width / 2, mid.Y + mid.Height / 2);
    Keyboard.Press(VirtualKeyShort.CONTROL);
    try { Mouse.Scroll(1); Thread.Sleep(500); Mouse.Scroll(1); Thread.Sleep(600); }
    finally { Keyboard.Release(VirtualKeyShort.CONTROL); }
    Ok("ctrl+wheel: wheel up makes the icons bigger (details to small icons)", Until(() => TilesShown(), 3000));
    Shot("ctrl-wheel");
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_6);
    Ok("Ctrl+Shift+6: back to details", Until(() => ListShown(), 3000));

    // Each folder remembers its own view.
    Item("Sub")!.DoubleClick(); Until(() => Named("Path Sub") != null);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_2);
    Ok("per folder: Sub set to large icons", Until(() => TilesShown(), 3000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP);
    Ok("per folder: the parent folder keeps details", Until(() => ListShown() && Items().Contains("Sub"), 4000));
    Item("Sub")!.DoubleClick(); Until(() => Named("Path Sub") != null);
    Ok("per folder: going back into Sub shows large icons again", Until(() => TilesShown(), 4000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Items().Contains("Sub"));

    // Group by type.
    PickFrom("SortButton", "Group by", "Type");
    Ok("group by: Type shows group headers", Until(() => win.FindAllDescendants((c) => c.ByName("Group Text document (3)")).Length > 0 || win.FindAllDescendants().Any((e) => (e.Properties.Name.ValueOrDefault ?? "").StartsWith("Group Text document")), 4000));
    Shot("grouped");
    PickFrom("SortButton", "Group by", "(None)");
    Ok("group by: None takes the groups away", Until(() => !win.FindAllDescendants().Any((e) => (e.Properties.Name.ValueOrDefault ?? "").StartsWith("Group ")), 4000));

    // Columns: add Date created and Dimensions from the header's right-click menu.
    HeaderMenu(); Shot("header-menu");
    MenuItem("Date created")!.Patterns.Toggle.Pattern.Toggle(); Thread.Sleep(600);
    Ok("columns: Date created is added", Until(() => Named("Column Date created") != null, 3000));
    HeaderMenu(); Thread.Sleep(600);
    MenuItem("Dimensions")!.Patterns.Toggle.Pattern.Toggle(); Thread.Sleep(600);
    Ok("columns: Dimensions is added", Until(() => Named("Column Dimensions") != null, 3000));
    Ok("columns: a picture shows its dimensions", Until(() => FileList()!.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => System.Text.RegularExpressions.Regex.IsMatch(t.Properties.Name.ValueOrDefault ?? "", @"^\D?\d{3,5} x \d{3,5}\D?$")), 6000));
    Shot("columns");
    Ok("columns: they are remembered", File.ReadAllText(Path.Combine(settingsDir, "settings.json")).Contains("\"dimensions\""));
    // Resize Name by dragging its edge.
    var grip = Named("Resize Name")!; var gp = grip.GetClickablePoint();
    double before = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(settingsDir, "settings.json"))).RootElement.GetProperty("Columns")[0].GetProperty("Width").GetDouble();
    // A hand-like drag: button down, small real moves, button up.
    Native.SetCursorPos(gp.X, gp.Y); Thread.Sleep(200); Native.Mouse(0x0002, 0, 0); Thread.Sleep(200);
    for (int k = 1; k <= 20; k++) { Native.Mouse(0x0001, 6, 0); Thread.Sleep(25); }
    Thread.Sleep(200); Native.Mouse(0x0004, 0, 0); Thread.Sleep(600);
    double after = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(settingsDir, "settings.json"))).RootElement.GetProperty("Columns")[0].GetProperty("Width").GetDouble();
    Ok("columns: dragging the edge makes Name wider", after > before + 40, $"{before} -> {after}");
    // Take them away again.
    foreach (var c in new[] { "Date created", "Dimensions" }) { HeaderMenu(); Thread.Sleep(500); MenuItem(c)!.Patterns.Toggle.Pattern.Toggle(); Thread.Sleep(500); }
    Ok("columns: and removed", Until(() => Named("Column Dimensions") == null && Named("Column Date created") == null, 3000));

    // Compact view: lower rows.
    double RowH() => Item("zulu.txt")!.BoundingRectangle.Height;
    var tall = RowH();
    PickFrom("ViewMore", "Compact view");
    Ok("compact: rows get lower", Until(() => RowH() < tall - 3, 3000), $"{tall} -> {RowH()}");
    PickFrom("ViewMore", "Compact view");

    // ================= Batch 3: preview pane, details pane, tooltips =================
    string PaneTitle() => Id("PaneTitle")?.Properties.Name.ValueOrDefault ?? "";
    Select("zulu.txt");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
    Ok("preview pane: Alt+P opens it on the selected file", Until(() => PaneTitle() == "zulu.txt", 4000), PaneTitle());
    Select("wall.jpg");
    Ok("preview pane: follows the selection", Until(() => PaneTitle() == "wall.jpg", 4000), PaneTitle());
    Thread.Sleep(800); Shot("preview-pane");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.KEY_P);
    Ok("preview pane: Alt+P closes it", Until(() => Id("PaneTitle") is null or { IsOffscreen: true }, 3000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_P);
    Ok("details pane: Alt+Shift+P opens it", Until(() => PaneTitle() == "wall.jpg", 4000), PaneTitle());
    Ok("details pane: shows the picture's dimensions", Until(() => win.FindAllDescendants((c) => c.ByControlType(ControlType.Text)).Any((t) => (t.Properties.Name.ValueOrDefault ?? "").Contains("3840 x 2400")), 5000));
    var titleBox = win.FindFirstDescendant((c) => c.ByName("Title").And(c.ByControlType(ControlType.Edit)));
    Ok("details pane: the title can be edited", titleBox != null);
    if (titleBox != null)
    {
        titleBox.Click(); Thread.Sleep(200); Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); Type("Omebly test title"); Keys(VirtualKeyShort.ENTER);
        Ok("details pane: saved in the file", Until(() => Named("Saved in the file") != null, 5000));
        Select("zulu.txt"); Thread.Sleep(600); Select("wall.jpg");
        Ok("details pane: the new title is read back from the file", Until(() => win.FindFirstDescendant((c) => c.ByName("Title").And(c.ByControlType(ControlType.Edit)))?.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault == "Omebly test title", 6000));
    }
    Shot("details-pane");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_P);
    // Hover tooltip.
    var z = Item("zulu.txt")!.BoundingRectangle; Mouse.MoveTo(z.X + 40, z.Y + z.Height / 2); for (int k = 0; k < 8; k++) { Native.Mouse(0x0001, 2, 0); Thread.Sleep(60); } Thread.Sleep(1800);
    Shot("tooltip");
    Ok("tooltip: hovering a file shows its type, size and date", Until(() => ui.GetDesktop().FindAllDescendants((c) => c.ByControlType(ControlType.ToolTip).Or(c.ByControlType(ControlType.Text))).Any((t) => (t.Properties.Name.ValueOrDefault ?? "").Contains("Type: Text document") || t.FindAllDescendants().Any((x) => (x.Properties.Name.ValueOrDefault ?? "").Contains("Type: Text document"))), 4000));

    // ================= Batch 4: New menu, rotate, share, drive menu =================
    Id("NewButton")!.Click(); Thread.Sleep(600);
    var zipItem = ui.GetDesktop().FindAllDescendants((c) => c.ByControlType(ControlType.MenuItem)).FirstOrDefault((e) => (e.Properties.Name.ValueOrDefault ?? "").Contains("Compressed"));
    Ok("new menu: Windows' own kinds are there (Compressed (zipped) Folder)", zipItem != null);
    if (zipItem != null)
    {
        zipItem.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(900);
        Keys(VirtualKeyShort.ESCAPE);
        var made = Directory.GetFiles(Root, "New *.zip").FirstOrDefault();
        Ok("new menu: it makes an empty ZIP", made != null && new FileInfo(made).Length == 22, made ?? "none");
        Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        Ok("new menu: Ctrl+Z takes it away", Until(() => Directory.GetFiles(Root, "New *.zip").Length == 0, 6000));
    }
    else Keys(VirtualKeyShort.ESCAPE);
    // Rotate: a JPEG only changes its orientation tag (pixels untouched); a PNG is turned.
    File.Copy(F("wall.jpg"), F("rot.jpg"), true); File.Copy(@"D:\oxtest\viewers\pattern.png", F("rot.png"), true);
    Until(() => Item("rot.png") != null);
    var jBefore = Pic(F("rot.jpg"));
    Item("rot.jpg")!.RightClick(); Thread.Sleep(500); Click("Rotate right");
    Ok("rotate: a JPEG is turned by its orientation tag", Until(() => { try { return Pic(F("rot.jpg")).orient == 6; } catch { return false; } }, 6000));
    var jAfter = Pic(F("rot.jpg"));
    Ok("rotate: the JPEG's picture stays exactly the same (no quality lost)", jAfter.w == jBefore.w && jAfter.h == jBefore.h && jAfter.px == jBefore.px, $"{jBefore} -> {jAfter}");
    Item("rot.png")!.RightClick(); Thread.Sleep(500); Click("Rotate right");
    Ok("rotate: a PNG is turned (640 x 360 becomes 360 x 640)", Until(() => { try { var p = Pic(F("rot.png")); return p.w == 360 && p.h == 640; } catch { return false; } }, 6000));
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
    Ok("rotate: Ctrl+Z turns it back", Until(() => { try { var p = Pic(F("rot.png")); return p.w == 640 && p.h == 360; } catch { return false; } }, 6000));
    // Drives: the right-click menu has Windows' drive tools (not run here).
    win.SetForeground();
    var dDrive = win.FindAllDescendants((c) => c.ByControlType(ControlType.Button)).FirstOrDefault((b) => b.Properties.Name.ValueOrDefault == "Local Disk (D:)");
    dDrive?.RightClick(); Thread.Sleep(500);
    Ok("drives: right-click shows Drive tools", MenuItem("Drive tools") != null);
    Keys(VirtualKeyShort.ESCAPE);

    // ================= Batch 5: tabs, history, path arrows, sidebar tree, folder options =================
    int Tabs() => win.FindAllDescendants((c) => c.ByAutomationId("Tab")).Length;
    win.FindAllDescendants((c) => c.ByAutomationId("Tab"))[0].RightClick(); Thread.Sleep(500); Click("Duplicate tab");
    Ok("tabs: Duplicate tab", Until(() => Tabs() == 2, 3000), Tabs().ToString());
    win.FindAllDescendants((c) => c.ByAutomationId("Tab"))[0].RightClick(); Thread.Sleep(500); Click("Close other tabs");
    Ok("tabs: Close other tabs", Until(() => Tabs() == 1, 3000), Tabs().ToString());
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_T);
    Ok("tabs: Ctrl+Shift+T reopens the closed tab", Until(() => Tabs() == 2, 3000), Tabs().ToString());
    win.FindAllDescendants((c) => c.ByAutomationId("Tab"))[1].RightClick(); Thread.Sleep(500); Click("Move to new window");
    Window? other = null;
    var mine = win.Properties.NativeWindowHandle.ValueOrDefault;
    Ok("tabs: Move to new window opens a second window", Until(() => (other = app.GetAllTopLevelWindows(ui).FirstOrDefault((w) => w.Properties.NativeWindowHandle.ValueOrDefault != mine && w.Title == "Omebly Explorer")) != null, 6000) && Tabs() == 1);
    other?.Close(); Thread.Sleep(800);
    win.SetForeground();
    // History: right-click Back lists where you were.
    Go(F("Sub")); Until(() => Named("Path Sub") != null);
    Id("BackButton")!.RightClick(); Thread.Sleep(500);
    Ok("history: right-click Back lists the folders you came from", MenuItem("parity") != null);
    MenuItem("parity")?.Patterns.Invoke.Pattern.Invoke();
    Ok("history: choosing one goes there", Until(() => Item("Sub") != null, 4000));
    // Path arrows: the arrow after "parity" lists its folders.
    Click("Folders in parity");
    Ok("path bar: the arrow lists the folders inside", Until(() => MenuItem("Sub") != null, 4000));
    MenuItem("Sub")?.Patterns.Invoke.Pattern.Invoke();
    Ok("path bar: choosing one opens it", Until(() => Named("Path Sub") != null, 4000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("Sub") != null);
    // Sidebar tree.
    Click("Expand Local Disk (D:)");
    Ok("sidebar tree: a drive opens up to show its folders", Until(() => win.FindAllDescendants((c) => c.ByName("oxtest").And(c.ByControlType(ControlType.Button))).Length > 0, 5000));
    Shot("sidebar-tree");
    Click("Collapse Local Disk (D:)");
    Ok("sidebar tree: and closes again", Until(() => win.FindAllDescendants((c) => c.ByName("oxtest").And(c.ByControlType(ControlType.Button))).Length == 0, 4000));
    // Folder options: single-click to open.
    { Keys(VirtualKeyShort.ESCAPE); Id("MoreButton")!.Click(); Thread.Sleep(500); MenuItem("Folder options…")!.Patterns.Invoke.Pattern.Invoke(); }
    Thread.Sleep(600);
    win.FindFirstDescendant((c) => c.ByName("Single-click to open an item"))?.Click(); Thread.Sleep(200);
    Shot("folder-options");
    win.FindAllDescendants((c) => c.ByName("Save").And(c.ByControlType(ControlType.Button))).FirstOrDefault()?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(800);
    Item("Sub")!.Click();
    Ok("folder options: single-click opens a folder", Until(() => Named("Path Sub") != null, 4000));
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("Sub") != null);
    { Keys(VirtualKeyShort.ESCAPE); Id("MoreButton")!.Click(); Thread.Sleep(500); MenuItem("Folder options…")!.Patterns.Invoke.Pattern.Invoke(); } Thread.Sleep(600);
    win.FindFirstDescendant((c) => c.ByName("Double-click to open an item (single-click to select)"))?.Click(); Thread.Sleep(200);
    win.FindAllDescendants((c) => c.ByName("Save").And(c.ByControlType(ControlType.Button))).FirstOrDefault()?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(600);

    // ================= Last items: compare on a name clash, check boxes, move a column =================
    File.WriteAllText(F(@"Sub\zulu.txt"), "older and smaller");
    Select("zulu.txt"); Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
    Item("Sub")!.DoubleClick(); Until(() => Named("Path Sub") != null);
    Keys(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    if (Until(() => Named("Let me decide for each file") != null, 8000)) { Named("Let me decide for each file")!.Click(); Thread.Sleep(200); Named("Continue")?.Click(); }
    Ok("same name: the question compares both files", Until(() => Named("Already there") != null && Named("Coming in") != null, 8000));
    Shot("compare");
    var skip = win.FindAllDescendants((c) => c.ByName("Skip").And(c.ByControlType(ControlType.Button))).LastOrDefault(); skip?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(800);
    Ok("same name: Skip leaves the file there as it was", File.ReadAllText(F(@"Sub\zulu.txt")) == "older and smaller");
    Keys(VirtualKeyShort.ALT, VirtualKeyShort.UP); Until(() => Item("zulu.txt") != null);
    // Item check boxes: clicking two files keeps both picked.
    PickFrom("ViewMore", "Item check boxes");
    Item("zeta.txt")!.Click(); Thread.Sleep(200); Item("zulu.txt")!.Click(); Thread.Sleep(300);
    Ok("check boxes: clicking items adds them to the selection", Selected().Contains("zeta.txt") && Selected().Contains("zulu.txt"), string.Join(", ", Selected()));
    Shot("checkboxes");
    PickFrom("ViewMore", "Item check boxes");
    Select("zulu.txt");
    Ok("check boxes: off again, a click picks one", Until(() => Selected().SequenceEqual(new[] { "zulu.txt" }), 3000), string.Join(", ", Selected()));
    // Move a column left from its right-click menu.
    var typeX = Named("Column Type")!.BoundingRectangle.X; var dateX = Named("Column Date modified")!.BoundingRectangle.X;
    HeaderMenu();
    MenuItem("Move “Type” left")?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(600);
    Ok("columns: Type moves left of Date modified", Until(() => Named("Column Type")!.BoundingRectangle.X < Named("Column Date modified")!.BoundingRectangle.X, 3000));
    HeaderMenu();
    MenuItem("Move “Type” right")?.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(600);

    // Share opens Windows' share window (closed again with Esc).
    // (No picture is taken: Windows' share window shows the person's own contacts.)
    Item("zulu.txt")!.RightClick(); Thread.Sleep(500); Click("Share");
    // Windows' share window: the top-level window that holds "Nearby Sharing".
    AutomationElement? ShareWin() => ui.GetDesktop().FindAllChildren().FirstOrDefault((w) => { try { return w.FindFirstDescendant((c) => c.ByName("Nearby Sharing")) != null; } catch { return false; } });
    Ok("share: Windows' share window opens with the file", Until(() => ShareWin() != null, 6000));
    var shareClose = ShareWin()?.FindFirstDescendant((c) => c.ByName("Close").And(c.ByControlType(ControlType.Button)));
    if (shareClose?.Patterns.Invoke.IsSupported == true) shareClose.Patterns.Invoke.Pattern.Invoke(); else { ShareWin()?.Focus(); Keyboard.Type(VirtualKeyShort.ESCAPE); }
    Ok("share: it closes and the window works again", Until(() => ShareWin() == null && win.IsEnabled, 5000));
    Ok("app: still responds", Process.GetProcessById(app.ProcessId).Responding);
    var crash = Path.Combine(settingsDir, "crash.log");
    Ok("app: nothing went wrong inside (no crash log)", !File.Exists(crash), File.Exists(crash) ? File.ReadAllText(crash)[..Math.Min(1500, File.ReadAllText(crash).Length)] : "");
}
catch (Exception e) { Console.WriteLine("ERROR " + e); fail++; Shot("error"); }
finally
{
    try { app.Close(); Thread.Sleep(800); if (!app.HasExited) app.Kill(); } catch { }
    CleanBin($"omebly-parity-{tag}-");
}
Console.WriteLine($"\n{pass} passed, {fail} failed (pictures in {shots})");
return fail == 0 ? 0 : 1;

// The tests' own items in this person's Recycle Bin (anything deleted from the D:\oxtest test folders) (read from the $I files, like the app does).
static IEnumerable<(string info, string data, string original)> BinEntries()
{
    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    var dir = Path.Combine(@"D:\$Recycle.Bin", sid);
    if (!Directory.Exists(dir)) yield break;
    foreach (var info in Directory.GetFiles(dir, "$I*"))
    {
        string original;
        try { var b = File.ReadAllBytes(info); int n = BitConverter.ToInt32(b, 24); original = System.Text.Encoding.Unicode.GetString(b, 28, n * 2).TrimEnd('\0'); } catch { continue; }
        yield return (info, Path.Combine(dir, "$R" + Path.GetFileName(info)[2..]), original);
    }
}
// A picture's width, height, EXIF orientation and one pixel (System.Drawing ignores the orientation tag).
static (int w, int h, int orient, int px) Pic(string p) { using var im = System.Drawing.Image.FromFile(p); int o = im.PropertyIdList.Contains(0x0112) ? BitConverter.ToUInt16(im.GetPropertyItem(0x0112)!.Value!, 0) : 1; using var b = new System.Drawing.Bitmap(im); return (im.Width, im.Height, o, b.GetPixel(b.Width / 3, b.Height / 3).ToArgb()); }
static bool RecycleBinHas(string name) => BinEntries().Any((e) => Path.GetFileName(e.original) == name && (File.Exists(e.data) || Directory.Exists(e.data)));
static void CleanBin(string prefix)
{
    foreach (var e in BinEntries().Where((e) => Path.GetFileName(e.original).StartsWith(prefix) || e.original.StartsWith(@"D:\oxtest\", StringComparison.OrdinalIgnoreCase)).ToList())
    {
        try { if (Directory.Exists(e.data)) Directory.Delete(e.data, true); else File.Delete(e.data); File.Delete(e.info); } catch { }
    }
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    public static void Mouse(uint flags, int dx, int dy) => mouse_event(flags, dx, dy, 0, UIntPtr.Zero);
}
