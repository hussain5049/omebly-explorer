// One file or folder as shown in a list. Kept small: a folder with 500,000 files holds 500,000 of these.
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace OmeblyExplorer.Core;

public sealed class FileItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public DateTime Created { get; init; }
    public DateTime Accessed { get; init; }
    /// <summary>Windows properties for the extra columns (column key to text), read when the row is on screen.</summary>
    public Dictionary<string, string>? Props { get; set; }
    internal string? PropsKey; // which columns Props was read for
    /// <summary>The folder's colour (its own, or Windows yellow).</summary>
    public string FolderFill => Library.Current.ColorOf(Path) ?? "#F4C04E";
    public IReadOnlyList<string> TagColors => Library.Current.TagsOf(Path);
    /// <summary>A folder's total size, once worked out ("Show folder sizes").</summary>
    public long? FolderSize { get; set; }

    public string Extension => IsFolder ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public string SizeText => IsFolder ? "" : Format.Size(Size);
    public string ModifiedText => Modified == default ? "" : Format.When(Modified);
    public string TypeText => IsFolder ? "File folder" : FileKinds.Describe(Extension);
    public FileKind Kind => IsFolder ? FileKind.Folder : FileKinds.Of(Extension);

    private ImageSource? _icon;
    /// <summary>The thumbnail or icon, filled in the background when the item scrolls into view.</summary>
    public ImageSource? Icon { get => _icon; set { if (_icon != value) { _icon = value; PropertyChanged?.Invoke(this, IconArgs); } } }
    internal int IconSizeLoaded;
    private static readonly PropertyChangedEventArgs IconArgs = new(nameof(Icon));
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
    /// <summary>Windows' "File name extensions" setting: off shows "photo" for "photo.jpg".</summary>
    public static bool ShowExtensions = true;
    public string DisplayName => ShowExtensions || IsFolder || System.IO.Path.GetFileNameWithoutExtension(Name).Length == 0 ? Name : System.IO.Path.GetFileNameWithoutExtension(Name); // what screen readers (and the automatic tests) read
}

public static class Format
{
    public static string Size(long b) => b == 1 ? "1 byte" : b < 1024 ? $"{b} bytes"
        : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB"
        : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB"
        : b < 1024L * 1024 * 1024 * 1024 ? $"{b / 1073741824.0:0.#} GB"
        : $"{b / 1099511627776.0:0.##} TB";
    public static string Gb(long b) => b >= 1024L * 1024 * 1024 * 1024 ? $"{b / 1099511627776.0:0.#} TB" : $"{b / 1073741824.0:0.#} GB";
    public static string When(DateTime t)
    {
        var now = DateTime.Now; var d = now - t;
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (t.Date == now.Date) return $"Today {t:HH:mm}";
        if (t.Date == now.Date.AddDays(-1)) return $"Yesterday {t:HH:mm}";
        return t.Year == now.Year ? t.ToString("d MMM, HH:mm") : t.ToString("d MMM yyyy");
    }
    public static string Ago(DateTime t)
    {
        var now = DateTime.Now; var d = now - t;
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24 && t.Date == now.Date) return $"{(int)d.TotalHours} h ago";
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
        return t.ToString("d MMM");
    }
}

