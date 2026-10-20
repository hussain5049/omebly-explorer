// PDF tools on right-click: merge, split into pages, rotate, add or remove a password, make smaller, and read the
// text out of any PDF (scanned pages too, with Windows' own text recognition). PDFsharp (MIT) edits the files;
// every result is a new file next to the original, which is never changed.
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmeblyExplorer.Core;

public static class PdfTools
{
    public sealed class WrongPassword : Exception { public WrongPassword() : base("That password isn't right.") { } }

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password = null)
    {
        try { return password == null ? PdfReader.Open(path, mode) : PdfReader.Open(path, password, mode); }
        catch (Exception e) when (password != null || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)) { throw new WrongPassword(); }
    }

    public static string Merge(IReadOnlyList<string> pdfs, JobProgress p, CancellationToken ct)
    {
        var outPath = Converters.OutFor(pdfs[0], ".pdf", "Combined");
        using var doc = new PdfDocument();
        p.Total = pdfs.Count;
        foreach (var f in pdfs)
        {
            ct.ThrowIfCancellationRequested();
            p.Current = System.IO.Path.GetFileName(f);
            using var src = Open(f, PdfDocumentOpenMode.Import);
            foreach (var page in src.Pages) doc.AddPage(page);
            p.Done++; p.Fraction = (double)p.Done / p.Total;
        }
        doc.Save(outPath);
        return outPath;
    }

    /// <summary>One PDF per page, in a folder named after the file.</summary>
    public static List<string> Split(string pdf, JobProgress p, CancellationToken ct)
    {
        var made = new List<string>();
        using var src = Open(pdf, PdfDocumentOpenMode.Import);
        var dir = Archives.FolderFor(pdf.Replace(".pdf", " pages.zip", StringComparison.OrdinalIgnoreCase)); // "<name> pages"
        Directory.CreateDirectory(dir);
        var stem = System.IO.Path.GetFileNameWithoutExtension(pdf);
        p.Total = src.PageCount;
        for (int i = 0; i < src.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var one = new PdfDocument();
            one.AddPage(src.Pages[i]);
            var outPath = System.IO.Path.Combine(dir, $"{stem} page {i + 1}.pdf");
            one.Save(outPath); made.Add(outPath);
            p.Done++; p.Fraction = (double)p.Done / p.Total;
        }
        return made;
    }

    public static string Rotate(string pdf, int degrees)
    {
        var outPath = Converters.OutFor(pdf, ".pdf", System.IO.Path.GetFileNameWithoutExtension(pdf) + " rotated");
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages) page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        doc.Save(outPath);
        return outPath;
    }

    /// <summary>A copy that asks for this password to open (AES encryption).</summary>
    public static string Protect(string pdf, string password)
    {
        var outPath = Converters.OutFor(pdf, ".pdf", System.IO.Path.GetFileNameWithoutExtension(pdf) + " protected");
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = password;
        doc.SecuritySettings.OwnerPassword = password;
        doc.SecurityHandler.SetEncryptionToV5(); // AES-256
        doc.Save(outPath);
        return outPath;
    }

    /// <summary>A copy without the password (you need to know it).</summary>
    public static string Unprotect(string pdf, string password)
    {
        var outPath = Converters.OutFor(pdf, ".pdf", System.IO.Path.GetFileNameWithoutExtension(pdf) + " unlocked");
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify, password);
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        doc.Save(outPath);
        return outPath;
    }

    /// <summary>A smaller copy: streams compressed and unused objects dropped (no quality is lost).</summary>
    public static string Compress(string pdf)
    {
        var outPath = Converters.OutFor(pdf, ".pdf", System.IO.Path.GetFileNameWithoutExtension(pdf) + " smaller");
        using var doc = Open(pdf, PdfDocumentOpenMode.Modify);
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Save(outPath);
        if (new FileInfo(outPath).Length >= new FileInfo(pdf).Length) { File.Delete(outPath); throw new InvalidOperationException("This PDF is already as small as it can get without losing quality."); }
        return outPath;
    }

    /// <summary>The text of every page, read the way a person would read it (works on scanned pages too).</summary>
    public static async Task<string> ToText(string pdf, JobProgress p, CancellationToken ct)
    {
        var ocr = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("Windows has no text-recognition language installed (Settings, Time & language, Language).");
        var file = await StorageFile.GetFileFromPathAsync(pdf);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            using var ms = new InMemoryRandomAccessStream();
            var width = (uint)Math.Min(Windows.Media.Ocr.OcrEngine.MaxImageDimension, page.Size.Width * 2.5);
            await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width });
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms);
            using var bmp = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var result = await ocr.RecognizeAsync(bmp);
            if (doc.PageCount > 1) sb.AppendLine($"--- Page {i + 1} ---");
            foreach (var line in result.Lines) sb.AppendLine(line.Text);
            sb.AppendLine();
            p.Fraction = (p.Done + (i + 1.0) / doc.PageCount) / Math.Max(1, p.Total);
        }
        var outPath = Converters.OutFor(pdf, ".txt");
        await File.WriteAllTextAsync(outPath, sb.ToString(), ct);
        return outPath;
    }
}
