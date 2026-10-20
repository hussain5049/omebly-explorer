// Pictures: fits the window, zoom with the wheel, pinch or + and -, drag to move, rotate, a strip of the folder's
// other pictures along the bottom. Windows decodes the picture (JPG, PNG, GIF with movement, WebP, HEIC, AVIF,
// TIFF, ICO); for formats Windows can only make a preview of (RAW camera photos, PSD), its large preview is shown.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OmeblyExplorer.Core;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed class PictureViewer : ViewerBase
{
    public event Action<FileItem>? Picked;
    private readonly ScrollViewer _scroll = new() { ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 16, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto };
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly RotateTransform _rotate = new();
    private readonly TextBlock _zoomText = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly IReadOnlyList<FileItem> _pictures;
    private int _w, _h;
    private FileItem? _file;

    public PictureViewer(IReadOnlyList<FileItem> list)
    {
        _pictures = list.Where((f) => ViewerKinds.Of(f) == ViewKind.Picture).ToList();
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _image.RenderTransform = _rotate; _image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _scroll.Content = new Grid { Children = { _image } };
        ((Grid)_scroll.Content).HorizontalAlignment = HorizontalAlignment.Center; ((Grid)_scroll.Content).VerticalAlignment = VerticalAlignment.Center;
        _scroll.ViewChanged += (_, _) => _zoomText.Text = $"{Math.Round(_scroll.ZoomFactor * _base * 100)}%";
        _scroll.SizeChanged += (_, _) => Fit();
        Children.Add(_scroll);
        // Zoom pill, bottom right.
        var pill = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(6, 2, 6, 2), Background = Ui.Brush("#CC0B1412"), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 24, 16) };
        var ps = new StackPanel { Orientation = Orientation.Horizontal };
        ps.Children.Add(IconButton("", "Zoom out", () => ZoomBy(1 / 1.25)));
        _zoomText.Foreground = Ui.Brush("#EEF6F2"); ps.Children.Add(_zoomText);
        ps.Children.Add(IconButton("", "Zoom in", () => ZoomBy(1.25)));
        foreach (var b in ps.Children.OfType<Button>()) b.Foreground = Ui.Brush("#EEF6F2");
        pill.Child = ps; Children.Add(pill);
        // The folder's pictures along the bottom.
        if (_pictures.Count > 1)
        {
            var strip = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 88, Padding = new Thickness(16, 6, 16, 6), ItemsSource = _pictures };
            strip.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollMode(strip, ScrollMode.Enabled); ScrollViewer.SetHorizontalScrollBarVisibility(strip, ScrollBarVisibility.Hidden);
            ScrollViewer.SetVerticalScrollMode(strip, ScrollMode.Disabled);
            strip.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Width='104' Height='68' CornerRadius='8' Background='#22FFFFFF'><Image Source='{Binding Icon}' Stretch='UniformToFill'/></Border></DataTemplate>");
            strip.ContainerContentChanging += (_, a) => { if (a.Item is FileItem f && !a.InRecycleQueue) Thumbnails.Request(f, 104); };
            strip.ItemClick += (_, a) => { if (a.ClickedItem is FileItem f && f != _file) Picked?.Invoke(f); };
            strip.IsItemClickEnabled = true;
            Loaded += (_, _) => { if (_file != null) { strip.SelectedItem = _pictures.FirstOrDefault((p) => p.Path == _file.Path); if (strip.SelectedItem != null) strip.ScrollIntoView(strip.SelectedItem); } };
            Grid.SetRow(strip, 1); Children.Add(strip);
        }
    }

    public override bool ArrowsBrowse => true;
    public override (string, string)[] Hints => new[] { ("+ −", "zoom"), ("R", "rotate"), ("0", "fit") };
    public override IReadOnlyList<Tool> Tools => new[] { new Tool("", "Fit to window (0)", Fit), new Tool("", "Actual size (1)", () => ZoomTo(1)), new Tool("", "Rotate (R)", Rotate) };
    public override string Info(FileItem f)
    {
        var at = _pictures.ToList().FindIndex((p) => p.Path == f.Path);
        var pos = _pictures.Count > 1 && at >= 0 ? $"{at + 1} of {_pictures.Count} · " : "";
        return $"{pos}{(_w > 0 ? $"{_w} × {_h} · " : "")}{Format.Size(f.Size)}";
    }

    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        _file = file;
        ImageSource? src = null;
        if (file.Extension == ".svg")
        {
            var svg = new SvgImageSource();
            using (var s = File.OpenRead(file.Path)) await svg.SetSourceAsync(s.AsRandomAccessStream());
            _w = (int)svg.RasterizePixelWidth; _h = (int)svg.RasterizePixelHeight; src = svg;
        }
        else if (file.Extension is not (".psd" or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf"))
        {
            try
            {
                var bmp = new BitmapImage();
                int realW = 0, realH = 0;
                try
                {
                    using var probe = File.OpenRead(file.Path);
                    var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(probe.AsRandomAccessStream());
                    realW = (int)dec.PixelWidth; realH = (int)dec.PixelHeight;
                    if (Math.Max(realW, realH) > 8000)
                    {
                        bmp.DecodePixelType = DecodePixelType.Physical;
                        if (realW >= realH) bmp.DecodePixelWidth = 8000; else bmp.DecodePixelHeight = 8000;
                    }
                }
                catch { /* the decoder can't read the size: BitmapImage still tries below */ }
                var opened = new TaskCompletionSource<bool>();
                bmp.ImageOpened += (_, _) => opened.TrySetResult(true);
                bmp.ImageFailed += (_, _) => opened.TrySetResult(false);
                using (var s = File.OpenRead(file.Path)) await bmp.SetSourceAsync(s.AsRandomAccessStream());
                _image.Source = bmp;
                var ok = await Task.WhenAny(opened.Task, Task.Delay(8000)) == opened.Task && opened.Task.Result;
                if (ok) { _w = realW > 0 ? realW : bmp.PixelWidth; _h = realH > 0 ? realH : bmp.PixelHeight; src = bmp; }
            }
            catch { /* Windows can't decode it: use its preview below */ }
        }
        ct.ThrowIfCancellationRequested();
        if (src == null)
        {
            // Windows' own large preview of the file (works for camera RAW, PSD and anything with a thumbnail maker).
            var img = await Task.Run(() => Native.GetImage(file.Path, 2560, Native.SIIGBF.BiggerSizeOk), ct);
            if (img is { } p)
            {
                var wb = new WriteableBitmap(p.w, p.h);
                using (var s = wb.PixelBuffer.AsStream()) s.Write(p.pixels, 0, p.pixels.Length);
                wb.Invalidate(); src = wb; _w = p.w; _h = p.h;
            }
        }
        if (src == null) { ShowMessage("This picture can't be shown", "Windows doesn't know how to open this kind of picture. Try Open in its usual app."); return; }
        _image.Source = src;
        // Laid out at full size, or smaller for huge pictures (over 8000 pixels) so "fit" stays above 10%.
        _base = Math.Min(1, 8000.0 / Math.Max(_w, _h));
        _image.Width = _w * _base; _image.Height = _h * _base;
        RaiseInfo();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Fit);
    }

    private void Fit()
    {
        if (_w == 0 || _scroll.ViewportWidth == 0) return;
        bool turned = Math.Abs(_rotate.Angle % 180) == 90;
        double w = turned ? _h : _w, h = turned ? _w : _h;
        var z = Math.Min(1, Math.Min((_scroll.ViewportWidth - 40) / w, (_scroll.ViewportHeight - 40) / h)); // never blown up past real size
        // The zoom can't go under 10%: a big picture in a small space (the side pane) is laid out smaller instead.
        if (z / _base < 0.1) { _base = Math.Max(z / 0.1, 0.0001); _image.Width = _w * _base; _image.Height = _h * _base; }
        _scroll.ChangeView(0, 0, (float)Math.Clamp(z / _base, 0.1, 16), disableAnimation: true);
    }
    private double _base = 1; // layout size / real size
    private void ZoomTo(double z) => _scroll.ChangeView(null, null, (float)Math.Clamp(z / _base, 0.1, 16));
    private void ZoomBy(double f) => ZoomTo(_scroll.ZoomFactor * _base * f);
    private void Rotate() { _rotate.Angle = (_rotate.Angle + 90) % 360; Fit(); }

    public override bool OnKey(VirtualKey key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Add or (VirtualKey)187: ZoomBy(1.25); return true;  // + and =
            case VirtualKey.Subtract or (VirtualKey)189: ZoomBy(1 / 1.25); return true;
            case VirtualKey.Number0 or VirtualKey.NumberPad0: Fit(); return true;
            case VirtualKey.Number1 or VirtualKey.NumberPad1: ZoomTo(1); return true;
            case VirtualKey.R: Rotate(); return true;
        }
        return false;
    }
}
