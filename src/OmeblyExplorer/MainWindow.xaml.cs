// The Omebly Explorer window: tabs, the path bar, the sidebar, Home and folder views, keyboard shortcuts and the
// right-click menu. Each tab remembers its own place and history.
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using OmeblyExplorer.Views;
using OmeblyExplorer.Views.Viewers;
using Windows.System;

namespace OmeblyExplorer;

public sealed class TabState
{
    public string Location = "home"; // "home", "this-pc", or a folder path
    public readonly Stack<string> Back = new(), Forward = new();
    public string? SelectAfterLoad;
    public string Title => Location switch
    {
        "home" => "Home", "this-pc" => "This PC", "recycle-bin" => "Recycle Bin", "shelf" => "Shelf", "tools" => "Tools",
        _ when Location.StartsWith("tools:") => Location.Split('|')[0][6..] switch { "map" => "Disk space map", "dupes" => "Duplicates", "compare" => "Compare", "startup" => "Start-up apps", "rules" => "Rules", "backup" => "Backup", _ => "Tools" },
        _ when Location.StartsWith("found:") => Location.Split('|')[0][6..],
        _ when Location.StartsWith("phone:") => Location.Split('|').ElementAtOrDefault(2) is { Length: > 0 } pn ? pn : "Phone",
        _ when Location.StartsWith("server:") => Library.Current.Servers.ElementAtOrDefault(int.TryParse(Location.Split('|')[0][7..], out var si) ? si : -1)?.Name ?? "Server",
        _ when Location.StartsWith("tag:") => (Library.TagColors.FirstOrDefault((t) => t.color == Location[4..]).name ?? "Tag") + " tag",
        _ when Location == "search:everywhere|" => "Search",
        _ when Location.StartsWith("archive:") => System.IO.Path.GetFileName(MainWindow.ArchiveOf(Location).archive),
        _ when Location.StartsWith("search:") => $"Search “{MainWindow.SearchOf(Location).query}”",
        _ => NameOf(Location),
    };
    public static string NameOf(string path) { var n = System.IO.Path.GetFileName(path.TrimEnd('\\')); return string.IsNullOrEmpty(n) ? path.TrimEnd('\\') : n; }
}

