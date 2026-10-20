// The glass pop-up from the design: the file's name and facts at the top, the viewer in the middle, keyboard hints
// at the bottom; next and previous file, pop out into its own window, full screen. Opens over the folder (which
// stays behind, blurred) or fills a pop-out window.
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed class ViewerHost : Grid
{
    public event Action? Closed;
    public event Action<FileItem, IReadOnlyList<FileItem>>? PopOut;
    public event Action<bool>? FullScreenWanted;
    public event Action<FileItem>? DeleteWanted;
    public bool InWindow { get; }
    public FileItem? Current { get; private set; }

    private IReadOnlyList<FileItem> _list = Array.Empty<FileItem>();
    private int _index;
    private ViewerBase? _viewer;
    private CancellationTokenSource? _cts;
    private readonly Border _card = new();
    private readonly TextBlock _name = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _info = new() { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Grid _body = new();
    private readonly StackPanel _hints = new() { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 12) };
    private readonly Button _prev, _next;
    private readonly Grid _details = new() { Width = 300, Visibility = Visibility.Collapsed, Padding = new Thickness(18) };
    private bool _full;

    public ViewerHost(bool inWindow)
    {
        InWindow = inWindow;
        IsTabStop = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_name, "ViewerName");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_info, "ViewerInfo");
        if (!inWindow) Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(150, 0, 0, 0)); // the folder behind goes dim
        // The card.
        _card.CornerRadius = new CornerRadius(inWindow ? 0 : 16);
        _card.Margin = inWindow ? new Thickness(0) : new Thickness(48, 24, 48, 24);
        _card.Background = Theme.Current == ElementTheme.Light ? Ui.Brush("#F7F9F8F7") : Ui.Brush("#F2060D0B");
        _card.BorderBrush = Theme.Get("OxLine2"); _card.BorderThickness = new Thickness(inWindow ? 0 : 1);
        if (!inWindow) { _card.Shadow = new ThemeShadow(); _card.Translation = new Vector3(0, 0, 40); }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Top bar.
        var top = new Grid { Padding = new Thickness(24, 12, 16, 12), ColumnSpacing = 12, BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(0, 0, 0, 1) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _name.Foreground = Theme.Get("OxText"); _info.Foreground = Theme.Get("OxMuted");
        top.Children.Add(_name); Grid.SetColumn(_info, 1); top.Children.Add(_info);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        right.Children.Add(_tools);
        right.Children.Add(new Border { Width = 1, Height = 22, Background = Theme.Get("OxLine2"), Margin = new Thickness(8, 0, 8, 0) });
        right.Children.Add(Btn("", "Details", () => _details.Visibility = _details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        right.Children.Add(Btn("", "Open in its usual app", () => { if (Current != null) Native.ShellRun(Current.Path); }));
        if (!inWindow) right.Children.Add(Btn("", "Open in its own window", () => { if (Current != null) { var (c, l) = (Current, _list); CloseViewer(); PopOut?.Invoke(c, l); } }));
        right.Children.Add(Btn("", "Full screen (F)", ToggleFull));
        right.Children.Add(Btn("", "Close (Esc)", CloseViewer));
        Grid.SetColumn(right, 2); top.Children.Add(right);
        grid.Children.Add(top);
        // Middle: the viewer, details on the right, arrows on the sides.
        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition());
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.Children.Add(_body);
        _details.Background = Theme.Get("OxSide"); _details.BorderBrush = Theme.Get("OxLine"); _details.BorderThickness = new Thickness(1, 0, 0, 0);
        Grid.SetColumn(_details, 1); middle.Children.Add(_details);
        _prev = Arrow("", "Previous file", () => Go(-1)); _prev.HorizontalAlignment = HorizontalAlignment.Left; _prev.Margin = new Thickness(24, 0, 0, 0);
        _next = Arrow("", "Next file", () => Go(1)); _next.HorizontalAlignment = HorizontalAlignment.Right; _next.Margin = new Thickness(0, 0, 24, 0);
        middle.Children.Add(_prev); middle.Children.Add(_next);
        Grid.SetRow(middle, 1); grid.Children.Add(middle);
        Grid.SetRow(_hints, 2); grid.Children.Add(_hints);
        _card.Child = grid;
        Children.Add(_card);

        // Opening motion: rises a little and fades in (under 200 ms, drawn by the compositor).
        _card.Opacity = 0; _card.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        _card.Scale = new Vector3(0.98f, 0.98f, 1); _card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        _card.SizeChanged += (_, e) => _card.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        Loaded += (_, _) => { _card.Opacity = 1; _card.Scale = Vector3.One; Focus(FocusState.Programmatic); };
        // A control inside can vanish while it has focus (the video's buttons change when it ends): focus then falls to
        // nothing and keys like Esc would go nowhere. Take it back whenever that happens.
        Loaded += (_, _) => FocusManager.LostFocus += OnLostFocus;
        Unloaded += (_, _) => FocusManager.LostFocus -= OnLostFocus;
        // Clicking the dim area around the card closes it, like a photo viewer.
        if (!inWindow) Tapped += (_, e) => { if (e.OriginalSource == this) CloseViewer(); };
    }

    public void Show(FileItem item, IReadOnlyList<FileItem> list)
    {
        _list = list.Where((f) => !f.IsFolder).ToList();
        _index = Math.Max(0, _list.ToList().FindIndex((f) => f.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)));
        if (_list.Count == 0 || _index >= _list.Count) { _list = new[] { item }; _index = 0; }
        _ = Load(_list[_index]);
    }

    private async Task Load(FileItem item)
    {
        try { await LoadCore(item); }
        catch (Exception e) { CrashLog.Write(e); _body.Children.Clear(); _body.Children.Add(new TextBlock { Text = "This file can't be shown here. Try Open in its usual app.", Foreground = Theme.Get("OxMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); }
    }
    private async Task LoadCore(FileItem item)
    {
        if (_viewer != null && !await _viewer.CanCloseAsync()) return; // unsaved edits: the person chose to stay
        _cts?.Cancel(); var cts = _cts = new CancellationTokenSource();
        _viewer?.Close();
        Current = item;
        _name.Text = item.Name; _info.Text = "";
        var kind = ViewerKinds.Of(item);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(_name, kind.ToString()); // which viewer opened, for tests and screen readers
        var v = ViewerKinds.Create(kind, _list);
        _viewer = v;
        if (v is PictureViewer pv) pv.Picked += (f) => { _index = _list.ToList().IndexOf(f); _ = Load(f); };
        v.InfoChanged += () => _info.Text = v.Info(item);
        _body.Children.Clear(); _body.Children.Add(v);
        _tools.Children.Clear();
        foreach (var t in v.Tools) _tools.Children.Add(Btn(t.Glyph, t.Name, t.Run));
        _tools.Children.Add(Btn("", "Copy the file", () => _ = Core.Clip.Put(new[] { item }, false)));
        _tools.Children.Add(Btn("", "Delete (to the Recycle Bin)", () => { DeleteWanted?.Invoke(item); Remove(item); }));
        _hints.Children.Clear();
        foreach (var (k, what) in v.Hints.Concat(_list.Count > 1 ? new[] { (v.ArrowsBrowse ? "← →" : v.PageKeysScroll ? "Ctrl PgDn" : "PgDn", "next file") } : Array.Empty<(string, string)>()).Concat(new[] { ("Esc", "close") })) _hints.Children.Add(Hint(k, what));
        _prev.Visibility = Ui.Show(_list.Count > 1 && v.ArrowsBrowse); _next.Visibility = _prev.Visibility;
        DrawDetails(item);
        try { await v.LoadAsync(item, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { CrashLog.Write(e); }
        if (_cts == cts) _info.Text = v.Info(item);
        KeepFocus(); KeysBackToWindow();
    }

    /// <summary>Keys only arrive while something inside has focus: if focus was lost (a control was removed), take it back.</summary>
    private void OnLostFocus(object? sender, FocusManagerLostFocusEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (_viewer != null && XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) == null) Focus(FocusState.Programmatic); KeysBackToWindow(); });
    /// <summary>Windows' media player moves the keyboard to the bare window frame when a video starts, so no key reaches
    /// the page any more (Esc, Space). Hand the keyboard back to the window's content.</summary>
    private void KeysBackToWindow()
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } env) return;
        var top = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
        if (top == IntPtr.Zero || GetFocus() != top) return;
        var child = FindWindowEx(top, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        if (child != IntPtr.Zero) { SetFocus(child); Perf.Log("viewer: keyboard handed back to the window content"); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    private void KeepFocus()
    {
        if (XamlRoot == null) return;
        for (var d = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d)) if (d == this) return;
        Focus(FocusState.Programmatic);
    }

    private void Remove(FileItem item)
    {
        var l = _list.ToList(); var i = l.IndexOf(item); if (i < 0) return;
        l.RemoveAt(i); _list = l;
        if (l.Count == 0) { CloseViewer(); return; }
        _index = Math.Min(i, l.Count - 1); _ = Load(l[_index]);
    }

    private void Go(int step)
    {
        if (_list.Count < 2) return;
        _index = (_index + step + _list.Count) % _list.Count;
        _ = Load(_list[_index]);
    }

    public async void CloseViewer()
    {
        if (_viewer != null && !await _viewer.CanCloseAsync()) return;
        _cts?.Cancel(); _viewer?.Close(); _viewer = null;
        if (_full) { _full = false; FullScreenWanted?.Invoke(false); }
        Closed?.Invoke();
    }

    private void ToggleFull() { _full = !_full; FullScreenWanted?.Invoke(_full); }

    /// <summary>A key pressed anywhere in the window while the viewer is open. True = used.</summary>
    public bool HandleKey(VirtualKey key, bool ctrl, bool shift)
    {
        if (_viewer == null) return false;
        var focused = XamlRoot == null ? null : FocusManager.GetFocusedElement(XamlRoot);
        bool typing = focused is TextBox { IsReadOnly: false } or AutoSuggestBox or NumberBox;
        if (typing)
        {
            // In the editor or the page box: only Esc and the viewer's Ctrl keys (Ctrl+S); letters and arrows are typing.
            if (ctrl && _viewer.OnKey(key, ctrl, shift)) return true;
            if (key == VirtualKey.Escape) { if (_full) ToggleFull(); else CloseViewer(); return true; }
            return false;
        }
        if (_viewer.OnKey(key, ctrl, shift)) return true;
        bool pageNext = !_viewer.PageKeysScroll || ctrl;
        switch (key)
        {
            case VirtualKey.Escape: if (_full) ToggleFull(); else CloseViewer(); return true;
            case VirtualKey.F when !ctrl: ToggleFull(); return true;
            case VirtualKey.PageDown when pageNext: Go(1); return true;
            case VirtualKey.PageUp when pageNext: Go(-1); return true;
            case VirtualKey.Right when _viewer.ArrowsBrowse: Go(1); return true;
            case VirtualKey.Left when _viewer.ArrowsBrowse: Go(-1); return true;
        }
        return false;
    }

    private void DrawDetails(FileItem f)
    {
        _details.Children.Clear();
        var sp = new StackPanel { Spacing = 12 };
        void Row(string k, string v) { if (string.IsNullOrEmpty(v)) return; var r = new StackPanel { Spacing = 2 }; r.Children.Add(new TextBlock { Text = k, FontSize = 12, Foreground = Theme.Get("OxFaint") }); r.Children.Add(new TextBlock { Text = v, FontSize = 13.5, Foreground = Theme.Get("OxText"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }); sp.Children.Add(r); }
        sp.Children.Add(new TextBlock { Text = "Details", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText") });
        Row("Name", f.Name); Row("Type", f.TypeText); Row("Size", f.Size > 0 ? $"{Format.Size(f.Size)} ({f.Size:N0} bytes)" : "");
        Row("Modified", f.Modified == default ? "" : f.Modified.ToString("d MMM yyyy, HH:mm"));
        try { var fi = new FileInfo(f.Path); Row("Created", fi.CreationTime.ToString("d MMM yyyy, HH:mm")); } catch { }
        Row("Where", System.IO.Path.GetDirectoryName(f.Path) ?? "");
        _details.Children.Add(new ScrollViewer { Content = sp });
    }

    private static Button Btn(string glyph, string name, Action run)
    {
        var b = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 15 }, Width = 38, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Foreground = Theme.Get("OxText") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        b.Click += (_, _) => run();
        return b;
    }
    private static Button Arrow(string glyph, string name, Action run)
    {
        var b = new Button { Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 16 }, Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Background = Ui.Brush("#B3101A17"), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1), Foreground = Ui.Brush("#EEF6F2") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => run();
        return b;
    }
    private static UIElement Hint(string key, string what)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        sp.Children.Add(new Border { Padding = new Thickness(7, 1, 7, 2), CornerRadius = new CornerRadius(5), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = key, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Get("OxText") } });
        sp.Children.Add(new TextBlock { Text = what, FontSize = 13, Foreground = Theme.Get("OxMuted"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }
}
