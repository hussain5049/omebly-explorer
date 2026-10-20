// What every viewer has: it loads one file, can be closed, may add buttons to the top bar and may take keys.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed record Tool(string Glyph, string Name, Action Run);

public abstract class ViewerBase : Grid
{
    /// <summary>Shows the file. Called on the UI thread; heavy work goes to the background.</summary>
    public abstract Task LoadAsync(FileItem file, CancellationToken ct);
    /// <summary>The grey text after the name in the top bar ("4032 × 3024 · 6.1 MB").</summary>
    public virtual string Info(FileItem f) => Format.Size(f.Size);
    public virtual IReadOnlyList<Tool> Tools => Array.Empty<Tool>();
    /// <summary>Keys this viewer uses itself (return true if handled).</summary>
    public virtual bool OnKey(VirtualKey key, bool ctrl, bool shift) => false;
    /// <summary>The keyboard hints at the bottom ("Space play or pause").</summary>
    public virtual (string key, string what)[] Hints => Array.Empty<(string, string)>();
    /// <summary>Before closing or moving on (unsaved edits ask first). True = fine to go.</summary>
    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);
    /// <summary>Stop playing, let go of files.</summary>
    public virtual void Close() { }
    /// <summary>Arrows go to the next file (pictures) or are used inside (video seeking, text cursor).</summary>
    public virtual bool ArrowsBrowse => false;
    /// <summary>Page Up / Page Down scroll inside (text, PDF): next file is then Ctrl+Page Down.</summary>
    public virtual bool PageKeysScroll => false;
    public event Action? InfoChanged;
    protected void RaiseInfo() => InfoChanged?.Invoke();

    protected static FontFamily Icons => (FontFamily)Application.Current.Resources["OxIcons"];
    protected static TextBlock Text(string s, double size = 13, string color = "OxText", bool bold = false) =>
        new() { Text = s, FontSize = size, Foreground = Theme.Get(color), FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    protected static Button IconButton(string glyph, string name, Action run)
    {
        var b = new Button { Content = new FontIcon { FontFamily = Icons, Glyph = glyph, FontSize = 15 }, Width = 38, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
        b.Click += (_, _) => run();
        return b;
    }
    /// <summary>Lists draw each row with code: a list item can't simply be given a control as its content (the list
    /// puts the item's own text back), so each row gets an empty holder that is filled when the row comes into view.</summary>
    protected static void Rows(ListViewBase list, Func<object, UIElement?> make)
    {
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border/></DataTemplate>");
        list.ContainerContentChanging += (_, a) => { if (!a.InRecycleQueue && a.ItemContainer.ContentTemplateRoot is Border b) b.Child = make(a.Item); };
    }
    /// <summary>A message in the middle of the viewer (a file that can't be shown, still loading...).</summary>
    protected void ShowMessage(string title, string? detail = null)
    {
        Children.Clear();
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 8, MaxWidth = 520 };
        sp.Children.Add(new FontIcon { FontFamily = Icons, Glyph = "", FontSize = 34, Foreground = Theme.Get("OxWarn") });
        var t = Text(title, 16, bold: true); t.TextAlignment = TextAlignment.Center; sp.Children.Add(t);
        if (detail != null) { var d = Text(detail, 13, "OxMuted"); d.TextAlignment = TextAlignment.Center; sp.Children.Add(d); }
        Children.Add(sp);
    }
}