public sealed partial class MainWindow : Window
{
    private static int _openWindows;
    private static readonly List<MainWindow> AllWindows = new();
    /// <summary>A folder opened from outside (Win+E, the desktop, another app) while Omebly runs: a new tab in the last window.</summary>
    public static void OpenFromOutside(string path)
    {
        MainWindow? w; lock (AllWindows) w = AllWindows.LastOrDefault();
        if (w == null) { new MainWindow(path.Length == 0 ? null : path).Activate(); return; }
        if (path.Length == 0) { new MainWindow().Activate(); return; }
        w.AddTab(path);
        w.AppWindow.Show(); w.Activate();
        if (w.AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized) op.Restore();
        SetForegroundWindow(w._hwnd);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    /// <summary>Places that aren't a plain folder on disk: Home, This PC, the Recycle Bin, inside an archive, search results.</summary>
    public static bool Special(string loc) => loc is "home" or "this-pc" or "recycle-bin" or "shelf" or "tools" || loc.StartsWith("archive:") || loc.StartsWith("search:") || loc.StartsWith("tag:") || loc.StartsWith("tools:") || loc.StartsWith("found:") || loc.StartsWith("phone:") || loc.StartsWith("server:");
    public static (string archive, string inside) ArchiveOf(string loc) { var s = loc["archive:".Length..]; var bar = s.IndexOf('|'); return bar < 0 ? (s, "") : (s[..bar], s[(bar + 1)..]); }
    public static (string folder, string query) SearchOf(string loc) { var s = loc["search:".Length..]; var bar = s.IndexOf('|'); return bar < 0 ? (s, "") : (s[..bar], s[(bar + 1)..]); }
    private readonly List<TabState> _tabs = new();
    private TabState _tab = null!;
    private readonly IntPtr _hwnd;

    public MainWindow(string? startPath = null, TabState? carry = null)
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        FileOps.Owner = _hwnd;
        SystemBackdrop = new DesktopAcrylicBackdrop(); // the glass: the desktop shows softly through
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "OmeblyExplorer.ico"));
        // 1440 × 900 at the screen's scale (sizes here are real pixels), never bigger than the screen.
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1440 * scale), (int)(area.Width * 0.92)), h = Math.Min((int)(900 * scale), (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter op) { op.PreferredMinimumWidth = 760; op.PreferredMinimumHeight = 480; }

        ApplyTheme(Settings.Current.Theme);
        HiddenToggle.IsChecked = Settings.Current.ShowHidden;
        FileItem.ShowExtensions = Settings.Current.ShowExtensions; ExtToggle.IsChecked = Settings.Current.ShowExtensions;
        Bin.Reveal += (p) => Navigate(p);
        Bin.BinChanged += BuildSidebar;
        Bin.Confirm = Confirm;
        Arc.OpenFile += (f) => ShowViewer(f, new[] { f });
        Tools.Run += RunTool;
        Tools.Navigate += (p) => Navigate(p);
        Tools.Recycle += (paths) => _ = RecycleWithUndo(paths);
        Tools.Copy += (paths, target, move) => StartCopy(paths, target, move);
        Tools.Confirm = Confirm;
        Tools.NewRule = NewRule;
        Tools.NewBackup = NewBackup;
        Tools.RulesChanged += RuleEngine.Start;
        Tools.RulesChanged += UpdateStatus;
        Tools.RunBackupNow = (b) => { if (RuleEngine.DriveFor(b) is { } root) RunJob("Backing up", "Done: backed up", (p, ct) => RuleEngine.RunBackup(b, root, p, ct)); };
        RuleEngine.Note += (t) => DispatcherQueue.TryEnqueue(() => ShowNote(t));
        RuleEngine.TidyNow = (r) => DispatcherQueue.TryEnqueue(() => _ = TidyUp(r.Folder, ask: false));
        RuleEngine.Start();
        if (Settings.Current.CheckUpdates) _ = Updater.CheckOnStart((u) => DispatcherQueue.TryEnqueue(() => _ = OfferUpdate(u)));
        Browse.InsideChanged += (inside) => { var parts = _tab.Location.Split('|'); parts[^1] = inside; var loc = string.Join('|', parts); if (_tab.Location != loc) { _tab.Back.Push(_tab.Location); _tab.Forward.Clear(); _tab.Location = loc; DrawCrumbs(); DrawTabs(); BackButton.IsEnabled = true; } UpdateStatus(); };
        Arc.AskPassword = AskPassword;
        Arc.InsideChanged += (inside) => { var a = Arc.Archive; var loc = "archive:" + a + "|" + inside; if (_tab.Location != loc) { if (_tab.Location.StartsWith("archive:")) { _tab.Back.Push(_tab.Location); _tab.Forward.Clear(); } _tab.Location = loc; DrawCrumbs(); DrawTabs(); BackButton.IsEnabled = _tab.Back.Count > 0; } UpdateStatus(); };
        Arc.ExtractWanted += (a, inside) => Extract(a, inside.Length == 0 ? null : new[] { inside }, Archives.FolderFor(a), Arc.Password);
        UndoLog.Changed += () => DispatcherQueue.TryEnqueue(MarkUndo);
        Folder.SetView(Settings.Current.View); MarkView();
        BuildSortMenu();
        Thumbnails.Start(DispatcherQueue);

        Home.OpenPath += (p) => Navigate(p);
        Home.OpenFile += OpenItem;
        Home.QuickAction += QuickAction;
        Home.PinMenu += (el, pin) => PinFlyout(pin).ShowAt(el);
        Folder.OpenRequested += OpenItem;
        Folder.SelectionChangedOut += UpdateStatus;
        Folder.MenuRequested += ShowItemMenu;
        Folder.FilesDropped += (paths, target, kind) => { if (kind == DropKind.Link) MakeShortcuts(paths, target); else StartCopy(paths, target, kind == DropKind.Move); };
        Folder.LoadFailed += (path, msg) => _ = Message("Can't open this folder", $"{path}\n\n{msg}");
        Folder.Loaded2 += UpdateStatus;
        Folder.ViewChanged += () => { MarkView(); BuildSortMenu(); };
        Folder.SelectionChangedOut += ShowInPane;
        Pane.Closed += () => { Settings.Current.Pane = "none"; Settings.Save(); BuildViewMenu(); };
        if (Settings.Current.Pane is "preview" or "details") Pane.SetMode(Settings.Current.Pane);

        AddShortcuts();
        BuildSidebar();
        Folder.UseSingleClick(Settings.Current.SingleClick);
        Folder.UseCheckBoxes(Settings.Current.CheckBoxes);
        WatchPhones();
        if (carry != null) { _tabs.Add(carry); SwitchTab(carry); } else AddTab(startPath ?? Settings.Current.StartAt);
        // Closing the window while copying: the window hides and the copy finishes, then the app closes.
        AppWindow.Closing += (_, e) =>
        {
            int busy; lock (Running) busy = Running.Count;
            if (busy > 0 && !_hiddenForCopy) { e.Cancel = true; _hiddenForCopy = true; AppWindow.Hide(); }
        };
        Interlocked.Increment(ref _openWindows);
        lock (AllWindows) AllWindows.Add(this);
        Closed += (_, _) => { lock (AllWindows) AllWindows.Remove(this); };
        JumpBar.CurrentFolders = () => { lock (AllWindows) return AllWindows.Select((w) => w._tab?.Location ?? "home").Where((l) => !Special(l)).Distinct().ToList(); };
        // When the last window closes the app ends, even if Windows' share window left something behind that would
        // otherwise keep it running unseen.
        Closed += (_, _) => { if (Interlocked.Decrement(ref _openWindows) == 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Application.Current.Exit()); };
        Closed += (_, _) => { Settings.Current.OpenTabs = _tabs.Select((t) => t.Location).Where((l) => l != "home").Take(10).ToList(); Settings.Save(); };
    }

    // ---------- Themes ----------
    private void ApplyTheme(string theme)
    {
        Settings.Current.Theme = theme;
        Theme.Current = theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = Theme.Current;
        ThemeGlass.IsChecked = theme == "glass"; ThemeLight.IsChecked = theme == "light";
        var tb = AppWindow.TitleBar;
        var fg = theme == "light" ? Colors.Black : Colors.White;
        tb.ButtonForegroundColor = fg; tb.ButtonHoverForegroundColor = fg;
        tb.ButtonBackgroundColor = Colors.Transparent; tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = theme == "light" ? ColorHelper.FromArgb(24, 0, 0, 0) : ColorHelper.FromArgb(30, 255, 255, 255);
        BuildSidebar(); DrawTabs();
        if (_tab != null) DrawCrumbs();
        if (_tab?.Location == "home") Home.Refresh();
    }
    private void Theme_Click(object sender, RoutedEventArgs e) { Perf.Log($"theme click {((FrameworkElement)sender).Tag}"); ApplyTheme((string)((FrameworkElement)sender).Tag); Settings.Save(); }

    // ---------- Tabs ----------
    private void AddTab(string location)
    {
        var t = new TabState();
        _tabs.Add(t);
        SwitchTab(t);
        Navigate(location, addHistory: false);
    }
    private void SwitchTab(TabState t) { _tab = t; Show(); DrawTabs(); }
    private void CloseTab(TabState t)
    {
        if (_tabs.Count == 1) { Close(); return; }
        _closed.Push(t);
        var i = _tabs.IndexOf(t); _tabs.Remove(t);
        if (_tab == t) SwitchTab(_tabs[Math.Min(i, _tabs.Count - 1)]); else DrawTabs();
    }
    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab("home");
    private void NewWindow_Click(object sender, RoutedEventArgs e) => new MainWindow().Activate();

    // ---------- More tab tools: duplicate, move to a new window, close others, reopen closed ----------
    private readonly Stack<TabState> _closed = new();
    private static TabState CopyOf(TabState t)
    {
        var c = new TabState { Location = t.Location };
        foreach (var b in t.Back.Reverse()) c.Back.Push(b);
        foreach (var f in t.Forward.Reverse()) c.Forward.Push(f);
        return c;
    }
    private void DuplicateTab(TabState t) { var c = CopyOf(t); _tabs.Insert(_tabs.IndexOf(t) + 1, c); SwitchTab(c); }
    private void MoveToNewWindow(TabState t, Windows.Graphics.PointInt32? at = null)
    {
        var w = new MainWindow(carry: CopyOf(t));
        if (at is { } p) w.AppWindow.Move(new Windows.Graphics.PointInt32(p.X - 120, p.Y - 20));
        w.Activate();
        if (_tabs.Count > 1) { var i = _tabs.IndexOf(t); _tabs.Remove(t); if (_tab == t) SwitchTab(_tabs[Math.Min(i, _tabs.Count - 1)]); else DrawTabs(); }
        else Close();
    }
    private void ReopenClosedTab() { if (_closed.Count == 0) return; var t = _closed.Pop(); _tabs.Add(t); SwitchTab(t); }
    private MenuFlyout TabMenu(TabState t)
    {
        var m = new MenuFlyout();
        m.Items.Add(Item("New tab", "\uE710", () => AddTab("home"), "Ctrl+T"));
        m.Items.Add(Item("Duplicate tab", "\uE8C8", () => DuplicateTab(t)));
        m.Items.Add(Item("Move to new window", "\uE78B", () => MoveToNewWindow(t)));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Item("Close tab", "\uE711", () => CloseTab(t), "Ctrl+W"));
        var others = Item("Close other tabs", null, () => { foreach (var o in _tabs.Where((x) => x != t).ToList()) { _closed.Push(o); _tabs.Remove(o); } SwitchTab(t); }); others.IsEnabled = _tabs.Count > 1; m.Items.Add(others);
        var right = Item("Close tabs to the right", null, () => { foreach (var o in _tabs.Skip(_tabs.IndexOf(t) + 1).ToList()) { _closed.Push(o); _tabs.Remove(o); } if (!_tabs.Contains(_tab)) SwitchTab(t); else DrawTabs(); }); right.IsEnabled = _tabs.IndexOf(t) < _tabs.Count - 1; m.Items.Add(right);
        var reopen = Item("Reopen closed tab", "\uE7A7", ReopenClosedTab, "Ctrl+Shift+T"); reopen.IsEnabled = _closed.Count > 0; m.Items.Add(reopen);
        return m;
    }
    /// <summary>Drag a tab: sideways to reorder, down out of the tab bar to tear it off into its own window.</summary>
    private void MakeDraggable(Button tab, TabState t)
    {
        Windows.Foundation.Point start = default; bool down = false, torn = false;
        tab.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
            start = e.GetCurrentPoint(TabStrip).Position; down = true; torn = false; tab.CapturePointer(e.Pointer);
        }), true);
        tab.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (!down || torn) return;
            var p = e.GetCurrentPoint(TabStrip).Position;
            if (Math.Abs(p.Y - start.Y) > 60 && (_tabs.Count > 1))
            {
                torn = true; down = false; tab.ReleasePointerCaptures();
                GetCursorPos(out var cur);
                MoveToNewWindow(t, new Windows.Graphics.PointInt32(cur.X, cur.Y));
                return;
            }
            // Reorder: past the middle of a neighbour, swap with it.
            int i = _tabs.IndexOf(t);
            double w = tab.ActualWidth + TabStrip.Spacing;
            if (p.X - start.X > w / 2 && i < _tabs.Count - 1) { _tabs.RemoveAt(i); _tabs.Insert(i + 1, t); start.X += w; DrawTabs(); }
            else if (start.X - p.X > w / 2 && i > 0) { _tabs.RemoveAt(i); _tabs.Insert(i - 1, t); start.X -= w; DrawTabs(); }
        }), true);
        tab.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { down = false; tab.ReleasePointerCaptures(); }), true);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private void DrawTabs()
    {
        if (_tab == null) return;
        TabStrip.Children.Clear();
        foreach (var t in _tabs)
        {
            bool on = t == _tab;
            var row = new Grid { ColumnSpacing = 9, Padding = new Thickness(12, 0, 6, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(TabIcon(t));
            var title = new TextBlock { Text = t.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Get(on ? "OxText" : "OxMuted") };
            Grid.SetColumn(title, 1); row.Children.Add(title);
            var close = new Button { Content = "\uE711", FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], FontSize = 9, Width = 24, Height = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), Foreground = Theme.Get("OxMuted") };
            ToolTipService.SetToolTip(close, "Close tab (Ctrl+W)");
            close.Click += (_, _) => CloseTab(t);
            Grid.SetColumn(close, 2); row.Children.Add(close);
            // A real button, so tabs work with the keyboard and screen readers too.
            var tab = new Button { Width = 210, Height = 36, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(9, 9, 0, 0), Background = on ? Theme.Get("OxTab") : new SolidColorBrush(Colors.Transparent), Content = row };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, (on ? "Active tab " : "Tab ") + t.Title);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tab, "Tab");
            tab.Click += (_, _) => SwitchTab(t);
            tab.RightTapped += (_, e) => { TabMenu(t).ShowAt(tab, e.GetPosition(tab)); e.Handled = true; };
            MakeDraggable(tab, t);
            tab.PointerReleased += (_, e) => { if (e.GetCurrentPoint(tab).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased) CloseTab(t); };
            ToolTipService.SetToolTip(tab, t.Location is "home" or "this-pc" ? t.Title : t.Location);
            TabStrip.Children.Add(tab);
        }
    }
    private static FrameworkElement TabIcon(TabState t)
    {
        if (t.Location == "home") return new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "\uE80F", FontSize = 14, Foreground = Theme.Get("OxAcc") };
        if (t.Location == "this-pc") return new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "\uE7F8", FontSize = 14 };
        return new Microsoft.UI.Xaml.Shapes.Path { Data = HomeView.Folder(), Fill = Ui.Brush(FolderColor(t.Location)), Width = 18, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    }

    // ---------- Going places ----------
    public void Navigate(string location, bool addHistory = true, string? select = null)
    {
        Perf.Log($"navigate {location}");
        if (location is "shell:RecycleBinFolder") location = "recycle-bin";
        if (!Special(location))
        {
            location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            if (File.Exists(location)) { select = System.IO.Path.GetFileName(location); location = System.IO.Path.GetDirectoryName(location)!; }
            if (!Directory.Exists(location)) { _ = Message("Can't find this place", $"\"{location}\" doesn't exist. Check the spelling and try again."); return; }
            if (location.Length == 2 && location[1] == ':') location += "\\";
        }
        if (addHistory && _tab.Location != location) { _tab.Back.Push(_tab.Location); _tab.Forward.Clear(); }
        _tab.Location = location; _tab.SelectAfterLoad = select;
        Show();
        DrawTabs();
    }

    private void Show()
    {
        var loc = _tab.Location;
        BackButton.IsEnabled = _tab.Back.Count > 0; ForwardButton.IsEnabled = _tab.Forward.Count > 0; UpButton.IsEnabled = loc != "home";
        SearchBox.Text = ""; SearchBox.PlaceholderText = _tab.Location == "home" ? "Search the whole PC (Enter)" : _tab.Location.StartsWith("search:") ? "Filter these results" : Special(_tab.Location) ? $"Search {_tab.Title}" : $"Search {_tab.Title}";
        DrawCrumbs(); MarkSidebar();
        bool home = loc == "home", bin = loc == "recycle-bin", arc = loc.StartsWith("archive:"), tools = loc == "tools" || loc.StartsWith("tools:");
        Home.Visibility = Ui.Show(home); Folder.Visibility = Ui.Show(!home && !bin && !arc && !tools); Bin.Visibility = Ui.Show(bin); Arc.Visibility = Ui.Show(arc); Tools.Visibility = Ui.Show(tools);
        Browse.Visibility = Ui.Show(loc.StartsWith("phone:") || loc.StartsWith("server:"));
        if (loc.StartsWith("phone:") || loc.StartsWith("server:")) { Folder.Visibility = Visibility.Collapsed; Folder.Unload(); _ = ShowBrowse(loc); UpdateStatus(); return; }
        if (tools) { Folder.Unload(); ShowTools(loc); UpdateStatus(); return; }
        if (loc.StartsWith("found:")) { _ = ShowFound(loc); UpdateStatus(); return; }
        if (arc) { Folder.Unload(); var (a, inside) = ArchiveOf(loc); _ = Arc.LoadAsync(a, inside); UpdateStatus(); return; }
        if (loc.StartsWith("search:")) { var (f, q) = SearchOf(loc); _ = SearchInside(f, q); UpdateStatus(); return; }
        if (loc == "shelf") { Folder.ShowResults(Library.Current.Shelf.Where(UndoLog.Exists).Select(ItemFor).ToList(), "The shelf is empty. Right-click files anywhere and choose Add to shelf."); UpdateStatus(); return; }
        if (loc.StartsWith("tag:")) { var c = loc[4..]; Folder.ShowResults(Library.Current.Tags.Where((t) => t.Value.Contains(c) && UndoLog.Exists(t.Key)).Select((t) => ItemFor(t.Key)).ToList(), "Nothing has this tag yet."); UpdateStatus(); return; }
        if (home) { Folder.Unload(); Home.Refresh(); UpdateStatus(); return; }
        if (bin) { Folder.Unload(); _ = ShowBin(); return; }
        if (loc == "this-pc") { ShowThisPc(); return; }
        _ = Folder.LoadAsync(loc, _tab.SelectAfterLoad);
        UpdateStatus();
        Pane.Show(null, 0, loc);
    }

    private async Task ShowBin() { await Bin.LoadAsync(); UpdateStatus(); Bin.FocusList(); }

    private async void ShowThisPc()
    {
        // This PC = the drives, shown as a folder.
        Folder.Unload();
        var drives = await Task.Run(Places.Drives);
        await Folder.LoadAsync(Places.Folders().FirstOrDefault()?.Path is string any ? System.IO.Path.GetPathRoot(any)! : "C:\\");
        Navigate(drives.Count > 0 ? drives[0].Root : "C:\\", addHistory: false);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    /// <summary>Right-click Back or Forward: the places you can go back or forward to.</summary>
    private void History_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        bool back = sender == BackButton;
        var list = (back ? _tab.Back : _tab.Forward).Take(15).ToList();
        if (list.Count == 0) return;
        var m = new MenuFlyout();
        for (int n = 0; n < list.Count; n++)
        {
            int steps = n + 1; var loc = list[n];
            m.Items.Add(Item(new TabState { Location = loc }.Title, null, () => { for (int k = 0; k < steps; k++) { if (back) GoBack(); else GoForward(); } }));
        }
        m.ShowAt((FrameworkElement)sender);
        e.Handled = true;
    }
    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Show();
    private void GoBack() { if (_tab.Back.Count == 0) return; _tab.Forward.Push(_tab.Location); var from = _tab.Location; _tab.Location = _tab.Back.Pop(); _tab.SelectAfterLoad = TabState.NameOf(from); Show(); DrawTabs(); }
    private void GoForward() { if (_tab.Forward.Count == 0) return; _tab.Back.Push(_tab.Location); _tab.Location = _tab.Forward.Pop(); Show(); DrawTabs(); }
    private void GoUp()
    {
        var loc = _tab.Location;
        if (loc.StartsWith("archive:")) { var (a, inside) = ArchiveOf(loc); if (inside.Length > 0) Navigate("archive:" + a + "|" + (System.IO.Path.GetDirectoryName(inside) ?? "")); else Navigate(System.IO.Path.GetDirectoryName(a)!, select: System.IO.Path.GetFileName(a)); return; }
        if (loc.StartsWith("search:")) { var sf = SearchOf(loc).folder; Navigate(sf == "everywhere" ? "home" : sf); return; }
        if (loc == "shelf" || loc.StartsWith("tag:")) { Navigate("home"); return; }
        if (loc.StartsWith("tools:") || loc.StartsWith("found:")) { Navigate("tools"); return; }
        if (loc.StartsWith("phone:") || loc.StartsWith("server:")) { Navigate("home"); return; }
        if (loc == "tools") { Navigate(_toolsFrom ?? "home"); return; }
        if (loc is "home" or "this-pc" or "recycle-bin") { Navigate("home"); return; }
        var parent = Directory.GetParent(loc.TrimEnd('\\'));
        if (parent == null) Navigate("home"); else Navigate(parent.FullName, select: TabState.NameOf(loc));
    }

    // ---------- The path bar ----------
    private void DrawCrumbs()
    {
        Crumbs.Children.Clear();
        string? previous = null;
        void Crumb(string text, string target, bool last, string? glyph = null)
        {
            if (Crumbs.Children.Count > 0 && previous != null) Crumbs.Children.Add(CrumbArrow(previous));
            previous = target;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (glyph != null) sp.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 14, Foreground = Theme.Get("OxAcc") });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 14, Foreground = Theme.Get(last ? "OxText" : "OxMuted") });
            var b = new Button { Content = sp, Padding = new Thickness(6, 3, 6, 3), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6) };
            b.Click += (_, _) => Navigate(target);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Path " + text);
            Crumbs.Children.Add(b);
        }
        var loc = _tab.Location;
        if (loc == "home") { Crumb("This PC", "this-pc", false, "\uE80F"); Crumb("Home", "home", true); return; }
        if (loc == "this-pc") { Crumb("This PC", "this-pc", true, "\uE80F"); return; }
        if (loc == "recycle-bin") { Crumb("Recycle Bin", "recycle-bin", true, "\uE74D"); return; }
        if (loc.StartsWith("search:")) { var (sf, sq) = SearchOf(loc); if (sf == "everywhere") Crumb("This PC", "this-pc", false, "\uE80F"); else Crumb(TabState.NameOf(sf), sf, false); Crumb($"Search “{sq}”", loc, true, "\uE721"); return; }
        if (loc == "shelf" || loc.StartsWith("tag:")) { Crumb(new TabState { Location = loc }.Title, loc, true, loc == "shelf" ? "\uE7B8" : "\uE8EC"); return; }
        if (loc.StartsWith("phone:") || loc.StartsWith("server:")) { var bp = loc.Split('|'); Crumb(new TabState { Location = loc }.Title, string.Join('|', bp.Take(bp.Length - 1)) + "|", bp[^1].Length == 0, loc.StartsWith("phone:") ? "\uE8EA" : "\uE968"); if (bp[^1].Length > 0) Crumb(bp[^1], loc, true); return; }
        if (loc == "tools" || loc.StartsWith("tools:") || loc.StartsWith("found:")) { Crumb("Tools", "tools", loc == "tools", "\uE90F"); if (loc != "tools") Crumb(new TabState { Location = loc }.Title, loc, true); return; }
        if (loc.StartsWith("archive:"))
        {
            // The archive's own folder, then the archive, then the folders inside it.
            var (a, inside) = ArchiveOf(loc);
            Crumb("This PC", "this-pc", false, "\uE80F");
            var outer = new List<string>(); for (var d = new DirectoryInfo(System.IO.Path.GetDirectoryName(a)!); d != null; d = d.Parent) outer.Insert(0, d.FullName);
            for (int i = 0; i < outer.Count; i++) Crumb(i == 0 ? DriveName(outer[i]) : TabState.NameOf(outer[i]), outer[i], false);
            Crumb(System.IO.Path.GetFileName(a), "archive:" + a + "|", inside.Length == 0, "\uE7B8");
            var acc = "";
            var inner = inside.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < inner.Length; i++) { acc = acc.Length == 0 ? inner[i] : acc + "\\" + inner[i]; Crumb(inner[i], "archive:" + a + "|" + acc, i == inner.Length - 1); }
            return;
        }
        Crumb("This PC", "this-pc", false, "\uE80F");
        var parts = new List<string>();
        for (var d = new DirectoryInfo(loc); d != null; d = d.Parent) parts.Insert(0, d.FullName);
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var name = i == 0 ? DriveName(p) : TabState.NameOf(p);
            Crumb(name, p, i == parts.Count - 1);
        }
        Crumbs.Children.Add(CrumbArrow(loc)); // and after the last part: the folders inside this one
    }
    /// <summary>The arrow between path parts: a list of the folders inside the part before it.</summary>
    private Button CrumbArrow(string parent)
    {
        var b = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "\uE76C", FontSize = 10, Foreground = Theme.Get("OxFaint") }, Padding = new Thickness(4, 6, 4, 6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Folders in " + (parent == "this-pc" ? "This PC" : TabState.NameOf(parent)));
        var m = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        m.Opening += async (_, _) =>
        {
            m.Items.Clear();
            List<(string name, string path)> subs;
            if (parent == "this-pc") subs = Places.Drives().Select((d) => (d.Name, d.Root)).ToList();
            else subs = await Task.Run(() =>
            {
                try { return new DirectoryInfo(parent).EnumerateDirectories().Where((d) => Settings.Current.ShowHidden || (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).Take(400).Select((d) => (d.Name, d.FullName)).OrderBy((x) => x.Name, Comparer<string>.Create((a, c) => Native.StrCmpLogicalW(a, c))).ToList(); }
                catch { return new List<(string, string)>(); }
            });
            if (subs.Count == 0) m.Items.Add(new MenuFlyoutItem { Text = "No folders inside", IsEnabled = false });
            foreach (var (name, path) in subs) m.Items.Add(Item(name, "\uE8B7", () => Navigate(path)));
        };
        b.Flyout = m;
        return b;
    }
    private static string DriveName(string root) => Places.Drives().FirstOrDefault((d) => d.Root.Equals(root, StringComparison.OrdinalIgnoreCase))?.Name ?? root.TrimEnd('\\');

    private void PathBox_Tapped(object sender, TappedRoutedEventArgs e) { if (e.OriginalSource is Border or ScrollViewer or StackPanel) EditPath(); }
    private void EditPath()
    {
        PathEdit.Text = Special(_tab.Location) ? "" : _tab.Location;
        PathBox.Visibility = Visibility.Collapsed; PathEdit.Visibility = Visibility.Visible;
        PathEdit.Focus(FocusState.Keyboard); PathEdit.SelectAll();
    }
    private void PathEdit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The keyboard goes back to the files (not on to the search box), so the arrow keys work straight away.
        if (e.Key == VirtualKey.Enter) { var t = PathEdit.Text; EndPathEdit(); if (t.Trim().Length > 0) Navigate(t); if (!Special(_tab.Location)) Folder.FocusList(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { EndPathEdit(); if (!Special(_tab.Location)) Folder.FocusList(); e.Handled = true; }
    }
    private void PathEdit_LostFocus(object sender, RoutedEventArgs e) => EndPathEdit();
    private void EndPathEdit() { PathEdit.Visibility = Visibility.Collapsed; PathBox.Visibility = Visibility.Visible; }

    // ---------- Search (this stage: filter the current folder) ----------
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_tab.Location != "home" && !_tab.Location.StartsWith("archive:")) Folder.SetFilter(SearchBox.Text); }
    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { SearchBox.Text = ""; Folder.FocusList(); e.Handled = true; }
        // Enter: search inside files too, in this folder and every folder under it.
        if (e.Key == VirtualKey.Enter && SearchBox.Text.Trim().Length > 0 && !Special(_tab.Location)) { var q = SearchBox.Text.Trim(); Navigate("search:" + _tab.Location + "|" + q); e.Handled = true; }
        // On Home (and in results): instant search across the whole PC by name.
        else if (e.Key == VirtualKey.Enter && SearchBox.Text.Trim().Length > 0 && (_tab.Location == "home" || _tab.Location.StartsWith("search:everywhere"))) { var q = SearchBox.Text.Trim(); Navigate("search:everywhere|" + q); e.Handled = true; }
        if (e.Key == VirtualKey.Down) { Folder.FocusList(); e.Handled = true; }
    }

    // ---------- Views and sorting ----------
    private void View_Click(object sender, RoutedEventArgs e) => UseView((string)((FrameworkElement)sender).Tag);
    /// <summary>A view for this folder (each folder remembers its own, like Explorer).</summary>
    private void UseView(string v) { Folder.SetView(v); Folder.RememberHere(); MarkView(); }
    private void MarkView()
    {
        ViewDetails.IsChecked = Folder.View == "details"; ViewTiles.IsChecked = Folder.View == "tiles"; ViewGallery.IsChecked = Folder.View == "gallery";
        BuildViewMenu();
    }
    /// <summary>Every Explorer view, compact rows, grouping, and "use this view for all folders".</summary>
    private void BuildViewMenu()
    {
        ViewMenu.Items.Clear();
        foreach (var v in FolderView.Views)
        {
            var mi = new RadioMenuFlyoutItem { Text = v.Label, GroupName = "view", IsChecked = Folder.View == v.Key, KeyboardAcceleratorTextOverride = v.Keys ?? "" };
            mi.Click += (_, _) => UseView(v.Key);
            ViewMenu.Items.Add(mi);
        }
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        var compact = new ToggleMenuFlyoutItem { Text = "Compact view", IsChecked = Settings.Current.Compact };
        compact.Click += (_, _) => { Folder.SetCompact(compact.IsChecked); Settings.Save(); };
        ViewMenu.Items.Add(compact);
        var preview = new ToggleMenuFlyoutItem { Text = "Preview pane", IsChecked = Pane.Mode == "preview", KeyboardAcceleratorTextOverride = "Alt+P" };
        preview.Click += (_, _) => TogglePane("preview");
        var details = new ToggleMenuFlyoutItem { Text = "Details pane", IsChecked = Pane.Mode == "details", KeyboardAcceleratorTextOverride = "Alt+Shift+P" };
        details.Click += (_, _) => TogglePane("details");
        ViewMenu.Items.Add(preview); ViewMenu.Items.Add(details);
        var boxes = new ToggleMenuFlyoutItem { Text = "Item check boxes", IsChecked = Settings.Current.CheckBoxes };
        boxes.Click += (_, _) => { Settings.Current.CheckBoxes = boxes.IsChecked; Settings.Save(); Folder.UseCheckBoxes(boxes.IsChecked); };
        ViewMenu.Items.Add(boxes);
        var sizes = new ToggleMenuFlyoutItem { Text = "Show folder sizes", IsChecked = Settings.Current.FolderSizes };
        sizes.Click += (_, _) => { Settings.Current.FolderSizes = sizes.IsChecked; Settings.Save(); Folder.Redraw(); };
        ViewMenu.Items.Add(sizes);
        ViewMenu.Items.Add(Item("Split view (two folders side by side)", "\uE89F", SplitView));
        var ws = new MenuFlyoutSubItem { Text = "Workspaces", Icon = new FontIcon { Glyph = "\uE8A1" } };
        foreach (var w in Library.Current.Workspaces) { var ww = w; ws.Items.Add(Item($"{w.Name} ({w.Tabs.Count} tabs)", null, () => OpenWorkspace(ww))); }
        if (Library.Current.Workspaces.Count > 0) ws.Items.Add(new MenuFlyoutSeparator());
        ws.Items.Add(Item("Save these tabs as a workspace…", null, () => _ = SaveWorkspace()));
        ViewMenu.Items.Add(ws);
        var quick = new MenuFlyoutSubItem { Text = "Show only", Icon = new FontIcon { Glyph = "\uE71C" } };
        foreach (var (key, label) in FolderView.QuickFilters) { var mi = new RadioMenuFlyoutItem { Text = label, GroupName = "quick", IsChecked = Folder.Quick == key }; var k = key; mi.Click += (_, _) => { Folder.SetQuick(k); UpdateStatus(); }; quick.Items.Add(mi); }
        ViewMenu.Items.Add(quick);
        ViewMenu.Items.Add(GroupMenu());
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        ViewMenu.Items.Add(Item("Use this view for all folders", "\uE8A9", () => { Folder.UseViewEverywhere(); ShowNote($"{FolderView.Views.First((x) => x.Key == Folder.View).Label} is now the view for all folders"); }));
    }
    // ---------- The preview and details panes ----------
    private void TogglePane(string mode)
    {
        var next = Pane.Mode == mode ? "none" : mode;
        Pane.SetMode(next); Settings.Current.Pane = next; Settings.Save();
        ShowInPane(); BuildViewMenu();
    }
    private void ShowInPane()
    {
        if (Pane.Mode == "none") return;
        var sel = Special(_tab.Location) ? new List<FileItem>() : Folder.Selected;
        Pane.Show(sel.Count == 1 ? sel[0] : null, sel.Count, Special(_tab.Location) ? null : _tab.Location);
    }

    private MenuFlyoutSubItem GroupMenu()
    {
        var g = new MenuFlyoutSubItem { Text = "Group by", Icon = new FontIcon { Glyph = "\uF168" } };
        foreach (var (key, label) in FolderView.Groups)
        {
            var mi = new RadioMenuFlyoutItem { Text = label, GroupName = "group", IsChecked = Folder.Group == key };
            mi.Click += (_, _) => { Folder.SetGroup(key); Folder.RememberHere(); MarkView(); BuildSortMenu(); };
            g.Items.Add(mi);
        }
        return g;
    }
    private void BuildSortMenu()
    {
        SortMenu.Items.Clear();
        foreach (var (key, label) in new[] { ("name", "Name"), ("date", "Date modified"), ("type", "Type"), ("size", "Size"), ("created", "Date created") })
        {
            var mi = new RadioMenuFlyoutItem { Text = label, GroupName = "sort", IsChecked = Folder.Sort == key };
            mi.Click += (_, _) => { Folder.SetSort(key, Folder.SortDown); BuildSortMenu(); };
            SortMenu.Items.Add(mi);
        }
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        var down = new ToggleMenuFlyoutItem { Text = "Reverse order", IsChecked = Folder.SortDown };
        down.Click += (_, _) => { Folder.SetSort(Folder.Sort, !Folder.SortDown); BuildSortMenu(); };
        SortMenu.Items.Add(down);
        SortMenu.Items.Add(new MenuFlyoutSeparator());
        SortMenu.Items.Add(GroupMenu());
    }
    private void HiddenToggle_Click(object sender, RoutedEventArgs e) { Settings.Current.ShowHidden = HiddenToggle.IsChecked; Settings.Save(); Show(); }

    // ---------- Sidebar ----------
    private void BuildSidebar()
    {
        Sidebar.Children.Clear(); _open.Clear();
        Sidebar.Children.Add(Nav("Home", "home", glyph: "\uE80F", glyphColor: "acc"));
        var pins = Settings.Current.Pinned.Where((p) => Directory.Exists(p.Path)).ToList();
        if (pins.Count > 0)
        {
            Sidebar.Children.Add(new TextBlock { Text = "Pinned", Style = (Style)Application.Current.Resources["OxSideHeader"] });
            foreach (var p in pins) { var nav = Nav(TabState.NameOf(p.Path), p.Path, folderColor: p.Color); nav.RightTapped += (s, e) => { PinFlyout(p).ShowAt((FrameworkElement)s); e.Handled = true; }; Sidebar.Children.Add(Tree(nav, p.Path, 0)); }
        }
        var favs = Library.Current.Favorites.Where(UndoLog.Exists).ToList();
        if (favs.Count > 0)
        {
            Sidebar.Children.Add(new TextBlock { Text = "Favourites", Style = (Style)Application.Current.Resources["OxSideHeader"] });
            foreach (var f in favs.Take(12))
            {
                var isDir = Directory.Exists(f);
                var n = isDir ? Nav(TabState.NameOf(f), f, folderColor: FolderColor(f)) : Nav(System.IO.Path.GetFileName(f), "file:" + f, glyph: "\uE734", glyphColor: "#F4C04E");
                Sidebar.Children.Add(n);
            }
        }
        Sidebar.Children.Add(new TextBlock { Text = "Folders", Style = (Style)Application.Current.Resources["OxSideHeader"] });
        foreach (var f in Places.Folders()) Sidebar.Children.Add(Tree(Nav(f.Name, f.Path, folderColor: f.Color), f.Path, 0));
        Sidebar.Children.Add(new TextBlock { Text = "This PC", Style = (Style)Application.Current.Resources["OxSideHeader"] });
        foreach (var d in Places.Drives().Where((d) => !d.Cloud)) Sidebar.Children.Add(Tree(DriveNav(d), d.Root, 0));
        var cloud = Places.Cloud();
        if (cloud.Count > 0)
        {
            Sidebar.Children.Add(new TextBlock { Text = "Cloud", Style = (Style)Application.Current.Resources["OxSideHeader"] });
            foreach (var c in cloud) Sidebar.Children.Add(Nav(c.Name, c.Path, glyph: "\uE753", glyphColor: c.Color, extra: "Synced"));
        }
        // Network drives and servers.
        var net = Places.Drives().Where((d) => d.Root.Length > 0 && new DriveInfo(d.Root).DriveType == DriveType.Network).ToList();
        if (net.Count > 0 || Library.Current.Servers.Count > 0)
        {
            Sidebar.Children.Add(new TextBlock { Text = "Network", Style = (Style)Application.Current.Resources["OxSideHeader"] });
            for (int i = 0; i < Library.Current.Servers.Count; i++) { var sv = Library.Current.Servers[i]; var idx = i; var n = Nav(sv.Name, $"server:{idx}|", glyph: "\uE968"); n.RightTapped += (s, e) => { var fm = new MenuFlyout(); fm.Items.Add(Item("Forget this server", null, () => { Servers.Forget(sv); Library.Current.Servers.Remove(sv); Library.Save(); BuildSidebar(); })); fm.ShowAt((FrameworkElement)s); e.Handled = true; }; Sidebar.Children.Add(n); }
        }
        _ = AddPhones();
        var tagged = Library.TagColors.Select((t) => (t.color, t.name, count: Library.Current.Tags.Count((x) => x.Value.Contains(t.color)))).Where((t) => t.count > 0).ToList();
        if (tagged.Count > 0)
        {
            Sidebar.Children.Add(new TextBlock { Text = "Tags", Style = (Style)Application.Current.Resources["OxSideHeader"] });
            foreach (var (color, name, count) in tagged) Sidebar.Children.Add(Nav(name, "tag:" + color, glyph: "\uEA3B", glyphColor: color, extra: count.ToString()));
        }
        if (Library.Current.Shelf.Count > 0) { var sh = Nav("Shelf", "shelf", glyph: "\uE7B8", extra: $"{Library.Current.Shelf.Count} items"); sh.Margin = new Thickness(0, 12, 0, 0); Sidebar.Children.Add(sh); }
        var bin = Places.RecycleBin();
        var recycle = Nav("Recycle Bin", "recycle-bin", glyph: "\uE74D", extra: bin.items > 0 ? $"{bin.items} items" : null);
        recycle.Margin = new Thickness(0, 12, 0, 0);
        Sidebar.Children.Add(recycle);
        MarkSidebar();
    }

    private Button Nav(string text, string target, string? glyph = null, string? glyphColor = null, string? folderColor = null, string? extra = null)
    {
        var g = new Grid { ColumnSpacing = 11 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement icon = folderColor != null
            ? new Microsoft.UI.Xaml.Shapes.Path { Data = HomeView.Folder(), Fill = Ui.Brush(folderColor), Width = 19, Height = 17, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph ?? "\uE8B7", FontSize = 15, Foreground = glyphColor == "acc" ? Theme.Get("OxAcc") : glyphColor != null ? Ui.Brush(glyphColor) : Theme.Get("OxMuted") };
        g.Children.Add(icon);
        var t = new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Get("OxText") };
        Grid.SetColumn(t, 1); g.Children.Add(t);
        if (extra != null) { var x = new TextBlock { Text = extra, FontSize = 12, Foreground = Theme.Get("OxFaint"), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(x, 2); g.Children.Add(x); }
        var b = new Button { Content = g, Tag = target, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 34, Padding = new Thickness(12, 0, 10, 0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7) };
        b.Click += (_, _) => { if (target.StartsWith("shell:")) Native.ShellRun(target); else if (target.StartsWith("file:")) { var f = target[5..]; if (File.Exists(f)) OpenItem(ItemFor(f)); } else Navigate(target); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        b.AllowDrop = target is not ("home" or "recycle-bin" or "shelf") && !target.StartsWith("shell:") && !target.StartsWith("file:") && !target.StartsWith("tag:");
        if (target == "recycle-bin")
        {
            // Drop on the Recycle Bin = delete (to the bin), like Explorer.
            b.AllowDrop = true;
            b.DragOver += (_, e) => { if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) { e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move; e.DragUIOverride.Caption = "Move to Recycle Bin"; } };
            b.Drop += async (_, e) => { var items = await e.DataView.GetStorageItemsAsync(); await RecycleWithUndo(items.Select((i) => i.Path).Where((p) => !string.IsNullOrEmpty(p)).ToList()); };
            return b;
        }
        if (b.AllowDrop)
        {
            b.DragOver += (_, e) => { if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) { e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; e.DragUIOverride.Caption = $"Copy to {text}"; } };
            b.Drop += async (_, e) => { var items = await e.DataView.GetStorageItemsAsync(); StartCopy(items.Select((i) => i.Path).ToList(), target, false); };
        }
        ToolTipService.SetToolTip(b, target.StartsWith("shell:") || target == "home" ? text : target);
        return b;
    }

    // ---------- The sidebar tree ----------
    // Each folder or drive in the sidebar is a row: a small arrow (opens it up to show the folders inside, like
    // Explorer's tree) and the entry itself. The arrow is its own button, so keyboards and screen readers reach it.
    private readonly Dictionary<Grid, List<Grid>> _open = new();
    private Grid Tree(Button nav, string path, int level)
    {
        var row = new Grid { Tag = "tree" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18 + level * 14) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var arrow = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "\uE76C", FontSize = 9, Foreground = Theme.Get("OxFaint") }, Width = 18, Height = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, "Expand " + ((nav.Content as Grid)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? TabState.NameOf(path)));
        arrow.Click += async (_, _) => await ToggleTree(row, arrow, path, level);
        nav.Padding = new Thickness(6, nav.Padding.Top, nav.Padding.Right, nav.Padding.Bottom);
        row.Children.Add(arrow);
        Grid.SetColumn(nav, 1); row.Children.Add(nav);
        return row;
    }
    private async Task ToggleTree(Grid row, Button arrow, string path, int level)
    {
        if (_open.ContainsKey(row)) { Collapse(row); ((FontIcon)arrow.Content).Glyph = "\uE76C"; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(arrow).Replace("Collapse ", "Expand ")); return; }
        var subs = await Task.Run(() =>
        {
            try { return new DirectoryInfo(path).EnumerateDirectories().Where((d) => Settings.Current.ShowHidden || (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).Take(300).Select((d) => d.FullName).OrderBy((x) => System.IO.Path.GetFileName(x), Comparer<string>.Create((a, c) => Native.StrCmpLogicalW(a, c))).ToList(); }
            catch { return new List<string>(); }
        });
        var list = new List<Grid>();
        int at = Sidebar.Children.IndexOf(row) + 1;
        foreach (var sub in subs)
        {
            var nav = Nav(TabState.NameOf(sub), sub, folderColor: FolderColor(sub));
            nav.Height = 30;
            var child = Tree(nav, sub, level + 1);
            Sidebar.Children.Insert(at++, child); list.Add(child);
        }
        _open[row] = list;
        ((FontIcon)arrow.Content).Glyph = "\uE70D";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(arrow, Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(arrow).Replace("Expand ", "Collapse "));
        MarkSidebar();
    }
    private void Collapse(Grid row)
    {
        if (!_open.Remove(row, out var kids)) return;
        foreach (var k in kids) { Collapse(k); Sidebar.Children.Remove(k); }
    }
    /// <summary>The sidebar's entries, inside tree rows too.</summary>
    private IEnumerable<Button> SidebarButtons() => Sidebar.Children.SelectMany((c) => c is Grid { Tag: "tree" } g ? g.Children.OfType<Button>().Skip(1) : c is Button b ? new[] { b } : Enumerable.Empty<Button>());

    // ---------- Phones (stage 9) ----------
    private readonly List<UIElement> _phoneRows = new();
    private async Task AddPhones()
    {
        List<(string name, string id)> phones;
        try { phones = await Phones.List(); } catch { return; }
        foreach (var r in _phoneRows) Sidebar.Children.Remove(r);
        _phoneRows.Clear();
        if (phones.Count == 0) return;
        int at = Math.Max(0, Sidebar.Children.Count - 1);
        var head = new TextBlock { Text = "Phones and cameras", Style = (Style)Application.Current.Resources["OxSideHeader"] };
        Sidebar.Children.Insert(at++, head); _phoneRows.Add(head);
        foreach (var (name, id) in phones) { var n = Nav(name, $"phone:{id}|{name}|", glyph: "\uE8EA"); Sidebar.Children.Insert(at++, n); _phoneRows.Add(n); }
    }
    private Windows.Devices.Enumeration.DeviceWatcher? _phoneWatch;
    private void WatchPhones()
    {
        try
        {
            _phoneWatch = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(Windows.Devices.Portable.StorageDevice.GetDeviceSelector());
            _phoneWatch.Added += (_, _) => DispatcherQueue.TryEnqueue(() => _ = AddPhones());
            _phoneWatch.Removed += (_, _) => DispatcherQueue.TryEnqueue(() => _ = AddPhones());
            _phoneWatch.Start();
        }
        catch { }
    }

    // ---------- Phones and servers as folders ----------
    private Servers.Session? _session; private int _sessionFor = -1;
    private async Task ShowBrowse(string loc)
    {
        var parts = loc.Split('|');
        var inside = parts[^1];
        if (loc.StartsWith("phone:"))
        {
            var id = parts[0][6..]; var name = parts.Length > 2 ? parts[1] : "Phone";
            Browse.Lister = async (p) => (await Phones.Items(await Phones.FolderAt(id, p))).Select((e) => new BrowseRow(e.Name, e.IsFolder, (long)e.Size, e.Modified.LocalDateTime, e.Item)).ToList();
            Browse.CopyOut = async (rows) =>
            {
                var target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "From " + name);
                Directory.CreateDirectory(target);
                var items = rows.Select((r) => (Windows.Storage.IStorageItem)r.Tag).ToList();
                var jp = new JobProgress { Total = rows.Count };
                var card = new TaskCard($"Copying from {name}"); card.ShowFile += (f) => Navigate(f); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
                await card.Run((ct) => Phones.CopyToPc(items, target, jp, ct), () => (jp.Total == 0 ? 0 : (double)jp.Done / jp.Total, jp.Current), $"Done: copied to Pictures\\From {name}");
            };
            Browse.PasteIn = async () =>
            {
                var (paths, _) = await Core.Clip.Get(); if (paths.Count == 0) { await Message("Nothing to paste", "Copy files in Omebly or any app first, then paste them here."); return; }
                var folder = await Phones.FolderAt(id, Browse.Inside); var jp = new JobProgress { Total = paths.Count };
                var card = new TaskCard($"Copying to {name}"); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
                await card.Run(async (ct) => { await Phones.CopyToPhone(paths, folder, jp, ct); return new List<string>(); }, () => ((double)jp.Done / jp.Total, jp.Current), "Done: copied to the phone");
                await Browse.Load(Browse.Inside);
            };
            Browse.OpenFile = async (r) =>
            {
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Omebly Explorer", "Phone"); Directory.CreateDirectory(tmp);
                var made = await Phones.CopyToPc(new[] { (Windows.Storage.IStorageItem)r.Tag }, tmp, new JobProgress(), CancellationToken.None);
                if (made.Count > 0) ShowViewer(ItemFor(made[0]), new[] { ItemFor(made[0]) });
            };
        }
        else
        {
            if (!int.TryParse(parts[0][7..], out var idx) || idx >= Library.Current.Servers.Count) return;
            var sv = Library.Current.Servers[idx];
            if (_session == null || _sessionFor != idx)
            {
                _session?.Dispose(); _session = null;
                var pw = Servers.Password(sv) ?? await AskPassword(sv.Name);
                if (pw == null) return;
                var s = new Servers.Session(sv, pw);
                try { await s.Connect(CancellationToken.None); } catch (Exception e) { s.Dispose(); await Message("Couldn't connect", e.Message.Contains("key") ? "The server's key has changed since last time, so Omebly didn't connect (someone could be in between). If the server was set up again, forget it and add it again." : e.Message); return; }
                _session = s; _sessionFor = idx;
            }
            var session = _session;
            Browse.Lister = async (p) => (await session.List(p.Length == 0 ? "/" : p, CancellationToken.None)).Select((e) => new BrowseRow(e.Name, e.IsFolder, e.Size, e.Modified, e)).ToList();
            Browse.CopyOut = async (rows) =>
            {
                var target = Places.Folders().FirstOrDefault((f) => f.Name == "Downloads")?.Path ?? System.IO.Path.GetTempPath();
                var files = rows.Where((r) => !r.IsFolder).Select((r) => (RemoteEntry)r.Tag).ToList(); var jp = new JobProgress { Total = files.Count };
                var card = new TaskCard($"Downloading from {sv.Name}"); card.ShowFile += (f) => Navigate(f); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
                await card.Run(async (ct) => { var made = new List<string>(); foreach (var f in files) { jp.Current = f.Name; made.Add(await session.Download(f, target, ct)); jp.Done++; } return made; }, () => (jp.Total == 0 ? 0 : (double)jp.Done / jp.Total, jp.Current), "Done: downloaded to Downloads");
            };
            Browse.PasteIn = async () =>
            {
                var (paths, _) = await Core.Clip.Get(); var files = paths.Where(File.Exists).ToList(); if (files.Count == 0) return;
                var into = Browse.Inside.Length == 0 ? "/" : Browse.Inside; var jp = new JobProgress { Total = files.Count };
                var card = new TaskCard($"Uploading to {sv.Name}"); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
                await card.Run(async (ct) => { foreach (var f in files) { jp.Current = System.IO.Path.GetFileName(f); await session.Upload(f, into, ct); jp.Done++; } return new List<string>(); }, () => ((double)jp.Done / jp.Total, jp.Current), "Done: uploaded");
                await Browse.Load(Browse.Inside);
            };
            Browse.OpenFile = async (r) =>
            {
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Omebly Explorer", "Server"); Directory.CreateDirectory(tmp);
                var f = await session.Download((RemoteEntry)r.Tag, tmp, CancellationToken.None);
                ShowViewer(ItemFor(f), new[] { ItemFor(f) });
            };
        }
        await Browse.Load(inside);
    }
    private async Task AddServer()
    {
        var kind = new ComboBox { Header = "Kind", Items = { "SFTP (secure, recommended)", "FTPS (FTP with encryption)", "FTP (not encrypted)" }, SelectedIndex = 0, MinWidth = 300 };
        var host = new TextBox { Header = "Server address", PlaceholderText = "example.com or 192.168.1.20" };
        var port = new NumberBox { Header = "Port", Value = 22, Minimum = 1, Maximum = 65535 };
        var user = new TextBox { Header = "User name" };
        var pass = new PasswordBox { Header = "Password (kept by Windows Credential Manager)" };
        var name = new TextBox { Header = "Name in the sidebar", PlaceholderText = "My website" };
        var warn = new TextBlock { Text = "Plain FTP sends the password unencrypted; use it only on your own network.", Foreground = Theme.Get("OxWarn"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        kind.SelectionChanged += (_, _) => { port.Value = kind.SelectedIndex == 0 ? 22 : 21; warn.Visibility = Ui.Show(kind.SelectedIndex == 2); };
        if (await Dialog("Add a server", new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = { kind, host, port, user, pass, name, warn } } }, "Add").ShowAsync() != ContentDialogResult.Primary) return;
        if (host.Text.Trim().Length == 0) return;
        var sv = new Server { Kind = kind.SelectedIndex switch { 1 => "ftps", 2 => "ftp", _ => "sftp" }, Host = host.Text.Trim(), Port = double.IsNaN(port.Value) ? 22 : (int)port.Value, User = user.Text.Trim(), Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : host.Text.Trim() };
        if (pass.Password.Length > 0) Servers.SavePassword(sv, pass.Password);
        Library.Current.Servers.Add(sv); Library.Save(); BuildSidebar();
        Navigate($"server:{Library.Current.Servers.Count - 1}|");
    }
    [System.Runtime.InteropServices.DllImport("mpr.dll")] private static extern int WNetConnectionDialog(IntPtr hwnd, int type);

    // ---------- Rules and backup set-up ----------
    private async Task<Rule?> NewRule()
    {
        var folder = new TextBox { Header = "Watch this folder", Text = Places.Folders().FirstOrDefault((f) => f.Name == "Downloads")?.Path ?? "", MinWidth = 420 };
        var kind = new ComboBox { Header = "For", Items = { "Any file", "Pictures", "Videos", "Music", "Documents", "Archives" }, SelectedIndex = 0 };
        var match = new TextBox { Header = "Whose name looks like (optional)", PlaceholderText = "*.pdf, *invoice*, IMG_*" };
        var action = new ComboBox { Header = "Do this", Items = { "Move it to…", "Copy it to…", "Convert it", "Zip it", "Tidy the folder every day" }, SelectedIndex = 0 };
        var target = new TextBox { Header = "To this folder", Text = Places.Folders().FirstOrDefault((f) => f.Name == "Documents")?.Path ?? "" };
        var rename = new TextBox { Header = "Name it (optional)", PlaceholderText = "{date} {name}" };
        var conv = new ComboBox { Header = "Convert to", Items = { "Smaller JPG (pictures)", "PDF (pictures)", "MP3 (video or music)", "Small MP4 for WhatsApp (videos)" }, SelectedIndex = 0 };
        void Shown() { target.Visibility = Ui.Show(action.SelectedIndex <= 1); rename.Visibility = target.Visibility; conv.Visibility = Ui.Show(action.SelectedIndex == 2); kind.Visibility = match.Visibility = Ui.Show(action.SelectedIndex != 4); }
        action.SelectionChanged += (_, _) => Shown(); Shown();
        if (await Dialog("Add a rule", new ScrollViewer { Content = new StackPanel { Spacing = 8, Children = { folder, kind, match, action, target, rename, conv } } }, "Add").ShowAsync() != ContentDialogResult.Primary) return null;
        if (!Directory.Exists(folder.Text.Trim()) || (action.SelectedIndex <= 1 && !Directory.Exists(target.Text.Trim()))) { await Message("Can't find a folder", "Type or paste folders that exist."); return null; }
        return new Rule
        {
            Folder = folder.Text.Trim(), Kind = kind.SelectedIndex switch { 1 => "pictures", 2 => "videos", 3 => "music", 4 => "documents", 5 => "archives", _ => "any" },
            Match = match.Text.Trim().Length == 0 ? "*" : match.Text.Trim(), Action = action.SelectedIndex switch { 1 => "copy", 2 => "convert", 3 => "zip", 4 => "tidy", _ => "move" },
            Target = target.Text.Trim(), Rename = rename.Text.Trim(), Convert = conv.SelectedIndex switch { 1 => "pdf-images", 2 => "mp3", 3 => "mp4-whatsapp", _ => "small" },
        };
    }
    private async Task<Backup?> NewBackup()
    {
        var drives = DriveInfo.GetDrives().Where((d) => d.IsReady && d.DriveType is DriveType.Removable or DriveType.Fixed && !d.RootDirectory.FullName.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (drives.Count == 0) { await Message("No backup drive", "Plug in the drive you want to back up to (a USB drive or a second disk), then try again."); return null; }
        var drive = new ComboBox { Header = "Back up to", MinWidth = 380 };
        foreach (var d in drives) drive.Items.Add($"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Drive" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')}) · {Format.Gb(d.AvailableFreeSpace)} free");
        drive.SelectedIndex = 0;
        var boxes = Places.Folders().Select((f) => new CheckBox { Content = f.Name, Tag = f.Path, IsChecked = f.Name is "Documents" or "Pictures" or "Desktop" }).ToList();
        var extra = new TextBox { Header = "Another folder (optional)", PlaceholderText = "D:\\Work" };
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(drive); panel.Children.Add(new TextBlock { Text = "Folders to back up", Margin = new Thickness(0, 6, 0, 0) }); foreach (var b in boxes) panel.Children.Add(b); panel.Children.Add(extra);
        if (await Dialog("Set up a backup", new ScrollViewer { Content = panel }, "Save").ShowAsync() != ContentDialogResult.Primary) return null;
        var folders = boxes.Where((b) => b.IsChecked == true).Select((b) => (string)b.Tag).ToList();
        if (Directory.Exists(extra.Text.Trim())) folders.Add(extra.Text.Trim());
        if (folders.Count == 0) return null;
        var dr = drives[Math.Max(0, drive.SelectedIndex)];
        return new Backup { Folders = folders, DriveLabel = string.IsNullOrWhiteSpace(dr.VolumeLabel) ? dr.Name.TrimEnd('\\') : dr.VolumeLabel, DriveSerial = RuleEngine.Serial(dr.RootDirectory.FullName) };
    }

    private Button DriveNav(DriveCard d)
    {
        var b = Nav(d.Name, d.Root, glyph: d.Removable ? "\uE88E" : "\uEDA2", extra: d.ShortFree);
        b.Height = 50;
        b.RightTapped += (s, e) => { DriveFlyout(d).ShowAt((FrameworkElement)s); e.Handled = true; };
        var g = (Grid)b.Content;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, d.UsedFraction), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.02, 1 - d.UsedFraction), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(2), Background = Theme.Get("OxRing") }; Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = d.Low ? Theme.Get("OxWarn") : Theme.Get("OxAcc") });
        Grid.SetRow(bar, 1); Grid.SetColumn(bar, 1); Grid.SetColumnSpan(bar, 2);
        g.Children.Add(bar);
        return b;
    }

    private void MarkSidebar()
    {
        if (_tab == null) return;
        foreach (var c in SidebarButtons())
        {
            var on = c.Tag is string t && (t == _tab.Location || (t.Length > 3 && _tab.Location.Length > 3 && t.TrimEnd('\\').Equals(_tab.Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            c.Background = on ? Theme.Get("OxSel") : new SolidColorBrush(Colors.Transparent);
            c.BorderThickness = new Thickness(on ? 3 : 0, 0, 0, 0);
            c.BorderBrush = Theme.Get("OxAcc");
        }
    }

    /// <summary>The colour a folder shows in: its pin colour, the known-folder colour, or Windows yellow.</summary>
    private static string FolderColor(string path)
    {
        var pin = Settings.Current.Pinned.FirstOrDefault((p) => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (pin != null) return pin.Color;
        if (Library.Current.ColorOf(path) is { } own) return own;
        return Places.Folders().FirstOrDefault((f) => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Color ?? "#F4C04E";
    }

    // ---------- Pins ----------
    private void Pin(string path)
    {
        if (Settings.Current.Pinned.Any((p) => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Settings.Current.Pinned.Add(new PinnedFolder { Path = path, Color = Settings.PinColors[Settings.Current.Pinned.Count % Settings.PinColors.Length] });
        Settings.Save(); BuildSidebar(); if (_tab.Location == "home") Home.Refresh();
    }
    private void Unpin(PinnedFolder p) { Settings.Current.Pinned.Remove(p); Settings.Save(); BuildSidebar(); if (_tab.Location == "home") Home.Refresh(); }
    private MenuFlyout PinFlyout(PinnedFolder p)
    {
        var m = new MenuFlyout();
        m.Items.Add(Item("Open", "\uE8DA", () => Navigate(p.Path)));
        m.Items.Add(Item("Open in new tab", "\uE737", () => AddTab(p.Path)));
        var colors = new MenuFlyoutSubItem { Text = "Colour", Icon = new FontIcon { Glyph = "\uE790" } };
        foreach (var c in Settings.PinColors)
        {
            var mi = new MenuFlyoutItem { Text = ColorName(c), Icon = new FontIcon { Glyph = "\uEA3B", Foreground = Ui.Brush(c) } };
            mi.Click += (_, _) => { p.Color = c; Settings.Save(); BuildSidebar(); DrawTabs(); if (_tab.Location == "home") Home.Refresh(); };
            colors.Items.Add(mi);
        }
        m.Items.Add(colors);
        int i = Settings.Current.Pinned.IndexOf(p);
        if (i > 0) m.Items.Add(Item("Move up", "\uE74A", () => { Settings.Current.Pinned.RemoveAt(i); Settings.Current.Pinned.Insert(i - 1, p); Settings.Save(); BuildSidebar(); if (_tab.Location == "home") Home.Refresh(); }));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Item("Unpin", "\uE77A", () => Unpin(p)));
        return m;
    }
    private static string ColorName(string c) => c switch { "#2EF2B3" => "Mint", "#4C9BFF" => "Blue", "#F4C04E" => "Yellow", "#FF6F91" => "Pink", "#9B7BFF" => "Purple", "#B9C4C0" => "Grey", "#FF8A3D" => "Orange", "#22C3E6" => "Cyan", _ => c };

    // ---------- Opening things ----------
    private void OpenItem(FileItem item)
    {
        Perf.Log($"open {item.Path}");
        if (item.IsFolder) { if (Settings.Current.FoldersInNewWindow) new MainWindow(item.Path).Activate(); else Navigate(item.Path); return; }
        // Programs and shortcuts run as usual; every other file opens inside Omebly Explorer.
        if (item.Extension is ".exe" or ".msi" or ".lnk" or ".bat" or ".cmd" or ".url" or ".appref-ms") { Native.ShellRun(item.Path, null, _hwnd); return; }
        if (Archives.IsArchive(item.Path)) { Navigate("archive:" + item.Path + "|"); return; } // like Windows: a ZIP opens as a folder
        ShowViewer(item, _tab.Location == "home" ? new[] { item } : Folder.Shown);
    }

    // ---------- Viewers (stage 4) ----------
    private ViewerHost? _viewer;
    private void ShowViewer(FileItem item, IReadOnlyList<FileItem> list)
    {
        if (_viewer != null) return;
        var v = new ViewerHost(inWindow: false);
        Grid.SetRow(v, 1); Grid.SetRowSpan(v, 3);
        v.Closed += () => { Root.Children.Remove(v); _viewer = null; Pane.Suspend(false); Folder.FocusList(); };
        Pane.Suspend(true); // Windows' preview windows in the pane would show through the big viewer
        v.PopOut += (f, l) => new ViewerWindow(f, l).Activate();
        v.FullScreenWanted += (on) => AppWindow.SetPresenter(on ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        v.DeleteWanted += (f) => _ = FileOps.Recycle(new[] { f.Path });
        Root.Children.Add(v);
        _viewer = v;
        v.Show(item, list);
    }

    private async void QuickAction(string action)
    {
        switch (action)
        {
            case "empty-bin":
                if (await Confirm("Empty the Recycle Bin?", "Everything in it will be deleted for good.", "Empty"))
                {
                    await Task.Run(() => SHEmptyRecycleBinW(_hwnd, null, 0x1 | 0x2));
                    Home.Refresh(); BuildSidebar();
                }
                break;
            case "edit-pins":
                await Message("Pinned folders", "Right-click any folder and choose Pin to Home. Right-click a pinned folder to change its colour, move it or unpin it.");
                break;
            case "big": _toolsFrom = null; RunTool("big"); break;
            case "dupes": _toolsFrom = null; RunTool("dupes"); break;
            case "tidy":
                var dl = Places.Folders().FirstOrDefault((p) => p.Name == "Downloads");
                if (dl != null) Navigate(dl.Path);
                break;
            default:
                await Message(action is "convert" or "zip" ? "How to" : "Coming soon", action switch
                {
                    "convert" => "Right-click any files and choose Convert to: pictures, PDF, Word, video and music.",
                    "zip" => "Right-click files or folders and choose Compress to. Double-click a ZIP to open it like a folder, or right-click it to extract.",
                    "big" => "Find big files arrives in stage 8 with the other power tools.",
                    "dupes" => "Find duplicates arrives in stage 8 with the other power tools.",
                    _ => "This arrives in a later stage.",
                });
                break;
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ---------- The right-click menu ----------
    private void ShowItemMenu(FileItem? item, Windows.Foundation.Point at, UIElement target)
    {
        var sel = Folder.Selected;
        var m = new MenuFlyout();
        string? here = Special(_tab.Location) ? null : _tab.Location;
        if (item != null)
        {
            m.Items.Add(Item("Open", "\uE8E5", () => OpenItem(item)));
            if (item.IsFolder) m.Items.Add(Item("Open in new tab", "\uE737", () => AddTab(item.Path)));
            else m.Items.Add(Item("Open with…", "\uE7AC", () => Native.ShellRun(item.Path, "openas", _hwnd)));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(Item("Cut", "\uE8C6", () => _ = Core.Clip.Put(sel, true), "Ctrl+X"));
            m.Items.Add(Item("Copy", "\uE8C8", () => { _ = Core.Clip.Put(sel, false); RememberClip(sel); }, "Ctrl+C"));
            if (item.IsFolder && sel.Count == 1) m.Items.Add(Item("Paste into folder", "\uE77F", () => Paste(item.Path)));
            if (sel.Count == 1) m.Items.Add(Item("Rename", "\uE8AC", () => _ = RenameAsync(item), "F2"));
            m.Items.Add(Item("Delete", "\uE74D", () => _ = DeleteAsync(sel, false), "Del"));
            m.Items.Add(new MenuFlyoutSeparator());
            if (item.IsFolder && sel.Count == 1)
            {
                var pin = Settings.Current.Pinned.FirstOrDefault((p) => p.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                m.Items.Add(pin == null ? Item("Pin to Home", "\uE718", () => Pin(item.Path)) : Item("Unpin from Home", "\uE77A", () => Unpin(pin)));
            }
            AddStage5(m, item, sel, here);
            AddOrganize(m, item, sel);
            AddPower(m, item, sel, here);
            if (sel.All((s) => CloudFiles.InCloud(s.Path)))
            {
                m.Items.Add(Item("Always keep on this device", "\uE73E", () => { foreach (var s in sel) CloudFiles.SetKeep(s.Path, true); ShowNote("These stay on this PC"); Show(); }));
                m.Items.Add(Item("Free up space", "\uE753", () => { foreach (var s in sel) CloudFiles.SetKeep(s.Path, false); ShowNote("Space will be freed; the files stay online"); Show(); }));
            }
            m.Items.Add(Item("Share", "\uE72D", () => Extras.Share(_hwnd, sel)));
            if (!item.IsFolder && sel.Count == 1)
            {
                if (Extras.IsPicture(item)) m.Items.Add(Item("Set as desktop background", "\uE7F4", () => { if (Extras.SetWallpaper(item.Path)) ShowNote("Desktop background set"); else _ = Message("Couldn't set the background", "Windows didn't accept this picture."); }));
                if (Extras.CanMount(item)) m.Items.Add(Item("Mount", "\uE958", () => Native.ShellRun(item.Path, "mount", _hwnd)));
                if (item.Extension is ".exe" or ".msi" or ".bat" or ".cmd") m.Items.Add(Item("Run as administrator", "\uE7EF", () => Native.ShellRun(item.Path, "runas", _hwnd)));
                if (item.Kind is FileKind.Document or FileKind.Pdf or FileKind.Text or FileKind.Picture or FileKind.Sheet or FileKind.Slides) m.Items.Add(Item("Print", "\uE749", () => Native.ShellRun(item.Path, "print", _hwnd)));
            }
            if (sel.Count > 0 && sel.All((s) => !s.IsFolder && Extras.CanRotate(s)))
            {
                m.Items.Add(Item("Rotate right", "\uE7AD", () => _ = RotateAsync(sel, true)));
                m.Items.Add(Item("Rotate left", "\uE7AD", () => _ = RotateAsync(sel, false)));
            }
            m.Items.Add(Item("Copy as path", "\uE71B", () => CopyPaths(sel), "Ctrl+Shift+C"));
            if (here != null) m.Items.Add(Item("Create shortcut", "\uE71B", () => MakeShortcuts(sel.Select((s) => s.Path).ToList(), here)));
            m.Items.Add(Item("Properties", "\uE946", () => Native.ShellRun(item.Path, "properties", _hwnd), "Alt+Enter"));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(Item("Show more options", "\uE712", () => { ShellMenu.Show(_hwnd, sel.Select((s) => s.Path).ToList()); Activate(); Folder.FocusList(); })); // the list gets the keyboard back
        }
        else if (here != null)
        {
            var view = new MenuFlyoutSubItem { Text = "View", Icon = new FontIcon { Glyph = "\uE8FD" } };
            foreach (var v in FolderView.Views)
            {
                var vi = new RadioMenuFlyoutItem { Text = v.Label, GroupName = "v", IsChecked = Folder.View == v.Key };
                vi.Click += (_, _) => UseView(v.Key);
                view.Items.Add(vi);
            }
            m.Items.Add(view);
            var sort = new MenuFlyoutSubItem { Text = "Sort by", Icon = new FontIcon { Glyph = "\uE8CB" } };
            foreach (var (k, l) in new[] { ("name", "Name"), ("date", "Date modified"), ("type", "Type"), ("size", "Size"), ("created", "Date created") }) sort.Items.Add(Item(l, null, () => { Folder.SetSort(k, Folder.SortDown); BuildSortMenu(); }));
            m.Items.Add(sort);
            m.Items.Add(GroupMenu());
            m.Items.Add(Item("Refresh", "\uE72C", Show, "F5"));
            m.Items.Add(new MenuFlyoutSeparator());
            var paste = Item("Paste", "\uE77F", () => Paste(here), "Ctrl+V"); paste.IsEnabled = Core.Clip.HasFiles(); m.Items.Add(paste);
            var nw = new MenuFlyoutSubItem { Text = "New", Icon = new FontIcon { Glyph = "\uE710" } };
            nw.Items.Add(Item("Folder", "\uE8B7", () => _ = NewFolderAsync(), "Ctrl+Shift+N"));
            nw.Items.Add(Item("Text document", "\uE8A5", () => _ = NewTextAsync()));
            foreach (var it in NewKindItems()) nw.Items.Add(it);
            m.Items.Add(nw);
            m.Items.Add(new MenuFlyoutSeparator());
            var pin = Settings.Current.Pinned.FirstOrDefault((p) => p.Path.Equals(here, StringComparison.OrdinalIgnoreCase));
            m.Items.Add(pin == null ? Item("Pin this folder to Home", "\uE718", () => Pin(here)) : Item("Unpin this folder", "\uE77A", () => Unpin(pin)));
            AddFolderOrganize(m, here);
            m.Items.Add(Item("Open terminal here", "\uE756", () => Terminal(here)));
            m.Items.Add(Item("Properties", "\uE946", () => Native.ShellRun(here, "properties", _hwnd)));
        }
        if (m.Items.Count > 0) m.ShowAt(target, at);
    }
    private static MenuFlyoutItem Item(string text, string? glyph, Action run, string? keys = null)
    {
        var mi = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = keys ?? "" };
        if (glyph != null) mi.Icon = new FontIcon { Glyph = glyph };
        mi.Click += (_, _) => run();
        return mi;
    }

    // ---------- File actions ----------
    private async void Paste(string target)
    {
        var (paths, cut) = await Core.Clip.Get();
        if (paths.Count == 0) return;
        StartCopy(paths, target, cut);
    }

    // ---------- Copy and move (Omebly's own engine and copy card) ----------
    private static readonly List<CopyJob> Running = new();
    private CopyJob StartCopy(List<string> paths, string target, bool move, bool recordUndo = true)
    {
        var none = new CopyJob { Sources = paths, Target = target, Move = move };
        if (paths.Count == 0) return none;
        var job = new CopyJob { Sources = paths, Target = target, Move = move };
        lock (Running) Running.Add(job);
        var card = new CopyCard(job);
        job.Changed += () => { if (job.State == JobState.Asking) DispatcherQueue.TryEnqueue(ComeBackIfHidden); };
        card.OpenTarget += (t) => Navigate(t);
        card.AskEachFile = AskSameName;
        card.Finished += (c) => { Cards.Children.Remove(c); if (c.Job.State == JobState.Done) AddChip(c.Job); };
        if (recordUndo)
        {
            var holder = new[] { job };
            var what = $"{(move ? "Move" : "Copy")} {(paths.Count == 1 ? $"“{TabState.NameOf(paths[0])}”" : $"{paths.Count} items")} to {TabState.NameOf(target)}";
            _ = Task.Run(async () =>
            {
                while (job.State is not (JobState.Done or JobState.Cancelled or JobState.Failed)) await Task.Delay(200);
                if (job.State != JobState.Done) return;
                // Colours, tags and notes go with moved files.
                if (move) foreach (var src in paths) Library.Current.Moved(src.TrimEnd('\\'), System.IO.Path.Combine(target, System.IO.Path.GetFileName(src.TrimEnd('\\'))));
                UndoLog.Add(new UndoStep
                {
                    What = what,
                    Undo = async () => { await CopyEngine.Undo(holder[0]); return true; },
                    Redo = () => { var t = new TaskCompletionSource<bool>(); DispatcherQueue.TryEnqueue(() => { holder[0] = StartCopy(paths, target, move, recordUndo: false); t.SetResult(true); }); return t.Task; },
                });
            });
        }
        Cards.Children.Insert(0, card);
        _ = CopyEngine.Run(job).ContinueWith((_) =>
        {
            lock (Running) Running.Remove(job);
            DispatcherQueue.TryEnqueue(() => { BuildSidebar(); if (_hiddenForCopy && Running.Count == 0) Close(); });
        });
        return job;
    }

    // ---------- Shortcuts (Create shortcut, Alt-drag) ----------
    private async void MakeShortcuts(List<string> paths, string folder)
    {
        var made = new List<string>();
        foreach (var p in paths) { try { made.Add(Shortcut.Create(p, folder)); } catch (Exception e) { await Message("Couldn't make a shortcut", e.Message); } }
        if (made.Count == 1) { UndoLog.Made(made[0], $"New shortcut “{System.IO.Path.GetFileName(made[0])}”"); await Task.Delay(300); Folder.Reveal(System.IO.Path.GetFileName(made[0])); }
        else if (made.Count > 1)
        {
            var when = DateTime.Now;
            UndoLog.Add(new UndoStep { What = $"New {made.Count} shortcuts", Undo = () => { when = DateTime.Now; return FileOps.Recycle(made.Where(UndoLog.Exists)); }, Redo = () => Task.Run(() => RecycleBin.Find(made, when).All((i) => RecycleBin.Restore(i) != null)) });
        }
    }

    private async Task RotateAsync(IReadOnlyList<FileItem> pics, bool clockwise)
    {
        var paths = pics.Select((p) => p.Path).ToList();
        var ok = 0; foreach (var p in paths) if (await Extras.Rotate(p, clockwise)) ok++;
        if (ok < paths.Count) await Message("Some pictures couldn't be turned", $"{paths.Count - ok} of {paths.Count} couldn't be changed (they may be open in another app or read-only).");
        if (ok > 0)
        {
            UndoLog.Add(new UndoStep
            {
                What = $"Rotate {(paths.Count == 1 ? $"“{System.IO.Path.GetFileName(paths[0])}”" : $"{paths.Count} pictures")} {(clockwise ? "right" : "left")}",
                Undo = async () => { foreach (var p in paths) await Extras.Rotate(p, !clockwise); return true; },
                Redo = async () => { foreach (var p in paths) await Extras.Rotate(p, clockwise); return true; },
            });
            foreach (var p in pics) p.IconSizeLoaded = 0;
            Show();
        }
    }

    // ---------- Stage 8: power tools and the control centre ----------
    private string? _toolsFrom;
    private void Tools_Click(object sender, RoutedEventArgs e) => OpenTools();
    private void OpenTools() { _toolsFrom = Special(_tab.Location) ? null : _tab.Location; Navigate("tools"); }
    private void ShowTools(string loc)
    {
        var parts = loc.Split('|');
        switch (parts[0])
        {
            case "tools:map": _ = Tools.ShowMap(parts[1]); break;
            case "tools:dupes": _ = Tools.ShowDuplicates(parts[1]); break;
            case "tools:compare": _ = Tools.ShowCompare(parts[1], parts[2]); break;
            case "tools:startup": Tools.ShowStartup(); break;
            case "tools:rules": Tools.ShowRules(); break;
            case "tools:backup": Tools.ShowBackup(); break;
            default: Tools.ShowHub(_toolsFrom); break;
        }
    }
    private string NeedFolder() => _toolsFrom ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private async void RunTool(string key)
    {
        var here = NeedFolder();
        switch (key)
        {
            case "dupes": Navigate("tools:dupes|" + here); break;
            case "map": Navigate("tools:map|" + (_toolsFrom ?? "C:\\")); break;
            case "big": Navigate("found:Big files|" + here + "|big"); break;
            case "empty": Navigate("found:Empty folders|" + here + "|empty"); break;
            case "old": Navigate("found:Old downloads|" + (Places.Folders().FirstOrDefault((f) => f.Name == "Downloads")?.Path ?? here) + "|old"); break;
            case "temp": Navigate("found:Temporary files|" + System.IO.Path.GetTempPath().TrimEnd('\\') + "|temp"); break;
            case "clips": Navigate("found:Copied files|-|clips"); break;
            case "startup": Navigate("tools:startup"); break;
            case "apps": Native.ShellRun("ms-settings:appsfeatures"); break;
            case "defaults": Native.ShellRun("ms-settings:defaultapps"); break;
            case "fonts": Navigate(Environment.GetFolderPath(Environment.SpecialFolder.Fonts)); break;
            case "screenshot": Screenshot(here); break;
            case "rules": Navigate("tools:rules"); break;
            case "backup": Navigate("tools:backup"); break;
            case "server": _ = AddServer(); break;
            case "mapdrive": _ = Task.Run(() => WNetConnectionDialog(_hwnd, 1 /* RESOURCETYPE_DISK */)).ContinueWith((_) => DispatcherQueue.TryEnqueue(BuildSidebar)); break;
            case "compare":
                var a = new TextBox { Header = "Left folder", Text = _toolsFrom ?? "", MinWidth = 420 };
                var b = new TextBox { Header = "Right folder", Text = _tabs.Select((t) => t.Location).FirstOrDefault((l) => !Special(l) && l != _toolsFrom) ?? "" };
                if (await Dialog("Compare two folders", new StackPanel { Spacing = 8, Children = { a, b } }, "Compare").ShowAsync() != ContentDialogResult.Primary) return;
                if (!Directory.Exists(a.Text.Trim()) || !Directory.Exists(b.Text.Trim())) { await Message("Can't find a folder", "Type or paste two folder paths that exist."); return; }
                Navigate("tools:compare|" + a.Text.Trim() + "|" + b.Text.Trim());
                break;
        }
    }
    /// <summary>The finders' results, shown like a folder (so they can be opened, deleted, copied).</summary>
    private async Task ShowFound(string loc)
    {
        var parts = loc.Split('|'); var folder = parts[1]; var kind = parts[2];
        Folder.Visibility = Visibility.Visible; Folder.ShowSearching();
        List<FileItem> found;
        try
        {
            found = kind == "clips" ? Clips.SelectMany((c) => c).Distinct(StringComparer.OrdinalIgnoreCase).Where(UndoLog.Exists).Select(ItemFor).ToList()
                : await Task.Run(() => kind switch
                {
                    "big" => Space.Large(folder, 500L << 20, CancellationToken.None),
                    "empty" => Space.EmptyFolders(folder, CancellationToken.None),
                    "old" => Space.Older(folder, TimeSpan.FromDays(90), CancellationToken.None),
                    _ => Space.TempFiles(CancellationToken.None),
                });
        }
        catch (Exception e) { await Message("Couldn't look here", e.Message); return; }
        if (_tab.Location != loc) return;
        Folder.ShowResults(found, found.Count == 0 ? "Nothing found. All tidy!" : null);
        if (found.Count > 0) ShowNote($"{found.Count:N0} found · {Format.Size(found.Sum((f) => f.Size))}. Select them and press Delete to send them to the Recycle Bin.");
        UpdateStatus();
    }
    /// <summary>Recently copied files (Ctrl+C or Copy in Omebly), newest first, to paste again.</summary>
    public static readonly List<List<string>> Clips = new();
    private static void RememberClip(IReadOnlyList<FileItem> s) { lock (Clips) { Clips.Insert(0, s.Select((x) => x.Path).ToList()); while (Clips.Count > 20) Clips.RemoveAt(Clips.Count - 1); } }
    private void Screenshot(string folder)
    {
        // Windows' own snipping; when the picture arrives on the clipboard it is saved here.
        void OnChange(object? s, object e)
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)) return;
            Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged -= OnChange;
            _ = Save(view);
        }
        async Task Save(Windows.ApplicationModel.DataTransfer.DataPackageView view)
        {
            try
            {
                var bmp = await view.GetBitmapAsync();
                using var src = await bmp.OpenReadAsync();
                var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(src);
                var path = System.IO.Path.Combine(folder, FileOps.FreeName(folder, "Screenshot " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"), ".png"));
                using (var outS = await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(path, Windows.Storage.FileAccessMode.ReadWrite, Windows.Storage.StorageOpenOptions.None, Windows.Storage.Streams.FileOpenDisposition.CreateNew))
                {
                    var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateForTranscodingAsync(outS, dec);
                    await enc.FlushAsync();
                }
                UndoLog.Made(path, "Screenshot");
                Navigate(path);
            }
            catch (Exception ex) { await Message("Couldn't save the screenshot", ex.Message); }
        }
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnChange;
        Native.ShellRun("ms-screenclip:");
    }

    private void AddPower(MenuFlyout m, FileItem item, IReadOnlyList<FileItem> sel, string? here)
    {
        var t = new MenuFlyoutSubItem { Text = "More tools", Icon = new FontIcon { Glyph = "\uE90F" } };
        if (sel.Count > 1) t.Items.Add(Item($"Rename {sel.Count} items…", null, () => _ = BulkRenameAsync(sel), "F2"));
        if (sel.Count == 1 && !item.IsFolder)
        {
            t.Items.Add(Item("What is using this file?", null, () => _ = WhoUses(item)));
            t.Items.Add(Item("Checksum (SHA-256, SHA-1, MD5)…", null, () => _ = Checksum(item)));
            if (Extras.IsPicture(item)) t.Items.Add(Item("Copy without location data (for sharing)", null, () => RunJob("Removing location data", "Done: copy without location", (p, ct) => new List<string> { PhotoPrivacy.RemoveLocation(item.Path).GetAwaiter().GetResult() })));
            if (item.Extension is ".ttf" or ".otf") t.Items.Add(Item("Install this font (for you)", null, async () => { try { WinControl.InstallFont(item.Path, System.IO.Path.GetFileNameWithoutExtension(item.Name)); ShowNote("Font installed"); } catch (Exception e) { await Message("Couldn't install the font", e.Message); } }));
        }
        if (sel.Count == 1 && here != null) t.Items.Add(Item("Make a link to it here", null, async () => { try { var l = Safety.MakeLink(item.Path, here); UndoLog.Made(l, "New link"); } catch (Exception e) { await Message("Couldn't make a link", e.Message); } }));
        if (sel.Count == 1) t.Items.Add(Item("Who can open it (permissions)…", null, () => _ = Permissions(item)));
        t.Items.Add(new MenuFlyoutSeparator());
        if (sel.Count == 1 && Locker.IsLocked(item.Path)) t.Items.Add(Item("Unlock with the password…", null, async () => { var pw = await AskPassword(item.Name); if (pw != null) RunJob("Unlocking", "Done: unlocked", (p, ct) => new List<string> { Locker.Unlock(item.Path, pw) }); }));
        else t.Items.Add(Item("Lock with a password…", null, async () => { var pw = await AskNewPassword("Lock with a password"); if (pw == null) return; var paths = sel.Select((s) => s.Path).ToList(); RunJob("Locking", "Done: locked", (p, ct) => { var made = paths.Select((x) => Locker.Lock(x, pw)).ToList(); return made; }); }));
        bool hidden = sel.All((s) => s.IsHidden);
        t.Items.Add(Item(hidden ? "Unhide" : "Hide", null, () => { foreach (var s in sel) { try { var a = File.GetAttributes(s.Path); File.SetAttributes(s.Path, hidden ? a & ~FileAttributes.Hidden : a | FileAttributes.Hidden); } catch { } } Show(); }));
        t.Items.Add(Item("Scan with Windows Security", null, () => RunJob("Scanning with Windows Security", "Scan finished", (p, ct) => { foreach (var s in sel) WinControl.Scan(s.Path); DispatcherQueue.TryEnqueue(() => _ = Message("Windows Security", WinControl.LastScan)); return new List<string>(); })));
        t.Items.Add(new MenuFlyoutSeparator());
        t.Items.Add(Item("Shred (delete for good, can't be recovered)…", null, () => _ = Shred(sel)));
        if (item.IsFolder && sel.Count == 1)
        {
            t.Items.Add(new MenuFlyoutSeparator());
            t.Items.Add(Item("Find duplicates in this folder", null, () => { _toolsFrom = item.Path; Navigate("tools:dupes|" + item.Path); }));
            t.Items.Add(Item("Disk space map of this folder", null, () => { _toolsFrom = item.Path; Navigate("tools:map|" + item.Path); }));
        }
        m.Items.Add(t);
    }

    private async Task BulkRenameAsync(IReadOnlyList<FileItem> items)
    {
        var rule = new RenameRule();
        TextBox Box(string header, string text = "", string ph = "") => new() { Header = header, Text = text, PlaceholderText = ph, MinWidth = 220 };
        var pattern = Box("New name", "", "e.g. Holiday {n}  ·  {date} {name}  ·  leave empty to keep names");
        var find = Box("Find"); var repl = Box("Replace with");
        var regex = new CheckBox { Content = "Find is a pattern (regular expression)" };
        var prefix = Box("Add before"); var suffix = Box("Add after");
        var start = new NumberBox { Header = "Numbers start at", Value = 1, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var digits = new NumberBox { Header = "Digits", Value = 2, Minimum = 1, Maximum = 6, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var caseBox = new ComboBox { Header = "Letters", Items = { "Keep as they are", "lowercase", "UPPERCASE", "Title Case" }, SelectedIndex = 0 };
        var keepExt = new CheckBox { Content = "Keep file extensions", IsChecked = true };
        var preview = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.None };
        var summary = new TextBlock { FontSize = 12, Foreground = Theme.Get("OxMuted") };
        List<(string from, string to, string? problem)> plan = new();
        void Update()
        {
            rule.Pattern = pattern.Text; rule.Find = find.Text; rule.Replace = repl.Text; rule.Regex = regex.IsChecked == true; rule.Prefix = prefix.Text; rule.Suffix = suffix.Text;
            rule.Start = double.IsNaN(start.Value) ? 1 : (int)start.Value; rule.Digits = double.IsNaN(digits.Value) ? 2 : (int)digits.Value; rule.KeepExtension = keepExt.IsChecked == true;
            rule.Case = caseBox.SelectedIndex switch { 1 => "lower", 2 => "upper", 3 => "title", _ => "keep" };
            plan = BulkRename.Preview(items, rule);
            preview.ItemsSource = plan.Take(300).Select((p) => p.problem != null ? $"{p.from}  →  {p.to}   ⚠ {p.problem}" : p.from == p.to ? $"{p.from}  (no change)" : $"{p.from}  →  {p.to}").ToList();
            var bad = plan.Count((p) => p.problem != null); var change = plan.Count((p) => p.problem == null && p.from != p.to);
            summary.Text = $"{change:N0} will be renamed{(bad > 0 ? $", {bad:N0} can't be (marked ⚠)" : "")}. {{n}} = number, {{name}} = old name, {{date}} = date taken or changed, {{folder}} = folder name.";
        }
        foreach (var b in new[] { pattern, find, repl, prefix, suffix }) b.TextChanged += (_, _) => Update();
        regex.Click += (_, _) => Update(); keepExt.Click += (_, _) => Update(); caseBox.SelectionChanged += (_, _) => Update();
        start.ValueChanged += (_, _) => Update(); digits.ValueChanged += (_, _) => Update();
        Update();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { find, repl } };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { prefix, suffix } };
        var row3 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, digits, caseBox } };
        var panel = new StackPanel { Spacing = 8, Children = { pattern, row1, regex, row2, row3, keepExt, summary, preview } };
        if (await Dialog($"Rename {items.Count:N0} items", new ScrollViewer { Content = panel, MaxHeight = 560 }, "Rename").ShowAsync() != ContentDialogResult.Primary) return;
        var done = await Task.Run(() => BulkRename.Apply(items, plan));
        if (done.Count > 0)
            UndoLog.Add(new UndoStep
            {
                What = $"Rename {done.Count:N0} items",
                Undo = () => Task.Run(() => { foreach (var (from, to) in done) { try { if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from); } catch { } } return true; }),
                Redo = () => Task.Run(() => { foreach (var (from, to) in done) { try { if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to); } catch { } } return true; }),
            });
        ShowNote($"Renamed {done.Count:N0} items");
    }
    private async Task WhoUses(FileItem item)
    {
        var who = await Task.Run(() => FileLocks.WhoUses(item.Path));
        if (who.Count == 0) { await Message("Nothing is using it", $"No app has \"{item.Name}\" open right now."); return; }
        var list = new StackPanel { Spacing = 6 };
        foreach (var (app, pid) in who)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = $"{app} (process {pid})", VerticalAlignment = VerticalAlignment.Center });
            var close = new Button { Content = "Close this app" };
            var id = pid;
            close.Click += async (_, _) =>
            {
                try { var pr = System.Diagnostics.Process.GetProcessById(id); if (!pr.CloseMainWindow()) { if (await Confirm("Force it to close?", $"{app} didn't close by itself. Forcing it may lose unsaved work in it.", "Force close")) pr.Kill(); } close.IsEnabled = false; close.Content = "Closed"; }
                catch (Exception e) { close.Content = e.Message; }
            };
            row.Children.Add(close);
            list.Children.Add(row);
        }
        await Dialog($"Using \"{item.Name}\"", list, "OK", "Close").ShowAsync();
    }
    private async Task Checksum(FileItem item)
    {
        var text = new TextBlock { Text = "Working it out…", IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var compare = new TextBox { Header = "Paste the checksum you were given to compare", MinWidth = 440 };
        var verdict = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var d = Dialog($"Checksum of {item.Name}", new StackPanel { Spacing = 10, Children = { text, compare, verdict } }, "Copy SHA-256", "Close");
        string sha256 = "", sha1 = "", md5 = "";
        _ = Task.Run(() => { sha256 = Duplicates.Hash(item.Path, "SHA256"); sha1 = Duplicates.Hash(item.Path, "SHA1"); md5 = Duplicates.Hash(item.Path, "MD5"); }).ContinueWith((_) => DispatcherQueue.TryEnqueue(() => text.Text = $"SHA-256  {sha256}\nSHA-1    {sha1}\nMD5      {md5}"));
        compare.TextChanged += (_, _) => { var c = compare.Text.Trim().Replace(" ", ""); verdict.Text = c.Length == 0 ? "" : new[] { sha256, sha1, md5 }.Any((h) => h.Length > 0 && h.Equals(c, StringComparison.OrdinalIgnoreCase)) ? "✓ It matches: the file is exactly right." : "✗ It doesn't match: the file is different or damaged."; verdict.Foreground = verdict.Text.StartsWith("✓") ? Theme.Get("OxAcc") : Theme.Get("OxWarn"); };
        if (await d.ShowAsync() == ContentDialogResult.Primary && sha256.Length > 0) { var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(sha256); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); ShowNote("SHA-256 copied"); }
    }
    private async Task Permissions(FileItem item)
    {
        try
        {
            var (owner, rules) = await Task.Run(() => Safety.Permissions(item.Path));
            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = $"Owner: {owner}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            foreach (var (who, can) in rules) sp.Children.Add(new TextBlock { Text = $"{who}: {can}", TextWrapping = TextWrapping.Wrap });
            if (await Dialog($"Who can open \"{item.Name}\"", new ScrollViewer { Content = sp, MaxHeight = 420 }, "Change in Windows…", "Close").ShowAsync() == ContentDialogResult.Primary)
                Native.ShellRun(item.Path, "properties", _hwnd);
        }
        catch (Exception e) { await Message("Couldn't read the permissions", e.Message); }
    }
    private async Task Shred(IReadOnlyList<FileItem> items)
    {
        var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count} items";
        if (!await Confirm("Shred for good?", $"{what} will be overwritten and deleted. They will NOT go to the Recycle Bin and can't be brought back by any tool.", "Shred")) return;
        if (!await Confirm("Are you sure?", "This is the second check: shredding can't be undone.", "Yes, shred")) return;
        var paths = items.Select((i) => i.Path).ToList();
        RunJob("Shredding", "Done: shredded", (p, ct) => { foreach (var x in paths) { p.Current = System.IO.Path.GetFileName(x); Safety.Shred(x); p.Done++; p.Fraction = (double)p.Done / paths.Count; } return new List<string>(); });
    }

    // ---------- Stage 6: colours, tags, notes, favourites, shelf, templates, tidy-up, workspaces ----------
    private static FileItem ItemFor(string p)
    {
        if (Directory.Exists(p)) { var d = new DirectoryInfo(p); return new FileItem { Name = d.Name, Path = d.FullName, IsFolder = true, Modified = d.LastWriteTime, Attributes = d.Attributes }; }
        var f = new FileInfo(p); return new FileItem { Name = f.Name, Path = f.FullName, Size = f.Exists ? f.Length : 0, Modified = f.Exists ? f.LastWriteTime : default, Attributes = f.Exists ? f.Attributes : default };
    }
    private void AddOrganize(MenuFlyout m, FileItem item, IReadOnlyList<FileItem> sel)
    {
        if (sel.All((s) => s.IsFolder))
        {
            var col = new MenuFlyoutSubItem { Text = "Folder colour", Icon = new FontIcon { Glyph = "\uE790" } };
            foreach (var c in Settings.PinColors.Concat(new[] { "#FF5A5F" }).Distinct())
            {
                var mi = new MenuFlyoutItem { Text = ColorName(c), Icon = new FontIcon { Glyph = "\uEA3B", Foreground = Ui.Brush(c) } };
                mi.Click += async (_, _) => { foreach (var s in sel) await FolderLook.SetColor(s.Path, c); Folder.Redraw(); BuildSidebar(); };
                col.Items.Add(mi);
            }
            col.Items.Add(new MenuFlyoutSeparator());
            col.Items.Add(Item("No colour", null, async () => { foreach (var s in sel) await FolderLook.SetColor(s.Path, null); Folder.Redraw(); BuildSidebar(); }));
            m.Items.Add(col);
        }
        var tag = new MenuFlyoutSubItem { Text = "Tag", Icon = new FontIcon { Glyph = "\uE8EC" } };
        foreach (var (c, name) in Library.TagColors)
        {
            bool on = sel.All((s) => s.TagColors.Contains(c));
            var mi = new ToggleMenuFlyoutItem { Text = name, IsChecked = on, Icon = new FontIcon { Glyph = "\uEA3B", Foreground = Ui.Brush(c) } };
            mi.Click += (_, _) =>
            {
                foreach (var s in sel)
                {
                    var list = Library.Current.Tags.TryGetValue(s.Path, out var l) ? l : (Library.Current.Tags[s.Path] = new List<string>());
                    if (on) list.Remove(c); else if (!list.Contains(c)) list.Add(c);
                    if (list.Count == 0) Library.Current.Tags.Remove(s.Path);
                }
                Library.Save(); Folder.Redraw(); BuildSidebar();
            };
            tag.Items.Add(mi);
        }
        m.Items.Add(tag);
        if (sel.Count == 1) m.Items.Add(Item(Library.Current.NoteOf(item.Path) == null ? "Add a note…" : "Edit the note…", "\uE70B", () => _ = EditNote(item)));
        bool fav = sel.All((s) => Library.Current.Favorites.Contains(s.Path, StringComparer.OrdinalIgnoreCase));
        m.Items.Add(Item(fav ? "Remove from favourites" : "Add to favourites", fav ? "\uE8D9" : "\uE734", () =>
        {
            foreach (var s in sel) { if (fav) Library.Current.Favorites.RemoveAll((f) => f.Equals(s.Path, StringComparison.OrdinalIgnoreCase)); else if (!Library.Current.Favorites.Contains(s.Path, StringComparer.OrdinalIgnoreCase)) Library.Current.Favorites.Add(s.Path); }
            Library.Save(); BuildSidebar();
        }));
        bool shelved = sel.All((s) => Library.Current.Shelf.Contains(s.Path, StringComparer.OrdinalIgnoreCase));
        m.Items.Add(Item(shelved ? "Take off the shelf" : "Add to shelf", "\uE7B8", () =>
        {
            foreach (var s in sel) { if (shelved) Library.Current.Shelf.RemoveAll((f) => f.Equals(s.Path, StringComparison.OrdinalIgnoreCase)); else if (!Library.Current.Shelf.Contains(s.Path, StringComparer.OrdinalIgnoreCase)) Library.Current.Shelf.Add(s.Path); }
            Library.Save(); BuildSidebar(); if (_tab.Location == "shelf") Show();
            if (!shelved) ShowNote($"On the shelf: {Library.Current.Shelf.Count} items");
        }));
    }
    private void AddFolderOrganize(MenuFlyout m, string here)
    {
        var tpl = new MenuFlyoutSubItem { Text = "New from template", Icon = new FontIcon { Glyph = "\uE8F1" } };
        foreach (var t in Library.BuiltIn.Concat(Library.Current.Templates)) tpl.Items.Add(Item($"{t.Name} ({string.Join(", ", t.Folders.Take(4))})", null, () => _ = FromTemplate(t, here)));
        tpl.Items.Add(new MenuFlyoutSeparator());
        tpl.Items.Add(Item("Save this folder's folders as a template…", null, () => _ = SaveTemplate(here)));
        m.Items.Add(tpl);
        m.Items.Add(Item("Tidy up this folder…", "\uE74C", () => _ = TidyUp(here)));
        if (Library.Current.Shelf.Count > 0)
        {
            var shelf = new MenuFlyoutSubItem { Text = $"Shelf ({Library.Current.Shelf.Count})", Icon = new FontIcon { Glyph = "\uE7B8" } };
            shelf.Items.Add(Item("Copy the shelf here", null, () => { StartCopy(Library.Current.Shelf.Where(UndoLog.Exists).ToList(), here, false); }));
            shelf.Items.Add(Item("Move the shelf here", null, () => { StartCopy(Library.Current.Shelf.Where(UndoLog.Exists).ToList(), here, true); Library.Current.Shelf.Clear(); Library.Save(); BuildSidebar(); }));
            shelf.Items.Add(Item("Zip the shelf here", null, () => { var files = Library.Current.Shelf.Where(UndoLog.Exists).ToList(); if (files.Count > 0) CompressTo(files, System.IO.Path.Combine(here, FileOps.FreeName(here, "Shelf", ".zip"))); }));
            shelf.Items.Add(Item("Empty the shelf", null, () => { Library.Current.Shelf.Clear(); Library.Save(); BuildSidebar(); }));
            m.Items.Add(shelf);
        }
    }
    private void CompressTo(List<string> paths, string target)
    {
        var p = new ArchiveProgress(); var card = new TaskCard($"Compressing to {System.IO.Path.GetFileName(target)}");
        card.ShowFile += (f) => Navigate(f); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
        _ = card.Run((ct) => { Archives.CreateZip(paths, target, null, p, ct); return Task.FromResult(new List<string> { target }); }, () => (p.Total == 0 ? 0 : (double)p.Done / p.Total, p.Current), $"Done: {System.IO.Path.GetFileName(target)}");
    }
    private async Task EditNote(FileItem item)
    {
        var box = new TextBox { Text = Library.Current.NoteOf(item.Path) ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 380, MinHeight = 120, PlaceholderText = "A note for yourself (shown on the file and in the details pane)" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Note");
        var r = await Dialog($"Note on {item.Name}", box, "Save", "Cancel").ShowAsync();
        if (r != ContentDialogResult.Primary) return;
        if (box.Text.Trim().Length == 0) Library.Current.Notes.Remove(item.Path); else Library.Current.Notes[item.Path] = box.Text.Trim();
        Library.Save(); Folder.Redraw();
    }
    private async Task FromTemplate(FolderTemplate t, string here)
    {
        var name = FileOps.FreeName(here, t.Name);
        var root = System.IO.Path.Combine(here, name);
        try { Directory.CreateDirectory(root); foreach (var f in t.Folders) if (FileOps.NameProblem(f) == null) Directory.CreateDirectory(System.IO.Path.Combine(root, f)); }
        catch (Exception e) { await Message("Couldn't make the folders", e.Message); return; }
        UndoLog.Made(root, $"New “{name}” from a template");
        await Task.Delay(300);
        await RenameAsync(new FileItem { Name = name, Path = root, IsFolder = true });
    }
    private async Task SaveTemplate(string here)
    {
        var subs = Directory.EnumerateDirectories(here).Select((d) => System.IO.Path.GetFileName(d)).Take(30).ToList();
        if (subs.Count == 0) { await Message("No folders here", "A template keeps the folders inside this one. Make the folders first, then save them as a template."); return; }
        var box = new TextBox { Text = TabState.NameOf(here), MinWidth = 320, Header = $"Template name ({string.Join(", ", subs.Take(5))}{(subs.Count > 5 ? "…" : "")})" };
        if (await Dialog("Save as a template", box, "Save").ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
        Library.Current.Templates.RemoveAll((t) => t.Name.Equals(box.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        Library.Current.Templates.Add(new FolderTemplate { Name = box.Text.Trim(), Folders = subs });
        Library.Save(); ShowNote("Template saved");
    }
    /// <summary>One-click tidy-up: loose files go into Pictures, Videos, Music, Documents, Archives, Apps and Other folders here.</summary>
    private async Task TidyUp(string here, bool ask = true)
    {
        var files = await Task.Run(() => new DirectoryInfo(here).EnumerateFiles().Where((f) => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !f.Name.EndsWith(".tmp") && !f.Name.EndsWith(".crdownload") && !f.Name.EndsWith(".part")).ToList());
        string Group(FileInfo f) => FileKinds.Of(f.Extension.ToLowerInvariant()) switch
        {
            FileKind.Picture => "Pictures", FileKind.Video => "Videos", FileKind.Music => "Music",
            FileKind.Pdf or FileKind.Document or FileKind.Sheet or FileKind.Slides or FileKind.Text => "Documents",
            FileKind.Archive => "Archives", FileKind.App => "Apps", _ => "Other",
        };
        var groups = files.GroupBy(Group).OrderByDescending((g) => g.Count()).ToList();
        if (groups.Count == 0) { if (ask) await Message("Already tidy", "There are no loose files here."); return; }
        var text = string.Join("\n", groups.Select((g) => $"{g.Key}: {g.Count():N0} files"));
        if (ask && !await Confirm("Tidy up this folder?", $"Loose files will go into folders here:\n\n{text}\n\nFolders already here stay as they are. You can undo it with Ctrl+Z.", "Tidy up")) return;
        foreach (var g in groups)
        {
            var target = System.IO.Path.Combine(here, g.Key);
            Directory.CreateDirectory(target);
            StartCopy(g.Select((f) => f.FullName).ToList(), target, true);
        }
    }

    // ---------- Command bar (Ctrl+K) ----------
    private async Task CommandBar()
    {
        var cmds = new List<(string name, Action run)>
        {
            ("New folder", () => _ = NewFolderAsync()), ("New tab", () => AddTab("home")), ("New window", () => new MainWindow().Activate()),
            ("Go to Home", () => Navigate("home")), ("Go to the Recycle Bin", () => Navigate("recycle-bin")), ("Go to the shelf", () => Navigate("shelf")), ("Tools (duplicates, space map, compare, rules, backup)", OpenTools),
            ("Show hidden files on/off", () => { Settings.Current.ShowHidden = !Settings.Current.ShowHidden; HiddenToggle.IsChecked = Settings.Current.ShowHidden; Settings.Save(); Show(); }),
            ("Preview pane on/off", () => TogglePane("preview")), ("Details pane on/off", () => TogglePane("details")),
            ("Undo", () => _ = UndoAsync(false)), ("Redo", () => _ = UndoAsync(true)),
            ("Select all", Folder.SelectAll), ("Invert selection", Folder.InvertSelection),
            ("Open terminal here", () => Terminal(Special(_tab.Location) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _tab.Location)),
            ("Folder options", () => FolderOptions_Click(this, new RoutedEventArgs())), ("Split view", SplitView),
            ("Theme: Omebly Glass", () => { ApplyTheme("glass"); Settings.Save(); }), ("Theme: Windows Light", () => { ApplyTheme("light"); Settings.Save(); }),
        };
        foreach (var v in FolderView.Views) { var k = v.Key; cmds.Add(("View: " + v.Label, () => UseView(k))); }
        foreach (var w in Library.Current.Workspaces) { var ws = w; cmds.Add(("Workspace: " + w.Name, () => OpenWorkspace(ws))); }
        var sel = Folder.Selected;
        if (sel.Count > 0 && !Special(_tab.Location))
        {
            var paths = sel.Select((s) => s.Path).ToList();
            cmds.Add(("Zip the selected files", () => Compress(paths, ".zip", null)));
            foreach (var o in Converters.OptionsFor(sel)) { var oo = o; cmds.Add(("Convert to " + o.Label, () => Convert(oo, sel))); }
            cmds.Add(("Copy as path", () => CopyPaths(sel))); cmds.Add(("Add to shelf", () => { foreach (var s in paths) if (!Library.Current.Shelf.Contains(s)) Library.Current.Shelf.Add(s); Library.Save(); BuildSidebar(); }));
        }
        foreach (var p in Settings.Current.Pinned) { var pp = p.Path; cmds.Add(("Open " + TabState.NameOf(pp), () => Navigate(pp))); }
        var box = new TextBox { PlaceholderText = "Type a command, a folder path, or what you want to do", MinWidth = 460 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Command");
        var list = new ListView { MaxHeight = 320, SelectionMode = ListViewSelectionMode.Single };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "Commands");
        void Fill() { var q = box.Text.Trim(); var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries); list.ItemsSource = cmds.Where((c) => words.All((w) => c.name.Contains(w, StringComparison.OrdinalIgnoreCase))).Select((c) => c.name).Take(40).ToList(); if (list.Items.Count > 0) list.SelectedIndex = 0; }
        Fill();
        var d = new ContentDialog { Title = "What do you want to do?", Content = new StackPanel { Spacing = 8, Children = { box, list } }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot, RequestedTheme = Theme.Current };
        Action? chosen = null;
        void Pick() { var t = list.SelectedItem as string; var q = box.Text.Trim(); if (t != null) chosen = cmds.First((c) => c.name == t).run; else if (Directory.Exists(Environment.ExpandEnvironmentVariables(q))) chosen = () => Navigate(q); d.Hide(); }
        box.TextChanged += (_, _) => Fill();
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Pick(); } else if (e.Key == VirtualKey.Down && list.Items.Count > 0) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; } else if (e.Key == VirtualKey.Up && list.Items.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; } };
        list.IsItemClickEnabled = true; list.ItemClick += (_, e) => { list.SelectedItem = e.ClickedItem; Pick(); };
        box.Loaded += (_, _) => box.Focus(FocusState.Keyboard);
        try { await d.ShowAsync(); } catch { return; }
        chosen?.Invoke();
    }

    // ---------- Workspaces and split view ----------
    private async Task SaveWorkspace()
    {
        var box = new TextBox { PlaceholderText = "Name, for example Client work", MinWidth = 320, Header = $"{_tabs.Count} tabs: {string.Join(", ", _tabs.Select((t) => t.Title).Take(5))}" };
        if (await Dialog("Save these tabs as a workspace", box, "Save").ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0) return;
        Library.Current.Workspaces.RemoveAll((w) => w.Name.Equals(box.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        Library.Current.Workspaces.Add(new Workspace { Name = box.Text.Trim(), Tabs = _tabs.Select((t) => t.Location).Where((l) => !Special(l) || l == "home").ToList() });
        Library.Save(); ShowNote("Workspace saved");
    }
    private void OpenWorkspace(Workspace w)
    {
        foreach (var t in w.Tabs.Where((t) => t == "home" || Directory.Exists(t))) AddTab(t);
    }
    /// <summary>Two folders side by side: this window takes the left half of the screen, a new one the right half.</summary>
    private void SplitView()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int half = area.Width / 2;
        if (AppWindow.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Maximized) op.Restore();
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X, area.Y, half, area.Height));
        var other = new MainWindow(_tab.Location is "home" ? null : _tab.Location);
        other.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + half, area.Y, area.Width - half, area.Height));
        other.Activate();
    }

    // ---------- Stage 5: zip, unzip, convert, PDF tools ----------
    private void AddStage5(MenuFlyout m, FileItem item, IReadOnlyList<FileItem> sel, string? here)
    {
        var paths = sel.Select((s) => s.Path).ToList();
        if (sel.Count == 1 && !item.IsFolder && Archives.IsArchive(item.Path))
        {
            m.Items.Add(Item("Extract here", "\uE896", () => Extract(item.Path, null, System.IO.Path.GetDirectoryName(item.Path)!, null)));
            m.Items.Add(Item($"Extract to “{System.IO.Path.GetFileName(Archives.FolderFor(item.Path))}\\”", "\uE8B7", () => Extract(item.Path, null, Archives.FolderFor(item.Path), null)));
            m.Items.Add(Item("Open as a folder", "\uE7B8", () => Navigate("archive:" + item.Path + "|")));
        }
        var zip = new MenuFlyoutSubItem { Text = "Compress to", Icon = new FontIcon { Glyph = "\uE7B8" } };
        zip.Items.Add(Item("ZIP file", null, () => Compress(paths, ".zip", null)));
        zip.Items.Add(Item("ZIP with a password…", null, async () => { var pw = await AskNewPassword("ZIP with a password"); if (pw != null) Compress(paths, ".zip", pw); }));
        zip.Items.Add(Item("TAR file", null, () => Compress(paths, ".tar", null)));
        zip.Items.Add(Item("TAR.GZ file", null, () => Compress(paths, ".tar.gz", null)));
        m.Items.Add(zip);
        var conv = Converters.OptionsFor(sel);
        if (conv.Count > 0)
        {
            var c = new MenuFlyoutSubItem { Text = "Convert to", Icon = new FontIcon { Glyph = "\uE8AB" } };
            foreach (var o in conv) c.Items.Add(Item(o.Label, null, () => Convert(o, sel)));
            m.Items.Add(c);
        }
        // The Omebly Tools PDF tools (the same as on omebly.com/tools), for the files they take.
        var web = WebTools.For(sel).ToList();
        if (web.Count > 0)
        {
            var pdf = new MenuFlyoutSubItem { Text = "PDF tools", Icon = new FontIcon { Glyph = "\uEA90" } };
            foreach (var g in web.GroupBy((t) => t.Group))
            {
                if (web.Count > 10)
                {
                    var sub = new MenuFlyoutSubItem { Text = g.Key };
                    foreach (var t in g) { var tool = t; sub.Items.Add(Item(t.Name, null, () => OpenWebTool(tool, sel))); }
                    pdf.Items.Add(sub);
                }
                else foreach (var t in g) { var tool = t; pdf.Items.Add(Item(t.Name, null, () => OpenWebTool(tool, sel))); }
            }
            m.Items.Add(pdf);
        }
    }

    private void OpenWebTool(WebTool tool, IReadOnlyList<FileItem> files)
    {
        if (!File.Exists(Path.Combine(WebTools.Root, "site", "tools", tool.Slug, "index.html"))) { _ = Message("This tool isn't in this copy", "The Omebly Tools files are missing from this build of Omebly Explorer."); return; }
        var w = new ToolWindow(tool, files);
        // In the middle of the screen this window is on (never off the edge).
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = w.AppWindow.Size;
        int ww = Math.Min(size.Width, area.Width - 40), wh = Math.Min(size.Height, area.Height - 40);
        w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - ww) / 2, area.Y + (area.Height - wh) / 2, ww, wh));
        w.Saved += (f) => ShowNote($"Saved: {Path.GetFileName(f)}");
        w.Activate();
    }

    private void RunJob(string title, string done, Func<JobProgress, CancellationToken, List<string>> work)
    {
        var p = new JobProgress();
        var card = new TaskCard(title);
        card.ShowFile += (f) => Navigate(f);
        card.Finished += (c) => Cards.Children.Remove(c);
        Cards.Children.Insert(0, card);
        _ = card.Run((ct) => Task.FromResult(work(p, ct)), () => (p.Fraction, p.Current), done);
    }
    private void Convert(ConvertOption o, IReadOnlyList<FileItem> files)
    {
        var p = new JobProgress();
        var card = new TaskCard(files.Count == 1 ? $"Converting {files[0].Name}" : $"Converting {files.Count} files");
        card.ShowFile += (f) => Navigate(f);
        card.Finished += (c) => Cards.Children.Remove(c);
        Cards.Children.Insert(0, card);
        _ = card.Run((ct) => Converters.Run(o, files, p, ct), () => (p.Fraction, p.Current), files.Count == 1 ? $"Done: {o.Label}" : $"Done: {files.Count} files converted");
    }
    private void Compress(List<string> paths, string ext, string? password)
    {
        var target = Archives.NameFor(paths, ext);
        var p = new ArchiveProgress();
        var card = new TaskCard($"Compressing to {System.IO.Path.GetFileName(target)}");
        card.ShowFile += (f) => Navigate(f);
        card.Finished += (c) => Cards.Children.Remove(c);
        Cards.Children.Insert(0, card);
        _ = card.Run((ct) =>
        {
            if (ext == ".zip") Archives.CreateZip(paths, target, password, p, ct);
            else Archives.CreateTar(paths, target, ext == ".tar.gz", p, ct);
            return Task.FromResult(new List<string> { target });
        }, () => (p.Total == 0 ? 0 : (double)p.Done / p.Total, p.Current), password != null ? "Done: password-protected ZIP made" : $"Done: {System.IO.Path.GetFileName(target)}");
    }
    private void Extract(string archive, IReadOnlyCollection<string>? only, string target, string? knownPassword)
    {
        var p = new ArchiveProgress();
        var card = new TaskCard($"Extracting {System.IO.Path.GetFileName(archive)}");
        card.ShowFile += (f) => Navigate(f);
        card.Finished += (c) => Cards.Children.Remove(c);
        Cards.Children.Insert(0, card);
        _ = card.Run(async (ct) =>
        {
            var pw = knownPassword;
            while (true)
            {
                try { return Archives.Extract(archive, target, pw, only, p, ct); }
                catch (NeedsPassword)
                {
                    var t = new TaskCompletionSource<string?>();
                    DispatcherQueue.TryEnqueue(async () => t.SetResult(await AskPassword(System.IO.Path.GetFileName(archive))));
                    pw = await t.Task;
                    if (pw == null) throw new OperationCanceledException();
                }
            }
        }, () => (p.Total == 0 ? 0 : (double)p.Done / p.Total, p.Current), $"Done: extracted {System.IO.Path.GetFileName(archive)}");
    }
    private async Task<string?> AskPassword(string name)
    {
        var box = new PasswordBox { PlaceholderText = "Password", MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Password");
        var d = Dialog($"Password for {name}", box, "OK");
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; d.Hide(); box.Tag = "ok"; } };
        try { var r = await d.ShowAsync(); return r == ContentDialogResult.Primary || box.Tag as string == "ok" ? box.Password : null; } catch { return null; }
    }
    private async Task<string?> AskNewPassword(string title)
    {
        var a = new PasswordBox { PlaceholderText = "Password", MinWidth = 320 }; var b = new PasswordBox { PlaceholderText = "Type it again" };
        var err = new TextBlock { Foreground = Theme.Get("OxWarn"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(a, "Password"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Password again");
        var d = Dialog(title, new StackPanel { Spacing = 8, Children = { a, b, err, new TextBlock { Text = "Keep it safe: without it, nobody (not even Omebly) can open the file.", FontSize = 12, Foreground = Theme.Get("OxMuted"), TextWrapping = TextWrapping.Wrap } } }, "OK");
        d.PrimaryButtonClick += (_, e) => { if (a.Password.Length < 4) { err.Text = "Use at least 4 characters."; err.Visibility = Visibility.Visible; e.Cancel = true; } else if (a.Password != b.Password) { err.Text = "The two don't match."; err.Visibility = Visibility.Visible; e.Cancel = true; } };
        try { return await d.ShowAsync() == ContentDialogResult.Primary ? a.Password : null; } catch { return null; }
    }

    // ---------- Search inside files (Windows Search index) ----------
    private async Task SearchInside(string folder, string query)
    {
        Folder.Visibility = Visibility.Visible; Folder.ShowSearching();
        var found = await Task.Run(() =>
        {
            if (folder != "everywhere") return ContentSearch.Find(folder, query);
            if (!FileIndex.Ready) FileIndex.Build(); // the first time: the list of every file is made now
            return FileIndex.Find(query);
        });
        if (!_tab.Location.StartsWith("search:")) return;
        Folder.ShowResults(found, found.Count == 0 ? $"Nothing inside files here mentions “{query}”" : null);
        UpdateStatus();
    }

    // ---------- Drives ----------
    private MenuFlyout DriveFlyout(DriveCard d)
    {
        var m = new MenuFlyout();
        m.Items.Add(Item("Open", "\uE8DA", () => Navigate(d.Root)));
        m.Items.Add(Item("Open in new tab", "\uE737", () => AddTab(d.Root)));
        if (d.Removable) m.Items.Add(Item("Eject", "\uF847", () => { Extras.Eject(d.Root); ShowNote($"Ejecting {d.Name}"); }));
        m.Items.Add(new MenuFlyoutSeparator());
        var tools = new MenuFlyoutSubItem { Text = "Drive tools", Icon = new FontIcon { Glyph = "\uE90F" } };
        tools.Items.Add(Item("Disk Cleanup", null, () => Extras.Cleanup(d.Root)));
        tools.Items.Add(Item("Optimise and defragment", null, Extras.Optimize));
        tools.Items.Add(Item("BitLocker", null, Extras.BitLocker));
        tools.Items.Add(Item("Check for errors (in Properties, Tools)", null, () => Native.ShellRun(d.Root, "properties", _hwnd)));
        tools.Items.Add(new MenuFlyoutSeparator());
        tools.Items.Add(Item("Format…", null, () => Extras.Format(_hwnd, d.Root)));
        m.Items.Add(tools);
        m.Items.Add(Item("Properties", "\uE946", () => Native.ShellRun(d.Root, "properties", _hwnd)));
        return m;
    }

    // ---------- Undo and redo ----------
    private async Task UndoAsync(bool redo)
    {
        var (what, ok) = redo ? await UndoLog.RedoLast() : await UndoLog.UndoLast();
        if (what == null) { ShowNote(redo ? "Nothing to redo" : "Nothing to undo"); return; }
        if (!ok) { await Message(redo ? "Couldn't redo" : "Couldn't undo", $"{what}: the files have changed since, so this can't be taken back."); return; }
        ShowNote((redo ? "Redone: " : "Undone: ") + what);
        BuildSidebar();
        if (_tab.Location == "recycle-bin") _ = ShowBin();
    }
    private void MarkUndo()
    {
        UndoItem.Text = UndoLog.NextUndo is { } u ? $"Undo: {u}" : "Undo"; UndoItem.IsEnabled = UndoLog.NextUndo != null;
        RedoItem.Text = UndoLog.NextRedo is { } r ? $"Redo: {r}" : "Redo"; RedoItem.IsEnabled = UndoLog.NextRedo != null;
    }
    /// <summary>A short note in the status bar that goes away by itself.</summary>
    private void ShowNote(string text)
    {
        Chips.Children.Clear();
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = Theme.Get("OxText"), VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(t, "Note");
        Chips.Children.Add(t);
        var timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(5); timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (Chips.Children.Contains(t)) Chips.Children.Remove(t); };
        timer.Start();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) => _ = UndoAsync(false);
    private void Redo_Click(object sender, RoutedEventArgs e) => _ = UndoAsync(true);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Folder.SelectAll();
    private void SelectNone_Click(object sender, RoutedEventArgs e) => Folder.SelectNone();
    private void Invert_Click(object sender, RoutedEventArgs e) => Folder.InvertSelection();
    private void ExtToggle_Click(object sender, RoutedEventArgs e) { Settings.Current.ShowExtensions = FileItem.ShowExtensions = ExtToggle.IsChecked; Settings.Save(); Show(); }
    private void CopyPaths(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(string.Join(Environment.NewLine, items.Select((s) => $"\"{s.Path}\"")));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        ShowNote(items.Count == 1 ? "Path copied" : $"{items.Count} paths copied");
    }
    /// <summary>"Copied 1,284 items · Open" in the status bar, after the card tucks away.</summary>
    private void AddChip(CopyJob job)
    {
        Chips.Children.Clear();
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "", FontSize = 11, Foreground = Theme.Get("OxAcc") });
        sp.Children.Add(new TextBlock { Text = $"{(job.Move ? "Moved" : "Copied")} {Math.Max(0, job.DoneItems - job.Skipped):N0} items", FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = "Open", FontSize = 12, Foreground = Theme.Get("OxAcc"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var chip = new Button { Content = sp, Padding = new Thickness(10, 2, 10, 2), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(1) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, "Copy finished, open");
        chip.Click += (_, _) => { Navigate(job.Target); Chips.Children.Clear(); };
        Chips.Children.Add(chip);
    }
    private readonly SemaphoreSlim _oneQuestion = new(1);
    private async Task<(SameName, bool)> AskSameName(string src, string dst)
    {
        ComeBackIfHidden();
        await _oneQuestion.WaitAsync();
        try { return await AskSameNameNow(src, dst); }
        finally { _oneQuestion.Release(); }
    }
    /// <summary>A copy that carried on after the window was closed needs an answer: the window shows again.</summary>
    private void ComeBackIfHidden() { if (_hiddenForCopy) { _hiddenForCopy = false; AppWindow.Show(); Activate(); } }
    private async Task<(SameName, bool)> AskSameNameNow(string src, string dst)
    {
        var all = new CheckBox { Content = "Do this for the rest too" };
        var choice = SameName.KeepBoth;
        var info = new TextBlock { Text = $"\"{System.IO.Path.GetFileName(dst)}\" is already in {TabState.NameOf(System.IO.Path.GetDirectoryName(dst)!)}.", TextWrapping = TextWrapping.Wrap };
        var compare = new Grid { ColumnSpacing = 16 };
        compare.ColumnDefinitions.Add(new ColumnDefinition()); compare.ColumnDefinitions.Add(new ColumnDefinition());
        var there = await Side(dst, "Already there"); var coming = await Side(src, "Coming in");
        compare.Children.Add(there); Grid.SetColumn(coming, 1); compare.Children.Add(coming);
        var d = new ContentDialog { Title = "Same name", Content = new StackPanel { Spacing = 12, Children = { info, compare, all } }, PrimaryButtonText = "Replace", SecondaryButtonText = "Keep both", CloseButtonText = "Skip", DefaultButton = ContentDialogButton.Secondary, XamlRoot = Content.XamlRoot, RequestedTheme = Theme.Current };
        var r = await d.ShowAsync();
        choice = r == ContentDialogResult.Primary ? SameName.Replace : r == ContentDialogResult.Secondary ? SameName.KeepBoth : SameName.Skip;
        return (choice, all.IsChecked == true);
    }
    /// <summary>One side of the same-name comparison: a picture, the size and the date (newer and bigger are marked).</summary>
    private static async Task<StackPanel> Side(string path, string title)
    {
        var sp = new StackPanel { Spacing = 4, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(1) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sp, title);
        sp.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = Theme.Get("OxMuted") });
        var px = await Task.Run(() => Native.GetImage(path, 96, Native.SIIGBF.BiggerSizeOk));
        if (px is { } p)
        {
            var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(p.w, p.h);
            using (var st = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(wb.PixelBuffer)) st.Write(p.pixels, 0, p.pixels.Length);
            wb.Invalidate();
            sp.Children.Add(new Image { Source = wb, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform });
        }
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { sp.Children.Add(new TextBlock { Text = Format.Size(fi.Length), FontSize = 13 }); sp.Children.Add(new TextBlock { Text = "Modified " + fi.LastWriteTime.ToString("d MMM yyyy, HH:mm"), FontSize = 12, Foreground = Theme.Get("OxMuted") }); }
            else if (Directory.Exists(path)) sp.Children.Add(new TextBlock { Text = "Folder · modified " + Directory.GetLastWriteTime(path).ToString("d MMM yyyy, HH:mm"), FontSize = 12, Foreground = Theme.Get("OxMuted") });
        }
        catch { }
        return sp;
    }
    private bool _hiddenForCopy;
    private async Task DeleteAsync(IReadOnlyList<FileItem> items, bool forever)
    {
        if (items.Count == 0) return;
        if (forever)
        {
            var what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {items.Count} items";
            if (!await Confirm("Delete for good?", $"{what} will be deleted and can't be brought back from the Recycle Bin.", "Delete for good")) return;
            await FileOps.DeleteForever(items.Select((i) => i.Path));
        }
        else await RecycleWithUndo(items.Select((i) => i.Path).ToList());
        BuildSidebar();
    }
    private async Task RecycleWithUndo(List<string> paths)
    {
        if (paths.Count == 0) return;
        var when = DateTime.Now;
        if (await FileOps.Recycle(paths)) UndoLog.Recycled(paths, when);
        BuildSidebar();
    }
    private async Task RenameAsync(FileItem item)
    {
        var box = new TextBox { Text = item.Name, MinWidth = 360 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Keyboard); var dot = item.IsFolder ? -1 : item.Name.LastIndexOf('.'); box.Select(0, dot > 0 ? dot : item.Name.Length); };
        var err = new TextBlock { Foreground = Theme.Get("OxWarn"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dlg = Dialog("Rename", new StackPanel { Spacing = 8, Children = { box, err } }, "Rename");
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            var p = FileOps.NameProblem(box.Text.Trim());
            if (p != null) { err.Text = p; err.Visibility = Visibility.Visible; return; } // explain, keep the box open
            dlg.Hide(); _ = DoRename();
        };
        dlg.PrimaryButtonClick += (_, e) => { var p = FileOps.NameProblem(box.Text.Trim()); if (p != null) { err.Text = p; err.Visibility = Visibility.Visible; e.Cancel = true; } };
        async Task DoRename()
        {
            var name = box.Text.Trim();
            if (name == item.Name || FileOps.NameProblem(name) != null) return;
            if (!await FileOps.Rename(item.Path, name)) await Message("Couldn't rename", "The file may be open in another app, or the name may already be taken.");
            else { var to = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\'))!, name); UndoLog.Renamed(item.Path, to); Library.Current.Moved(item.Path.TrimEnd('\\'), to); Folder.Reveal(name); }
        }
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) await DoRename();
    }
    private async Task NewFolderAsync()
    {
        if (Special(_tab.Location)) return;
        var name = FileOps.FreeName(_tab.Location, "New folder");
        try { Directory.CreateDirectory(System.IO.Path.Combine(_tab.Location, name)); }
        catch (Exception e) { await Message("Couldn't make a folder here", e.Message); return; }
        UndoLog.Made(System.IO.Path.Combine(_tab.Location, name), $"New folder “{name}”");
        await Task.Delay(300);
        var made = new FileItem { Name = name, Path = System.IO.Path.Combine(_tab.Location, name), IsFolder = true };
        Folder.Reveal(name);
        await RenameAsync(made);
    }
    private async Task NewTextAsync()
    {
        if (Special(_tab.Location)) return;
        var name = FileOps.FreeName(_tab.Location, "New text document", ".txt");
        try { File.WriteAllText(System.IO.Path.Combine(_tab.Location, name), ""); }
        catch (Exception e) { await Message("Couldn't make a file here", e.Message); return; }
        UndoLog.Made(System.IO.Path.Combine(_tab.Location, name), $"New file “{name}”");
        await Task.Delay(300);
        await RenameAsync(new FileItem { Name = name, Path = System.IO.Path.Combine(_tab.Location, name) });
    }
    /// <summary>Every kind of new file the installed apps offer (Windows' own New menu).</summary>
    private IEnumerable<MenuFlyoutItemBase> NewKindItems()
    {
        foreach (var k in ShellNew.Kinds().Where((k) => k.Extension != ".txt"))
            yield return Item(k.Name, "\uE8A5", () => _ = NewKindAsync(k));
    }
    private void NewMenu_Opening(object sender, object e)
    {
        // Folder, Text document, then the apps' kinds, then tabs and windows.
        while (NewMenu.Items.Count > 2 && NewMenu.Items[2] is not MenuFlyoutSeparator) NewMenu.Items.RemoveAt(2);
        int at = 2;
        foreach (var it in NewKindItems()) NewMenu.Items.Insert(at++, it);
        bool here = !Special(_tab.Location);
        for (int i = 0; i < at; i++) if (NewMenu.Items[i] is MenuFlyoutItem mi) mi.IsEnabled = here;
    }
    private async Task NewKindAsync(NewKind kind)
    {
        if (Special(_tab.Location)) return;
        string path;
        try { path = ShellNew.Create(kind, _tab.Location); }
        catch (Exception e) { await Message("Couldn't make a file here", e.Message); return; }
        UndoLog.Made(path, $"New file “{System.IO.Path.GetFileName(path)}”");
        await Task.Delay(300);
        await RenameAsync(new FileItem { Name = System.IO.Path.GetFileName(path), Path = path });
    }
    private void NewFolder_Click(object sender, RoutedEventArgs e) => _ = NewFolderAsync();
    private void NewText_Click(object sender, RoutedEventArgs e) => _ = NewTextAsync();

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (Special(_tab.Location)) Native.ShellRun("shell:MyComputerFolder", null, _hwnd);
        else Native.ShellRun(_tab.Location, "explore", _hwnd); // Windows opens it; the path is never a command line
    }
    private void Terminal_Click(object sender, RoutedEventArgs e) => Terminal(Special(_tab.Location) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _tab.Location);
    private static void Terminal(string folder) => Launch.Terminal(folder);
    private async void FolderOptions_Click(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        var single = new RadioButton { Content = "Single-click to open an item", GroupName = "click", IsChecked = s.SingleClick };
        var dbl = new RadioButton { Content = "Double-click to open an item (single-click to select)", GroupName = "click", IsChecked = !s.SingleClick };
        var same = new RadioButton { Content = "Open each folder in the same window", GroupName = "win", IsChecked = !s.FoldersInNewWindow };
        var own = new RadioButton { Content = "Open each folder in its own window", GroupName = "win", IsChecked = s.FoldersInNewWindow };
        var start = new ComboBox { Header = "Open Omebly Explorer to", Items = { "Home", "This PC" }, SelectedIndex = s.StartAt == "this-pc" ? 1 : 0, MinWidth = 200 };
        var hidden = new CheckBox { Content = "Show hidden files and folders", IsChecked = s.ShowHidden };
        var ext = new CheckBox { Content = "Show file name extensions", IsChecked = s.ShowExtensions };
        var recent = new CheckBox { Content = "Show recently used files on Home", IsChecked = s.ShowRecent };
        var clear = new Button { Content = "Clear history" };
        var cleared = new TextBlock { FontSize = 12, Foreground = Theme.Get("OxAcc"), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += async (_, _) =>
        {
            // Windows' own list of recent files (the same one File Explorer clears) and this window's back/forward lists.
            var n = await Task.Run(() =>
            {
                int c = 0;
                var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                try { foreach (var f in Directory.EnumerateFiles(recent, "*.lnk")) { try { File.Delete(f); c++; } catch { } } } catch { }
                // The jump lists' recent items too, as Explorer's Clear does.
                foreach (var sub in new[] { "AutomaticDestinations", "CustomDestinations" })
                    try { foreach (var f in Directory.EnumerateFiles(Path.Combine(recent, sub), "*-ms")) { try { File.Delete(f); } catch { } } } catch { }
                return c;
            });
            foreach (var t in _tabs) { t.Back.Clear(); t.Forward.Clear(); }
            _closed.Clear(); Show();
            cleared.Text = $"Cleared {n} recent items";
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Click items as follows", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(single); panel.Children.Add(dbl);
        panel.Children.Add(new TextBlock { Text = "Browse folders", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(same); panel.Children.Add(own);
        panel.Children.Add(start);
        panel.Children.Add(new TextBlock { Text = "Show", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(hidden); panel.Children.Add(ext); panel.Children.Add(recent);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { clear, cleared } });
        panel.Children.Add(new TextBlock { Text = "Windows", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var take = new CheckBox { Content = "Open folders and Win+E in Omebly Explorer (instead of File Explorer)", IsChecked = Takeover.IsOn };
        panel.Children.Add(take);
        panel.Children.Add(new TextBlock { Text = "Only your account is changed. Turn it off any time to give folders back to Windows; if Omebly ever fails to start, it gives them back by itself.", FontSize = 12, Foreground = Theme.Get("OxMuted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        var jump = new CheckBox { Content = "Show a Jump-to bar on other apps' Open and Save windows", IsChecked = s.JumpBar };
        panel.Children.Add(jump);
        panel.Children.Add(new TextBlock { Text = "Your pinned folders and the folder open in Omebly, one click away in Photoshop, Chrome, Word and every other app (while Omebly Explorer is open).", FontSize = 12, Foreground = Theme.Get("OxMuted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        if (await Dialog("Folder options", new ScrollViewer { Content = panel }, "Save").ShowAsync() != ContentDialogResult.Primary) return;
        s.SingleClick = single.IsChecked == true; s.FoldersInNewWindow = own.IsChecked == true; s.StartAt = start.SelectedIndex == 1 ? "this-pc" : "home";
        s.ShowHidden = hidden.IsChecked == true; s.ShowExtensions = FileItem.ShowExtensions = ext.IsChecked == true; s.ShowRecent = recent.IsChecked == true;
        HiddenToggle.IsChecked = s.ShowHidden; ExtToggle.IsChecked = s.ShowExtensions;
        Folder.UseSingleClick(s.SingleClick);
        if (take.IsChecked == true && !Takeover.IsOn) { try { Takeover.TurnOn(); ShowNote("Folders and Win+E now open in Omebly Explorer"); } catch (Exception ex) { await Message("Couldn't change this", ex.Message); } }
        else if (take.IsChecked != true && Takeover.IsOn) { Takeover.TurnOff(); ShowNote("Folders open in File Explorer again"); }
        s.JumpBar = jump.IsChecked == true;
        if (s.JumpBar) JumpBar.Begin(); else JumpBar.End();
        Settings.Save(); Show();
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var r = await Dialog("Omebly Explorer", new TextBlock { Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}\nA free, open-source file manager for Windows, by Omebly.\nEverything stays on this PC: Omebly Explorer never uploads your files.\nThe only thing it asks the internet is whether there's a new version (when it opens).", TextWrapping = TextWrapping.Wrap }, "Check for updates", "Close").ShowAsync();
        if (r != ContentDialogResult.Primary) return;
        var u = await Updater.Check();
        if (u == null) { await Message("Up to date", "You have the newest version (or omebly.com couldn't be reached)."); return; }
        await OfferUpdate(u);
    }
    private async Task OfferUpdate(UpdateInfo u)
    {
        if (!await Confirm("Update Omebly Explorer", $"Version {u.Version} is ready (you have {Updater.Current.ToString(3)}). Click Update now: it downloads, checks Omebly's signature, installs and opens again by itself. Your settings, tags and pins stay.", "Update now")) return;
        var jp = new JobProgress();
        var card = new TaskCard($"Downloading version {u.Version}"); card.Finished += (c) => Cards.Children.Remove(c); Cards.Children.Insert(0, card);
        var made = await card.Run(async (ct) => new List<string> { await Updater.Download(u, new Progress<double>((f) => jp.Fraction = f), ct) }, () => (jp.Fraction, ""), "Downloaded and checked");
        if (made is { Count: 1 }) { Updater.Install(made[0]); Application.Current.Exit(); }
    }

    // ---------- Status bar ----------
    private void UpdateStatus()
    {
        if (_tab.Location == "home")
        {
            StatusLeft.Text = $"{Places.Drives().Count} drives · {Settings.Current.Pinned.Count} pinned folders";
            StatusRight.Text = "";
            return;
        }
        if (_tab.Location.StartsWith("phone:") || _tab.Location.StartsWith("server:")) { StatusLeft.Text = $"{Browse.Count:N0} items"; StatusRight.Text = ""; return; }
        if (_tab.Location == "tools" || _tab.Location.StartsWith("tools:")) { StatusLeft.Text = Tools.Status; StatusRight.Text = ""; return; }
        if (_tab.Location.StartsWith("archive:")) { StatusLeft.Text = $"{Arc.Count:N0} items in this archive"; StatusRight.Text = ""; return; }
        if (_tab.Location == "recycle-bin") { StatusLeft.Text = $"{Bin.Count:N0} items in the Recycle Bin"; StatusRight.Text = ""; return; }
        var sel = Folder.Selected;
        StatusLeft.Text = $"{Folder.TotalCount:N0} items";
        if (sel.Count == 0) { StatusRight.Text = ""; return; }
        long size = sel.Where((s) => !s.IsFolder).Sum((s) => s.Size);
        StatusRight.Text = sel.Count == 1 ? $"1 selected{(size > 0 ? $" · {Format.Size(size)}" : "")}" : $"{sel.Count:N0} selected{(size > 0 ? $" · {Format.Size(size)}" : "")}";
    }

    // ---------- Keyboard ----------
    private void AddShortcuts()
    {
        void Key(VirtualKeyModifiers mods, VirtualKey key, Action run, bool whenTyping = false)
        {
            var ka = new KeyboardAccelerator { Modifiers = mods, Key = key };
            ka.Invoked += (_, e) =>
            {
                if (_viewer != null) return; // the viewer is open: folder shortcuts are off
                if (!whenTyping && FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;
                run(); e.Handled = true;
            };
            Root.KeyboardAccelerators.Add(ka);
        }
        var C = VirtualKeyModifiers.Control; var A = VirtualKeyModifiers.Menu; var S = VirtualKeyModifiers.Shift; var N = VirtualKeyModifiers.None;
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Key(C, VirtualKey.T, () => AddTab("home"), true);
        Key(C | S, VirtualKey.T, ReopenClosedTab, true);
        Key(C, VirtualKey.W, () => CloseTab(_tab), true);
        Key(C, VirtualKey.N, () => new MainWindow().Activate(), true);
        Key(C, VirtualKey.Tab, () => SwitchTab(_tabs[(_tabs.IndexOf(_tab) + 1) % _tabs.Count]), true);
        Key(C | S, VirtualKey.Tab, () => SwitchTab(_tabs[(_tabs.IndexOf(_tab) - 1 + _tabs.Count) % _tabs.Count]), true);
        Key(C, VirtualKey.L, EditPath, true);
        Key(A, VirtualKey.D, EditPath, true);
        Key(C, VirtualKey.F, () => { SearchBox.Focus(FocusState.Keyboard); SearchBox.SelectAll(); }, true);
        Key(C, VirtualKey.E, () => { SearchBox.Focus(FocusState.Keyboard); SearchBox.SelectAll(); }, true);
        Key(N, VirtualKey.F5, Show, true);
        Key(N, VirtualKey.Back, GoBack);
        Key(N, VirtualKey.GoBack, GoBack, true);
        Key(N, VirtualKey.GoForward, GoForward, true);
        Key(C, VirtualKey.A, Folder.SelectAll);
        Key(C, VirtualKey.Z, () => _ = UndoAsync(false));
        Key(C, VirtualKey.Y, () => _ = UndoAsync(true));
        Key(C | S, VirtualKey.Z, () => _ = UndoAsync(true));
        Key(C | S, VirtualKey.C, () => CopyPaths(Folder.Selected));
        Key(A, VirtualKey.P, () => TogglePane("preview"), true);
        Key(C, VirtualKey.K, () => _ = CommandBar(), true);
        Key(A | S, VirtualKey.P, () => TogglePane("details"), true);
        Key(C, VirtualKey.C, () => { var s = Folder.Selected; if (s.Count > 0) { _ = Core.Clip.Put(s, false); RememberClip(s); } });
        Key(C, VirtualKey.X, () => { var s = Folder.Selected; if (s.Count > 0) _ = Core.Clip.Put(s, true); });
        Key(C, VirtualKey.V, () => { if (!Special(_tab.Location)) Paste(_tab.Location); });
        // One key for both: Shift decides (so Shift+Delete never also sends to the Recycle Bin).
        // Alt+arrows and Delete are read straight from the key press (with the real Shift and Alt state at that
        // moment), so Shift+Delete never also sends to the Recycle Bin and Alt+arrows always work, wherever focus is.
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((s, e) =>
        {
            bool alt = Down(VirtualKey.Menu, 0x12), shift = Down(VirtualKey.Shift, 0x10), ctrl = Down(VirtualKey.Control, 0x11);
            if (_viewer != null) { if (_viewer.HandleKey(e.Key, ctrl, shift)) e.Handled = true; return; }
            var typing = FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;
            // Space: a quick look at the selected file (Esc closes it). Caught here because the list uses Space itself.
            if (e.Key == VirtualKey.Space && !alt && !ctrl && !shift && !Folder.TypingAhead && FocusManager.GetFocusedElement(Content.XamlRoot) is SelectorItem or ListViewBase && Folder.Selected is { Count: 1 } one && !one[0].IsFolder)
            { ShowViewer(one[0], Folder.Shown); e.Handled = true; return; }
            if (e.Key == VirtualKey.Delete) Perf.Log($"delete key: shift {shift} (winui {(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0}, sync {(GetKeyState(0x10) & 0x8000) != 0}, async {(GetAsyncKeyState(0x10) & 0x8000) != 0}), selected {Folder.Selected.Count}");
            if (alt && !ctrl && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up)
            {
                if (e.Key == VirtualKey.Left) GoBack(); else if (e.Key == VirtualKey.Right) GoForward(); else GoUp();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Delete && !typing && !alt && !ctrl && _tab.Location != "recycle-bin") // the bin handles its own Delete
            {
                _ = DeleteAsync(Folder.Selected, forever: shift);
                e.Handled = true;
            }
        }), true);
        Key(N, VirtualKey.F2, () => { var s = Folder.Selected; if (s.Count == 1) _ = RenameAsync(s[0]); else if (s.Count > 1) _ = BulkRenameAsync(s); });
        Key(C | S, VirtualKey.N, () => _ = NewFolderAsync(), true);
        Key(A, VirtualKey.Enter, () => { Perf.Log("alt+enter"); var s = Folder.Selected; Native.ShellRun(s.Count > 0 ? s[0].Path : _tab.Location, "properties", _hwnd); });
        Key(C, VirtualKey.Number1, () => UseView("details"));
        Key(C, VirtualKey.Number2, () => UseView("tiles"));
        Key(C, VirtualKey.Number3, () => UseView("gallery"));
        // Explorer's own view keys: Ctrl+Shift+1 extra large icons ... Ctrl+Shift+8 content.
        var numbers = new[] { VirtualKey.Number1, VirtualKey.Number2, VirtualKey.Number3, VirtualKey.Number4, VirtualKey.Number5, VirtualKey.Number6, VirtualKey.Number7, VirtualKey.Number8 };
        var order = new[] { "xl", "tiles", "medium", "small", "list", "details", "tilesinfo", "content" };
        for (int n = 0; n < 8; n++) { var v = order[n]; Key(C | S, numbers[n], () => UseView(v)); }
        // Mouse back/forward buttons.
        Root.PointerPressed += (s, e) =>
        {
            var p = e.GetCurrentPoint(Root).Properties;
            if (p.IsXButton1Pressed) { GoBack(); e.Handled = true; } else if (p.IsXButton2Pressed) { GoForward(); e.Handled = true; }
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    /// <summary>Is this key held right now? (WinUI's own view first, then Windows'.)</summary>
    private static bool Down(VirtualKey key, int vk) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
        || (GetKeyState(vk) & 0x8000) != 0 || (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---------- Dialogs ----------
    private ContentDialog Dialog(string title, object content, string primary, string close = "Cancel") =>
        new() { Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot, RequestedTheme = Theme.Current };
    private async Task<bool> Confirm(string title, string text, string yes)
    {
        try { return await Dialog(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, yes).ShowAsync() == ContentDialogResult.Primary; }
        catch { return false; } // another question is already open: the answer is no
    }
    private async Task Message(string title, string text)
    {
        var d = new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", XamlRoot = Content.XamlRoot, RequestedTheme = Theme.Current };
        try { await d.ShowAsync(); } catch { /* another dialog is open */ }
    }
}
