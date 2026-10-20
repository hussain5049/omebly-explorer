// A simple list for places that aren't plain folders: a phone over USB, an FTP or SFTP server. Go into folders,
// copy to the PC, paste files from the PC into it (Ctrl+V or the button).
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed record BrowseRow(string Name, bool IsFolder, long Size, DateTime Modified, object Tag) { public override string ToString() => Name; }

public sealed class BrowseView : Grid
{
    public Func<string, Task<List<BrowseRow>>>? Lister;            // inside path -> rows
    public Func<IReadOnlyList<BrowseRow>, Task>? CopyOut;           // copy rows to the PC
    public Func<Task>? PasteIn;                                     // paste PC files here
    public Func<BrowseRow, Task>? OpenFile;
    public event Action<string>? InsideChanged;
    public string Inside { get; private set; } = "";
    public string Title { get; set; } = "";
    public int Count => _list.Items.Count;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(10, 4, 10, 10) };
    private readonly TextBlock _note = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, TextAlignment = TextAlignment.Center };
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };

    public BrowseView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(18, 12, 18, 10) };
        bar.Children.Add(Btn("", "Up", () => { if (Inside.Length > 0) _ = Load(Up(Inside)); }));
        bar.Children.Add(Btn("", "Copy to the PC", () => { if (CopyOut != null) _ = CopyOut(Selected.Count > 0 ? Selected : _list.Items.OfType<BrowseRow>().ToList()); }));
        bar.Children.Add(Btn("", "Paste from the PC", () => { if (PasteIn != null) _ = PasteIn(); }));
        Children.Add(bar);
        _list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Microsoft.UI.Xaml.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(FrameworkElement.MinHeightProperty, 34.0), new Setter(Microsoft.UI.Xaml.Controls.Control.CornerRadiusProperty, new CornerRadius(7)) } };
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border/></DataTemplate>");
        _list.ContainerContentChanging += (_, a) => { if (!a.InRecycleQueue && a.ItemContainer.ContentTemplateRoot is Border b && a.Item is BrowseRow r) b.Child = Line(r); };
        _list.DoubleTapped += async (_, e) => { if (((e.OriginalSource as FrameworkElement)?.DataContext ?? _list.SelectedItem) is BrowseRow r) await Open(r); };
        _list.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && _list.SelectedItem is BrowseRow r) { e.Handled = true; await Open(r); } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_list, "BrowseList");
        Grid.SetRow(_list, 1); Children.Add(_list);
        _note.Foreground = Theme.Get("OxMuted"); Grid.SetRow(_note, 1); Children.Add(_note);
        Grid.SetRow(_busy, 1); _busy.Foreground = Theme.Get("OxAcc"); Children.Add(_busy);
    }
    public IReadOnlyList<BrowseRow> Selected => _list.SelectedItems.OfType<BrowseRow>().ToList();
    private static string Up(string inside) { var i = inside.TrimEnd('/', '\\').LastIndexOfAny(new[] { '/', '\\' }); return i <= 0 ? "" : inside[..i]; }

    public async Task Load(string inside)
    {
        Inside = inside; _note.Visibility = Visibility.Collapsed; _busy.Visibility = Visibility.Visible;
        try
        {
            var rows = Lister == null ? new() : await Lister(inside);
            _list.ItemsSource = rows;
            if (rows.Count == 0) { _note.Text = "This folder is empty"; _note.Visibility = Visibility.Visible; }
        }
        catch (Exception e) { _list.ItemsSource = null; _note.Text = "Couldn't open this: " + e.Message; _note.Visibility = Visibility.Visible; }
        finally { _busy.Visibility = Visibility.Collapsed; }
        InsideChanged?.Invoke(inside);
        _list.Focus(FocusState.Programmatic);
    }
    private async Task Open(BrowseRow r)
    {
        if (r.IsFolder) await Load(Inside.Length == 0 ? r.Name : Inside.TrimEnd('/', '\\') + (Inside.Contains('/') || Inside.StartsWith('/') ? "/" : "\\") + r.Name);
        else if (OpenFile != null) await OpenFile(r);
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
    private static FrameworkElement Line(BrowseRow r)
    {
        var g = new Grid { Height = 34, ColumnSpacing = 12 };
        foreach (var w in new[] { new GridLength(22), new GridLength(1, GridUnitType.Star), new GridLength(170), new GridLength(100) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        g.Children.Add(r.IsFolder
            ? new Microsoft.UI.Xaml.Shapes.Path { Width = 19, Height = 17, Stretch = Stretch.Uniform, Data = HomeView.Folder(), Fill = Ui.Brush("#F4C04E"), VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "", FontSize = 15, Foreground = Theme.Get("OxMuted"), VerticalAlignment = VerticalAlignment.Center });
        int c = 1;
        foreach (var (t, right) in new[] { (r.Name, false), (r.Modified == default ? "" : Format.When(r.Modified), false), (r.IsFolder ? "" : Format.Size(r.Size), true) })
        {
            var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = c == 1 ? 13 : 12.5, Foreground = Theme.Get(c == 1 ? "OxText" : "OxMuted"), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            Grid.SetColumn(tb, c++); g.Children.Add(tb);
        }
        return g;
    }
}
