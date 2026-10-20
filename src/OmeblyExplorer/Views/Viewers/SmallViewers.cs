// Markdown (formatted), CSV (a table), web pages (shown safely: no scripts, nothing loaded from the internet),
// fonts (the letters at several sizes), ZIP files (what's inside), and anything else (details and Open with).
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views.Viewers;

// ---------- Markdown ----------
public sealed class MarkdownViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    private readonly RichTextBlock _doc = new() { IsTextSelectionEnabled = true, MaxWidth = 860, FontSize = 15, LineHeight = 24 };
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        var (text, _, _) = await Task.Run(() => TextViewer.Read(file.Path), ct);
        _doc.Foreground = Theme.Get("OxText");
        bool inCode = false; var code = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```")) { if (inCode) { AddCode(code.ToString().TrimEnd('\n')); code.Clear(); } inCode = !inCode; continue; }
            if (inCode) { code.AppendLine(raw); continue; }
            if (line.Length == 0) continue;
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success) { var p = Para(h.Groups[2].Value, new[] { 0, 28, 23, 19, 17, 15, 15 }[h.Groups[1].Length], bold: true); p.Margin = new Thickness(0, 14, 0, 6); _doc.Blocks.Add(p); continue; }
            var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (li.Success) { var bullet = li.Groups[2].Value.Length > 1 || char.IsDigit(li.Groups[2].Value[0]) ? li.Groups[2].Value + " " : "•  "; var p = Para(li.Groups[3].Value, prefix: bullet); p.Margin = new Thickness(18 + li.Groups[1].Length * 8, 2, 0, 2); _doc.Blocks.Add(p); continue; }
            if (line.StartsWith('>')) { var p = Para(line.TrimStart('>', ' ')); p.Margin = new Thickness(16, 6, 0, 6); p.Foreground = Theme.Get("OxMuted"); _doc.Blocks.Add(p); continue; }
            if (Regex.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$")) { _doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "———" } }, Foreground = Theme.Get("OxFaint") }); continue; }
            var para = Para(line); para.Margin = new Thickness(0, 4, 0, 4); _doc.Blocks.Add(para);
        }
        if (inCode && code.Length > 0) AddCode(code.ToString());
        Children.Add(new ScrollViewer { Content = new Border { Padding = new Thickness(48, 24, 48, 48), Child = _doc, HorizontalAlignment = HorizontalAlignment.Center } });
    }
    private void AddCode(string code) => _doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = code } }, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 13, Margin = new Thickness(12, 8, 0, 8), Foreground = Theme.Get("OxMuted") });
    // Inline marks: **bold**, *italic*, `code`, [text](link). Links show as text (nothing is opened by itself).
    private static Paragraph Para(string text, double size = 15, bool bold = false, string? prefix = null)
    {
        var p = new Paragraph { FontSize = size };
        if (prefix != null) p.Inlines.Add(new Run { Text = prefix });
        foreach (Match m in Regex.Matches(text, @"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|\*(?<i>[^*]+)\*|_(?<i>[^_]+)_|`(?<c>[^`]+)`|\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\)|(?<t>[^*_`\[]+|.)"))
        {
            Inline run;
            if (m.Groups["b"].Success) run = new Run { Text = m.Groups["b"].Value, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
            else if (m.Groups["i"].Success) run = new Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic };
            else if (m.Groups["c"].Success) run = new Run { Text = m.Groups["c"].Value, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
            else if (m.Groups["lt"].Success) run = new Run { Text = m.Groups["lt"].Value, Foreground = Theme.Get("OxAcc") };
            else run = new Run { Text = m.Value };
            if (bold && run is Run r) r.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            p.Inlines.Add(run);
        }
        return p;
    }
}

// ---------- CSV ----------
public sealed class CsvViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    private int _rows, _cols; private bool _more;
    public override string Info(FileItem f) => $"{_rows:N0} rows · {_cols} columns{(_more ? " · showing the first 10,000" : "")} · {Format.Size(f.Size)}";
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        var rows = await Task.Run(() =>
        {
            var (text, _, _) = TextViewer.Read(file.Path);
            char sep = file.Extension == ".tsv" ? '\t' : Guess(text);
            return Parse(text, sep, 10_001);
        }, ct);
        _more = rows.Count > 10_000; if (_more) rows.RemoveAt(rows.Count - 1);
        _rows = Math.Max(0, rows.Count - 1); _cols = rows.Count > 0 ? Math.Min(60, rows.Max((r) => r.Count)) : 0;
        RaiseInfo();
        if (rows.Count == 0) { ShowMessage("This file is empty"); return; }
        var widths = Enumerable.Range(0, _cols).Select((c) => Math.Clamp(rows.Take(200).Max((r) => c < r.Count ? r[c].Length : 0) * 7.5 + 24, 60, 320)).ToArray();
        UIElement Row(List<string> cells, bool head)
        {
            var g = new StackPanel { Orientation = Orientation.Horizontal };
            for (int c = 0; c < _cols; c++)
                g.Children.Add(new Border { Width = widths[c], Padding = new Thickness(10, 6, 10, 6), BorderBrush = Theme.Get("OxLine"), BorderThickness = new Thickness(0, 0, 1, 1), Child = new TextBlock { Text = c < cells.Count ? cells[c] : "", TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = head ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, FontSize = 13, Foreground = Theme.Get(head ? "OxText" : "OxText") } });
            return g;
        }
        var header = new Border { Background = Theme.Get("OxCard2"), Child = Row(rows[0], true) };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, ItemsSource = rows.Skip(1).ToList(), Padding = new Thickness(0) };
        list.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(ListViewItem.PaddingProperty, new Thickness(0)), new Setter(ListViewItem.MinHeightProperty, 0d) } };
        Rows(list, (o) => o is List<string> r ? Row(r, false) : null);
        var inner = new Grid { Margin = new Thickness(24, 16, 24, 16) };
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); inner.RowDefinitions.Add(new RowDefinition());
        inner.Children.Add(header); Grid.SetRow(list, 1); inner.Children.Add(list);
        Children.Add(new ScrollViewer { Content = inner, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    private static char Guess(string t) { var first = t.Split('\n')[0]; return new[] { ',', ';', '\t', '|' }.OrderByDescending((c) => first.Count((x) => x == c)).First(); }
    /// <summary>CSV with quotes ("a, b" and "say ""hi""") and line breaks inside quotes.</summary>
    public static List<List<string>> Parse(string text, char sep, int maxRows)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length && rows.Count < maxRows; i++)
        {
            char c = text[i];
            if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); continue; }
            if (c == '"') q = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(row); row = new(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }
}

// ---------- Web pages ----------
public sealed class HtmlViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    private WebView2? _web;
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        _web = new WebView2();
        Children.Add(_web);
        try
        {
            var dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer", "WebView");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, dataDir, new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(env);
        }
        catch { ShowMessage("Web pages can't be shown here", "Microsoft Edge WebView2 isn't on this PC. Try Open in its usual app."); return; }
        var core = _web.CoreWebView2;
        // Safe preview: no scripts, no pop-ups, no downloads, nothing fetched from the internet, no leaving the page.
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        const string Host = "page.omebly.invalid";
        var folder = System.IO.Path.GetDirectoryName(file.Path)!;
        core.ClearVirtualHostNameToFolderMapping(Host);
        core.SetVirtualHostNameToFolderMapping(Host, folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Deny);
        var page = $"https://{Host}/{Uri.EscapeDataString(file.Name)}";
        core.NavigationStarting += (_, e) => { if (!string.Equals(e.Uri, page, StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            // Only the page's own folder (through the made-up host) and inline data. Everything else is blocked:
            // the internet, and file: links, which for \\server\share paths would make Windows sign in to that computer.
            var u = e.Request.Uri;
            if (!u.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.Navigate(page);
    }
    public override void Close() { try { _web?.Close(); } catch { } }
}

// ---------- Fonts ----------
public sealed class FontViewer : ViewerBase
{
    private string _family = "";
    public override string Info(FileItem f) => _family.Length > 0 ? $"{_family} · {Format.Size(f.Size)}" : Format.Size(f.Size);
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        _family = await Task.Run(() => FontName(file.Path) ?? "", ct);
        RaiseInfo();
        if (_family.Length == 0) { ShowMessage("This font can't be shown", "Its name couldn't be read."); return; }
        var ff = new FontFamily($"{new Uri(file.Path).AbsoluteUri}#{_family}");
        var sp = new StackPanel { Spacing = 14, Padding = new Thickness(56, 36, 56, 36) };
        sp.Children.Add(new TextBlock { Text = _family, FontSize = 14, Foreground = Theme.Get("OxMuted") });
        sp.Children.Add(new TextBlock { Text = "ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789 !?&@#%()", FontFamily = ff, FontSize = 30, Foreground = Theme.Get("OxText"), TextWrapping = TextWrapping.Wrap });
        foreach (var s in new[] { 14, 20, 28, 40, 64 })
            sp.Children.Add(new TextBlock { Text = "The quick brown fox jumps over the lazy dog", FontFamily = ff, FontSize = s, Foreground = Theme.Get("OxText"), TextWrapping = TextWrapping.Wrap });
        Children.Add(new ScrollViewer { Content = sp });
    }
    /// <summary>The family name from a TrueType / OpenType file's name table (read-only, size-checked).</summary>
    public static string? FontName(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 12 || b.Length > 64 << 20) return null;
            ushort U16(int o) => (ushort)(b[o] << 8 | b[o + 1]); uint U32(int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
            int tables = U16(4);
            for (int t = 0; t < tables && 12 + t * 16 + 16 <= b.Length; t++)
            {
                int rec = 12 + t * 16;
                if (b[rec] != 'n' || b[rec + 1] != 'a' || b[rec + 2] != 'm' || b[rec + 3] != 'e') continue;
                int off = (int)U32(rec + 8); if (off + 6 > b.Length) return null;
                int count = U16(off + 2), strings = off + U16(off + 4);
                string? best = null;
                for (int i = 0; i < count && off + 6 + i * 12 + 12 <= b.Length; i++)
                {
                    int r = off + 6 + i * 12; int platform = U16(r), nameId = U16(r + 6), len = U16(r + 8), so = strings + U16(r + 10);
                    if (nameId != 1 || so + len > b.Length) continue;
                    var name = platform is 0 or 3 ? System.Text.Encoding.BigEndianUnicode.GetString(b, so, len) : System.Text.Encoding.Latin1.GetString(b, so, len);
                    if (platform == 3) return name; best ??= name;
                }
                return best;
            }
        }
        catch { }
        return null;
    }
}

// ---------- ZIP ----------
public sealed class ZipViewer : ViewerBase
{
    private int _count; private long _total;
    public override string Info(FileItem f) => $"{_count:N0} items · {Format.Size(_total)} unpacked · {Format.Size(f.Size)}";
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        List<(string name, long size, DateTimeOffset when)> entries;
        try
        {
            entries = await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(file.Path);
                return zip.Entries.Take(100_000).Select((e) => (e.FullName, e.Length, e.LastWriteTime)).ToList();
            }, ct);
        }
        catch { ShowMessage("This ZIP can't be read", "It may be damaged or use a kind of packing ZIP readers don't know."); return; }
        _count = entries.Count((e) => !e.name.EndsWith('/')); _total = entries.Sum((e) => e.size); RaiseInfo();
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new Thickness(24, 12, 24, 24), ItemsSource = entries.Where((e) => !e.name.EndsWith('/')).OrderBy((e) => e.name, StringComparer.OrdinalIgnoreCase).ToList() };
        Rows(list, (o) =>
        {
            if (o is not ValueTuple<string, long, DateTimeOffset> e) return null;
            var g = new Grid { ColumnSpacing = 14, Height = 32 };
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.Children.Add(new TextBlock { Text = e.Item1.Replace('/', '\\'), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Get("OxText") });
            var w = new TextBlock { Text = Format.When(e.Item3.LocalDateTime), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Get("OxMuted"), FontSize = 12.5 }; Grid.SetColumn(w, 1); g.Children.Add(w);
            var s = new TextBlock { Text = Format.Size(e.Item2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Theme.Get("OxMuted"), FontSize = 12.5 }; Grid.SetColumn(s, 2); g.Children.Add(s);
            return g;
        });
        var top = Text("What's inside. Unpacking comes in the next stage (zip and unzip).", 13, "OxMuted"); top.Margin = new Thickness(36, 14, 24, 0);
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(top); Grid.SetRow(list, 1); grid.Children.Add(list);
        Children.Add(grid);
    }
}

// ---------- Anything else ----------
public sealed class OtherViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        // A preview Windows knows (from an installed app) is used when there is one.
        if (PreviewHandlerViewer.HasHandler(file.Extension))
        {
            var ph = new PreviewHandlerViewer(); Children.Add(ph); await ph.LoadAsync(file, ct); _inner = ph; return;
        }
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10, MaxWidth = 480 };
        var icon = new Image { Width = 96, Height = 96 };
        sp.Children.Add(icon);
        var name = Text(file.Name, 18, bold: true); name.TextAlignment = TextAlignment.Center; sp.Children.Add(name);
        var what = Text($"{file.TypeText} · {Format.Size(file.Size)}", 13.5, "OxMuted"); what.TextAlignment = TextAlignment.Center; sp.Children.Add(what);
        var note = Text("Omebly Explorer can't show this kind of file inside. You can open it with an app on this PC.", 13.5, "OxMuted"); note.TextAlignment = TextAlignment.Center; sp.Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var open = new Button { Content = "Open in its usual app", CornerRadius = new CornerRadius(9), Background = Theme.Get("OxAcc"), Foreground = Theme.Get("OxAccText") };
        open.Click += (_, _) => Native.ShellRun(file.Path);
        var with = new Button { Content = "Open with…", CornerRadius = new CornerRadius(9) };
        with.Click += (_, _) => Native.ShellRun(file.Path, "openas");
        row.Children.Add(open); row.Children.Add(with); sp.Children.Add(row);
        Children.Add(sp);
        Thumbnails.Request(file, 96);
        file.PropertyChanged += (_, _) => icon.Source = file.Icon;
        icon.Source = file.Icon;
    }
    private ViewerBase? _inner;
    public override void Close() => _inner?.Close();
}
