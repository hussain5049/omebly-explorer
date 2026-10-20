// The Omebly Tools PDF tools inside Omebly Explorer: the same tools as omebly.com/tools, copied into the app
// (tools/bundle-web-tools.py) and run in a glass window with no internet. The file you right-clicked is already
// loaded; whatever the tool makes is saved next to the original (never over it), and Undo takes it back.
//
// Safety: the tool page is served by Omebly itself from a made-up https address (tools.omebly.invalid); every
// other address (the internet, file: links, other hosts) is refused; it can't open other windows or leave the
// page; and it sees only the files you gave it.
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OmeblyExplorer.Core;
using OmeblyExplorer.Views;

namespace OmeblyExplorer;

public sealed record WebTool(string Slug, string Name, string Title, string[] Accept, bool Multiple, string Group);

public static class WebTools
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "WebTools");
    private static List<WebTool>? _all;
    public static IReadOnlyList<WebTool> All
    {
        get
        {
            if (_all != null) return _all;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tools.json")));
                _all = doc.RootElement.EnumerateArray().Select((t) => new WebTool(t.GetProperty("slug").GetString()!, t.GetProperty("name").GetString()!, t.GetProperty("title").GetString()!,
                    t.GetProperty("accept").EnumerateArray().Select((a) => a.GetString()!).ToArray(), t.GetProperty("multiple").GetBoolean(), t.GetProperty("group").GetString()!)).ToList();
            }
            catch { _all = new(); }
            return _all;
        }
    }
    /// <summary>The tools that take this kind of file (by extension, or by picture/PDF kind).</summary>
    public static IEnumerable<WebTool> For(IReadOnlyList<FileItem> files) => files.Count == 0 || files.Any((f) => f.IsFolder) ? Enumerable.Empty<WebTool>() :
        All.Where((t) => (files.Count == 1 || t.Multiple) && files.All((f) => Takes(t, f)));
    private static bool Takes(WebTool t, FileItem f)
    {
        var ext = f.Extension;
        if (t.Accept.Contains(ext)) return true;
        if (ext == ".pdf" && t.Accept.Contains("application/pdf")) return true;
        if (f.Kind == FileKind.Picture && t.Accept.Any((a) => a == "image/" + ext.TrimStart('.').Replace("jpg", "jpeg") || a == "image/*")) return true;
        return false;
    }
}

public sealed class ToolWindow : Window
{
    private const string Host = "tools.omebly.invalid";
    private readonly WebView2 _web = new();
    private readonly IReadOnlyList<FileItem> _files;
    private readonly string _saveTo;
    private readonly WebTool _tool;
    public event Action<string>? Saved; // a new file the tool made

