// A viewer in its own window ("Open in its own window"): resizable, can go full screen, glass like the Explorer.
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using OmeblyExplorer.Views;
using OmeblyExplorer.Views.Viewers;

namespace OmeblyExplorer;

public sealed class ViewerWindow : Window
{
    public ViewerWindow(FileItem item, IReadOnlyList<FileItem> list)
    {
        Title = item.Name;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "OmeblyExplorer.ico"));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(760 * scale)));
        var root = new Grid { RequestedTheme = Theme.Current, Background = Theme.Get("OxWin") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition());
        var drag = new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(drag);
        var host = new ViewerHost(inWindow: true);
        Grid.SetRow(host, 1); root.Children.Add(host);
        Content = root;
        SetTitleBar(drag);
        var tb = AppWindow.TitleBar; tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent; tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = Theme.Current == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        host.Closed += Close;
        host.FullScreenWanted += (on) => AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        host.DeleteWanted += (f) => _ = FileOps.Recycle(new[] { f.Path });
        root.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            bool ctrl = (GetKeyState(0x11) & 0x8000) != 0, shift = (GetKeyState(0x10) & 0x8000) != 0;
            if (host.HandleKey(e.Key, ctrl, shift)) e.Handled = true;
        }), true);
        host.Show(item, list);
        Closed += (_, _) => { if (host.Current != null) host.CloseViewer(); };
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int vk);
}
