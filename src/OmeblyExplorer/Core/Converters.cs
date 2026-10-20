// "Convert to..." on right-click. Everything runs on this PC with no internet:
//  - pictures: Windows' own image codecs (JPG, PNG, BMP, GIF, TIFF, HEIC when Windows has it, ICO), with resize
//  - pictures to PDF and PDF tools: PDFsharp (MIT); PDF pages to pictures: Windows' PDF renderer
//  - PDF, Word, Excel and PowerPoint conversions: the Omebly Tools inside the app (ToolWindow.cs), no Office needed
//  - video and audio: Windows' own encoders (Media Foundation): MP4 (H.264/AAC), MP3, M4A, WMV, WMA
using System.Runtime.InteropServices.WindowsRuntime;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmeblyExplorer.Core;

public sealed record ConvertOption(string Key, string Label, string Ext);

public sealed class JobProgress
{
    public double Fraction; public string Current = ""; public int Done, Total;
}

public static class Converters
{
    // ---------- What each kind of file can become ----------
    public static readonly string[] PictureIn = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp", ".ico", ".jxr" };
    public static readonly string[] OfficeIn = { ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx", ".odp" };
    public static readonly string[] MediaIn = { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".3gp", ".mp3", ".m4a", ".wav", ".wma", ".flac", ".aac", ".ogg", ".opus" };

    public static List<ConvertOption> OptionsFor(IReadOnlyList<FileItem> files)
    {
        var o = new List<ConvertOption>();
        if (files.Count == 0 || files.Any((f) => f.IsFolder)) return o;
        bool All(string[] kinds) => files.All((f) => kinds.Contains(f.Extension));
        if (All(PictureIn))
        {
            foreach (var (k, l, e) in new[] { ("jpg", "JPG picture", ".jpg"), ("png", "PNG picture", ".png"), ("bmp", "BMP picture", ".bmp"), ("gif", "GIF picture", ".gif"), ("tiff", "TIFF picture", ".tiff"), ("ico", "Icon (ICO)", ".ico") }) o.Add(new(k, l, e));
            if (HeifEncoder()) o.Add(new("heic", "HEIC picture", ".heic"));
            o.Add(new("small", "Smaller JPG (for sharing)", ".jpg"));
            o.Add(new("pdf-images", files.Count > 1 ? "One PDF with all of them" : "PDF", ".pdf"));
        }
        // PDF, Word, Excel and PowerPoint conversions are in "PDF tools" (the Omebly Tools, no Office needed).
        if (All(MediaIn))
        {
            bool video = files.Any((f) => f.Kind == FileKind.Video);
            if (video)
            {
                o.Add(new("mp4", "MP4 video (H.264)", ".mp4"));
                o.Add(new("mp4-whatsapp", "Small MP4 for WhatsApp (720p)", ".mp4"));
                o.Add(new("wmv", "WMV video", ".wmv"));
            }
            o.Add(new("mp3", "MP3 audio", ".mp3"));
            o.Add(new("m4a", "M4A audio (AAC)", ".m4a"));
            o.Add(new("wma", "WMA audio", ".wma"));
        }
        return o;
    }

    private static bool? _heif;
    private static bool HeifEncoder() => _heif ??= BitmapEncoder.GetEncoderInformationEnumerator().Any((e) => e.CodecId == BitmapEncoder.HeifEncoderId);

    /// <summary>Runs one conversion for each file (or one for all, for "one PDF"). Returns the new files.</summary>
    public static async Task<List<string>> Run(ConvertOption opt, IReadOnlyList<FileItem> files, JobProgress p, CancellationToken ct)
    {
        var made = new List<string>();
        p.Total = files.Count;
        if (opt.Key == "pdf-images")
        {
            var outPath = OutFor(files[0].Path, ".pdf", files.Count > 1 ? "Pictures" : null);
            var jpegs = new List<byte[]>();
            foreach (var f in files) { ct.ThrowIfCancellationRequested(); p.Current = f.Name; jpegs.Add(await AsJpeg(f.Path)); }
            await Task.Run(() => ImagesToPdf(jpegs, outPath, p, ct), ct);
            made.Add(outPath); return made;
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            p.Current = f.Name;
            switch (opt.Key)
            {
                case "jpg" or "png" or "bmp" or "gif" or "tiff" or "heic" or "small": made.Add(await Picture(f.Path, opt.Key, ct)); break;
                case "ico": made.Add(await Icon(f.Path, ct)); break;
                case "mp4" or "mp4-whatsapp" or "wmv" or "mp3" or "m4a" or "wma": made.Add(await Media(f.Path, opt.Key, p, ct)); break;
            }
            p.Done++; p.Fraction = (double)p.Done / p.Total;
        }
        return made;
    }

    /// <summary>"photo.png" next to "photo.jpg", or "photo (2).png" if taken.</summary>
    public static string OutFor(string source, string ext, string? name = null)
    {
        var dir = System.IO.Path.GetDirectoryName(source)!;
        var stem = name ?? System.IO.Path.GetFileNameWithoutExtension(source);
        return System.IO.Path.Combine(dir, FileOps.FreeName(dir, stem, ext));
    }

    // ---------- Pictures ----------
    private static async Task<string> Picture(string src, string kind, CancellationToken ct)
    {
        var (ext, id) = kind switch
        {
            "png" => (".png", BitmapEncoder.PngEncoderId), "bmp" => (".bmp", BitmapEncoder.BmpEncoderId), "gif" => (".gif", BitmapEncoder.GifEncoderId),
            "tiff" => (".tiff", BitmapEncoder.TiffEncoderId), "heic" => (".heic", BitmapEncoder.HeifEncoderId), _ => (".jpg", BitmapEncoder.JpegEncoderId),
        };
        var outPath = OutFor(src, ext, kind == "small" ? System.IO.Path.GetFileNameWithoutExtension(src) + " small" : null);
        using var input = await FileRandomAccessStream.OpenAsync(src, FileAccessMode.Read);
        var dec = await BitmapDecoder.CreateAsync(input);
        // Turned the right way up (EXIF orientation applied), every pixel as it looks.
        var transform = new BitmapTransform();
        uint w = dec.OrientedPixelWidth, h = dec.OrientedPixelHeight;
        if (kind == "small") { var s = Math.Min(1.0, 1920.0 / Math.Max(w, h)); w = (uint)Math.Max(1, w * s); h = (uint)Math.Max(1, h * s); transform.ScaledWidth = w; transform.ScaledHeight = h; transform.InterpolationMode = BitmapInterpolationMode.Fant; }
        var px = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, ext is ".jpg" or ".bmp" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        ct.ThrowIfCancellationRequested();
        var tmp = outPath + ".~omebly-convert";
        try
        {
            using (var output = await FileRandomAccessStream.OpenAsync(tmp, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateNew))
            {
                var props = ext == ".jpg" ? new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(kind == "small" ? 0.8f : 0.92f, Windows.Foundation.PropertyType.Single) } : null;
                var enc = props != null ? await BitmapEncoder.CreateAsync(id, output, props) : await BitmapEncoder.CreateAsync(id, output);
                enc.SetPixelData(BitmapPixelFormat.Bgra8, ext is ".jpg" or ".bmp" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied, w, h, dec.DpiX, dec.DpiY, px.DetachPixelData());
                await enc.FlushAsync();
            }
            File.Move(tmp, outPath);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
        return outPath;
    }

    /// <summary>An .ico with 16, 32, 48 and 256 pixel sizes (PNG inside, as Windows Vista and later read them).</summary>
    private static async Task<string> Icon(string src, CancellationToken ct)
    {
        var outPath = OutFor(src, ".ico");
        using var input = await FileRandomAccessStream.OpenAsync(src, FileAccessMode.Read);
        var dec = await BitmapDecoder.CreateAsync(input);
        var sizes = new[] { 16, 32, 48, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes)
        {
            ct.ThrowIfCancellationRequested();
            var t = new BitmapTransform { ScaledWidth = (uint)s, ScaledHeight = (uint)s, InterpolationMode = BitmapInterpolationMode.Fant };
            var px = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            using var ms = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)s, (uint)s, 96, 96, px.DetachPixelData());
            await enc.FlushAsync();
            var bytes = new byte[ms.Size]; await ms.AsStreamForRead().ReadExactlyAsync(bytes, ct); pngs.Add(bytes);
        }
        using var f = new FileStream(outPath, FileMode.CreateNew);
        using var bw = new BinaryWriter(f);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32); bw.Write(pngs[i].Length); bw.Write(offset); offset += pngs[i].Length;
        }
        foreach (var b in pngs) bw.Write(b);
        return outPath;
    }

    /// <summary>The picture as a JPEG (turned the right way up), whatever kind it was.</summary>
    private static async Task<byte[]> AsJpeg(string src)
    {
        using var input = await FileRandomAccessStream.OpenAsync(src, FileAccessMode.Read);
        var dec = await BitmapDecoder.CreateAsync(input);
        var px = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms, new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) });
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, dec.OrientedPixelWidth, dec.OrientedPixelHeight, dec.DpiX, dec.DpiY, px.DetachPixelData());
        await enc.FlushAsync();
        var bytes = new byte[ms.Size]; await ms.AsStreamForRead().ReadExactlyAsync(bytes);
        return bytes;
    }
    private static void ImagesToPdf(List<byte[]> jpegs, string outPath, JobProgress p, CancellationToken ct)
    {
        using var doc = new PdfDocument();
        foreach (var bytes in jpegs)
        {
            ct.ThrowIfCancellationRequested();
            using var img = XImage.FromStream(new MemoryStream(bytes));
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(img.PointWidth); page.Height = XUnit.FromPoint(img.PointHeight);
            using (var g = XGraphics.FromPdfPage(page)) g.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
            p.Done++; p.Fraction = (double)p.Done / jpegs.Count;
        }
        doc.Save(outPath);
    }

    private static async Task<List<string>> PdfToPictures(string pdf, bool png, JobProgress p, CancellationToken ct)
    {
        var made = new List<string>();
        var file = await StorageFile.GetFileFromPathAsync(pdf);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var dir = System.IO.Path.GetDirectoryName(pdf)!; var stem = System.IO.Path.GetFileNameWithoutExtension(pdf);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            var outPath = System.IO.Path.Combine(dir, FileOps.FreeName(dir, $"{stem} page {i + 1}", png ? ".png" : ".jpg"));
            using (var output = await FileRandomAccessStream.OpenAsync(outPath, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateNew))
            {
                // 2x the page size: sharp enough to print or read.
                using var ms = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)(page.Size.Width * 2), BitmapEncoderId = png ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId });
                ms.Seek(0);
                await RandomAccessStream.CopyAsync(ms, output);
            }
            made.Add(outPath);
            p.Fraction = (p.Done + (i + 1.0) / doc.PageCount) / p.Total;
        }
        return made;
    }

    // ---------- Video and audio ----------
    private static async Task<string> Media(string src, string kind, JobProgress p, CancellationToken ct)
    {
        var (ext, profile) = kind switch
        {
            "mp4" => (".mp4", MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto)),
            "mp4-whatsapp" => (".mp4", MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p)),
            "wmv" => (".wmv", MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto)),
            "m4a" => (".m4a", MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High)),
            "wma" => (".wma", MediaEncodingProfile.CreateWma(AudioEncodingQuality.High)),
            _ => (".mp3", MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High)),
        };
        if (kind == "mp4-whatsapp" && profile.Video != null) { profile.Video.Bitrate = 1_500_000; profile.Audio.Bitrate = 128_000; }
        var outPath = OutFor(src, ext, kind == "mp4-whatsapp" ? System.IO.Path.GetFileNameWithoutExtension(src) + " small" : null);
        var input = await StorageFile.GetFileFromPathAsync(src);
        File.WriteAllBytes(outPath, Array.Empty<byte>());
        var output = await StorageFile.GetFileFromPathAsync(outPath);
        try
        {
            var tc = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prep = await tc.PrepareFileTranscodeAsync(input, output, profile);
            if (!prep.CanTranscode) throw new InvalidOperationException(prep.FailureReason == TranscodeFailureReason.CodecNotFound ? "Windows can't read this kind of video or audio (its codec isn't on this PC)." : "Windows can't convert this file.");
            var op = prep.TranscodeAsync();
            op.Progress = (_, pct) => p.Fraction = (p.Done + pct / 100.0) / p.Total;
            using (ct.Register(() => op.Cancel())) await op;
        }
        catch { try { File.Delete(outPath); } catch { } throw; }
        return outPath;
    }
}