    public ToolWindow(WebTool tool, IReadOnlyList<FileItem> files)
    {
        _tool = tool; _files = files;
        _saveTo = Path.GetDirectoryName(files[0].Path)!;
        Title = $"{tool.Name} · Omebly Explorer";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "OmeblyExplorer.ico"));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(820 * scale)));
        var root = new Grid { RequestedTheme = Theme.Current, Background = Theme.Get("OxWin") };
        root.Children.Add(_web);
        Content = root;
        _ = Start();
    }

    private async Task Start()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Omebly", "Explorer", "ToolsWeb");
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, data, new CoreWebView2EnvironmentOptions());
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        // Only the tools' own pages (Sign PDF and Fill forms hand the file to the PDF editor page); nothing else.
        core.NavigationStarting += (_, e) =>
        {
            bool ok = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
                && u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [ "tools", var slug, .. ] && WebTools.All.Any((t) => t.Slug == slug);
            if (!ok) e.Cancel = true;
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => { var r = Serve(core.Environment, e.Request.Uri); if (r != null) e.Response = r; };
        core.DownloadStarting += OnDownload;
        if (Environment.GetEnvironmentVariable("OMEBLY_TOOLTEST") == "1")
        {
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) => { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "omebly-webtools-refused.txt"), $"{_tool.Slug} JS {e.ParameterObjectAsJson[..Math.Min(600, e.ParameterObjectAsJson.Length)]}\n"); } catch { } };
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) => { if (e.ParameterObjectAsJson.Contains("\"error\"")) try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "omebly-webtools-refused.txt"), $"{_tool.Slug} CONSOLE {e.ParameterObjectAsJson[..Math.Min(800, e.ParameterObjectAsJson.Length)]}\n"); } catch { } };
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        }
        // The page looks like a clean tool window: the website's header, menus, footer and articles are hidden,
        // and the files are handed to the tool's own file picker.
        var names = JsonSerializer.Serialize(_files.Select((f, i) => new { url = $"https://{Host}/__in/{i}/{Uri.EscapeDataString(f.Name)}", name = f.Name, type = Mime(f.Extension) }));
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Inject.Replace("__FILES__", names));
        // Automatic test runs only (OMEBLY_TOOLTEST=1): press the tool's main button, fill password boxes with a test
        // password, then press Download, as a person would.
        if (Environment.GetEnvironmentVariable("OMEBLY_TOOLTEST") == "1") await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoRun);
        core.Navigate($"https://{Host}/tools/{_tool.Slug}/index.html");
    }

    private const string Inject = """
        (() => {
          const css = `.hdr,.drawer,.tools-side,.ftr,.consent,[data-consent],[data-prefs],.tt-info,.faq,.tt-related,.skip,.side-toggle,.wa-float,[data-wa-float]{display:none!important}
            .tools-shell{display:block!important}.tools-main{margin:0!important;max-width:none!important;padding-top:12px!important}
            html{scroll-padding-top:0!important}`;
          const add = () => { const s = document.createElement('style'); s.textContent = css; (document.head || document.documentElement).appendChild(s); };
          if (document.documentElement) add(); else document.addEventListener('readystatechange', add, { once: true });
          const files = __FILES__;
          const give = async () => {
            const input = document.querySelector('[data-file], [data-pe-file]');
            if (!input) return setTimeout(give, 200);
            const dt = new DataTransfer();
            for (const f of files) { const r = await fetch(f.url); const b = await r.blob(); dt.items.add(new File([b], f.name, { type: f.type })); }
            input.files = dt.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
          };
          window.addEventListener('load', () => setTimeout(give, 150));
        })();
        """;

    private const string AutoRun = """
        window.addEventListener('load', () => {
          let ran = false, downloaded = false, tries = 0;
          const tick = () => {
            tries++;
            document.querySelectorAll('input[type=password]').forEach((i) => { if (!i.value) { i.value = 'test1234'; i.dispatchEvent(new Event('input', { bubbles: true })); } });
            // Tools that need a choice: the first page, and a quarter turn.
            if (tries === 4) {
              document.querySelectorAll('.pt-panel input[type=text], .pt-panel input:not([type])').forEach((i) => { if (!i.value && /e\.g\./.test(i.placeholder || '')) { i.value = '1'; i.dispatchEvent(new Event('input', { bubbles: true })); } });
              [...document.querySelectorAll('.pt-panel button')].find((b) => /Right$/.test(b.textContent.trim()))?.click();
            }
            const result = document.querySelector('[data-result]');
            if (result && !result.hidden && !downloaded) {
              const all = document.querySelector('[data-download-all]:not([hidden])');
              const one = result.querySelector('a[download], [data-download], button.pt-dl, .pt-file button, .pt-files button');
              (all && all.offsetParent ? all : one)?.click(); downloaded = !!(all || one);
            }
            const run = document.querySelector('[data-run]');
            if (run && !ran && !run.disabled && run.offsetParent && tries > 5) { run.click(); ran = true; }
            if (tries < 600 && !downloaded) setTimeout(tick, 500);
          };
          setTimeout(tick, 1500);
        });
        """;

    /// <summary>Serves the tool from the app's own copy, and the files you gave it. Anything else: refused.</summary>
    private CoreWebView2WebResourceResponse Serve(CoreWebView2Environment env, string uri)
    {
        CoreWebView2WebResourceResponse Refuse(int code = 403)
        {
            if (Environment.GetEnvironmentVariable("OMEBLY_TOOLTEST") == "1") try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "omebly-webtools-refused.txt"), $"{_tool.Slug} {code} {uri}\n"); } catch { }
            return env.CreateWebResourceResponse(null, code, code == 404 ? "Not found" : "Blocked", "");
        }
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return Refuse();
        if (u.Scheme is "data" or "blob") return null!; // the page's own in-memory data: left to the browser
        if (u.Scheme != "https" || !u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)) return Refuse();
        var path = Uri.UnescapeDataString(u.AbsolutePath);
        string? file = null;
        if (path.StartsWith("/__in/"))
        {
            var parts = path.Split('/', 4);
            if (parts.Length >= 3 && int.TryParse(parts[2], out var i) && i >= 0 && i < _files.Count) file = _files[i].Path;
        }
        else
        {
            if (path.EndsWith('/')) path += "index.html";
            var full = Path.GetFullPath(Path.Combine(WebTools.Root, "site", path.TrimStart('/').Replace('/', '\\')));
            if (full.StartsWith(Path.GetFullPath(Path.Combine(WebTools.Root, "site")) + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) file = full;
        }
        if (file == null) return Refuse(404);
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).AsRandomAccessStream();
        var headers = $"Content-Type: {Mime(Path.GetExtension(file).ToLowerInvariant())}\r\n" +
            "Content-Security-Policy: default-src 'self' blob: data:; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; connect-src 'self' blob: data:; img-src 'self' blob: data:; font-src 'self' data:; worker-src 'self' blob:; frame-src 'none'; object-src 'none'; form-action 'none'\r\n" +
            "X-Content-Type-Options: nosniff\r\nCache-Control: no-store";
        return env.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private static string Mime(string ext) => ext switch
    {
        ".html" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript", ".css" => "text/css", ".json" => "application/json", ".wasm" => "application/wasm",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", ".avif" => "image/avif",
        ".ttf" => "font/ttf", ".otf" => "font/otf", ".woff2" => "font/woff2", ".woff" => "font/woff", ".pdf" => "application/pdf", ".onnx" or ".bin" or ".icc" or ".odttf" => "application/octet-stream",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation", ".epub" => "application/epub+zip", ".txt" => "text/plain", ".md" => "text/markdown",
        ".htm" => "text/html", _ => "application/octet-stream",
    };

    /// <summary>The tool's "Download": saved next to the original file, with "(2)" if the name is taken. No prompts.</summary>
    private static readonly HashSet<string> SafeResult = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".docx", ".xlsx", ".pptx", ".jpg", ".jpeg", ".png", ".txt", ".md", ".epub", ".zip", ".csv", ".json" };

    private void OnDownload(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var name = Path.GetFileName(e.ResultFilePath);
        // Only what the tools make, and only from the tool's own page: never a program or a shortcut, whatever a page asks.
        var src = e.DownloadOperation.Uri ?? "";
        if (!SafeResult.Contains(Path.GetExtension(name)) || !(src.StartsWith("blob:https://" + Host + "/", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
        { e.Cancel = true; return; }
        if (string.IsNullOrWhiteSpace(name) || FileOps.NameProblem(name) != null) name = "Omebly result" + Path.GetExtension(name);
        var target = Path.Combine(_saveTo, FileOps.FreeName(_saveTo, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name)));
        e.ResultFilePath = target;
        e.Handled = true; // no download bar
        e.DownloadOperation.StateChanged += (op, _) =>
        {
            if (op.State == CoreWebView2DownloadState.Completed) DispatcherQueue.TryEnqueue(() => { UndoLog.Made(target, $"{_tool.Name}: {Path.GetFileName(target)}"); Saved?.Invoke(target); });
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
