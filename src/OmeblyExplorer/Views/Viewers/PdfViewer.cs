// PDFs, drawn by Windows' own PDF reader: page pictures on the left, the pages one under another (only the ones on
// screen are drawn, so a 1,000-page PDF opens at once), page number, zoom and print.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OmeblyExplorer.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed class PdfViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    private sealed class Page { public uint Index; public double Ratio = 1.414; public int Number => (int)Index + 1; }
    private PdfDocument? _doc;
    private readonly ListView _pages = new() { SelectionMode = ListViewSelectionMode.None, Padding = new Thickness(0, 24, 0, 80) };
    private readonly ListView _thumbs = new() { SelectionMode = ListViewSelectionMode.Single, Width = 170, Padding = new Thickness(18, 18, 18, 18) };
    private readonly List<Page> _list = new();
    private readonly TextBox _pageBox = new() { Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _of = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14, MinWidth = 52, TextAlignment = TextAlignment.Center };
    private double _zoom = 1;
    private string _path = "";
    private int _current = 1;

    public PdfViewer()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        var side = new Border { BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(0, 0, 1, 0), Child = _thumbs };
        Children.Add(side);
        _thumbs.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(ListViewItem.PaddingProperty, new Thickness(0)), new Setter(ListViewItem.MarginProperty, new Thickness(0, 0, 0, 10)), new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) } };
        Rows(_thumbs, (o) => o is Page p ? ThumbFor(p) : null);
        _thumbs.SelectionChanged += (_, _) => { if (_thumbs.SelectedItem is Page p && p.Number != _current) _pages.ScrollIntoView(p, ScrollIntoViewAlignment.Leading); };
        _pages.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(ListViewItem.PaddingProperty, new Thickness(0)), new Setter(ListViewItem.MarginProperty, new Thickness(0, 0, 0, 18)), new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center) } };
        Rows(_pages, (o) => o is Page p ? PageFor(p) : null);
        Grid.SetColumn(_pages, 1); Children.Add(_pages);
        _pages.Loaded += (_, _) => { if (FindScroll(_pages) is { } sv) sv.ViewChanged += (_, _) => TrackPage(sv); };
        // Page and zoom bar, bottom centre (from the design).
        var bar = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 6, 12, 6), Background = Theme.Current == ElementTheme.Light ? Ui.Brush("#F2FFFFFF") : Ui.Brush("#F20B1412"), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(IconButton("", "Previous page", () => GoPage(_current - 1)));
        _pageBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter && int.TryParse(_pageBox.Text, out var n)) { GoPage(n); e.Handled = true; } };
        sp.Children.Add(_pageBox); sp.Children.Add(_of);
        sp.Children.Add(IconButton("", "Next page", () => GoPage(_current + 1)));
        sp.Children.Add(new Border { Width = 1, Height = 20, Background = Theme.Get("OxLine2"), Margin = new Thickness(6, 0, 6, 0) });
        sp.Children.Add(IconButton("", "Zoom out", () => SetZoom(_zoom / 1.2)));
        sp.Children.Add(_zoomText);
        sp.Children.Add(IconButton("", "Zoom in", () => SetZoom(_zoom * 1.2)));
        foreach (var t in sp.Children.OfType<TextBlock>()) t.Foreground = Theme.Get("OxText");
        bar.Child = sp;
        Grid.SetColumn(bar, 1); Children.Add(bar);
    }

    public override (string, string)[] Hints => new[] { ("+ −", "zoom"), ("Home End", "first, last page") };
    public override IReadOnlyList<Tool> Tools => new[] { new Tool("", "Print", () => Native.ShellRun(_path, "print")) };
    public override string Info(FileItem f) => _doc == null ? Format.Size(f.Size) : $"Page {_current} of {_doc.PageCount} · {Format.Size(f.Size)}";

    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        _path = file.Path;
        try
        {
            var sf = await StorageFile.GetFileFromPathAsync(file.Path);
            _doc = await PdfDocument.LoadFromFileAsync(sf);
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x8007052B))
        { ShowMessage("This PDF has a password", "Open it in its usual app to type the password."); return; }
        catch { ShowMessage("This PDF can't be opened", "The file may be damaged. Try Open in its usual app."); return; }
        ct.ThrowIfCancellationRequested();
        for (uint i = 0; i < _doc.PageCount; i++)
        {
            var p = new Page { Index = i };
            if (i < 50) { using var pg = _doc.GetPage(i); p.Ratio = pg.Size.Width > 0 ? pg.Size.Height / pg.Size.Width : 1.414; }
            _list.Add(p);
        }
        _of.Text = $"/ {_doc.PageCount}";
        _pages.ItemsSource = _list; _thumbs.ItemsSource = _list;
        _pageBox.Text = "1";
        SetZoom(1);
        RaiseInfo();
    }

    private double PageWidth => Math.Max(200, Math.Min(1600, (_pages.ActualWidth > 0 ? _pages.ActualWidth : 900) * 0.62)) * _zoom;

    private UIElement PageFor(Page p)
    {
        double w = PageWidth;
        var img = new Image { Width = w, Height = w * p.Ratio, Stretch = Stretch.Uniform };
        var holder = new Border { Background = Ui.Brush("#FFFFFFFF"), CornerRadius = new CornerRadius(4), Child = img, Width = w, Height = w * p.Ratio };
        _ = Render(p, img, (uint)(w * XamlRoot.RasterizationScale));
        return holder;
    }
    private UIElement ThumbFor(Page p)
    {
        var img = new Image { Width = 118, Height = 118 * p.Ratio };
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(new Border { Background = Ui.Brush("#FFFFFFFF"), CornerRadius = new CornerRadius(4), Child = img });
        sp.Children.Add(new TextBlock { Text = p.Number.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12.5, Foreground = Theme.Get("OxMuted") });
        _ = Render(p, img, (uint)(118 * XamlRoot.RasterizationScale));
        return sp;
    }
    private async Task Render(Page p, Image target, uint width)
    {
        if (_doc == null) return;
        try
        {
            using var page = _doc.GetPage(p.Index);
            if (Math.Abs(p.Ratio - page.Size.Height / page.Size.Width) > 0.01) { p.Ratio = page.Size.Height / page.Size.Width; target.Height = target.Width * p.Ratio; }
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = Math.Max(64, width) });
            var bmp = new BitmapImage(); await bmp.SetSourceAsync(stream); target.Source = bmp;
        }
        catch { /* a page that can't be drawn stays white */ }
    }

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.3, 4);
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
        if (_list.Count == 0) return;
        var keep = _current;
        _pages.ItemsSource = null; _pages.ItemsSource = _list; // pages redraw at the new size
        DispatcherQueue.TryEnqueue(() => GoPage(keep));
    }
    private void GoPage(int n)
    {
        if (_list.Count == 0) return;
        n = Math.Clamp(n, 1, _list.Count);
        _pages.ScrollIntoView(_list[n - 1], ScrollIntoViewAlignment.Leading);
        SetCurrent(n);
    }
    private void SetCurrent(int n)
    {
        if (n == _current && _pageBox.Text == n.ToString()) return;
        _current = n; _pageBox.Text = n.ToString(); RaiseInfo();
        if (_thumbs.SelectedIndex != n - 1) { _thumbs.SelectedIndex = n - 1; _thumbs.ScrollIntoView(_list[n - 1]); }
    }
    private void TrackPage(ScrollViewer sv)
    {
        if (_list.Count == 0) return;
        double each = PageWidth * _list[0].Ratio + 18;
        SetCurrent(Math.Clamp((int)((sv.VerticalOffset + sv.ViewportHeight / 3) / each) + 1, 1, _list.Count));
    }
    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var c = VisualTreeHelper.GetChild(d, i); if (c is ScrollViewer s) return s; var r = FindScroll(c); if (r != null) return r; }
        return null;
    }

    public override bool OnKey(VirtualKey key, bool ctrl, bool shift)
    {
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is TextBox) return false;
        switch (key)
        {
            case VirtualKey.Add or (VirtualKey)187: SetZoom(_zoom * 1.2); return true;
            case VirtualKey.Subtract or (VirtualKey)189: SetZoom(_zoom / 1.2); return true;
            case VirtualKey.Home: GoPage(1); return true;
            case VirtualKey.End: GoPage(_list.Count); return true;
            case VirtualKey.Right: GoPage(_current + 1); return true;
            case VirtualKey.Left: GoPage(_current - 1); return true;
        }
        return false;
    }
    public override void Close() { _pages.ItemsSource = null; _thumbs.ItemsSource = null; _doc = null; }
}
