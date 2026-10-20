// Which viewer opens which file. Everything opens inside Omebly Explorer; files nothing here can show get the
// details card with "Open with".
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views.Viewers;

public enum ViewKind { Picture, Video, Music, Pdf, Office, Text, Code, Markdown, Csv, Html, Font, Zip, Other }

public static class ViewerKinds
{
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif", ".avif", ".jxr", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".psd", ".svg" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp", ".mts", ".m2ts", ".ts", ".flv", ".mpg", ".mpeg" };
    private static readonly HashSet<string> Music = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".amr" };
    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".msg", ".eml", ".vsdx" };
    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".dart", ".c", ".h", ".cpp", ".hpp", ".rs", ".go", ".php", ".rb", ".swift", ".sql", ".json", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".css", ".scss", ".sh", ".ps1", ".bat", ".cmd", ".ini", ".cfg", ".csproj", ".sln", ".gradle", ".lua", ".r", ".vue", ".svelte" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".log", ".text", ".nfo", ".srt", ".vtt", ".env", ".gitignore", ".editorconfig", ".license" };

    /// <summary>The viewer for this kind of file. In the side preview pane media doesn't start by itself.</summary>
    public static ViewerBase Create(ViewKind kind, IReadOnlyList<FileItem> list, bool pane = false) => kind switch
    {
        ViewKind.Picture => new PictureViewer(list),
        ViewKind.Video => new MediaViewer(video: true, autoPlay: !pane),
        ViewKind.Music => new MediaViewer(video: false, autoPlay: !pane),
        ViewKind.Pdf => new PdfViewer(),
        ViewKind.Text or ViewKind.Code => new TextViewer(code: kind == ViewKind.Code),
        ViewKind.Markdown => new MarkdownViewer(),
        ViewKind.Csv => new CsvViewer(),
        ViewKind.Html => new HtmlViewer(),
        ViewKind.Font => new FontViewer(),
        ViewKind.Zip => new ZipViewer(),
        ViewKind.Office => new PreviewHandlerViewer(),
        _ => new OtherViewer(),
    };

    public static ViewKind Of(FileItem item)
    {
        var e = item.Extension;
        if (Pictures.Contains(e)) return ViewKind.Picture;
        if (Videos.Contains(e)) return ViewKind.Video;
        if (Music.Contains(e)) return ViewKind.Music;
        if (e == ".pdf") return ViewKind.Pdf;
        if (Office.Contains(e)) return ViewKind.Office;
        if (e is ".md" or ".markdown") return ViewKind.Markdown;
        if (e is ".csv" or ".tsv") return ViewKind.Csv;
        if (e is ".html" or ".htm") return ViewKind.Html;
        if (e is ".ttf" or ".otf") return ViewKind.Font;
        if (e == ".zip") return ViewKind.Zip;
        if (Code.Contains(e)) return ViewKind.Code;
        if (Text.Contains(e) || item.Name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || item.Name.Equals("README", StringComparison.OrdinalIgnoreCase)) return ViewKind.Text;
        // No extension, or an unknown one: text if it looks like text.
        return LooksLikeText(item) ? ViewKind.Text : ViewKind.Other;
    }

    /// <summary>The first 4 KB has no NUL bytes and is mostly printable: treat it as text.</summary>
    public static bool LooksLikeText(FileItem item)
    {
        if (item.Size == 0 || item.Size > 50L << 20) return false;
        try
        {
            using var f = File.OpenRead(item.Path);
            var buf = new byte[Math.Min(4096, (int)Math.Min(item.Size, 4096))];
            int n = f.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true; // UTF-16
            int odd = 0;
            for (int i = 0; i < n; i++) { var b = buf[i]; if (b == 0) return false; if (b < 9 || (b > 13 && b < 32)) odd++; }
            return odd < n / 20;
        }
        catch { return false; }
    }
}
