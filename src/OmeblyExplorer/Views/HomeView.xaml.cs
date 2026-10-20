// Home: drives with how full they are, quick actions, pinned folders and recent files, as in the approved design.
// Everything is read in the background, so Home opens at once even with a slow USB or network drive.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed partial class HomeView : UserControl
{
    public event Action<string>? OpenPath;
    public event Action<FileItem>? OpenFile;
    public event Action<string>? QuickAction;
    public event Action<FrameworkElement, PinnedFolder>? PinMenu;

    public HomeView()
    {
        InitializeComponent();
        OpenThisPc.Click += (_, _) => OpenPath?.Invoke("this-pc");
        EditPins.Click += (_, _) => QuickAction?.Invoke("edit-pins");
    }

    public async void Refresh()
    {
        var drives = await Task.Run(Places.Drives);
        DrivesList.ItemsSource = drives.Select(DriveCardView).ToList();
        var bin = await Task.Run(Places.RecycleBin);
        var downloads = Places.Folders().FirstOrDefault((p) => p.Name == "Downloads");
        int dlCount = downloads == null ? 0 : await Task.Run(() => { try { return Directory.EnumerateFileSystemEntries(downloads.Path).Take(10000).Count(); } catch { return 0; } });
        ActionsList.ItemsSource = new List<UIElement>
        {
            Chip("", "#2EF2B3", "Tidy Downloads", dlCount > 0 ? $"{dlCount} files" : null, "tidy"),
            Chip("", "#FF5A5F", "Convert to PDF", null, "convert"),
            Chip("", "#F4C04E", "Make a zip", null, "zip"),
            Chip("", "#4C9BFF", "Find big files", null, "big"),
            Chip("", "#9B7BFF", "Find duplicates", null, "dupes"),
            Chip("", "#9FB6AD", "Empty Recycle Bin", bin.bytes > 0 ? Format.Size(bin.bytes) : null, "empty-bin"),
        };
        var pins = Settings.Current.Pinned.Where((p) => Directory.Exists(p.Path)).ToList();
        var counts = await Task.Run(() => pins.Select((p) => { try { return Directory.EnumerateFileSystemEntries(p.Path).Take(100000).Count(); } catch { return -1; } }).ToList());
        PinsList.ItemsSource = pins.Select((p, i) => PinCard(p, counts[i])).ToList();
        NoPins.Visibility = Ui.Show(pins.Count == 0);
        var recent = Settings.Current.ShowRecent ? await Task.Run(() => Places.RecentFiles(6)) : new List<FileItem>();
        RecentList.ItemsSource = recent.Select(RecentCard).ToList();
        NoRecent.Visibility = Ui.Show(recent.Count == 0);
    }

    private UIElement DriveCardView(DriveCard d)
    {
        var ringColor = d.Low ? Res("OxWarn") : Res("OxAcc");
        var ring = new Grid { Width = 58, Height = 58 };
        ring.Children.Add(new Ellipse { Stroke = Res("OxRing"), StrokeThickness = 5 });
        ring.Children.Add(Arc(d.UsedFraction, 58, 5, ringColor));
        ring.Children.Add(new TextBlock { Text = $"{d.UsedPercent}%", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = d.Cloud ? "" : d.Removable ? "" : "", FontSize = 14, Foreground = d.Cloud ? Ui.Brush("#2FCB7A") : Res("OxMuted") });
        title.Children.Add(new TextBlock { Text = d.Name, FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = d.FreeText, FontSize = 12.5, Foreground = Res("OxMuted") });
        if (d.Note.Length > 0) text.Children.Add(new TextBlock { Text = d.Note, FontSize = 12, Foreground = d.Low ? Res("OxWarn") : Res("OxFaint") });
        var g = new Grid { ColumnSpacing = 16, Padding = new Thickness(18, 12, 14, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(ring); Grid.SetColumn(text, 1); g.Children.Add(text);
        var b = new Button { Style = (Style)Application.Current.Resources["OxCardButton"], Content = g, Padding = new Thickness(0) };
        b.Click += (_, _) => OpenPath?.Invoke(d.Root);
        ToolTipService.SetToolTip(b, $"Open {d.Name}");
        return b;
    }

    /// <summary>An arc for "how full": starts at the top, goes clockwise.</summary>
    private static Microsoft.UI.Xaml.Shapes.Path Arc(double fraction, double size, double thickness, Brush brush)
    {
        fraction = Math.Clamp(fraction, 0.001, 0.999);
        double r = (size - thickness) / 2, c = size / 2, angle = fraction * 2 * Math.PI;
        var end = new Windows.Foundation.Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment { Point = end, Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = fraction > 0.5 });
        var geo = new PathGeometry(); geo.Figures.Add(fig);
        return new Microsoft.UI.Xaml.Shapes.Path { Data = geo, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    }

    private UIElement Chip(string glyph, string color, string label, string? extra, string action)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Ui.Brush("#22" + color.TrimStart('#')) };
        badge.Child = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 13, Foreground = Ui.Brush(color) };
        sp.Children.Add(badge);
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (extra != null) sp.Children.Add(new TextBlock { Text = extra, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = Res("OxMuted") });
        var b = new Button { Style = (Style)Application.Current.Resources["OxChip"], Content = sp, Padding = new Thickness(8, 0, 14, 0) };
        b.Click += (_, _) => QuickAction?.Invoke(action);
        return b;
    }

    private UIElement PinCard(PinnedFolder p, int count)
    {
        var sp = new StackPanel { Spacing = 2, Padding = new Thickness(16, 14, 16, 14) };
        sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Folder(), Fill = Ui.Brush(p.Color), Width = 44, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
        sp.Children.Add(new TextBlock { Text = NameOf(p.Path), FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = count < 0 ? "Can't open" : $"{count:N0} items", FontSize = 12.5, Foreground = Res("OxMuted") });
        var b = new Button { Style = (Style)Application.Current.Resources["OxCardButton"], Content = sp, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => OpenPath?.Invoke(p.Path);
        b.RightTapped += (s, e) => { PinMenu?.Invoke(b, p); e.Handled = true; };
        ToolTipService.SetToolTip(b, p.Path);
        return b;
    }

    private UIElement RecentCard(FileItem f)
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) });
        g.RowDefinitions.Add(new RowDefinition());
        var thumb = new Border { Background = Res("OxCard2"), CornerRadius = new CornerRadius(11, 11, 0, 0) };
        var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        thumb.Child = img;
        f.PropertyChanged += (_, _) => img.Source = f.Icon;
        Thumbnails.Request(f, 180);
        g.Children.Add(thumb);
        var sp = new StackPanel { Padding = new Thickness(14, 10, 14, 10), Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = f.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = $"{NameOf(System.IO.Path.GetDirectoryName(f.Path) ?? "")} · {Format.Ago(f.Modified)}", FontSize = 12.5, Foreground = Res("OxMuted"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetRow(sp, 1); g.Children.Add(sp);
        var b = new Button { Style = (Style)Application.Current.Resources["OxCardButton"], Content = g, Padding = new Thickness(0) };
        b.Click += (_, _) => OpenFile?.Invoke(f);
        ToolTipService.SetToolTip(b, f.Path);
        return b;
    }

    private static string NameOf(string path) { var n = System.IO.Path.GetFileName(path.TrimEnd('\\')); return string.IsNullOrEmpty(n) ? path : n; }
    private static Brush Res(string key) => Theme.Get(key);
    public static Geometry Folder() => (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), (string)Application.Current.Resources["FolderPath"]);
}
