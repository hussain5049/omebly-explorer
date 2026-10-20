// What Omebly Explorer remembers, in one small file on this PC (%LOCALAPPDATA%\Omebly\Explorer\settings.json).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmeblyExplorer.Core;

public sealed class PinnedFolder
{
    public string Path { get; set; } = "";
    public string Color { get; set; } = "#2EF2B3";
}

public sealed class ColumnSetting
{
    public string Key { get; set; } = "name";
    public double Width { get; set; } = 160;
}

public sealed class FolderPrefs
{
    public string View { get; set; } = "details";
    public string Sort { get; set; } = "name";
    public bool Down { get; set; }
    public string Group { get; set; } = "none";
    public DateTime Used { get; set; }
}

public sealed class Settings
{
    public string Theme { get; set; } = "glass"; // "glass" (Omebly Glass, dark) or "light" (Windows Light)
    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; } = true; // Explorer hides them by default; Omebly shows them (safer: "photo.jpg.exe" is visible)
    public string View { get; set; } = "details"; // the view for folders without their own (see FolderView.Views)
    public bool Compact { get; set; }
    public string Pane { get; set; } = "none"; // the right-hand pane: none, preview, details
    public double PaneWidth { get; set; } = 380;
    // Folder options (like Explorer's).
    public bool SingleClick { get; set; }          // open items with one click instead of two
    public bool FoldersInNewWindow { get; set; }   // open each folder in its own window
    public string StartAt { get; set; } = "home";  // where a new window opens: home or this-pc
    public bool ShowRecent { get; set; } = true;   // recent files on Home
    public bool CheckBoxes { get; set; }
    public bool FolderSizes { get; set; }
    public bool Takeover { get; set; }             // Omebly opens folders and Win+E instead of File Explorer
    public bool JumpBar { get; set; }
    public DateTime LastUpdateCheck { get; set; }
    public bool CheckUpdates { get; set; } = true;              // the Jump-to bar on other apps' Open/Save windows            // work out and show folder sizes in Details           // item check boxes for picking several files
    /// <summary>The details view's columns, in order, with their widths.</summary>
    public List<ColumnSetting> Columns { get; set; } = new() { new() { Key = "name", Width = 320 }, new() { Key = "modified", Width = 170 }, new() { Key = "type", Width = 170 }, new() { Key = "size", Width = 100 } };
    /// <summary>Each folder's own view, sort and grouping (like Explorer remembers them), newest last.</summary>
    public Dictionary<string, FolderPrefs> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PinnedFolder> Pinned { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();

    [JsonIgnore] public static Settings Current { get; private set; } = new();
    // OMEBLY_SETTINGS_DIR: the automatic tests keep their own settings, away from yours.
    private static readonly string Dir = Environment.GetEnvironmentVariable("OMEBLY_SETTINGS_DIR") is { Length: > 0 } d ? d : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer");
    private static readonly string FilePath = System.IO.Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Load()
    {
        try { if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { Current = new(); }
        Current.Pinned = Current.Pinned.Where((p) => !string.IsNullOrWhiteSpace(p.Path)).Take(50).ToList();
        foreach (var p in Current.Pinned) if (!Launch.IsColor(p.Color)) p.Color = PinColors[0];
        if (Current.Theme is not ("glass" or "light")) Current.Theme = "glass";
        if (!OmeblyExplorer.Views.FolderView.IsView(Current.View)) Current.View = "details";
        Current.Folders ??= new(StringComparer.OrdinalIgnoreCase);
        Current.Folders = new(Current.Folders.Where((f) => f.Value != null && OmeblyExplorer.Views.FolderView.IsView(f.Value.View)).OrderByDescending((f) => f.Value.Used).Take(2000), StringComparer.OrdinalIgnoreCase);
        Current.Columns = (Current.Columns ?? new()).Where((c) => Core.Columns.All.Any((a) => a.Key == c.Key)).GroupBy((c) => c.Key).Select((g) => g.First()).ToList();
        foreach (var c in Current.Columns) c.Width = Math.Clamp(c.Width, 40, 1200);
        if (!Current.Columns.Any((c) => c.Key == "name")) Current.Columns.Insert(0, new ColumnSetting { Key = "name", Width = 320 });
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, FilePath, true); // written whole, so a crash never leaves half a file
        }
        catch { /* not saved this time */ }
    }

    public static readonly string[] PinColors = { "#2EF2B3", "#4C9BFF", "#F4C04E", "#FF6F91", "#9B7BFF", "#B9C4C0", "#FF8A3D", "#22C3E6" };
}
