// The Recycle Bin inside Omebly Explorer: what was deleted, where it came from and when; restore some or all,
// delete some for good, or empty it.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed class RecycleView : Grid
{
    public event Action<string>? Reveal;   // a restored item's new place
    public event Action? BinChanged;
    public Func<string, string, string, Task<bool>>? Confirm; // title, text, yes
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, Padding = new Thickness(10, 4, 10, 10) };
    private readonly TextBlock _empty = new() { Text = "The Recycle Bin is empty", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _restore, _restoreAll, _delete, _emptyAll;
    private List<BinItem> _items = new();
    public int Count => _items.Count;
    public IReadOnlyList<BinItem> Selected => _list.SelectedItems.Cast<BinItem>().ToList();

    public RecycleView()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(18, 12, 18, 10) };
        _restore = Btn("", "Restore", async () => await RestoreAsync(Selected));
        _restoreAll = Btn("", "Restore all", async () => await RestoreAsync(_items));
        _delete = Btn("", "Delete for good", async () => await DeleteAsync(Selected));
        _emptyAll = Btn("", "Empty Recycle Bin", async () => await DeleteAsync(_items, all: true));
        foreach (var b in new[] { _restore, _restoreAll, _delete, _emptyAll }) bar.Children.Add(b);
        Children.Add(bar);
        var header = Row("Name", "Original location", "Date deleted", "Size", header: true);
        ((Grid)header).Padding = new Thickness(26, 0, 30, 0);
        Grid.SetRow(header, 1); Children.Add(header);
        _list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(FrameworkElement.MinHeightProperty, 34.0), new Setter(Control.CornerRadiusProperty, new CornerRadius(7)) } };
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border/></DataTemplate>");
        _list.ContainerContentChanging += (_, a) => { if (!a.InRecycleQueue && a.ItemContainer.ContentTemplateRoot is Border b && a.Item is BinItem i) b.Child = Row(i.Name, i.From, Format.When(i.Deleted), i.IsFolder ? "" : Format.Size(i.Size), folder: i.IsFolder); };
        _list.SelectionChanged += (_, _) => Enable();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Delete) { e.Handled = true; await DeleteAsync(Selected); }
            else if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await RestoreAsync(Selected); }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_list, "BinList");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_list, "Deleted items");
        Grid.SetRow(_list, 2); Children.Add(_list);
        _empty.Foreground = Theme.Get("OxMuted"); Grid.SetRow(_empty, 2); Children.Add(_empty);
    }

    public async Task LoadAsync()
    {
        _items = await Task.Run(RecycleBin.Items);
        _list.ItemsSource = _items;
        _empty.Visibility = Ui.Show(_items.Count == 0);
        Enable();
    }
    public void FocusList() => _list.Focus(FocusState.Programmatic);
    private void Enable() { _restore.IsEnabled = _delete.IsEnabled = _list.SelectedItems.Count > 0; _restoreAll.IsEnabled = _emptyAll.IsEnabled = _items.Count > 0; }

    private async Task RestoreAsync(IReadOnlyList<BinItem> items)
    {
        if (items.Count == 0) return;
        var list = items.ToList();
        var back = await Task.Run(() => list.Select(RecycleBin.Restore).ToList());
        var first = back.FirstOrDefault((p) => p != null);
        // Undo of a restore sends them back to the Recycle Bin.
        var restored = back.Where((p) => p != null).Cast<string>().ToList();
        if (restored.Count > 0) UndoLog.Add(new UndoStep
        {
            What = restored.Count == 1 ? $"Restore “{Path.GetFileName(restored[0])}”" : $"Restore {restored.Count} items",
            Undo = () => FileOps.Recycle(restored.Where(UndoLog.Exists)),
            Redo = () => Task.Run(() => RecycleBin.Find(restored, DateTime.Now.AddMinutes(-60)).All((i) => RecycleBin.Restore(i) != null)),
        });
        await LoadAsync(); BinChanged?.Invoke();
        if (list.Count == 1 && first != null) Reveal?.Invoke(first);
    }

    private async Task DeleteAsync(IReadOnlyList<BinItem> items, bool all = false)
    {
        if (items.Count == 0) return;
        var what = all ? "Everything in the Recycle Bin" : items.Count == 1 ? $"“{items[0].Name}”" : $"These {items.Count} items";
        if (Confirm != null && !await Confirm(all ? "Empty the Recycle Bin?" : "Delete for good?", $"{what} will be deleted for good. This can't be undone.", all ? "Empty" : "Delete for good")) return;
        var list = items.ToList();
        await Task.Run(() => { foreach (var i in list) RecycleBin.DeleteForever(i); });
        await LoadAsync(); BinChanged?.Invoke();
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

    private static FrameworkElement Row(string name, string from, string when, string size, bool header = false, bool folder = false)
    {
        var g = new Grid { Height = 34, ColumnSpacing = 12 };
        foreach (var w in new[] { new GridLength(22), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(150), new GridLength(90) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        if (!header) g.Children.Add(new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["OxIcons"], Glyph = folder ? "" : "", FontSize = 15, Foreground = folder ? Ui.Brush("#F4C04E") : Theme.Get("OxMuted"), VerticalAlignment = VerticalAlignment.Center });
        int c = 1;
        foreach (var t in new[] { name, from, when, size })
        {
            var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = header ? 12 : c == 1 ? 13 : 12.5, Foreground = Theme.Get(header || c > 1 ? "OxMuted" : "OxText") };
            if (c == 4) tb.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(tb, c++); g.Children.Add(tb);
        }
        if (header) { g.BorderBrush = Theme.Get("OxLine"); g.BorderThickness = new Thickness(0, 0, 0, 1); }
        return g;
    }
}
