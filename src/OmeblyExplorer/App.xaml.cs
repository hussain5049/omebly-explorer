using Microsoft.UI.Xaml;

namespace OmeblyExplorer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // A problem is written down (on this PC only) so it can be fixed; nothing is sent anywhere.
        UnhandledException += (_, e) => Core.CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Core.CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Core.CrashLog.Write(e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Core.Settings.Load();
            Core.Takeover.Starting();
            var cmd = Environment.GetCommandLineArgs().Skip(1).ToList();
            var path = cmd.FirstOrDefault((a) => !a.StartsWith('-'));
            // Engine self-test (no window): --selftest <empty test folder> <samples folder>
            if (cmd.Count >= 2 && cmd[0] == "--selftest") { Core.Takeover.Started(); var code = Task.Run(() => Core.SelfTest.Run(cmd[1], cmd.Count > 2 ? cmd[2] : "")).GetAwaiter().GetResult(); Environment.Exit(code); return; }
            // One Omebly Tools tool on these files, in its own window: --webtool <slug> <file> [file...]
            if (cmd.Count >= 3 && cmd[0] == "--webtool")
            {
                Core.Takeover.Started();
                var tool = WebTools.All.FirstOrDefault((t) => t.Slug == cmd[1]);
                var files = cmd.Skip(2).Where(File.Exists).Select((f) => { var fi = new FileInfo(f); return new Core.FileItem { Name = fi.Name, Path = fi.FullName, Size = fi.Length, Modified = fi.LastWriteTime }; }).ToList();
                if (tool == null || files.Count == 0) { Exit(); return; }
                var tw = new ToolWindow(tool, files);
                tw.Closed += (_, _) => Exit();
                _window = tw; tw.Activate();
                return;
            }
            // The uninstaller asks Omebly to give folders and Win+E back to File Explorer, then stops.
            if (cmd.Contains("--give-back")) { if (Core.Takeover.IsOn) Core.Takeover.TurnOff(); JumpBar.End(); Core.Takeover.Started(); Exit(); return; }
            // Already running: the running copy opens it (a new tab), and this copy ends.
            if (Environment.GetEnvironmentVariable("OMEBLY_SETTINGS_DIR") is not { Length: > 0 } && Core.SingleInstance.HandOver(path)) { Core.Takeover.Started(); Exit(); return; }
            Core.FileIndex.Start(); // the list for instant search, loaded or refreshed in the background
            _window = new MainWindow(path);
            _window.Activate();
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Core.SingleInstance.Listen((p) => ui.TryEnqueue(() => MainWindow.OpenFromOutside(p)));
            if (Core.Settings.Current.JumpBar) JumpBar.Begin();
            Core.Takeover.Started();
        }
        catch (Exception e) { Core.CrashLog.Write(e); throw; }
    }
}
