// An archive (ZIP, 7Z, RAR, TAR, GZ) opened like a folder, without unpacking it: go into its folders, open a file
// (it is unpacked to a private temporary folder first), or extract some or all of it.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed class ArchiveView : Grid
{
    public event Action<string, string>? ExtractWanted;      // archive, inside path ("" = everything)
    public event Action<FileItem>? OpenFile;                  // a file unpacked to the temporary folder
    public event Action<string>? InsideChanged;               // the inside folder now shown
    public Func<string, Task<string?>>? AskPassword;           // archive name -> password (null = cancel)
    public string Archive { get; private set; } = "";
    public string Inside { get; private set; } = "";
    private string? _password;
    private List<ArchiveEntry> _all = new();
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(10, 4, 10, 10) };
    private readonly TextBlock _note = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center };
    public int Count => _list.Items.Count;

    public ArchiveView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(18, 12, 18, 10) };
        bar.Children.Add(Btn("", "Up", Up));
        bar.Children.Add(Btn("", "Extract all", () => ExtractWanted?.Invoke(Archive, "")));
        bar.Children.Add(Btn("", "Extract selected", () => { foreach (var e in Selected) ExtractWanted?.Invoke(Archive, e.Path); }));
        Children.Add(bar);
        _list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(FrameworkElement.MinHeightProperty, 34.0), new Setter(Control.CornerRadiusProperty, new CornerRadius(7)) } };
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border/></DataTemplate>");
        _list.ContainerContentChanging += (_, a) => { if (!a.InRecycleQueue && a.ItemContainer.ContentTemplateRoot is Border b && a.Item is Row r) b.Child = Line(r); };
        _list.DoubleTapped += async (_, e) => { if ((e.OriginalSource as FrameworkElement)?.DataContext is Row r) await Open(r); else if (_list.SelectedItem is Row s) await Open(s); };
        _list.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && _list.SelectedItem is Row r) { e.Handled = true; await Open(r); } else if (e.Key == Windows.System.VirtualKey.Back) { e.Handled = true; Up(); } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_list, "ArchiveList");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_list, "Inside the archive");
        Grid.SetRow(_list, 1); Children.Add(_list);
        _note.Foreground = Theme.Get("OxMuted"); Grid.SetRow(_note, 1); Children.Add(_note);
    }

    private sealed record Row(ArchiveEntry Entry, string Name) { public override string ToString() => Name; }
    private IEnumerable<ArchiveEntry> Selected => _list.SelectedItems.OfType<Row>().Select((r) => r.Entry);

    public async Task LoadAsync(string archive, string inside)
    {
        if (archive != Archive) { _password = null; _all = new(); }
        Archive = archive; Inside = inside;
        _note.Visibility = Visibility.Collapsed;
        if (_all.Count == 0)
        {
            while (true)
            {
                try { var pw = _password; _all = await Task.Run(() => Archives.List(archive, pw)); break; }
                catch (NeedsPassword)
                {
                    _password = AskPassword == null ? null : await AskPassword(System.IO.Path.GetFileName(archive));
                    if (_password == null) { Show("This archive is locked with a password."); return; }
                }
                catch (Exception e) { Show(e is ArchiveProblem ? e.Message : "This archive can't be read: " + e.Message); return; }
            }
        }
        var here = _all.Where((e) => string.Equals(System.IO.Path.GetDirectoryName(e.Path) ?? "", inside, StringComparison.OrdinalIgnoreCase))
            .OrderBy((e) => !e.IsFolder).ThenBy((e) => System.IO.Path.GetFileName(e.Path), Comparer<string>.Create((a, b) => Native.StrCmpLogicalW(a, b)))
            .Select((e) => new Row(e, System.IO.Path.GetFileName(e.Path))).ToList();
        _list.ItemsSource = here;
        if (here.Count == 0) Show("This folder is empty");
        InsideChanged?.Invoke(inside);
        _list.Focus(FocusState.Programmatic);
    }
    public string? Password => _password;
    private void Show(string text) { _list.ItemsSource = null; _note.Text = text; _note.Visibility = Visibility.Visible; }

    private void Up()
    {
        if (Inside.Length == 0) return;
        _ = LoadAsync(Archive, System.IO.Path.GetDirectoryName(Inside) ?? "");
    }

    private async Task Open(Row r)
    {
        if (r.Entry.IsFolder) { await LoadAsync(Archive, r.Entry.Path); return; }
        // Unpacked to Omebly's own temporary folder, then shown in the viewer.
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Omebly Explorer", "Archives", Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(Archive)).ToString("x"));
        try
        {
            var pw = _password; var archive = Archive; var key = r.Entry.Path;
            var made = await Task.Run(() => Archives.Extract(archive, tmp, pw, new[] { key }, new ArchiveProgress(), CancellationToken.None));
            if (made.Count == 0) return;
            var fi = new FileInfo(made[0]);
            OpenFile?.Invoke(new FileItem { Name = fi.Name, Path = fi.FullName, Size = fi.Length, Modified = fi.LastWriteTime });
        }
        catch (Exception e) { Show("This file couldn't be opened: " + e.Message); }
    }

    private static Button Btn(string glyph, string text, Action run)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 13 });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 13 });
        var b = new Button { Content = sp, Height = 34, Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(8), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(1) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => run();
        return b;
    }

    private static FrameworkElement Line(Row r)
    {
        var g = new Grid { Height = 34, ColumnSpacing = 12 };
        foreach (var w in new[] { new GridLength(22), new GridLength(1, GridUnitType.Star), new GridLength(160), new GridLength(100), new GridLength(100) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        g.Children.Add(r.Entry.IsFolder
            ? new Microsoft.UI.Xaml.Shapes.Path { Width = 19, Height = 17, Stretch = Stretch.Uniform, Data = HomeView.Folder(), Fill = Ui.Brush("#F4C04E"), VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = r.Entry.Encrypted ? "" : "", FontSize = 15, Foreground = Theme.Get("OxMuted"), VerticalAlignment = VerticalAlignment.Center });
        int c = 1;
        foreach (var (t, right) in new[] { (r.Name, false), (r.Entry.Modified == default ? "" : Format.When(r.Entry.Modified), false), (r.Entry.IsFolder ? "" : Format.Size(r.Entry.Size), true), (r.Entry.IsFolder || r.Entry.Size == 0 ? "" : $"{100 - Math.Min(100, r.Entry.Packed * 100 / Math.Max(1, r.Entry.Size))}% smaller", true) })
        {
            var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = c == 1 ? 13 : 12.5, Foreground = Theme.Get(c == 1 ? "OxText" : "OxMuted"), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            Grid.SetColumn(tb, c++); g.Children.Add(tb);
        }
        return g;
    }
}
