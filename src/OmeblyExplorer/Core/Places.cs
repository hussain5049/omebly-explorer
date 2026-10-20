// The places in the sidebar and on Home: the usual folders, drives, cloud folders, the Recycle Bin and recent files.
using System.Runtime.InteropServices;

namespace OmeblyExplorer.Core;

public sealed record Place(string Name, string Path, string Glyph, string Color);

public sealed record DriveCard(string Name, string Root, long Free, long Total, bool Removable, bool Cloud)
{
    public double UsedFraction => Total > 0 ? 1 - (double)Free / Total : 0;
    public int UsedPercent => (int)Math.Round(UsedFraction * 100);
    public bool Low => UsedFraction >= 0.75 && !Removable;
    public string FreeText => $"{Format.Gb(Free)} free of {Format.Gb(Total)}";
    public string ShortFree => $"{Format.Gb(Free)} free";
    public string Note => Low ? "Running low, free up space" : Removable ? "Safe to remove" : "";
}

public static class Places
{
    public static IReadOnlyList<Place> Folders()
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string downloads;
        try { downloads = Native.SHGetKnownFolderPath(Native.FOLDERID_Downloads, 0, IntPtr.Zero); }
        catch { downloads = System.IO.Path.Combine(P(Environment.SpecialFolder.UserProfile), "Downloads"); }
        return new List<Place>
        {
            new("Desktop", P(Environment.SpecialFolder.DesktopDirectory), "desktop", "#4C9BFF"),
            new("Downloads", downloads, "download", "#2FCB9A"),
            new("Documents", P(Environment.SpecialFolder.MyDocuments), "doc", "#9AA7A2"),
            new("Pictures", P(Environment.SpecialFolder.MyPictures), "picture", "#FF6F91"),
            new("Videos", P(Environment.SpecialFolder.MyVideos), "video", "#9B7BFF"),
            new("Music", P(Environment.SpecialFolder.MyMusic), "music", "#F4C04E"),
        }.Where((p) => Directory.Exists(p.Path)).ToList();
    }

    public static IReadOnlyList<DriveCard> Drives()
    {
        var list = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                var letter = d.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : d.DriveType == DriveType.Network ? "Network drive" : "Local Disk") : d.VolumeLabel;
                bool cloud = label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase);
                if (d.Name.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..3], StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(d.VolumeLabel)) label = "Windows";
                list.Add(new DriveCard($"{label} ({letter})", d.RootDirectory.FullName, d.AvailableFreeSpace, d.TotalSize, d.DriveType == DriveType.Removable, cloud));
            }
            catch { /* a drive that is not ready is skipped */ }
        }
        return list;
    }

    /// <summary>OneDrive and Google Drive folders, if this PC has them.</summary>
    public static IReadOnlyList<Place> Cloud()
    {
        var list = new List<Place>();
        var od = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(od) && Directory.Exists(od)) list.Add(new Place("OneDrive", od, "cloud", "#3E8BFF"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var g in new[] { System.IO.Path.Combine(home, "Google Drive"), System.IO.Path.Combine(home, "My Drive") })
            if (Directory.Exists(g)) { list.Add(new Place("Google Drive", g, "cloud", "#2FCB7A")); break; }
        foreach (var d in Drives()) if (d.Cloud && !list.Any((p) => p.Name == "Google Drive")) list.Add(new Place("Google Drive", d.Root, "cloud", "#2FCB7A"));
        var biz = Environment.GetEnvironmentVariable("OneDriveCommercial");
        if (!string.IsNullOrEmpty(biz) && Directory.Exists(biz) && !string.Equals(biz, od, StringComparison.OrdinalIgnoreCase)) list.Add(new Place("OneDrive (work)", biz, "cloud", "#3E8BFF"));
        // Dropbox writes where it keeps your files in info.json.
        foreach (var info in new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json") })
        {
            try
            {
                if (!File.Exists(info)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
                foreach (var acct in doc.RootElement.EnumerateObject())
                    if (acct.Value.TryGetProperty("path", out var p) && p.GetString() is { } dp && Directory.Exists(dp) && !list.Any((x) => x.Path == dp)) list.Add(new Place("Dropbox", dp, "cloud", "#0061FE"));
            }
            catch { }
        }
        var icloud = System.IO.Path.Combine(home, "iCloudDrive");
        if (Directory.Exists(icloud)) list.Add(new Place("iCloud Drive", icloud, "cloud", "#3693F3"));
        return list;
    }

    public static (long items, long bytes) RecycleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    // ---------- Recent files (from Windows' own Recent list) ----------
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl); void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey); void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd); void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    /// <summary>The most recently opened files that still exist (no folders), newest first.</summary>
    public static List<FileItem> RecentFiles(int max = 12)
    {
        // OMEBLY_RECENT_DIR: a folder of shortcuts used instead, for the guide's screenshots (never someone's real list).
        var dir = Environment.GetEnvironmentVariable("OMEBLY_RECENT_DIR") is { Length: > 0 } d ? d : Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        var result = new List<FileItem>();
        if (!Directory.Exists(dir)) return result;
        var links = new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending((f) => f.LastWriteTimeUtc).Take(max * 4);
        foreach (var lnk in links)
        {
            if (result.Count >= max) break;
            object? sl = null;
            try
            {
                sl = new ShellLink();
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)sl).Load(lnk.FullName, 0);
                var sb = new System.Text.StringBuilder(1024);
                ((IShellLinkW)sl).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                var target = sb.ToString();
                if (string.IsNullOrEmpty(target) || !File.Exists(target) || result.Any((r) => r.Path.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                var fi = new FileInfo(target);
                result.Add(new FileItem { Name = fi.Name, Path = fi.FullName, Size = fi.Length, Modified = lnk.LastWriteTime, Attributes = fi.Attributes });
            }
            catch { /* a broken shortcut is skipped */ }
            finally { if (sl != null) Marshal.ReleaseComObject(sl); }
        }
        return result;
    }
}