public enum FileKind { Folder, Picture, Video, Music, Pdf, Document, Sheet, Slides, Archive, Code, Text, App, Other }

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> Map = new()
    {
        [".jpg"] = FileKind.Picture, [".jpeg"] = FileKind.Picture, [".png"] = FileKind.Picture, [".gif"] = FileKind.Picture, [".webp"] = FileKind.Picture,
        [".bmp"] = FileKind.Picture, [".heic"] = FileKind.Picture, [".avif"] = FileKind.Picture, [".tif"] = FileKind.Picture, [".tiff"] = FileKind.Picture,
        [".svg"] = FileKind.Picture, [".ico"] = FileKind.Picture, [".psd"] = FileKind.Picture, [".raw"] = FileKind.Picture, [".cr2"] = FileKind.Picture, [".nef"] = FileKind.Picture, [".arw"] = FileKind.Picture, [".dng"] = FileKind.Picture,
        [".mp4"] = FileKind.Video, [".mkv"] = FileKind.Video, [".mov"] = FileKind.Video, [".avi"] = FileKind.Video, [".webm"] = FileKind.Video, [".wmv"] = FileKind.Video, [".m4v"] = FileKind.Video, [".flv"] = FileKind.Video,
        [".mp3"] = FileKind.Music, [".wav"] = FileKind.Music, [".flac"] = FileKind.Music, [".m4a"] = FileKind.Music, [".aac"] = FileKind.Music, [".ogg"] = FileKind.Music, [".opus"] = FileKind.Music, [".wma"] = FileKind.Music,
        [".pdf"] = FileKind.Pdf,
        [".doc"] = FileKind.Document, [".docx"] = FileKind.Document, [".rtf"] = FileKind.Document, [".odt"] = FileKind.Document,
        [".xls"] = FileKind.Sheet, [".xlsx"] = FileKind.Sheet, [".csv"] = FileKind.Sheet, [".ods"] = FileKind.Sheet,
        [".ppt"] = FileKind.Slides, [".pptx"] = FileKind.Slides, [".odp"] = FileKind.Slides,
        [".zip"] = FileKind.Archive, [".7z"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".tar"] = FileKind.Archive, [".gz"] = FileKind.Archive, [".tgz"] = FileKind.Archive,
        [".cs"] = FileKind.Code, [".js"] = FileKind.Code, [".ts"] = FileKind.Code, [".py"] = FileKind.Code, [".html"] = FileKind.Code, [".css"] = FileKind.Code, [".json"] = FileKind.Code, [".xml"] = FileKind.Code, [".dart"] = FileKind.Code, [".java"] = FileKind.Code, [".cpp"] = FileKind.Code, [".c"] = FileKind.Code, [".h"] = FileKind.Code, [".rs"] = FileKind.Code, [".go"] = FileKind.Code, [".php"] = FileKind.Code, [".sql"] = FileKind.Code, [".yaml"] = FileKind.Code, [".yml"] = FileKind.Code,
        [".txt"] = FileKind.Text, [".md"] = FileKind.Text, [".log"] = FileKind.Text, [".ini"] = FileKind.Text,
        [".exe"] = FileKind.App, [".msi"] = FileKind.App, [".bat"] = FileKind.App, [".cmd"] = FileKind.App, [".lnk"] = FileKind.App,
    };
    public static FileKind Of(string ext) => Map.TryGetValue(ext, out var k) ? k : FileKind.Other;
    public static string Describe(string ext) => Of(ext) switch
    {
        FileKind.Picture => $"{Up(ext)} picture",
        FileKind.Video => $"{Up(ext)} video",
        FileKind.Music => $"{Up(ext)} audio",
        FileKind.Pdf => "PDF document",
        FileKind.Document => "Word document",
        FileKind.Sheet => ext == ".csv" ? "CSV spreadsheet" : "Excel spreadsheet",
        FileKind.Slides => "PowerPoint presentation",
        FileKind.Archive => $"{Up(ext)} archive",
        FileKind.Code => $"{Up(ext)} file",
        FileKind.Text => ext == ".md" ? "Markdown" : "Text document",
        FileKind.App => ext == ".lnk" ? "Shortcut" : "Application",
        _ => ext.Length > 1 ? $"{Up(ext)} file" : "File",
    };
    private static string Up(string ext) => ext.TrimStart('.').ToUpperInvariant();
    /// <summary>The design's colour for each kind (small badges, empty thumbnails).</summary>
    public static string Color(FileKind k) => k switch
    {
        FileKind.Folder => "#F4C04E", FileKind.Picture => "#4C9BFF", FileKind.Video => "#9B7BFF", FileKind.Music => "#FF6FA8",
        FileKind.Pdf => "#FF5A5F", FileKind.Document => "#3E8BFF", FileKind.Sheet => "#2FCB7A", FileKind.Slides => "#FF8A3D",
        FileKind.Archive => "#F4C04E", FileKind.Code => "#2EF2B3", _ => "#9FB6AD",
    };
}
