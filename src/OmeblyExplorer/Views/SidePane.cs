// The panel on the right, like Explorer's: the preview pane (Alt+P) shows the selected file with the same viewers
// as the pop-up; the details pane (Alt+Shift+P) shows its picture and facts and lets you edit title, tags, authors,
// rating and comments. Drag its left edge to make it wider.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using OmeblyExplorer.Views.Viewers;

namespace OmeblyExplorer.Views;

public sealed class SidePane : Grid
{
    public string Mode { get; private set; } = "none"; // none, preview, details
    private readonly Grid _body = new();
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private ViewerBase? _viewer;
    private CancellationTokenSource? _cts;
    private FileItem? _item;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _wait;
    private (FileItem? item, int count, string? folder) _pending;
    private bool _suspended;
    public event Action? Closed;

    public SidePane()
    {
        Visibility = Visibility.Collapsed;
        Width = Math.Clamp(Settings.Current.PaneWidth, 240, 900);
        Background = Theme.Get("OxSide"); BorderBrush = Theme.Get("OxLine"); BorderThickness = new Thickness(1, 0, 0, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var top = new Grid { Padding = new Thickness(16, 10, 8, 10), ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title.Foreground = Theme.Get("OxText");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_title, "PaneTitle");
        top.Children.Add(_title);
        var close = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "", FontSize = 11 }, Width = 30, Height = 30, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close pane");
        close.Click += (_, _) => { SetMode("none"); Closed?.Invoke(); };
        Grid.SetColumn(close, 1); top.Children.Add(close);
        Children.Add(top);
        Grid.SetRow(_body, 1); Children.Add(_body);
        // The left edge: drag to resize.
        var grip = new Microsoft.UI.Xaml.Controls.Primitives.Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-3, 0, 0, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, "Resize pane");
        Grid.SetRowSpan(grip, 2); Canvas.SetZIndex(grip, 5);
        grip.DragDelta += (_, e) => Width = Math.Clamp(Width - e.HorizontalChange, 240, 900);
        grip.DragCompleted += (_, _) => { Settings.Current.PaneWidth = Width; Settings.Save(); };
        Children.Add(grip);
        _wait = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _wait.Interval = TimeSpan.FromMilliseconds(180); _wait.IsRepeating = false; // moving through files quickly doesn't load each one
        _wait.Tick += (_, _) => _ = Load(_pending.item, _pending.count, _pending.folder);
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        Visibility = Ui.Show(mode != "none");
        if (mode == "none") Clear();
        else { _item = null; _ = Load(_pending.item, _pending.count, _pending.folder); }
    }

    /// <summary>The selection changed: show it (after a short pause).</summary>
    public void Show(FileItem? item, int count, string? folder)
    {
        _pending = (item, count, folder);
        if (Mode == "none" || _suspended) return;
        _wait.Stop(); _wait.Start();
    }

    /// <summary>While the big viewer is open over everything, Windows' preview windows here must not show through.</summary>
    public void Suspend(bool on) { _suspended = on; if (on) Clear(); else if (Mode != "none") { _item = null; _ = Load(_pending.item, _pending.count, _pending.folder); } }

    private void Clear() { _cts?.Cancel(); _viewer?.Close(); _viewer = null; _item = null; _body.Children.Clear(); }

    private async Task Load(FileItem? item, int count, string? folder)
    {
        if (Mode == "none" || _suspended) return;
        if (item != null && item == _item) return;
        Clear();
        _item = item;
        if (item == null)
        {
            _title.Text = count > 1 ? $"{count:N0} items selected" : folder != null ? TabState.NameOf(folder) : "";
            _body.Children.Add(Message(count > 1 ? $"{count:N0} items selected" : Mode == "preview" ? "Select a file to preview it" : "Select a file to see its details"));
            return;
        }
        _title.Text = item.Name;
        if (Mode == "preview")
        {
            if (item.IsFolder) { _body.Children.Add(Message("Folders don't have a preview", "Double-click to open it.")); return; }
            var cts = _cts = new CancellationTokenSource();
            var v = ViewerKinds.Create(ViewerKinds.Of(item), new[] { item }, pane: true);
            _viewer = v; _body.Children.Add(v);
            try { await v.LoadAsync(item, cts.Token); } catch (OperationCanceledException) { } catch (Exception e) { CrashLog.Write(e); }
        }
        else await ShowDetails(item);
    }

    // ---------- Details ----------
    private static readonly (string label, string prop, bool list, bool edit)[] Facts =
    {
        ("Title", "System.Title", false, true), ("Authors", "System.Author", true, true), ("Tags", "System.Keywords", true, true),
        ("Comments", "System.Comment", false, true), ("Rating", "System.Rating", false, true),
        ("Dimensions", "System.Image.Dimensions", false, false), ("Length", "System.Media.Duration", false, false),
        ("Date taken", "System.Photo.DateTaken", false, false), ("Camera model", "System.Photo.CameraModel", false, false),
        ("Contributing artists", "System.Music.Artist", true, false), ("Album", "System.Music.AlbumTitle", false, false),
        ("Pages", "System.Document.PageCount", false, false), ("Bit rate", "System.Audio.EncodingBitrate", false, false),
        ("Owner", "System.FileOwner", false, false), ("Computer", "System.ComputerName", false, false),
    };

    private async Task ShowDetails(FileItem item)
    {
        var sp = new StackPanel { Spacing = 10, Padding = new Thickness(16, 4, 16, 20) };
        var pic = new Image { Height = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        if (!item.IsFolder) { Thumbnails.Request(item, 200); pic.SetBinding(Image.SourceProperty, new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(nameof(FileItem.Icon)), Source = item, Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay }); sp.Children.Add(pic); }
        else sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = HomeView.Folder(), Fill = Ui.Brush("#F4C04E"), Width = 120, Height = 100, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = item.TypeText, FontSize = 12.5, Foreground = Theme.Get("OxMuted"), HorizontalAlignment = HorizontalAlignment.Center });
        void Row(string k, string v) { if (string.IsNullOrEmpty(v)) return; sp.Children.Add(Fact(k, v)); }
        Row("Size", item.IsFolder ? "" : $"{Format.Size(item.Size)} ({item.Size:N0} bytes)");
        Row("Date modified", item.Modified == default ? "" : item.Modified.ToString("d MMM yyyy, HH:mm"));
        Row("Date created", item.Created == default ? "" : item.Created.ToString("d MMM yyyy, HH:mm"));
        Row("Where", System.IO.Path.GetDirectoryName(item.Path) ?? "");
        Row("Attributes", Columns.Text(item, "attributes"));
        Row("Note", Library.Current.NoteOf(item.Path) ?? "");
        if (item.TagColors.Count > 0) Row("Tags", string.Join(", ", item.TagColors.Select((c) => Library.TagColors.FirstOrDefault((t) => t.color == c).name ?? c)));
        var scroll = new ScrollViewer { Content = sp };
        _body.Children.Add(scroll);
        // Windows properties, read in the background; editable ones get a box.
        var path = item.Path;
        var cts = _cts = new CancellationTokenSource();
        var props = await Task.Run(() => Props.Read(path, Facts.Select((f) => (f.label, f.prop))));
        if (cts.IsCancellationRequested || _item != item) return;
        bool canWrite = !item.IsFolder && (item.Attributes & System.IO.FileAttributes.ReadOnly) == 0;
        foreach (var f in Facts)
        {
            props.TryGetValue(f.label, out var v);
            if (f.edit && canWrite && Editable(item)) sp.Children.Add(EditBox(item, f, v ?? ""));
            else if (!string.IsNullOrEmpty(v)) sp.Children.Add(Fact(f.label, v));
        }
    }

    /// <summary>Kinds of files Windows can store titles, tags and ratings in (pictures, music, video, Office, PDF needs an app).</summary>
    private static bool Editable(FileItem f) => f.Extension is ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".png" or ".heic" or ".mp3" or ".m4a" or ".wma" or ".flac" or ".mp4" or ".m4v" or ".mov" or ".wmv" or ".docx" or ".xlsx" or ".pptx";

    private UIElement EditBox(FileItem item, (string label, string prop, bool list, bool edit) f, string value)
    {
        var box = new StackPanel { Spacing = 3 };
        box.Children.Add(new TextBlock { Text = f.label, FontSize = 12, Foreground = Theme.Get("OxFaint") });
        if (f.label == "Rating")
        {
            int stars = value.Length > 0 && char.IsDigit(value[0]) ? value[0] - '0' : 0;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            for (int s = 1; s <= 5; s++)
            {
                int n = s;
                var b = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = n <= stars ? "" : "", FontSize = 15, Foreground = n <= stars ? Ui.Brush("#F4C04E") : Theme.Get("OxMuted") }, Padding = new Thickness(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{n} star{(n > 1 ? "s" : "")}");
                b.Click += async (_, _) =>
                {
                    var ok = await Task.Run(() => Props.Write(item.Path, f.prop, (n switch { 1 => 1, 2 => 25, 3 => 50, 4 => 75, _ => 99 }).ToString()));
                    if (ok) { _item = null; await Load(item, 1, null); } else Saved(box, false);
                };
                row.Children.Add(b);
            }
            box.Children.Add(row);
            return box;
        }
        var tb = new TextBox { Text = value.Replace("; ", ";"), PlaceholderText = f.list ? "Add, separated by ;" : "Add", FontSize = 13, AcceptsReturn = f.label == "Comments", TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tb, f.label);
        var start = tb.Text;
        async void Save()
        {
            if (tb.Text == start) return;
            var text = tb.Text.Trim();
            var ok = await Task.Run(() => Props.Write(item.Path, f.prop, text, f.list));
            if (ok) start = tb.Text;
            Saved(box, ok);
        }
        tb.LostFocus += (_, _) => Save();
        tb.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && f.label != "Comments") { e.Handled = true; Save(); } };
        box.Children.Add(tb);
        return box;
    }
    private static void Saved(StackPanel box, bool ok)
    {
        var note = box.Children.OfType<TextBlock>().FirstOrDefault((t) => t.Tag as string == "note");
        if (note == null) { note = new TextBlock { Tag = "note", FontSize = 11.5 }; box.Children.Add(note); }
        note.Text = ok ? "Saved in the file" : "This file can't keep this (or it's open in another app)";
        note.Foreground = ok ? Theme.Get("OxAcc") : Theme.Get("OxWarn");
    }

    private static UIElement Fact(string k, string v)
    {
        var r = new StackPanel { Spacing = 2 };
        r.Children.Add(new TextBlock { Text = k, FontSize = 12, Foreground = Theme.Get("OxFaint") });
        r.Children.Add(new TextBlock { Text = v, FontSize = 13, Foreground = Theme.Get("OxText"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        return r;
    }
    private static UIElement Message(string title, string? detail = null)
    {
        var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Foreground = Theme.Get("OxMuted"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (detail != null) sp.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Theme.Get("OxFaint"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return sp;
    }
}
