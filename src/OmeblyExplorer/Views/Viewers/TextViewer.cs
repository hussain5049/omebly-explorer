// Text and code: line numbers, colours for code (keywords, text in quotes, comments, numbers), and quick edits with
// Save (Ctrl+S). Keeps the file's own text encoding and line endings. Very big files open their first 5 MB.
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed class TextViewer : ViewerBase
{
    public override bool PageKeysScroll => true;
    private const long MaxBytes = 5L << 20, ColourUpTo = 400_000;
    private readonly bool _code;
    private string _path = "", _text = "";
    private Encoding _encoding = new UTF8Encoding(false);
    private bool _truncated, _editing, _dirty;
    private int _lines;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private TextBox? _editor;
    private static readonly FontFamily Mono = new("Cascadia Mono,Cascadia Code,Consolas");

    public TextViewer(bool code) { _code = code; Children.Add(_scroll); }

    public override (string, string)[] Hints => _editing ? new[] { ("Ctrl S", "save") } : new[] { ("E", "edit") };
    public override IReadOnlyList<Tool> Tools => new[] { new Tool("", "Edit (E)", StartEdit), new Tool("", "Save (Ctrl+S)", () => _ = Save()) };
    public override string Info(FileItem f) => $"{_lines:N0} lines · {EncodingName} · {Format.Size(f.Size)}{(_truncated ? " · showing the first 5 MB" : "")}{(_dirty ? " · not saved" : "")}";
    private string EncodingName => _encoding is UTF8Encoding u ? (u.GetPreamble().Length > 0 ? "UTF-8 (BOM)" : "UTF-8") : _encoding.EncodingName;

    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        _path = file.Path;
        (_text, _encoding, _truncated) = await Task.Run(() => Read(file.Path), ct);
        ct.ThrowIfCancellationRequested();
        _lines = 1; foreach (var c in _text) if (c == '\n') _lines++;
        if (_text.Length <= ColourUpTo) ShowColoured(); // with line numbers (and colours for code)
        else ShowPlain(readOnly: true);
        RaiseInfo();
    }

    public static (string text, Encoding enc, bool truncated) Read(string path)
    {
        using var f = File.OpenRead(path);
        var len = (int)Math.Min(f.Length, MaxBytes);
        var buf = new byte[len]; int read = 0; while (read < len) { int n = f.Read(buf, read, len - read); if (n <= 0) break; read += n; }
        Encoding enc;
        int skip = 0;
        if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) { enc = new UTF8Encoding(true); skip = 3; }
        else if (read >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) { enc = Encoding.Unicode; skip = 2; }
        else if (read >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) { enc = Encoding.BigEndianUnicode; skip = 2; }
        else
        {
            try { var strict = new UTF8Encoding(false, true); strict.GetString(buf, 0, read - TrailingPartial(buf, read)); enc = new UTF8Encoding(false); }
            catch { enc = Encoding.Latin1; } // not valid UTF-8: old Windows text, shown as it is
        }
        return (enc.GetString(buf, skip, read - skip), enc, f.Length > MaxBytes);
    }
    // A cut at 5 MB may split a UTF-8 character: don't call that invalid.
    private static int TrailingPartial(byte[] b, int n) { int k = 0; while (k < 3 && n - k - 1 >= 0 && (b[n - k - 1] & 0xC0) == 0x80) k++; return n - k - 1 >= 0 && (b[n - k - 1] & 0xC0) == 0xC0 ? k + 1 : 0; }

    private void ShowPlain(bool readOnly)
    {
        _editor = new TextBox { IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, FontSize = 13.5, IsSpellCheckEnabled = false, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(24, 16, 24, 16) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        foreach (var k in new[] { "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundReadOnly" }) _editor.Resources[k] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _editor.Text = _text.Replace("\r\n", "\n").Replace('\n', '\r'); // after AcceptsReturn, or only the first line is kept
        _editor.TextChanged += (_, _) => { if (_editing && !_dirty) { _dirty = true; RaiseInfo(); } };
        Children.Clear(); Children.Add(_editor);
    }

    private void ShowColoured()
    {
        var grid = new Grid { ColumnSpacing = 18, Padding = new Thickness(18, 16, 24, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var numbers = new TextBlock { FontFamily = Mono, FontSize = 13.5, Foreground = Theme.Get("OxFaint"), TextAlignment = TextAlignment.Right, Text = string.Join("\n", Enumerable.Range(1, _lines)) };
        grid.Children.Add(numbers);
        var rich = new RichTextBlock { FontFamily = Mono, FontSize = 13.5, IsTextSelectionEnabled = true, Foreground = Theme.Get("OxText") };
        var para = new Paragraph();
        foreach (var (text, kind) in _code ? Highlight(_text) : new[] { (_text, (Tok?)null) }) { var run = new Run { Text = text }; if (kind != null) run.Foreground = Ui.Brush(Colour(kind.Value)); para.Inlines.Add(run); } // a run with no colour of its own uses the text colour
        rich.Blocks.Add(para);
        Grid.SetColumn(rich, 1); grid.Children.Add(rich);
        _scroll.Content = grid;
    }

    private enum Tok { Keyword, Str, Comment, Number }
    private static string Colour(Tok t) => (t, Theme.Current == ElementTheme.Light) switch
    {
        (Tok.Keyword, false) => "#B79BFF", (Tok.Keyword, true) => "#6A3FD1",
        (Tok.Str, false) => "#5EF0C0", (Tok.Str, true) => "#0A7A54",
        (Tok.Comment, false) => "#6F867D", (Tok.Comment, true) => "#86928D",
        (Tok.Number, false) => "#FFB648", _ => "#B86E00",
    };
    private static readonly Regex Tokens = new(
        @"(?<c>//[^\n]*|/\*[\s\S]*?\*/|#(?![0-9a-fA-F]{3,8}\b)[^\n]*|<!--[\s\S]*?-->|--[^\n]*)|(?<s>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`)|(?<n>\b\d+(?:\.\d+)?\b)|(?<k>\b(?:abstract|async|await|break|case|catch|class|const|continue|def|default|delegate|do|else|enum|export|extends|false|final|finally|fn|for|foreach|from|func|function|if|implements|import|in|interface|internal|is|let|match|namespace|new|null|nil|None|override|package|private|protected|public|readonly|return|sealed|select|self|static|struct|super|switch|this|throw|true|True|False|try|type|typeof|using|var|virtual|void|while|with|yield|where|and|or|not|elif|lambda|pass|raise|impl|pub|mod|use|mut|go|defer|chan|SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|JOIN|ORDER|BY|GROUP|CREATE|TABLE)\b)",
        RegexOptions.Compiled);
    private static IEnumerable<(string, Tok?)> Highlight(string text)
    {
        int at = 0;
        foreach (Match m in Tokens.Matches(text))
        {
            if (m.Index > at) yield return (text[at..m.Index], null);
            Tok t = m.Groups["c"].Success ? Tok.Comment : m.Groups["s"].Success ? Tok.Str : m.Groups["n"].Success ? Tok.Number : Tok.Keyword;
            yield return (m.Value, t);
            at = m.Index + m.Length;
        }
        if (at < text.Length) yield return (text[at..], null);
    }

    private void StartEdit()
    {
        if (_editing) return;
        if (_truncated) { ShowMessage("This file is too big to edit here", "Only the first 5 MB is shown. Use Open in its usual app to edit it."); return; }
        _editing = true; ShowPlain(readOnly: false); _editor!.Focus(FocusState.Programmatic); RaiseInfo();
    }

    public async Task<bool> Save()
    {
        if (!_editing || _editor == null) return true;
        try
        {
            var text = _editor.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            if (_text.EndsWith('\n') && !text.EndsWith('\n')) text += "\n"; // the text box hides the last line break
            if (_text.Contains("\r\n")) text = text.Replace("\n", "\r\n"); // keep Windows line endings if the file had them
            var tmp = _path + ".~omebly-save";
            await File.WriteAllTextAsync(tmp, text, _encoding);
            // Swapped in one step (never half a file). Replace, not Move, so the original's permissions, attributes and
            // Mark of the Web (Windows' "downloaded from the internet" warning) stay on the file.
            File.Replace(tmp, _path, null);
            _text = text; _dirty = false; RaiseInfo();
            return true;
        }
        catch (Exception e) { ShowSaveError(e.Message); return false; }
    }
    private async void ShowSaveError(string msg) { try { await new ContentDialog { Title = "Couldn't save", Content = msg, CloseButtonText = "OK", XamlRoot = XamlRoot, RequestedTheme = Theme.Current }.ShowAsync(); } catch { } }

    /// <summary>Unsaved changes: Save, Don't save, or stay.</summary>
    public override async Task<bool> CanCloseAsync()
    {
        if (!_dirty) return true;
        var d = new ContentDialog { Title = "Save your changes?", Content = $"\"{System.IO.Path.GetFileName(_path)}\" has changes that aren't saved.", PrimaryButtonText = "Save", SecondaryButtonText = "Don't save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot, RequestedTheme = Theme.Current };
        var r = await d.ShowAsync();
        return r == ContentDialogResult.Primary ? await Save() : r == ContentDialogResult.Secondary;
    }

    public override bool OnKey(VirtualKey key, bool ctrl, bool shift)
    {
        if (ctrl && key == VirtualKey.S) { _ = Save(); return true; }
        if (!_editing && key == VirtualKey.E) { StartEdit(); return true; }
        return false;
    }
}
