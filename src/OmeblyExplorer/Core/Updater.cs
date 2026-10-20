// Updates. When it opens (at most every few hours, and from About), Omebly Explorer asks omebly.com for
// update-<x64|arm64|x86>.json; that is its only network request, and it sends nothing about you. The file is signed with Omebly's update key (ECDSA P-256); a file
// that isn't signed by that key, or an installer whose SHA-256 doesn't match, is refused, so a fake update can't
// get in. The installer only runs when you click Update now; it then installs quietly and opens Omebly Explorer again.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OmeblyExplorer.Core;

public sealed record UpdateInfo(string Version, string Url, string Sha256);

public static class Updater
{
    private static string ManifestUrl => $"https://omebly.com/tools/explorer/update-{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.json";
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5K0fYZpQWERytuUAZx0oFtazAiD5lTCOZ4IamFtQ+CS8mkGg3Csnv5tPxY4YXwzyYOZZWM3Pwa9MgDbh1MoqNA==";
    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 1);

    /// <summary>A newer signed version, or null (no update, no internet, or a file that failed the check).</summary>
    public static async Task<UpdateInfo?> Check()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OmeblyExplorer"); // no version, no id: nothing about you
            var json = await http.GetStringAsync(ManifestUrl);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string v = r.GetProperty("version").GetString()!, url = r.GetProperty("url").GetString()!, sha = r.GetProperty("sha256").GetString()!;
            // The signature this version checks names the app and the processor type too, so a file signed for
            // another Omebly app or for ARM can never be used here (Security 340). ("signature" is for 1.0.0 and 1.0.1.)
            if (!r.TryGetProperty("signature2", out var s2) || !Verify(SignedText(v, url, sha), s2.GetString() ?? "")) return null;
            if (!url.StartsWith("https://omebly.com/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://github.com/hussain5049/omebly-explorer/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Version.TryParse(v, out var ver) || ver <= Current) return null;
            return new UpdateInfo(v, url, sha);
        }
        catch { return null; }
    }
    public static string Arch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string SignedText(string version, string url, string sha256) => $"omebly-explorer|{Arch}|{version}|{url}|{sha256}";
    public static bool Verify(string message, string signatureB64)
    {
        using var k = ECDsa.Create();
        k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        try { return k.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signatureB64), HashAlgorithmName.SHA256); }
        catch { return false; }
    }

    /// <summary>Downloads the installer to a private temporary folder and checks it before anything runs.</summary>
    public static async Task<string> Download(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Omebly Explorer", "Update"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"OmeblyExplorer-{u.Version}-setup.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using (var s = await resp.Content.ReadAsStreamAsync(ct))
        await using (var f = File.Create(file))
        {
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0) { await f.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct)));
        if (!sha.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(file); throw new InvalidOperationException("The download didn't match the signed update, so it was thrown away."); }
        return file;
    }

    /// <summary>Runs the checked installer quietly; it closes this copy, installs, and opens the new version.</summary>
    public static void Install(string installer) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = false });

    /// <summary>When the app opens (not more often than every 6 hours): if a signed newer version exists, the callback offers it.</summary>
    public static async Task CheckOnStart(Action<UpdateInfo> offer)
    {
        if (DateTime.Now - Settings.Current.LastUpdateCheck < TimeSpan.FromHours(6)) return;
        Settings.Current.LastUpdateCheck = DateTime.Now; Settings.Save();
        if (await Check() is { } u) offer(u);
    }
}
