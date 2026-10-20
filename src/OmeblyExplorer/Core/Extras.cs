// Explorer's everyday extras: share with Windows' share window, set a picture as the desktop background, rotate
// pictures, and the drive tools (eject, format, optimise, disk cleanup, BitLocker). Windows' own tools are started
// by their full path and do the work themselves; Omebly never changes a disk on its own.
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmeblyExplorer.Core;

public static class Extras
{
    // ---------- Share ----------
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr hwnd, ref Guid riid);
        void ShowShareUIForWindow(IntPtr hwnd);
    }
    private static readonly Guid DtmIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    /// <summary>Windows' share window (Nearby sharing, Mail, WhatsApp, Teams...) for these files.</summary>
    public static void Share(IntPtr hwnd, IReadOnlyList<FileItem> items)
    {
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DtmIid;
        var dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
        void OnRequest(DataTransferManager s, DataRequestedEventArgs e)
        {
            s.DataRequested -= OnRequest;
            var d = e.Request.GetDeferral();
            e.Request.Data.Properties.Title = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            _ = Fill();
            async Task Fill()
            {
                try { e.Request.Data.SetStorageItems(await Clip.ToStorageItems(items.Select((i) => (i.Path, i.IsFolder))), readOnly: true); }
                finally { d.Complete(); }
            }
        }
        dtm.DataRequested += OnRequest;
        interop.ShowShareUIForWindow(hwnd);
    }

    // ---------- Pictures ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SystemParametersInfoW(uint action, uint param, string value, uint flags);
    public static bool SetWallpaper(string path) => SystemParametersInfoW(0x0014 /* SETDESKWALLPAPER */, 0, path, 0x01 | 0x02 /* save and tell everyone */);
    public static bool IsPicture(FileItem f) => f.Extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".heic" or ".webp" or ".jfif";
    public static bool CanRotate(FileItem f) => f.Extension is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tif" or ".tiff";

    /// <summary>Turns a picture 90 degrees. JPEG and TIFF only change their orientation tag (no quality is lost, like
    /// Explorer); PNG and BMP are redrawn turned, which is also lossless. The file is swapped in whole.</summary>
    public static async Task<bool> Rotate(string path, bool clockwise)
    {
        var tmp = path + ".~omebly-rotate";
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using (var input = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read))
            using (var output = await FileRandomAccessStream.OpenAsync(tmp, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateNew))
            {
                var dec = await BitmapDecoder.CreateAsync(input);
                var enc = await BitmapEncoder.CreateForTranscodingAsync(output, dec);
                if (ext is ".jpg" or ".jpeg" or ".jfif" or ".tif" or ".tiff")
                {
                    ushort now = 1;
                    try { var p = await dec.BitmapProperties.GetPropertiesAsync(new[] { "System.Photo.Orientation" }); if (p.TryGetValue("System.Photo.Orientation", out var v) && v.Value is ushort u) now = u; } catch { }
                    var next = Turn(now, clockwise);
                    await enc.BitmapProperties.SetPropertiesAsync(new[] { new KeyValuePair<string, BitmapTypedValue>("System.Photo.Orientation", new BitmapTypedValue(next, Windows.Foundation.PropertyType.UInt16)) });
                }
                else enc.BitmapTransform.Rotation = clockwise ? BitmapRotation.Clockwise90Degrees : BitmapRotation.Clockwise270Degrees;
                await enc.FlushAsync();
            }
            File.Replace(tmp, path, null); // keeps the file's permissions and Mark of the Web
            return true;
        }
        catch { try { File.Delete(tmp); } catch { } return false; }
    }
    /// <summary>EXIF orientation after a quarter turn (1 = upright, 6 = 90 clockwise, 3 = 180, 8 = 270; 2/4/5/7 mirrored).</summary>
    public static ushort Turn(ushort o, bool clockwise)
    {
        ushort[] ring = { 1, 6, 3, 8 }, mirrored = { 2, 7, 4, 5 };
        var r = Array.IndexOf(ring, o); var m = Array.IndexOf(mirrored, o);
        int step = clockwise ? 1 : 3;
        if (r >= 0) return ring[(r + step) % 4];
        if (m >= 0) return mirrored[(m + step) % 4];
        return clockwise ? (ushort)6 : (ushort)8;
    }

    // ---------- Drives ----------
    [DllImport("shell32.dll")] private static extern uint SHFormatDrive(IntPtr hwnd, uint drive, uint fmtID, uint options);
    /// <summary>Windows' own Format window for this drive (it asks and warns; Omebly formats nothing itself).</summary>
    public static void Format(IntPtr hwnd, string root) { if (root.Length >= 2 && char.IsLetter(root[0])) _ = Task.Run(() => SHFormatDrive(hwnd, (uint)(char.ToUpperInvariant(root[0]) - 'A'), 0xFFFF, 0)); }
    public static void Eject(string root) => Native.ShellRun(root, "eject");
    private static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);
    public static void Optimize() => Start(Sys("dfrgui.exe"), "");
    public static void Cleanup(string root) => Start(Sys("cleanmgr.exe"), $"/d {root[0]}");
    public static void BitLocker() => Start(Sys("control.exe"), "/name Microsoft.BitLockerDriveEncryption");
    private static void Start(string exe, string args) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { } }
    public static bool CanMount(FileItem f) => f.Extension is ".iso" or ".vhd" or ".vhdx" or ".img";
}
