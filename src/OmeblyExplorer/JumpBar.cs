// The small glass bar that sits on top of other apps' Open and Save windows (see Core/DialogWatcher.cs).
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using OmeblyExplorer.Views;

namespace OmeblyExplorer;

public sealed class JumpBar : Window
{
    private static readonly Dictionary<IntPtr, JumpBar> Open = new();
    /// <summary>Where Omebly's windows are right now (the bar offers them first).</summary>
    public static Func<IEnumerable<string>>? CurrentFolders;
    private readonly IntPtr _dialog, _hwnd;

    public static void Begin()
    {
        if (DialogWatcher.Running) return;
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        DialogWatcher.DialogShown += (d) => ui.TryEnqueue(() => { if (!Open.ContainsKey(d) && Settings.Current.JumpBar) { var b = new JumpBar(d); Open[d] = b; b.Place(); b.AppWindow.Show(false); } });
        DialogWatcher.DialogGone += (d) => ui.TryEnqueue(() => { if (Open.Remove(d, out var b)) b.Close(); });
        DialogWatcher.DialogMoved += (d) => ui.TryEnqueue(() => { if (Open.TryGetValue(d, out var b)) b.Place(); });
        DialogWatcher.Start();
    }
    public static void End() { DialogWatcher.Stop(); foreach (var b in Open.Values.ToList()) b.Close(); Open.Clear(); }

    private JumpBar(IntPtr dialog)
    {
        _dialog = dialog;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Omebly Jump to";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter op) { op.SetBorderAndTitleBar(true, false); op.IsResizable = false; op.IsMaximizable = false; op.IsMinimizable = false; op.IsAlwaysOnTop = false; }
        AppWindow.IsShownInSwitchers = false;
        // Belongs to the dialog: stays on top of it and goes away with it.
        SetWindowLongPtrW(_hwnd, -8 /* GWLP_HWNDPARENT */, dialog);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "Jump to", FontSize = 12, Foreground = Theme.Get("OxMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) });
        var places = new List<(string name, string path, string color)>();
        foreach (var f in CurrentFolders?.Invoke() ?? Array.Empty<string>()) places.Add(("In Omebly: " + TabState.NameOf(f), f, "#2EF2B3"));
        foreach (var p in Settings.Current.Pinned.Where((p) => Directory.Exists(p.Path)).Take(6)) places.Add((TabState.NameOf(p.Path), p.Path, p.Color));
        foreach (var f in Places.Folders().Where((f) => f.Name is "Desktop" or "Downloads" or "Documents" or "Pictures")) places.Add((f.Name, f.Path, f.Color));
        foreach (var (name, path, color) in places.GroupBy((p) => p.path, StringComparer.OrdinalIgnoreCase).Select((g) => g.First()).Take(10))
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = HomeView.Folder(), Fill = Ui.Brush(color), Width = 14, Height = 12, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12.5 });
            var b = new Button { Content = sp, Padding = new Thickness(9, 4, 10, 5), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(b, path);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Jump to " + name);
            var target = path;
            b.Click += async (_, _) => await DialogWatcher.GoTo(_dialog, target);
            row.Children.Add(b);
        }
        Content = new Grid { RequestedTheme = Theme.Current, Background = Theme.Get("OxWin"), Children = { new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled } } };
    }

    /// <summary>Just above the dialog, as wide as it (or inside its top edge if there's no room above).</summary>
    private void Place()
    {
        if (!DialogWatcher.GetWindowRect(_dialog, out var r)) return;
        int h = (int)(44 * GetDpiForWindow(_hwnd) / 96.0);
        int y = r.Top - h - 4;
        if (y < 0) y = r.Top + 2;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(r.Left, y, Math.Max(320, r.Right - r.Left), h));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
