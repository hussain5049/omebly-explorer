// A folder's contents in three views (details, large icons, gallery). Reading, sorting and watching the folder
// all happen off the UI thread; the lists only create rows that are on screen, so 500,000 files scroll smoothly.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OmeblyExplorer.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace OmeblyExplorer.Views;

public enum DropKind { Copy, Move, Link }

/// <summary>One group when the folder is grouped ("Today", "Large (128 MB - 1 GB)"...).</summary>
public sealed class FileGroup : List<FileItem>
{
    public required string Key { get; init; }
    public string Header => $"{Key} ({Count:N0})";
    public override string ToString() => Header; // what the list's group header shows
    public FileGroup(IEnumerable<FileItem> items) : base(items) { }
}

/// <summary>A view: its name, whether it is a list (rows) or a grid, its item look and its picture size.</summary>
public sealed record ViewInfo(string Key, string Label, bool Rows, string Template, int Icon, bool Down = false, string? Keys = null);

public sealed partial class FolderView : UserControl
{
    public string? CurrentPath { get; private set; }
    public string Sort { get; private set; } = "name";
    public bool SortDown { get; private set; }
    public string View { get; private set; } = "details";
    public string Group { get; private set; } = "none";
    /// <summary>The view, sort or grouping changed here (Ctrl+wheel, a menu, or the folder's own remembered view).</summary>
    public event Action? ViewChanged;

    public static readonly ViewInfo[] Views =
    {
        new("xl", "Extra large icons", false, "XlTemplate", 256, Keys: "Ctrl+Shift+1"),
        new("tiles", "Large icons", false, "TileTemplate", 96, Keys: "Ctrl+Shift+2"),
        new("medium", "Medium icons", false, "MediumTemplate", 48, Keys: "Ctrl+Shift+3"),
        new("small", "Small icons", false, "SmallTemplate", 20, Keys: "Ctrl+Shift+4"),
        new("list", "List", false, "SmallTemplate", 20, Down: true, Keys: "Ctrl+Shift+5"),
        new("details", "Details", true, "DetailsTemplate", 20, Keys: "Ctrl+Shift+6"),
        new("tilesinfo", "Tiles", false, "TilesInfoTemplate", 48, Keys: "Ctrl+Shift+7"),
        new("content", "Content", true, "ContentTemplate", 48, Keys: "Ctrl+Shift+8"),
        new("gallery", "Gallery", false, "GalleryTemplate", 200),
    };
    public static bool IsView(string? v) => Views.Any((x) => x.Key == v);
    private static ViewInfo Info(string v) => Views.FirstOrDefault((x) => x.Key == v) ?? Views[5];
    /// <summary>Ctrl + mouse wheel goes through these, smaller to bigger (like Explorer).</summary>
    private static readonly string[] WheelOrder = { "details", "list", "small", "medium", "tiles", "xl" };
    public static readonly (string key, string label)[] Groups = { ("none", "(None)"), ("name", "Name"), ("modified", "Date modified"), ("type", "Type"), ("size", "Size") };
    public int TotalCount => _all.Count;

    public event Action<FileItem>? OpenRequested;
    public event Action? SelectionChangedOut;
    public event Action<FileItem?, Windows.Foundation.Point, UIElement>? MenuRequested;
    public event Action<List<string>, string, DropKind>? FilesDropped; // paths, target folder, copy/move/shortcut
    public event Action<string, string>? LoadFailed; // path, message
    public event Action? Loaded2;

