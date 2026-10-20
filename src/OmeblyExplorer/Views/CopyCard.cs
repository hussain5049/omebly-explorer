// The copy card from the design (frames 1-5): slides in, shows a live speed graph, pauses in amber, ends with a tick
// and a soft mint burst, then tucks away into a chip in the status bar. Every motion is under 250 ms (420 ms for
// the burst) and is drawn by Windows' compositor, so it never makes the window lag.
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed class CopyCard : UserControl
{
    public CopyJob Job { get; }
    public event Action<CopyCard>? Finished; // tucked away: the window shows the small chip
    public event Action<string>? OpenTarget;
    public Func<string, string, Task<(SameName, bool)>>? AskEachFile;

    private readonly TextBlock _title = T(15, true), _sub = T(12.5), _stats = T(12.5), _percent = T(13, true), _file = T(12.5);
    private readonly Grid _bar = new() { Height = 6, CornerRadius = new CornerRadius(3) };
    private readonly Grid _fill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Canvas _graph = new() { Height = 58, Margin = new Thickness(0, 14, 0, 4) };
    private readonly Microsoft.UI.Xaml.Shapes.Path _graphFill = new(), _graphLine = new();
    private readonly Border _badge = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(10) };
    private readonly FontIcon _badgeIcon = new() { FontSize = 17 };
    private readonly Button _pause = Btn("", "Pause"), _cancel = Btn(null, "Cancel"), _open = Btn("", "Open"), _undo = Btn(null, "Undo");
    private readonly HyperlinkButton _more = new() { Content = "More details", Padding = new Thickness(0), FontSize = 13 };
    private readonly StackPanel _details = new() { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _main = new() { Spacing = 0 }, _ask = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Grid _burst = new() { Width = 40, Height = 40, IsHitTestVisible = false };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Border _root = new();
    private SameName _choice = SameName.KeepBoth;
    private bool _doneShown;

    public CopyCard(CopyJob job)
    {
        Job = job;
        Width = 380;
        _root.Padding = new Thickness(18, 16, 18, 16); _root.CornerRadius = new CornerRadius(14);
        _root.Background = Theme.Get("OxCard2"); _root.BorderBrush = Theme.Get("OxLine2"); _root.BorderThickness = new Thickness(1);
        Shadow = new ThemeShadow(); Translation = new Vector3(0, 16, 32);
        Opacity = 0;
        OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(240) };
        TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(240) };

        // Header: icon, title, where to, and a minimise mark.
        _badge.Child = _badgeIcon; _badgeIcon.FontFamily = (FontFamily)Application.Current.Resources["OxIcons"];
        var badgeHost = new Grid(); badgeHost.Children.Add(_burst); badgeHost.Children.Add(_badge);
        var head = new Grid { ColumnSpacing = 14 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(badgeHost);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 }; titles.Children.Add(_title); titles.Children.Add(_sub);
        Grid.SetColumn(titles, 1); head.Children.Add(titles);
        var min = new Button { Content = "", FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], FontSize = 10, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(min, "Make small (it keeps going)");
        min.Click += (_, _) => TuckAway();
        Grid.SetColumn(min, 2); head.Children.Add(min);
        _sub.Foreground = Theme.Get("OxMuted"); _stats.Foreground = Theme.Get("OxMuted"); _file.Foreground = Theme.Get("OxMuted");
        _file.TextTrimming = TextTrimming.CharacterEllipsis;

        // Progress bar (the fill grows with a scale, drawn by the compositor) and the speed graph.
        _bar.Background = Theme.Get("OxRing"); _bar.Margin = new Thickness(0, 16, 0, 0);
        _fill.Background = Theme.Get("OxAcc");
        _fill.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(160) };
        _fill.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(240) };
        _fill.Scale = new Vector3(0.02f, 1, 1);
        _bar.Children.Add(_fill);
        _bar.SizeChanged += (_, e) => _fill.CenterPoint = new Vector3(0, (float)e.NewSize.Height / 2, 0);
        _graph.Children.Add(_graphFill); _graph.Children.Add(_graphLine);
        _graphLine.StrokeThickness = 2; _graphLine.StrokeLineJoin = PenLineJoin.Round;

        var statsRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        statsRow.Children.Add(_stats); _percent.HorizontalAlignment = HorizontalAlignment.Right; statsRow.Children.Add(_percent);
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        fileRow.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "", FontSize = 13, Foreground = Theme.Get("OxFaint") });
        fileRow.Children.Add(_file);

        var buttons = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(_pause); left.Children.Add(_cancel); left.Children.Add(_open); left.Children.Add(_undo);
        buttons.Children.Add(left);
        _more.HorizontalAlignment = HorizontalAlignment.Right; _more.Foreground = Theme.Get("OxAcc"); buttons.Children.Add(_more);
        _pause.Click += (_, _) => { if (Job.State == JobState.Paused) Job.Resume(); else Job.Pause(); };
        _cancel.Click += (_, _) => Job.Cancel();
        _open.Click += (_, _) => OpenTarget?.Invoke(Job.Target);
        _undo.Click += async (_, _) => { _undo.IsEnabled = false; await CopyEngine.Undo(Job); _title.Text = Job.Move ? "Moved back" : "Copies removed"; _sub.Text = Job.Move ? "Everything is where it was" : "They're in the Recycle Bin"; _undo.Visibility = Visibility.Collapsed; };
        _more.Click += (_, _) => { _details.Visibility = _details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; _more.Content = _details.Visibility == Visibility.Visible ? "Less" : "More details"; DrawDetails(); };
        _open.Background = Theme.Get("OxAcc"); _open.Foreground = Theme.Get("OxAccText");
        _open.Visibility = Visibility.Collapsed; _undo.Visibility = Visibility.Collapsed;

        _main.Children.Add(head); _main.Children.Add(_bar); _main.Children.Add(_graph); _main.Children.Add(statsRow); _main.Children.Add(fileRow); _main.Children.Add(_details); _main.Children.Add(buttons);
        var both = new StackPanel(); both.Children.Add(_ask); both.Children.Add(_main);
        _root.Child = both; Content = _root;

        job.Changed += () => DispatcherQueue.TryEnqueue(Draw);
        job.AskEach = (src, dst) =>
        {
            var tcs = new TaskCompletionSource<(SameName, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = DispatcherQueue.TryEnqueue(async () =>
            {
                try { tcs.TrySetResult(AskEachFile != null ? await AskEachFile(src, dst) : (SameName.Skip, false)); }
                catch { tcs.TrySetResult((SameName.Skip, false)); } // a question that can't be shown never blocks the copy
            });
            if (!queued) tcs.TrySetResult((SameName.Skip, false));
            return tcs.Task;
        };
        _tick.Tick += (_, _) => Draw();
        Loaded += (_, _) => { Opacity = 1; Translation = new Vector3(0, 0, 32); _tick.Start(); Draw(); }; // frame 1: slide in
        Unloaded += (_, _) => _tick.Stop();
    }

    private string Verb => Job.Move ? "Moving" : "Copying";
    private string Where => $"to {Job.TargetName}";

    private void Draw()
    {
        var j = Job;
        double fraction = j.TotalBytes > 0 ? (double)j.DoneBytes / j.TotalBytes : j.TotalItems > 0 ? (double)j.DoneItems / j.TotalItems : 0;
        fraction = Math.Clamp(fraction, 0, 1);
        switch (j.State)
        {
            case JobState.Preparing:
                Badge("", "OxAcc"); _title.Text = $"Getting ready to {(j.Move ? "move" : "copy")}"; _sub.Text = $"{j.TotalItems:N0} items {Where}";
                _stats.Text = "Counting files…"; _percent.Text = ""; _file.Text = ""; _pause.IsEnabled = false;
                break;
            case JobState.Asking:
                ShowAsk(); return;
            case JobState.Running:
            case JobState.Paused:
                _ask.Visibility = Visibility.Collapsed; _main.Visibility = Visibility.Visible;
                bool paused = j.State == JobState.Paused;
                Badge(paused ? "" : "", paused ? "OxWarn" : "OxAcc");
                _title.Text = paused ? "Paused" : $"{Verb} {j.TotalItems:N0} items";
                _sub.Text = paused ? $"{j.TotalItems:N0} items {Where}" : Where;
                _fill.Background = Theme.Get(paused ? "OxWarn" : "OxAcc");
                _pause.IsEnabled = true; SetButton(_pause, paused ? "" : "", paused ? "Resume" : "Pause", paused);
                _stats.Text = paused ? $"{j.DoneItems:N0} of {j.TotalItems:N0} items · {Format.Size(j.DoneBytes)} of {Format.Size(j.TotalBytes)}"
                    : $"{Format.Size((long)j.BytesPerSecond)}/s · {TimeLeft(j)}";
                _percent.Text = $"{(int)(fraction * 100)}%";
                _file.Text = Pretty(j.CurrentFile);
                break;
            default:
                ShowDone(); break;
        }
        _fill.Scale = new Vector3((float)Math.Max(0.02, fraction), 1, 1);
        DrawGraph(j.State == JobState.Paused);
        if (_details.Visibility == Visibility.Visible) DrawDetails();
    }

    private void ShowAsk()
    {
        _main.Visibility = Visibility.Collapsed; _ask.Visibility = Visibility.Visible;
        if (_ask.Children.Count > 0) return;
        var n = Job.Conflicts.Count;
        _ask.Children.Add(new TextBlock { Text = n == 1 ? $"\"{System.IO.Path.GetFileName(Job.Conflicts[0])}\" is already in {Job.TargetName}" : $"{n:N0} files are already in {Job.TargetName}", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        _ask.Children.Add(new TextBlock { Text = "Pick what to do with files that have the same name.", FontSize = 13, Foreground = Theme.Get("OxMuted"), Margin = new Thickness(0, 0, 0, 6) });
        var options = new List<Border>();
        void Opt(SameName v, string title, string? sub)
        {
            var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 14, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            dot.Children.Add(new Ellipse { Stroke = Theme.Get("OxMuted"), StrokeThickness = 1.5 });
            var check = new Grid { Visibility = Ui.Show(_choice == v) };
            check.Children.Add(new Ellipse { Fill = Theme.Get("OxAcc") });
            check.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = "", FontSize = 11, Foreground = Theme.Get("OxAccText") });
            dot.Children.Add(check);
            g.Children.Add(dot);
            var tx = new StackPanel(); tx.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (sub != null) tx.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Theme.Get("OxMuted") });
            Grid.SetColumn(tx, 1); g.Children.Add(tx);
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = g, Tag = v };
            b.Tapped += (_, _) => { _choice = v; foreach (var o in options) Mark(o); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
            options.Add(b); Mark(b); _ask.Children.Add(b);
        }
        void Mark(Border b)
        {
            bool on = (SameName)b.Tag == _choice;
            b.Background = Theme.Get(on ? "OxSel" : "OxCard"); b.BorderBrush = Theme.Get(on ? "OxSelLine" : "OxLine");
            ((Grid)((Grid)((Grid)b.Child).Children[0]).Children[1]).Visibility = Ui.Show(on);
        }
        Opt(SameName.Replace, "Replace them", "Keep the newer copies");
        Opt(SameName.Skip, "Skip them", "Leave the files that are there");
        Opt(SameName.KeepBoth, "Keep both", "Add “(2)” to the new names");
        Opt(SameName.EachFile, "Let me decide for each file", null);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var cancel = Btn(null, "Cancel"); cancel.Click += (_, _) => Job.Cancel();
        var go = Btn(null, "Continue"); go.Background = Theme.Get("OxAcc"); go.Foreground = Theme.Get("OxAccText");
        go.Click += (_, _) => { Job.Answer(_choice); _ask.Visibility = Visibility.Collapsed; _main.Visibility = Visibility.Visible; };
        row.Children.Add(cancel); row.Children.Add(go); _ask.Children.Add(row);
    }

    private async void ShowDone()
    {
        _tick.Stop();
        if (_doneShown) return;
        _doneShown = true;
        var j = Job;
        _ask.Visibility = Visibility.Collapsed; _main.Visibility = Visibility.Visible;
        _pause.Visibility = Visibility.Collapsed; _cancel.Visibility = Visibility.Collapsed; _graph.Visibility = Visibility.Collapsed; _file.Visibility = Visibility.Collapsed;
        int done = Math.Max(0, j.DoneItems - j.Skipped - j.Problems.Count(p => !p.reason.StartsWith("Copied,")));
        if (j.State == JobState.Cancelled)
        {
            Badge("", "OxMuted"); _title.Text = "Stopped"; _sub.Text = $"{done:N0} of {j.TotalItems:N0} items were {(j.Move ? "moved" : "copied")} {Where}";
            _stats.Text = ""; _percent.Text = "";
            if (done > 0) _undo.Visibility = Visibility.Visible;
        }
        else
        {
            bool problems = j.Problems.Count > 0;
            Badge(problems ? "" : "", problems ? "OxWarn" : "OxAcc");
            _fill.Scale = new Vector3(1, 1, 1);
            _title.Text = problems ? $"Done, but {j.Problems.Count:N0} need a look" : "Done";
            _sub.Text = $"{(j.Move ? "Moved" : "Copied")} {done:N0} items {Where}{(j.Skipped > 0 ? $" · {j.Skipped:N0} skipped" : "")}";
            _stats.Text = j.TotalBytes > 0 ? $"{Format.Size(j.TotalBytes)} in {Took(j.Elapsed)}" : $"in {Took(j.Elapsed)}"; _percent.Text = "";
            _open.Visibility = Visibility.Visible; SetButton(_open, "", $"Open {Job.TargetName}", true);
            _undo.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (problems) { _details.Visibility = Visibility.Visible; _more.Content = "Less"; DrawDetails(); }
            if (!problems) Burst(); // frame 4: a soft mint burst
        }
        // Frame 5: after 4 s it tucks away (unless the mouse is on it, or something needs a look).
        await Task.Delay(4000);
        while (IsPointerOver() ) await Task.Delay(1000);
        if (j.Problems.Count == 0) TuckAway();
    }

    private bool IsPointerOver() => _over;
    private bool _over;
    protected override void OnPointerEntered(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _over = true; base.OnPointerEntered(e); }
    protected override void OnPointerExited(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _over = false; base.OnPointerExited(e); }

    public void TuckAway()
    {
        Opacity = 0; Translation = new Vector3(0, 16, 32);
        _ = Task.Delay(260).ContinueWith((_) => DispatcherQueue.TryEnqueue(() => Finished?.Invoke(this)));
    }

    private void Burst()
    {
        _burst.Children.Clear();
        for (int i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Theme.Get("OxAcc"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(420) };
            dot.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(420) };
            _burst.Children.Add(dot);
            var target = new Vector3((float)(Math.Cos(a) * 30), (float)(Math.Sin(a) * 30), 0);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { dot.Translation = target; dot.Opacity = 0; });
        }
    }

    private void DrawGraph(bool paused)
    {
        List<double> pts; lock (Job.SpeedHistory) pts = Job.SpeedHistory.ToList();
        double w = Math.Max(1, _graph.ActualWidth > 0 ? _graph.ActualWidth * 0.5 : 170), h = _graph.Height;
        if (pts.Count < 2) { _graphLine.Data = null; _graphFill.Data = null; return; }
        var max = Math.Max(1, pts.Max());
        var line = new PathFigure { StartPoint = new Windows.Foundation.Point(0, h - pts[0] / max * (h - 6)) };
        var area = new PathFigure { StartPoint = new Windows.Foundation.Point(0, h), IsClosed = true };
        area.Segments.Add(new LineSegment { Point = line.StartPoint });
        for (int i = 1; i < pts.Count; i++)
        {
            var p = new Windows.Foundation.Point(i * w / 79.0, h - pts[i] / max * (h - 6));
            line.Segments.Add(new LineSegment { Point = p }); area.Segments.Add(new LineSegment { Point = p });
        }
        area.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point((pts.Count - 1) * w / 79.0, h) });
        var lg = new PathGeometry(); lg.Figures.Add(line); var ag = new PathGeometry(); ag.Figures.Add(area);
        _graphLine.Data = lg; _graphFill.Data = ag;
        var color = paused ? "OxWarn" : "OxAcc";
        _graphLine.Stroke = Theme.Get(color);
        var b = Theme.Get(color) as SolidColorBrush;
        _graphFill.Fill = new SolidColorBrush(b?.Color ?? Microsoft.UI.Colors.Teal) { Opacity = 0.22 };
    }

    private void DrawDetails()
    {
        _details.Children.Clear();
        var j = Job;
        _details.Children.Add(T(12.5, text: $"{j.DoneItems:N0} of {j.TotalItems:N0} items · {Format.Size(j.DoneBytes)} of {Format.Size(j.TotalBytes)}"));
        if (j.Skipped > 0) _details.Children.Add(T(12.5, text: $"{j.Skipped:N0} skipped (same name)"));
        List<(string path, string reason)> problems; lock (j.Problems) problems = j.Problems.ToList();
        foreach (var (path, reason) in problems.Take(6)) _details.Children.Add(new TextBlock { Text = $"{System.IO.Path.GetFileName(path)}: {reason}", FontSize = 12, Foreground = Theme.Get("OxWarn"), TextWrapping = TextWrapping.Wrap });
        if (problems.Count > 6) _details.Children.Add(T(12, text: $"and {problems.Count - 6:N0} more"));
    }

    // ---------- Small helpers ----------
    private void Badge(string glyph, string color)
    {
        _badgeIcon.Glyph = glyph; _badgeIcon.Foreground = Theme.Get(color);
        var c = (Theme.Get(color) as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Teal;
        _badge.Background = new SolidColorBrush(c) { Opacity = 0.16 };
    }
    private static string TimeLeft(CopyJob j)
    {
        if (j.BytesPerSecond < 1 || j.TotalBytes == 0) return "working it out";
        var s = (j.TotalBytes - j.DoneBytes) / j.BytesPerSecond;
        return s < 5 ? "almost done" : s < 60 ? $"about {Math.Ceiling(s / 5) * 5:0} seconds left" : s < 3600 ? $"about {Math.Round(s / 60):0} minute{(Math.Round(s / 60) == 1 ? "" : "s")} left" : $"about {s / 3600:0.#} hours left";
    }
    private static string Took(TimeSpan t) => t.TotalSeconds < 60 ? $"{Math.Max(1, (int)t.TotalSeconds)} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";
    private static string Pretty(string path) { if (string.IsNullOrEmpty(path)) return ""; var dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? ""); return string.IsNullOrEmpty(dir) ? System.IO.Path.GetFileName(path) : $"{dir} › {System.IO.Path.GetFileName(path)}"; }
    private static TextBlock T(double size, bool bold = false, string text = "") => new() { FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Text = text };
    private static Button Btn(string? glyph, string text)
    {
        var b = new Button { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7), Background = Theme.Get("OxCard"), BorderBrush = Theme.Get("OxLine2"), BorderThickness = new Thickness(1) };
        SetButton(b, glyph, text, false);
        return b;
    }
    private static void SetButton(Button b, string? glyph, string text, bool primary)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph != null) sp.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
        b.Content = sp;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        if (primary) { b.Background = Theme.Get("OxAcc"); b.Foreground = Theme.Get("OxAccText"); b.BorderThickness = new Thickness(0); }
        else { b.Background = Theme.Get("OxCard"); b.Foreground = Theme.Get("OxText"); b.BorderThickness = new Thickness(1); }
    }
}
