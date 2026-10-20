// The Tools page (stage 8): power tools and a small Windows control centre, as glass cards; and the pages some
// tools need: the disk space map, duplicates, folder comparison and start-up apps.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed class ToolsView : Grid
{
    public event Action<string>? Run;              // a tool's key ("dupes", "map", ...)
    public event Action<string>? Navigate;         // open a folder or file
    public event Action<List<string>>? Recycle;    // send these to the Recycle Bin
    public event Action<List<string>, string, bool>? Copy; // sources, target, move
    public Func<string, string, string, Task<bool>>? Confirm;
    private readonly Grid _page = new();
    public string Status { get; private set; } = "";

    public ToolsView() { Children.Add(_page); }

    private static TextBlock Title(string t) => new() { Text = t, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText"), Margin = new Thickness(0, 0, 0, 4) };
    private static TextBlock Sub(string t) => new() { Text = t, FontSize = 13, Foreground = Theme.Get("OxMuted"), TextWrapping = TextWrapping.Wrap };
    private static Button Btn(string text, Action run, bool accent = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 7), CornerRadius = new CornerRadius(8) };
        if (accent) { b.Background = Theme.Get("OxAcc"); b.Foreground = Theme.Get("OxAccText"); b.BorderThickness = new Thickness(0); }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => run();
        return b;
    }

    // ---------- The hub ----------
    public void ShowHub(string? here)
    {
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 18, Padding = new Thickness(32, 24, 32, 32) };
        sp.Children.Add(Title("Tools"));
        sp.Children.Add(Sub(here != null ? $"Tools that work on a folder use the one you came from: {here}" : "Open a folder first for tools that work on a folder; the others work anywhere."));
        void Section(string name, (string key, string glyph, string title, string text)[] cards)
        {
            sp.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxAcc"), Margin = new Thickness(0, 6, 0, 0) });
            var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 280, ItemHeight = 104 };
            foreach (var (key, glyph, title, text) in cards)
            {
                var g = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 22, Foreground = Theme.Get("OxAcc"), VerticalAlignment = VerticalAlignment.Top });
                var t = new StackPanel { Spacing = 3 }; t.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText") }); t.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Theme.Get("OxMuted"), TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
                Grid.SetColumn(t, 1); g.Children.Add(t);
                var b = new Button { Content = g, Width = 268, Height = 94, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(12), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(1) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
                var k = key; b.Click += (_, _) => Run?.Invoke(k);
                wrap.Children.Add(b);
            }
            sp.Children.Add(wrap);
        }
        Section("Clean up", new[]
        {
            ("dupes", "", "Find duplicates", "Files with exactly the same content, wherever they are in this folder."),
            ("map", "", "Disk space map", "See what fills the drive, as coloured blocks you can open."),
            ("big", "", "Find big files", "Files over 500 MB in this folder and below."),
            ("empty", "", "Empty folders", "Folders with nothing inside."),
            ("old", "", "Old downloads", "Files in Downloads you haven't touched for 3 months."),
            ("temp", "", "Clean temporary files", "What Windows and apps left behind (you see the list first)."),
        });
        Section("Compare and sync", new[] { ("compare", "", "Compare two folders", "What's only on one side, newer or different; then make one match the other.") });
        Section("Automatic", new[]
        {
            ("rules", "", "Rules", "When a file arrives in a folder, move, copy, rename, convert or zip it by itself."),
            ("backup", "", "Backup", "Copy your folders to a backup drive whenever it's plugged in; restore any time."),
        });
        Section("Connect", new[]
        {
            ("server", "", "Add a server", "FTP or SFTP servers as folders. Passwords stay in Windows' Credential Manager."),
            ("mapdrive", "", "Map a network drive", "Shared folders on other PCs or a NAS, as a drive letter (Windows' own window)."),
        });
        Section("Windows", new[]
        {
            ("startup", "", "Start-up apps", "Apps that start with Windows; turn yours on or off."),
            ("apps", "", "Installed apps", "Uninstall or change apps (Windows Settings)."),
            ("defaults", "", "Default apps", "Which app opens each kind of file (Windows Settings)."),
            ("fonts", "", "Fonts", "All fonts on this PC; open a font file to preview and install it."),
            ("clips", "", "Copied files", "Files you copied recently in Omebly, to paste again."),
            ("screenshot", "", "Screenshot into this folder", "Snip part of the screen; it's saved here as a PNG."),
        });
        _page.Children.Add(new ScrollViewer { Content = sp });
        Status = "Tools";
    }

    // ---------- Disk space map ----------
    private CancellationTokenSource? _cts;
    public async Task ShowMap(string folder)
    {
        _cts?.Cancel(); var cts = _cts = new CancellationTokenSource();
        _page.Children.Clear();
        var top = new StackPanel { Spacing = 6, Padding = new Thickness(24, 16, 24, 8) };
        top.Children.Add(Title("Disk space map")); var sub = Sub($"Measuring {folder}…"); top.Children.Add(sub);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var parent = System.IO.Path.GetDirectoryName(folder.TrimEnd('\\'));
        if (parent != null) bar.Children.Add(Btn("Up", () => _ = ShowMap(parent)));
        bar.Children.Add(Btn("Open this folder", () => Navigate?.Invoke(folder)));
        top.Children.Add(bar);
        var canvas = new Canvas { Margin = new Thickness(24, 4, 24, 24) };
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(top); Grid.SetRow(canvas, 1); grid.Children.Add(canvas);
        _page.Children.Add(grid);
        List<SizeNode> nodes;
        try { nodes = await Task.Run(() => Space.Children(folder, cts.Token)); } catch (OperationCanceledException) { return; } catch (Exception e) { sub.Text = e.Message; return; }
        if (cts.IsCancellationRequested) return;
        long total = nodes.Sum((n) => n.Size);
        sub.Text = $"{Format.Size(total)} in {folder}. Click a block to look inside; right-click to open it.";
        Status = $"{Format.Size(total)} in {nodes.Count:N0} items";
        void Draw()
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 10 || h < 10 || total == 0) return;
            var rects = Squarify(nodes.Where((n) => n.Size > 0).Take(200).ToList(), 0, 0, w, h);
            string[] palette = { "#2EF2B3", "#4C9BFF", "#9B7BFF", "#FF6F91", "#F4C04E", "#22C3E6", "#FF8A3D", "#B9C4C0" };
            int i = 0;
            foreach (var (n, x, y, rw, rh) in rects)
            {
                var color = Ui.Brush(palette[i++ % palette.Length]); color.Opacity = n.IsFolder ? 0.55 : 0.32;
                var cell = new Border { Width = Math.Max(1, rw - 2), Height = Math.Max(1, rh - 2), Background = color, CornerRadius = new CornerRadius(6), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1) };
                if (rw > 70 && rh > 34) cell.Child = new StackPanel { Padding = new Thickness(8, 6, 8, 6), Children = { new TextBlock { Text = n.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText"), TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { Text = $"{Format.Size(n.Size)} · {n.Size * 100.0 / total:0.#}%", FontSize = 11.5, Foreground = Theme.Get("OxText") } } };
                ToolTipService.SetToolTip(cell, $"{n.Name}\n{Format.Size(n.Size)} ({n.Size * 100.0 / total:0.#}%)");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, n.Name);
                var node = n;
                cell.Tapped += (_, _) => { if (node.IsFolder) _ = ShowMap(node.Path); else Navigate?.Invoke(node.Path); };
                cell.RightTapped += (_, e) => { Navigate?.Invoke(node.Path); e.Handled = true; };
                Canvas.SetLeft(cell, x + 1); Canvas.SetTop(cell, y + 1);
                canvas.Children.Add(cell);
            }
        }
        canvas.SizeChanged += (_, _) => Draw();
        Draw();
    }
    /// <summary>Squarified treemap: blocks as close to squares as possible, biggest first.</summary>
    private static List<(SizeNode n, double x, double y, double w, double h)> Squarify(List<SizeNode> items, double x, double y, double w, double h)
    {
        var result = new List<(SizeNode, double, double, double, double)>();
        double total = items.Sum((i) => (double)i.Size);
        if (items.Count == 0 || total <= 0) return result;
        var scaled = items.Select((i) => (i, area: i.Size / total * w * h)).ToList();
        int start = 0;
        while (start < scaled.Count)
        {
            double side = Math.Min(w, h);
            var row = new List<(SizeNode i, double area)> { scaled[start] };
            double Worst(List<(SizeNode i, double area)> r) { double s = r.Sum((q) => q.area), mx = r.Max((q) => q.area), mn = r.Min((q) => q.area); return Math.Max(side * side * mx / (s * s), s * s / (side * side * mn)); }
            int next = start + 1;
            while (next < scaled.Count) { var tryRow = new List<(SizeNode, double)>(row) { scaled[next] }; if (Worst(tryRow) > Worst(row)) break; row = tryRow; next++; }
            double rowArea = row.Sum((q) => q.area);
            if (w >= h)
            {
                double rw = rowArea / h, cy = y;
                foreach (var (i, a) in row) { var rh = a / rw; result.Add((i, x, cy, rw, rh)); cy += rh; }
                x += rw; w -= rw;
            }
            else
            {
                double rh = rowArea / w, cx = x;
                foreach (var (i, a) in row) { var rw = a / rh; result.Add((i, cx, y, rw, rh)); cx += rw; }
                y += rh; h -= rh;
            }
            start = next;
        }
        return result;
    }

    // ---------- Duplicates ----------
    public async Task ShowDuplicates(string folder)
    {
        _cts?.Cancel(); var cts = _cts = new CancellationTokenSource();
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 10, Padding = new Thickness(24, 16, 24, 24) };
        sp.Children.Add(Title("Duplicates")); var sub = Sub($"Looking in {folder}…"); sp.Children.Add(sub);
        var bar = new ProgressBar { IsIndeterminate = true, Foreground = Theme.Get("OxAcc") }; sp.Children.Add(bar);
        _page.Children.Add(new ScrollViewer { Content = sp });
        var p = new JobProgress();
        List<List<FileItem>> groups;
        try { groups = await Task.Run(() => Duplicates.Find(folder, p, cts.Token)); } catch (OperationCanceledException) { return; } catch (Exception e) { sub.Text = e.Message; bar.Visibility = Visibility.Collapsed; return; }
        bar.Visibility = Visibility.Collapsed;
        long waste = groups.Sum((g) => g[0].Size * (g.Count - 1));
        sub.Text = groups.Count == 0 ? "No duplicates here." : $"{groups.Count:N0} sets of the same file; {Format.Size(waste)} could be freed. The oldest copy of each is kept (unticked); the ticked copies can go to the Recycle Bin.";
        Status = $"{groups.Count:N0} duplicate sets";
        var boxes = new List<(CheckBox box, string path)>();
        foreach (var g in groups.Take(500))
        {
            var card = new StackPanel { Spacing = 2, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(1) };
            card.Children.Add(new TextBlock { Text = $"{g[0].Name} · {Format.Size(g[0].Size)} × {g.Count}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText") });
            for (int i = 0; i < g.Count; i++)
            {
                var cb = new CheckBox { Content = g[i].Path, IsChecked = i > 0, FontSize = 12.5 };
                boxes.Add((cb, g[i].Path)); card.Children.Add(cb);
            }
            sp.Children.Add(card);
        }
        if (groups.Count > 0)
            sp.Children.Insert(3, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Btn("Move ticked copies to the Recycle Bin", async () =>
            {
                var chosen = boxes.Where((b) => b.box.IsChecked == true).Select((b) => b.path).ToList();
                if (chosen.Count == 0) return;
                if (Confirm != null && !await Confirm("Move duplicates to the Recycle Bin?", $"{chosen.Count:N0} copies go to the Recycle Bin; one copy of each stays. You can undo it.", "Move")) return;
                Recycle?.Invoke(chosen);
                await ShowDuplicates(folder);
            }, accent: true) } });
    }

    // ---------- Compare two folders ----------
    public async Task ShowCompare(string left, string right)
    {
        _cts?.Cancel(); var cts = _cts = new CancellationTokenSource();
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 8, Padding = new Thickness(24, 16, 24, 24) };
        sp.Children.Add(Title("Compare two folders")); var sub = Sub($"{left}\n{right}"); sp.Children.Add(sub);
        _page.Children.Add(new ScrollViewer { Content = sp });
        List<DiffRow> rows;
        try { rows = await Task.Run(() => FolderCompare.Compare(left, right, cts.Token)); } catch (OperationCanceledException) { return; } catch (Exception e) { sub.Text = e.Message; return; }
        var changed = rows.Where((r) => r.State != Diff.Same).ToList();
        sub.Text = $"Left: {left}\nRight: {right}\n{changed.Count:N0} differences, {rows.Count - changed.Count:N0} files the same.";
        Status = $"{changed.Count:N0} differences";
        var toRight = changed.Where((r) => r.State is Diff.OnlyLeft or Diff.Newer).Select((r) => r.Rel).ToList();
        var toLeft = changed.Where((r) => r.State is Diff.OnlyRight or Diff.Older).Select((r) => r.Rel).ToList();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (toRight.Count > 0) buttons.Children.Add(Btn($"Copy {toRight.Count:N0} new and newer files to the right", () => CopyRel(toRight, left, right), accent: true));
        if (toLeft.Count > 0) buttons.Children.Add(Btn($"Copy {toLeft.Count:N0} to the left", () => CopyRel(toLeft, right, left)));
        sp.Children.Add(buttons);
        foreach (var r in changed.Take(3000))
        {
            var (label, color) = r.State switch { Diff.OnlyLeft => ("Only on the left", "#4C9BFF"), Diff.OnlyRight => ("Only on the right", "#9B7BFF"), Diff.Newer => ("Newer on the left", "#2EF2B3"), Diff.Older => ("Newer on the right", "#F4C04E"), _ => ("Different", "#FF6F91") };
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, Foreground = Ui.Brush(color), FontSize = 12.5 });
            var t = new TextBlock { Text = r.Rel, FontSize = 12.5, Foreground = Theme.Get("OxText"), TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(t, 1); g.Children.Add(t);
            sp.Children.Add(g);
        }
    }
    /// <summary>Copies files (by their path inside the folder) from one side to the same place on the other side.</summary>
    private void CopyRel(List<string> rels, string from, string to)
    {
        foreach (var grp in rels.GroupBy((r) => System.IO.Path.GetDirectoryName(r) ?? ""))
        {
            var target = System.IO.Path.Combine(to, grp.Key);
            Directory.CreateDirectory(target);
            Copy?.Invoke(grp.Select((r) => System.IO.Path.Combine(from, r)).ToList(), target, false);
        }
    }

    // ---------- Rules ----------
    public event Action? RulesChanged;
    public Func<Task<Rule?>>? NewRule;
    public void ShowRules()
    {
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 8, Padding = new Thickness(24, 16, 24, 24) };
        sp.Children.Add(Title("Rules"));
        sp.Children.Add(Sub("Rules work while Omebly Explorer is open. They move, copy, rename, convert or zip files; they never delete anything."));
        sp.Children.Add(Btn("Add a rule…", async () => { if (NewRule != null && await NewRule() is { } r) { Library.Current.Rules.Add(r); Library.Save(); RulesChanged?.Invoke(); ShowRules(); } }, accent: true));
        foreach (var r in Library.Current.Rules)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = r.Describe(), TextWrapping = TextWrapping.Wrap, Foreground = Theme.Get("OxText"), VerticalAlignment = VerticalAlignment.Center });
            var sw = new ToggleSwitch { IsOn = r.On, OnContent = "On", OffContent = "Off", MinWidth = 0 };
            var rule = r; sw.Toggled += (_, _) => { rule.On = sw.IsOn; Library.Save(); RulesChanged?.Invoke(); };
            Grid.SetColumn(sw, 1); row.Children.Add(sw);
            var del = Btn("Remove", () => { Library.Current.Rules.Remove(rule); Library.Save(); RulesChanged?.Invoke(); ShowRules(); });
            Grid.SetColumn(del, 2); row.Children.Add(del);
            sp.Children.Add(row);
        }
        if (Library.Current.Rules.Count == 0) sp.Children.Add(Sub("No rules yet. Example: when a PDF arrives in Downloads, move it to Documents and name it by date."));
        _page.Children.Add(new ScrollViewer { Content = sp });
        Status = $"{Library.Current.Rules.Count} rules";
    }

    // ---------- Backup ----------
    public Func<Task<Backup?>>? NewBackup;
    public Action<Backup>? RunBackupNow;
    public void ShowBackup()
    {
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 8, Padding = new Thickness(24, 16, 24, 24) };
        sp.Children.Add(Title("Backup"));
        sp.Children.Add(Sub("New and changed files are copied to the backup drive whenever it is plugged in (about once a day while Omebly is open). Nothing is ever deleted from the backup; older versions are kept beside new ones."));
        sp.Children.Add(Btn("Set up a backup…", async () => { if (NewBackup != null && await NewBackup() is { } b) { Library.Current.Backups.Add(b); Library.Save(); ShowBackup(); } }, accent: true));
        foreach (var b in Library.Current.Backups)
        {
            var root = RuleEngine.DriveFor(b);
            var card = new StackPanel { Spacing = 4, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard") };
            card.Children.Add(new TextBlock { Text = $"{string.Join(", ", b.Folders.Select(TabState.NameOf))} → {b.DriveLabel}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText"), TextWrapping = TextWrapping.Wrap });
            card.Children.Add(Sub(root == null ? "The backup drive isn't plugged in." : $"The backup drive is plugged in ({root})."));
            if (b.LastResult.Length > 0) card.Children.Add(Sub("Last time: " + b.LastResult));
            var bk = b;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (root != null) { buttons.Children.Add(Btn("Back up now", () => RunBackupNow?.Invoke(bk), accent: true)); buttons.Children.Add(Btn("Open the backup (to restore)", () => Navigate?.Invoke(System.IO.Path.Combine(root, "Omebly Backup", Environment.MachineName)))); }
            buttons.Children.Add(Btn("Remove", () => { Library.Current.Backups.Remove(bk); Library.Save(); ShowBackup(); }));
            card.Children.Add(buttons);
            sp.Children.Add(card);
        }
        _page.Children.Add(new ScrollViewer { Content = sp });
        Status = $"{Library.Current.Backups.Count} backups";
    }

    // ---------- Start-up apps ----------
    public void ShowStartup()
    {
        _page.Children.Clear();
        var sp = new StackPanel { Spacing = 8, Padding = new Thickness(24, 16, 24, 24) };
        sp.Children.Add(Title("Start-up apps")); sp.Children.Add(Sub("These start when you sign in to Windows. Turning one off doesn't remove it; it just doesn't start by itself."));
        var apps = WinControl.StartupApps();
        foreach (var a in apps)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), Background = Theme.Get("OxCard") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel(); t.Children.Add(new TextBlock { Text = a.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText") }); t.Children.Add(new TextBlock { Text = $"{a.Where} · {a.Command}", FontSize = 11.5, Foreground = Theme.Get("OxMuted"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(t);
            var sw = new ToggleSwitch { IsOn = a.Enabled, IsEnabled = a.CanChange, OnContent = "On", OffContent = "Off", MinWidth = 0 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sw, a.Name);
            var name = a.Name; sw.Toggled += (_, _) => { try { WinControl.SetStartup(name, sw.IsOn); } catch { } };
            Grid.SetColumn(sw, 1); row.Children.Add(sw);
            sp.Children.Add(row);
        }
        if (apps.Count == 0) sp.Children.Add(Sub("Nothing starts with Windows for your account."));
        _page.Children.Add(new ScrollViewer { Content = sp });
        Status = $"{apps.Count} start-up apps";
    }
}