    private readonly BulkList<FileItem> _shown = new();
    private List<FileItem> _all = new();
    private string _filter = "";
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherQueueTimer _watchTimer;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    public FolderView()
    {
        InitializeComponent();
        List.ItemsSource = _shown; Tiles.ItemsSource = _shown;
        BuildHeader();
        // Ctrl + mouse wheel changes the icon size (seen before the list scrolls).
        RootGrid.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0) return;
            var d = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
            var at = Array.IndexOf(WheelOrder, View); if (at < 0) at = d > 0 ? 3 : 1;
            var next = WheelOrder[Math.Clamp(at + (d > 0 ? 1 : -1), 0, WheelOrder.Length - 1)];
            if (next != View) { SetView(next); RememberHere(); ViewChanged?.Invoke(); }
            e.Handled = true;
        }), true);
        List.Loaded += (_, _) =>
        {
            // The column headers follow the list when it scrolls sideways.
            if (FindScroll(List) is { } sv) sv.ViewChanged += (_, _) => Header.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { X = -sv.HorizontalOffset };
        };
        _watchTimer = DispatcherQueue.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _watchTimer.IsRepeating = false;
        _watchTimer.Tick += (_, _) => ApplyWatchedChanges();
    }

    private ListViewBase Active => Info(View).Rows ? List : Tiles;
    public IReadOnlyList<FileItem> Selected => Active.SelectedItems.Cast<FileItem>().ToList();
    /// <summary>The files as shown now (sorted and filtered), for next and previous in the viewer.</summary>
    public IReadOnlyList<FileItem> Shown => _groups != null ? _groups.SelectMany((g) => g).ToList() : _shown.Snapshot().ToList();
    public void SelectAll() => Active.SelectAll();
    public void SelectNone() => Active.SelectedItems.Clear();
    public void InvertSelection()
    {
        var on = new HashSet<FileItem>(Active.SelectedItems.Cast<FileItem>());
        Active.SelectedItems.Clear();
        foreach (var i in _shown.Snapshot()) if (!on.Contains(i)) Active.SelectedItems.Add(i);
    }

    // ---------- Type to jump (type the start of a name, like Explorer) ----------
    private string _typed = "";
    private DateTime _typedAt;
    /// <summary>True while letters are being typed to jump, so Space is part of the name (not a quick look).</summary>
    public bool TypingAhead => _typed.Length > 0 && (DateTime.Now - _typedAt).TotalMilliseconds < 1000;
    private void Items_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        var c = e.Character;
        if (char.IsControl(c) || (c == ' ' && !TypingAhead)) return;
        if (!TypingAhead) _typed = "";
        _typed += c; _typedAt = DateTime.Now;
        var items = _shown.Snapshot();
        var cur = Active.SelectedItem as FileItem;
        // One letter typed again goes to the next name with that letter; more letters find the first match.
        FileItem? hit = null;
        if (_typed.Length > 1 && _typed.All((x) => char.ToLowerInvariant(x) == char.ToLowerInvariant(_typed[0])))
        {
            var start = cur == null ? 0 : items.IndexOf(cur) + 1;
            hit = items.Skip(start).Concat(items.Take(start)).FirstOrDefault((i) => i.DisplayName.StartsWith(_typed[0].ToString(), StringComparison.CurrentCultureIgnoreCase));
        }
        hit ??= items.FirstOrDefault((i) => i.DisplayName.StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase));
        if (hit != null)
        {
            Active.SelectedItems.Clear(); Active.SelectedItem = hit; Active.ScrollIntoView(hit);
            (Active.ContainerFromItem(hit) as SelectorItem)?.Focus(FocusState.Keyboard);
        }
        e.Handled = true;
    }
    public void FocusList() => Active.Focus(FocusState.Programmatic);

    public void SetView(string view)
    {
        if (!IsView(view)) view = "details";
        var info = Info(view);
        View = view;
        HeaderHost.Visibility = Ui.Show(view == "details");
        List.Visibility = Ui.Show(info.Rows);
        Tiles.Visibility = Ui.Hide(info.Rows);
        if (info.Rows)
        {
            List.ItemTemplate = (DataTemplate)Resources[info.Template];
            ScrollViewer.SetHorizontalScrollMode(List, view == "details" ? ScrollMode.Enabled : ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(List, view == "details" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        }
        else
        {
            Tiles.ItemTemplate = (DataTemplate)Resources[info.Template];
            Tiles.ItemsPanel = (ItemsPanelTemplate)Resources[info.Down ? "WrapDown" : "WrapAcross"];
            // List runs top to bottom in columns and scrolls sideways, like Explorer's List view.
            ScrollViewer.SetHorizontalScrollMode(Tiles, info.Down ? ScrollMode.Enabled : ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(Tiles, info.Down ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(Tiles, info.Down ? ScrollMode.Disabled : ScrollMode.Enabled);
            ScrollViewer.SetVerticalScrollBarVisibility(Tiles, info.Down ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }
        foreach (var i in _all) i.IconSizeLoaded = 0; // pictures at the new size
        ShowCount();
    }

    /// <summary>Compact view: tighter rows in Details.</summary>
    public void SetCompact(bool on) { Settings.Current.Compact = on; if (View == "details") { List.ItemTemplate = null; List.ItemTemplate = (DataTemplate)Resources["DetailsTemplate"]; } }

    // ---------- This folder's own view (remembered like Explorer) ----------
    private void UsePrefsFor(string path)
    {
        string view = Settings.Current.View, sort = "name", group = "none"; bool down = false;
        if (Settings.Current.Folders.TryGetValue(path.TrimEnd('\\'), out var p)) { view = p.View; sort = p.Sort; down = p.Down; group = p.Group; p.Used = DateTime.Now; }
        else if (Places.Folders().FirstOrDefault((f) => f.Path.Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) is { Name: "Pictures" or "Videos" }) view = "gallery";
        Sort = sort; SortDown = down; Group = Groups.Any((g) => g.key == group) ? group : "none";
        if (view != View) SetView(view);
    }
    /// <summary>Saves this folder's view, sort and grouping.</summary>
    public void RememberHere()
    {
        if (CurrentPath == null) return;
        var key = CurrentPath.TrimEnd('\\');
        bool plain = View == Settings.Current.View && Sort == "name" && !SortDown && Group == "none";
        if (plain) Settings.Current.Folders.Remove(key);
        else Settings.Current.Folders[key] = new FolderPrefs { View = View, Sort = Sort, Down = SortDown, Group = Group, Used = DateTime.Now };
        if (Settings.Current.Folders.Count > 2000) foreach (var old in Settings.Current.Folders.OrderBy((f) => f.Value.Used).Take(200).Select((f) => f.Key).ToList()) Settings.Current.Folders.Remove(old);
        Settings.Save();
    }
    /// <summary>"Use this view for all folders": this view becomes the default and folders forget their own.</summary>
    public void UseViewEverywhere() { Settings.Current.View = View; Settings.Current.Folders.Clear(); Settings.Save(); }

    // ---------- Group by ----------
    private List<FileGroup>? _groups;
    public void SetGroup(string group) { Group = Groups.Any((g) => g.key == group) ? group : "none"; Regroup(); }
    private void Regroup()
    {
        if (Group == "none")
        {
            if (_groups == null) return;
            _groups = null;
            List.ItemsSource = _shown; Tiles.ItemsSource = _shown;
            return;
        }
        var cmp = Comparer();
        var groups = _shown.Snapshot().GroupBy((i) => GroupKey(i)).OrderBy((g) => GroupOrder(g.Key)).ThenBy((g) => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select((g) => { var l = g.ToList(); l.Sort(cmp); return new FileGroup(l) { Key = g.Key }; }).ToList();
        _groups = groups;
        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = groups };
        List.ItemsSource = cvs.View; Tiles.ItemsSource = cvs.View;
    }
    // Group headers ("Today (12)"), drawn in code.
    private void Items_ChoosingGroupHeader(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs e)
    {
        if (e.Group is not FileGroup g) return;
        e.GroupHeaderContainer ??= sender is GridView ? new GridViewHeaderItem() : new ListViewHeaderItem();
        e.GroupHeaderContainer.Content = new TextBlock { Text = g.Header, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxAcc") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e.GroupHeaderContainer, "Group " + g.Header);
    }
    private string GroupKey(FileItem i) => Group switch
    {
        "name" => NameGroup(i.DisplayName),
        "type" => i.TypeText,
        "size" => i.IsFolder ? "Folders" : i.Size == 0 ? "Empty (0 KB)" : i.Size < 16 << 10 ? "Tiny (0 - 16 KB)" : i.Size < 1 << 20 ? "Small (16 KB - 1 MB)" : i.Size < 128 << 20 ? "Medium (1 - 128 MB)" : i.Size < 1L << 30 ? "Large (128 MB - 1 GB)" : i.Size < 4L << 30 ? "Huge (1 - 4 GB)" : "Gigantic (> 4 GB)",
        "modified" => DateGroup(i.Modified),
        _ => "",
    };
    private static string NameGroup(string n)
    {
        var c = n.Length > 0 ? char.ToUpperInvariant(n[0]) : ' ';
        return c is >= 'A' and <= 'H' ? "A - H" : c is >= 'I' and <= 'P' ? "I - P" : c is >= 'Q' and <= 'Z' ? "Q - Z" : char.IsDigit(c) ? "0 - 9" : "Other";
    }
    private static string DateGroup(DateTime t)
    {
        if (t == default) return "Unknown";
        var today = DateTime.Today;
        int dow = ((int)today.DayOfWeek + 6) % 7; // Monday first
        if (t >= today) return t > DateTime.Now.AddMinutes(1) ? "In the future" : "Today";
        if (t >= today.AddDays(-1)) return "Yesterday";
        if (t >= today.AddDays(-dow)) return "Earlier this week";
        if (t >= today.AddDays(-dow - 7)) return "Last week";
        if (t >= new DateTime(today.Year, today.Month, 1)) return "Earlier this month";
        if (t >= new DateTime(today.Year, today.Month, 1).AddMonths(-1)) return "Last month";
        if (t >= new DateTime(today.Year, 1, 1)) return "Earlier this year";
        return "A long time ago";
    }
    private int GroupOrder(string key)
    {
        string[] order = Group switch
        {
            "modified" => new[] { "In the future", "Today", "Yesterday", "Earlier this week", "Last week", "Earlier this month", "Last month", "Earlier this year", "A long time ago", "Unknown" },
            "size" => new[] { "Folders", "Empty (0 KB)", "Tiny (0 - 16 KB)", "Small (16 KB - 1 MB)", "Medium (1 - 128 MB)", "Large (128 MB - 1 GB)", "Huge (1 - 4 GB)", "Gigantic (> 4 GB)" },
            "name" => new[] { "0 - 9", "A - H", "I - P", "Q - Z", "Other" },
            _ => Array.Empty<string>(),
        };
        var at = Array.IndexOf(order, key);
        return at < 0 ? 0 : at;
    }

    // ---------- Details columns ----------
    public event Action? ColumnsChanged;
    private static double RowHeight => Settings.Current.Compact ? 28 : 34;
    private bool _results;
    private static readonly ColumnSetting FolderColumn = new() { Key = "folder", Width = 280 };
    /// <summary>The columns shown: the chosen ones, plus Folder for results gathered from many folders.</summary>
    private List<ColumnSetting> Shown_Columns => _results && !Settings.Current.Columns.Any((c) => c.Key == "folder") ? Settings.Current.Columns.Concat(new[] { FolderColumn }).ToList() : Settings.Current.Columns;
    private void BuildHeader()
    {
        Header.Children.Clear(); Header.ColumnDefinitions.Clear();
        Header.ColumnSpacing = 12;
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        int i = 1;
        foreach (var cs in Shown_Columns)
        {
            var col = Columns.Get(cs.Key);
            Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cs.Width) });
            var cell = new Grid();
            var arrow = Sort == SortKeyOf(col.Key) ? (SortDown ? " \u2193" : " \u2191") : "";
            var b = new HyperlinkButton { Content = col.Header + arrow, Padding = new Thickness(0), Foreground = Theme.Get("OxMuted"), FontSize = 12, HorizontalAlignment = col.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left, IsEnabled = col.Sortable, Tag = col.Key, Margin = new Thickness(0, 0, col.Right ? 8 : 0, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Column " + col.Header);
            b.Click += (s, _) => { SetSort(SortKeyOf((string)((FrameworkElement)s).Tag)); };
            cell.Children.Add(b);
            // Drag the edge to make the column wider or narrower.
            var grip = new Microsoft.UI.Xaml.Controls.Primitives.Thumb { Width = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, -11, 6), Background = Theme.Get("OxLine"), Opacity = 0.6 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, "Resize " + col.Header);
            int at = i; var setting = cs;
            grip.DragDelta += (_, e) => { setting.Width = Math.Clamp(setting.Width + e.HorizontalChange, 40, 1200); Header.ColumnDefinitions[at].Width = new GridLength(setting.Width); ResizeRows(at, setting.Width); };
            grip.DragCompleted += (_, _) => { Settings.Save(); ColumnsChanged?.Invoke(); };
            grip.DoubleTapped += (_, e) => { setting.Width = Columns.Get(setting.Key).Width; Header.ColumnDefinitions[at].Width = new GridLength(setting.Width); ResizeRows(at, setting.Width); Settings.Save(); e.Handled = true; };
            grip.PointerEntered += (_, _) => ProtectedCursorFor(grip);
            Grid.SetColumn(cell, i); Header.Children.Add(cell);
            // The grip sits above the next column's header, so it can always be grabbed.
            Canvas.SetZIndex(grip, 10); Grid.SetColumn(grip, i++); Header.Children.Add(grip);
        }
    }
    private static void ProtectedCursorFor(UIElement el) { try { typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(el, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast)); } catch { } }
    private static string SortKeyOf(string column) => column switch { "modified" => "date", "name" => "name", "type" => "type", "size" => "size", "created" => "created", _ => column };
    private void ResizeRows(int col, double width)
    {
        if (List.ItemsPanelRoot == null) return;
        foreach (var c in List.ItemsPanelRoot.Children)
            if (c is ListViewItem { ContentTemplateRoot: Border { Child: Grid g } } && g.ColumnDefinitions.Count > col) g.ColumnDefinitions[col].Width = new GridLength(width);
    }
    private void HeaderHost_SizeChanged(object sender, SizeChangedEventArgs e) => HeaderHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    private void Header_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Choose columns, like right-clicking Explorer's column headers.
        var m = new MenuFlyout();
        // The column under the pointer can move left or right.
        var x = e.GetPosition(Header).X - Header.Padding.Left - 22 - 12;
        int at = -1; double edge = 0;
        for (int k = 0; k < Settings.Current.Columns.Count; k++) { edge += Settings.Current.Columns[k].Width + 12; if (x < edge) { at = k; break; } }
        if (at > 0 || (at >= 0 && at < Settings.Current.Columns.Count - 1))
        {
            var name = Columns.Get(Settings.Current.Columns[at].Key).Header;
            var left = new MenuFlyoutItem { Text = $"Move “{name}” left", IsEnabled = at > 0 };
            left.Click += (_, _) => { var c = Settings.Current.Columns[at]; Settings.Current.Columns.RemoveAt(at); Settings.Current.Columns.Insert(at - 1, c); ColumnsUpdated(); };
            var right = new MenuFlyoutItem { Text = $"Move “{name}” right", IsEnabled = at < Settings.Current.Columns.Count - 1 };
            right.Click += (_, _) => { var c = Settings.Current.Columns[at]; Settings.Current.Columns.RemoveAt(at); Settings.Current.Columns.Insert(at + 1, c); ColumnsUpdated(); };
            m.Items.Add(left); m.Items.Add(right); m.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var col in Columns.All.Where((c) => c.Key != "name"))
        {
            var on = Settings.Current.Columns.Any((c) => c.Key == col.Key);
            var mi = new ToggleMenuFlyoutItem { Text = col.Header, IsChecked = on };
            mi.Click += (_, _) =>
            {
                if (on) Settings.Current.Columns.RemoveAll((c) => c.Key == col.Key);
                else Settings.Current.Columns.Add(new ColumnSetting { Key = col.Key, Width = col.Width });
                ColumnsUpdated();
            };
            m.Items.Add(mi);
        }
        m.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Reset columns" };
        reset.Click += (_, _) => { Settings.Current.Columns = new Settings().Columns; ColumnsUpdated(); };
        m.Items.Add(reset);
        m.ShowAt(Header, e.GetPosition(Header));
        e.Handled = true;
    }
    public void ColumnsUpdated()
    {
        Settings.Save(); BuildHeader();
        foreach (var i in _all) i.PropsKey = null;
        if (View == "details") { List.ItemTemplate = null; List.ItemTemplate = (DataTemplate)Resources["DetailsTemplate"]; }
        ColumnsChanged?.Invoke();
    }

    /// <summary>One details row: the icon, then each chosen column.</summary>
    private Grid DetailsRow(FileItem item)
    {
        var g = new Grid { Height = RowHeight, ColumnSpacing = 12, Opacity = Ui.HiddenOpacity(item.IsHidden), HorizontalAlignment = HorizontalAlignment.Left };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        if (item.IsFolder) g.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Width = 20, Height = 20, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Data = HomeView.Folder(), Fill = Ui.Brush(item.FolderFill), VerticalAlignment = VerticalAlignment.Center });
        else
        {
            var img = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            img.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(FileItem.Icon)), Source = item, Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
            g.Children.Add(img);
        }
        int i = 1;
        foreach (var cs in Shown_Columns)
        {
            var col = Columns.Get(cs.Key);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cs.Width) });
            var t = new TextBlock { Text = Columns.Text(item, col.Key), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = col.Key == "name" ? 13 : 12.5 };
            if (col.Key != "name") t.Foreground = Theme.Get("OxMuted");
            if (col.Right) { t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 0, 8, 0); }
            if (col.Key == "name" && (item.TagColors.Count > 0 || Library.Current.NoteOf(item.Path) != null))
            {
                // Colour tags and a note mark after the name.
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                t.MaxWidth = Math.Max(60, cs.Width - 18 * item.TagColors.Count - 26);
                sp.Children.Add(t);
                foreach (var tc in item.TagColors) sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = Ui.Brush(tc), VerticalAlignment = VerticalAlignment.Center });
                if (Library.Current.NoteOf(item.Path) != null) sp.Children.Add(new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["OxIcons"], Glyph = "\uE70B", FontSize = 11, Foreground = Theme.Get("OxMuted") });
                Grid.SetColumn(sp, i++); g.Children.Add(sp);
                continue;
            }
            Grid.SetColumn(t, i++); g.Children.Add(t);
        }
        return g;
    }

    // ---------- Windows properties for the extra columns, only for rows on screen ----------
    private static readonly LinkedList<(FileItem item, string key, (string, string)[] wanted)> PropQueue = new();
    private static readonly SemaphoreSlim PropWork = new(0);
    private static bool _propThread;
    private void NeedProps(FileItem item)
    {
        var wanted = Settings.Current.Columns.Select((c) => Columns.Get(c.Key)).Where((c) => !c.Builtin).Select((c) => (c.Key, c.Property!)).ToArray();
        if (wanted.Length == 0 || item.IsFolder && wanted.All((w) => w.Item1 != "owner")) return;
        var key = string.Join(",", wanted.Select((w) => w.Item1));
        if (item.PropsKey == key) return;
        // Cloud files that aren't on this PC are left alone: reading them could start a download.
        if ((item.Attributes & (System.IO.FileAttributes)0x400000) != 0 || (item.Attributes & System.IO.FileAttributes.Offline) != 0) { item.PropsKey = key; return; }
        lock (PropQueue)
        {
            for (var n = PropQueue.First; n != null; n = n.Next) if (n.Value.item == item) { PropQueue.Remove(n); break; }
            PropQueue.AddFirst((item, key, wanted));
            if (PropQueue.Count > 400) PropQueue.RemoveLast();
            if (!_propThread)
            {
                _propThread = true;
                var t = new Thread(PropLoop) { IsBackground = true, Name = "properties", Priority = ThreadPriority.BelowNormal };
                t.SetApartmentState(ApartmentState.STA); t.Start();
            }
        }
        PropWork.Release();
    }
    private void PropLoop()
    {
        while (true)
        {
            PropWork.Wait();
            (FileItem item, string key, (string, string)[] wanted) job;
            lock (PropQueue) { if (PropQueue.First == null) continue; job = PropQueue.First.Value; PropQueue.RemoveFirst(); }
            Dictionary<string, string> found;
            try { found = Props.Read(job.item.Path, job.wanted); } catch { found = new(); }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                job.item.Props = found; job.item.PropsKey = job.key;
                if (View == "details" && List.ContainerFromItem(job.item) is ListViewItem { ContentTemplateRoot: Border b }) b.Child = DetailsRow(job.item);
            });
        }
    }
    // ---------- Folder sizes ("Show folder sizes") ----------
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long size, DateTime when)> Sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sizeGate = new(2);
    private void NeedSize(FileItem item)
    {
        if (Sizes.TryGetValue(item.Path, out var known) && DateTime.Now - known.when < TimeSpan.FromMinutes(10)) { item.FolderSize = known.size; return; }
        _ = Task.Run(async () =>
        {
            await _sizeGate.WaitAsync();
            try
            {
                long total = 0;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.ReparsePoint };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { foreach (var f in new DirectoryInfo(item.Path).EnumerateFiles("*", opts)) { total += f.Length; if (sw.ElapsedMilliseconds > 15000) break; } } catch { }
                Sizes[item.Path] = (total, DateTime.Now);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    item.FolderSize = total;
                    if (View == "details" && List.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Border b }) b.Child = DetailsRow(item);
                });
            }
            finally { _sizeGate.Release(); }
        });
    }
    public void Redraw() { if (View == "details") { List.ItemTemplate = null; List.ItemTemplate = (DataTemplate)Resources["DetailsTemplate"]; } else { var t = Tiles.ItemTemplate; Tiles.ItemTemplate = null; Tiles.ItemTemplate = t; } }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d); i++) if (FindScroll(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i)) is { } f) return f;
        return null;
    }

    /// <summary>Opens a folder: the first few hundred items show at once, the rest stream in.</summary>
    public async Task LoadAsync(string path, string? selectName = null)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        CurrentPath = path;
        _all = new(); _shown.Clear(); _filter = "";
        if (_results) { _results = false; BuildHeader(); Redraw(); }
        var before = (View, Sort, SortDown, Group);
        UsePrefsFor(path);
        if (_groups != null) { _groups = null; List.ItemsSource = _shown; Tiles.ItemsSource = _shown; }
        if (before != (View, Sort, SortDown, Group)) { BuildHeader(); ViewChanged?.Invoke(); }
        Loading.Visibility = Visibility.Visible; Empty.Visibility = Visibility.Collapsed;
        Watch(path);
        var collected = new List<FileItem>();
        bool first = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await DirectoryReader.ReadAsync(path, Settings.Current.ShowHidden, (batch) =>
            {
                lock (collected) collected.AddRange(batch);
                if (first) { first = false; var copy = batch.ToList(); copy.Sort(Comparer()); DispatcherQueue.TryEnqueue(() => { if (_cts == cts) { _shown.ReplaceAll(copy); Perf.Log($"first {copy.Count} shown in {clock.ElapsedMilliseconds} ms"); } }); }
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            if (_cts != cts) return;
            Loading.Visibility = Visibility.Collapsed;
            LoadFailed?.Invoke(path, e.Message);
            return;
        }
        if (_cts != cts) return;
        var sorted = await Task.Run(() => { var l = collected; l.Sort(Comparer()); return l; });
        if (_cts != cts) return;
        _all = sorted;
        _shown.ReplaceAll(new List<FileItem>(_all));
        Regroup();
        Loading.Visibility = Visibility.Collapsed;
        ShowCount();
        if (selectName != null) Reveal(selectName);
        else if (_all.Count > 0) Active.ScrollIntoView(_all[0]); // start at the top
        Perf.Log($"folder {path}: {_all.Count} items in {clock.ElapsedMilliseconds} ms");
        Loaded2?.Invoke();
    }

    /// <summary>Search results (from many folders) shown like a folder.</summary>
    public void ShowResults(List<FileItem> items, string? emptyText)
    {
        _cts?.Cancel(); _watcher?.Dispose(); _watcher = null; CurrentPath = null;
        _filter = "";
        if (!_results) { _results = true; BuildHeader(); Redraw(); }
        items.Sort(Comparer());
        _all = items; _shown.ReplaceAll(new List<FileItem>(items));
        if (_groups != null) Regroup();
        Loading.Visibility = Visibility.Collapsed;
        Empty.Visibility = Ui.Show(items.Count == 0);
        if (emptyText != null) EmptyText.Text = emptyText;
        Loaded2?.Invoke();
    }
    public void ShowSearching() { _all = new(); _shown.Clear(); Empty.Visibility = Visibility.Collapsed; Loading.Visibility = Visibility.Visible; }

    public void Reveal(string name)
    {
        var item = _shown.FirstOrDefault((i) => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        Active.SelectedItem = item; Active.ScrollIntoView(item);
    }

    public void Unload() { _cts?.Cancel(); _watcher?.Dispose(); _watcher = null; CurrentPath = null; _all = new(); _shown.Clear(); }

    private void ShowCount()
    {
        Empty.Visibility = Ui.Show(_shown.Count == 0 && Loading.Visibility != Visibility.Visible);
        EmptyText.Text = _filter.Length > 0 ? $"Nothing here matches \"{_filter}\"" : "This folder is empty";
    }

    // ---------- Sort and filter ----------
    private Comparison<FileItem> Comparer()
    {
        Comparison<FileItem> c = Sort switch
        {
            "date" => (a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : b.Modified.CompareTo(a.Modified),
            "size" => (a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : b.Size.CompareTo(a.Size),
            "type" => (a, b) => { var t = string.Compare(a.TypeText, b.TypeText, StringComparison.OrdinalIgnoreCase); return a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : t != 0 ? t : DirectoryReader.CompareByName(a, b); },
            "created" => (a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : b.Created.CompareTo(a.Created),
            _ => DirectoryReader.CompareByName,
        };
        if (!SortDown) return c;
        return (a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : -c(a, b);
    }

    public async void SetSort(string sort, bool? down = null)
    {
        SortDown = down ?? (Sort == sort && !SortDown);
        Sort = sort;
        var cmp = Comparer();
        var l = await Task.Run(() => { var copy = new List<FileItem>(_all); copy.Sort(cmp); return copy; });
        _all = l;
        ApplyFilter();
        BuildHeader(); RememberHere(); ViewChanged?.Invoke();
    }

    public void SetFilter(string text) { _filter = text.Trim(); ApplyFilter(); }
    /// <summary>Quick filters: kinds of file, or how recently they changed.</summary>
    public string Quick { get; private set; } = "all";
    public static readonly (string key, string label)[] QuickFilters = { ("all", "Everything"), ("pictures", "Pictures"), ("videos", "Videos"), ("music", "Music"), ("documents", "Documents"), ("archives", "Archives"), ("today", "Changed today"), ("week", "Changed this week"), ("month", "Changed this month"), ("big", "Bigger than 100 MB") };
    public void SetQuick(string key) { Quick = key; ApplyFilter(); }
    private bool QuickOk(FileItem i) => Quick switch
    {
        "pictures" => i.Kind == FileKind.Picture, "videos" => i.Kind == FileKind.Video, "music" => i.Kind == FileKind.Music,
        "documents" => i.Kind is FileKind.Document or FileKind.Pdf or FileKind.Sheet or FileKind.Slides or FileKind.Text,
        "archives" => i.Kind == FileKind.Archive,
        "today" => i.Modified >= DateTime.Today, "week" => i.Modified >= DateTime.Today.AddDays(-7), "month" => i.Modified >= DateTime.Today.AddMonths(-1),
        "big" => !i.IsFolder && i.Size > 100L << 20,
        _ => true,
    };
    private void ApplyFilter()
    {
        _shown.ReplaceAll(_all.Where((i) => (_filter.Length == 0 || i.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) && QuickOk(i)).ToList());
        Regroup();
        ShowCount();
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e) => SetSort((string)((FrameworkElement)sender).Tag);

    // ---------- Watching the folder (files added, removed or renamed by anyone) ----------
    private void Watch(string path)
    {
        _watcher?.Dispose(); _watcher = null;
        try
        {
            var w = new FileSystemWatcher(path) { IncludeSubdirectories = false, InternalBufferSize = 64 * 1024, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes };
            FileSystemEventHandler on = (_, e) => Changed(e.FullPath);
            w.Created += on; w.Deleted += on; w.Changed += on;
            w.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
            w.Error += (_, _) => DispatcherQueue.TryEnqueue(() => { if (CurrentPath != null) _ = LoadAsync(CurrentPath); });
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* some places can't be watched (some network drives); refresh by hand there */ }
    }
    private void Changed(string fullPath) { lock (_pendingChanges) _pendingChanges.Add(fullPath); DispatcherQueue.TryEnqueue(() => { _watchTimer.Stop(); _watchTimer.Start(); }); }

    private void ApplyWatchedChanges()
    {
        string[] paths;
        lock (_pendingChanges) { paths = _pendingChanges.ToArray(); _pendingChanges.Clear(); }
        if (CurrentPath == null || paths.Length == 0) return;
        if (paths.Length > 300) { _ = LoadAsync(CurrentPath); return; } // a big change: just read again
        var cmp = Comparer();
        foreach (var p in paths)
        {
            if (!string.Equals(System.IO.Path.GetDirectoryName(p), CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !string.Equals(System.IO.Path.GetDirectoryName(p) + "\\", CurrentPath, StringComparison.OrdinalIgnoreCase)) continue;
            var old = _all.FindIndex((i) => i.Path.Equals(p, StringComparison.OrdinalIgnoreCase));
            FileItem? now = null;
            try
            {
                if (Directory.Exists(p)) { var d = new DirectoryInfo(p); now = new FileItem { Name = d.Name, Path = d.FullName, IsFolder = true, Modified = d.LastWriteTime, Created = d.CreationTime, Accessed = d.LastAccessTime, Attributes = d.Attributes }; }
                else if (File.Exists(p)) { var f = new FileInfo(p); now = new FileItem { Name = f.Name, Path = f.FullName, Size = f.Length, Modified = f.LastWriteTime, Created = f.CreationTime, Accessed = f.LastAccessTime, Attributes = f.Attributes }; }
                if (now != null && !Settings.Current.ShowHidden && now.IsHidden) now = null;
            }
            catch { now = null; }
            if (old >= 0)
            {
                var gone = _all[old]; _all.RemoveAt(old);
                var si = _shown.IndexOf(gone);
                if (now != null && si >= 0) { _shown[si] = now; } else if (si >= 0) _shown.RemoveAt(si);
            }
            if (now != null)
            {
                var at = _all.BinarySearch(now, Comparer<FileItem>.Create(cmp)); if (at < 0) at = ~at;
                _all.Insert(at, now);
                if (!_shown.Contains(now) && (_filter.Length == 0 || now.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
                {
                    var sAt = 0; while (sAt < _shown.Count && cmp(_shown[sAt], now) < 0) sAt++;
                    _shown.Insert(sAt, now);
                }
            }
        }
        if (_groups != null) Regroup();
        ShowCount();
        Loaded2?.Invoke(); // the status bar's count follows files that arrive or leave
    }

    // ---------- Pictures: only for rows on screen ----------
    private void Items_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FileItem item) return;
        if (args.InRecycleQueue) { Thumbnails.Cancel(item); return; }
        if (View == "details" && args.ItemContainer.ContentTemplateRoot is Border holder) { holder.Child = DetailsRow(item); NeedProps(item); if (item.IsFolder && Settings.Current.FolderSizes && item.FolderSize == null) NeedSize(item); }
        if (ToolTipService.GetToolTip(args.ItemContainer) is not ToolTip)
        {
            var tip = new ToolTip();
            tip.Opened += (_, _) => { if (((SelectorItem)tip.Tag).Content is FileItem f) tip.Content = TipFor(f); };
            tip.Tag = args.ItemContainer;
            ToolTipService.SetToolTip(args.ItemContainer, tip);
        }
        if (item.IsFolder) return;
        Thumbnails.Request(item, Info(View).Icon);
    }

    /// <summary>What Explorer shows on hover: type, size, date, and for pictures and media their size or length.</summary>
    private static string TipFor(FileItem f)
    {
        var lines = new List<string> { f.Name };
        if (f.IsFolder) lines.Add("Date modified: " + f.ModifiedText);
        else
        {
            lines.Add("Type: " + f.TypeText);
            if (f.Props != null && f.Props.TryGetValue("dimensions", out var d)) lines.Add("Dimensions: " + d);
            if (f.Props != null && f.Props.TryGetValue("length", out var l)) lines.Add("Length: " + l);
            lines.Add("Size: " + f.SizeText);
            lines.Add("Date modified: " + f.ModifiedText);
        }
        return string.Join("\n", lines);
    }

    // ---------- Opening, selecting, menus ----------
    /// <summary>Item check boxes: a box on each item; clicking items adds them to the selection.</summary>
    public void UseCheckBoxes(bool on)
    {
        foreach (var l in new ListViewBase[] { List, Tiles }) { l.SelectionMode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended; l.IsMultiSelectCheckBoxEnabled = on; }
    }
    /// <summary>Folder options: open with one click.</summary>
    public void UseSingleClick(bool on) { List.IsItemClickEnabled = on; Tiles.IsItemClickEnabled = on; }
    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        var mods = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) | Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if ((mods & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) { if (e.ClickedItem is FileItem f) { if (Active.SelectedItems.Contains(f)) Active.SelectedItems.Remove(f); else Active.SelectedItems.Add(f); } return; } // Ctrl/Shift+click still selects
        if (e.ClickedItem is FileItem item) OpenRequested?.Invoke(item);
    }
    private void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Settings.Current.SingleClick) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item) OpenRequested?.Invoke(item);
    }
    private void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && Active.SelectedItem is FileItem item) { OpenRequested?.Invoke(item); e.Handled = true; }
    }
    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionChangedOut?.Invoke();
    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
        if (item != null && !Active.SelectedItems.Contains(item)) { Active.SelectedItems.Clear(); Active.SelectedItem = item; }
        if (item == null) Active.SelectedItems.Clear();
        MenuRequested?.Invoke(item, e.GetPosition(this), this);
        e.Handled = true;
    }
    private void Background_RightTapped(object sender, RightTappedRoutedEventArgs e) { if (!e.Handled) { Active.SelectedItems.Clear(); MenuRequested?.Invoke(null, e.GetPosition(this), this); } }

    // ---------- Drag and drop, with Explorer and every other app ----------
    /// <summary>The drive the current drag (from an Omebly window) started on, for the same-drive-moves rule.</summary>
    private static string? _dragRoot;
    private void Items_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _dragRoot = null;
    private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.Cast<FileItem>().Select((i) => (i.Path, i.IsFolder)).ToList();
        _dragRoot = paths.Count > 0 ? System.IO.Path.GetPathRoot(paths[0].Path) : null;
        Perf.Log($"drag start: {paths.Count} items");
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async (request) =>
        {
            var d = request.GetDeferral();
            try { request.SetData(await Core.Clip.ToStorageItems(paths)); }
            finally { d.Complete(); }
        });
    }
    private void Root_DragOver(object sender, DragEventArgs e) => AcceptDrag(e, CurrentPath);
    private void Item_DragOver(object sender, DragEventArgs e)
    {
        var target = ItemUnder(e);
        AcceptDrag(e, target?.IsFolder == true ? target.Path : CurrentPath);
        e.Handled = true;
    }
    /// <summary>The file or folder under the pointer during a drag (the folder a drop goes into).</summary>
    private FileItem? ItemUnder(DragEventArgs e)
    {
        var at = e.GetPosition(null);
        foreach (var el in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(at, Active))
            if (el is SelectorItem si && si.Content is FileItem fi) return fi;
        return (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
    }
    public static DropKind KindFor(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers m, string target, string? sourceRoot)
    {
        var M = Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.None;
        bool ctrl = (m & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Control) != M, shift = (m & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) != M, alt = (m & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Alt) != M;
        if (alt || (ctrl && shift)) return DropKind.Link;
        if (ctrl) return DropKind.Copy;
        if (shift) return DropKind.Move;
        return sourceRoot != null && string.Equals(sourceRoot, System.IO.Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase) ? DropKind.Move : DropKind.Copy;
    }
    private static void AcceptDrag(DragEventArgs e, string? target)
    {
        if (target == null || !e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var kind = KindFor(e.Modifiers, target, _dragRoot);
        e.AcceptedOperation = kind switch { DropKind.Move => DataPackageOperation.Move, DropKind.Link => DataPackageOperation.Link, _ => DataPackageOperation.Copy };
        e.DragUIOverride.Caption = (kind switch { DropKind.Move => "Move to ", DropKind.Link => "Create shortcut in ", _ => "Copy to " }) + System.IO.Path.GetFileName(target.TrimEnd('\\'));
    }
    private async void Root_Drop(object sender, DragEventArgs e) => await Dropped(e, CurrentPath);
    private async void Item_Drop(object sender, DragEventArgs e)
    {
        var target = ItemUnder(e);
        e.Handled = true;
        await Dropped(e, target?.IsFolder == true ? target.Path : CurrentPath);
    }
    private async Task Dropped(DragEventArgs e, string? target)
    {
        Perf.Log($"drop on {target}: has files {e.DataView.Contains(StandardDataFormats.StorageItems)}");
        if (target == null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var d = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select((i) => i.Path).Where((p) => !string.IsNullOrEmpty(p)).ToList();
            var kind = KindFor(e.Modifiers, target, _dragRoot);
            // Dropped back where they came from, or a folder onto itself: nothing to do (like Explorer).
            static string T(string x) => x.TrimEnd('\\');
            if (kind != DropKind.Link) paths = paths.Where((p) => !string.Equals(System.IO.Path.GetDirectoryName(T(p)), T(target), StringComparison.OrdinalIgnoreCase) && !string.Equals(T(p), T(target), StringComparison.OrdinalIgnoreCase)).ToList();
            if (paths.Count > 0) FilesDropped?.Invoke(paths, target, kind);
        }
        finally { d.Complete(); }
    }
}
