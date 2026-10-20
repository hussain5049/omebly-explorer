// A small glass card, bottom right, for jobs like zipping, unzipping and converting: what it is doing, a progress
// bar, Cancel; when done, Show (the new files) and Undo (they go to the Recycle Bin).
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OmeblyExplorer.Core;

namespace OmeblyExplorer.Views;

public sealed class TaskCard : Grid
{
    public event Action<string>? ShowFile;
    public event Action<TaskCard>? Finished;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly CancellationTokenSource _cts = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tick;

    public TaskCard(string title)
    {
        var card = new Border { Width = 360, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(14) };
        card.Background = Theme.Current == ElementTheme.Light ? Ui.Brush("#F2FFFFFF") : Ui.Brush("#F0101A17");
        card.BorderBrush = Theme.Get("OxLine2"); card.BorderThickness = new Thickness(1);
        card.Shadow = new ThemeShadow(); card.Translation = new System.Numerics.Vector3(0, 0, 24);
        Children.Add(card);
        _title.Text = title; _title.Foreground = Theme.Get("OxText"); _sub.Foreground = Theme.Get("OxMuted");
        _bar.Foreground = Theme.Get("OxAcc");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_title, "TaskTitle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_sub, "TaskSub");
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(_title); sp.Children.Add(_sub); sp.Children.Add(_bar);
        _buttons.Children.Add(Btn("Cancel", () => _cts.Cancel()));
        sp.Children.Add(_buttons);
        card.Child = sp;
        _tick = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(200);
    }

    /// <summary>Runs the job off the window's thread, showing progress; <paramref name="read"/> says how far it is.</summary>
    public async Task<List<string>?> Run(Func<CancellationToken, Task<List<string>>> job, Func<(double fraction, string current)> read, string doneText)
    {
        _tick.Tick += (_, _) => { var (f, c) = read(); _bar.Value = Math.Clamp(f, 0, 1); _sub.Text = c; };
        _tick.Start();
        List<string>? made = null;
        try
        {
            made = await Task.Run(() => job(_cts.Token));
            _title.Text = doneText;
            _sub.Text = made.Count == 1 ? System.IO.Path.GetFileName(made[0]) : $"{made.Count:N0} new files";
            _bar.Value = 1;
            _buttons.Children.Clear();
            if (made.Count > 0)
            {
                var list = made;
                _buttons.Children.Add(Btn("Undo", async () => { await FileOps.Recycle(list.Where(UndoLog.Exists)); _title.Text = "Taken back"; _sub.Text = "The new files are in the Recycle Bin"; _buttons.Children.Clear(); Close(4); }));
                _buttons.Children.Add(Btn("Show", () => { ShowFile?.Invoke(list[0]); Close(0); }, accent: true));
                // Undo later too, with Ctrl+Z.
                DateTime when = default;
                UndoLog.Add(new UndoStep { What = doneText.Replace("Done: ", ""), Undo = () => { when = DateTime.Now; return FileOps.Recycle(list.Where(UndoLog.Exists)); }, Redo = () => Task.Run(() => RecycleBin.Find(list, when).All((i) => RecycleBin.Restore(i) != null)) });
            }
            Close(8);
        }
        catch (OperationCanceledException) { _title.Text = "Stopped"; _sub.Text = "Nothing was left half-made"; _buttons.Children.Clear(); Close(3); }
        catch (Exception e)
        {
            _title.Text = "Couldn't finish"; _sub.Text = Plain(e); _sub.TextWrapping = TextWrapping.Wrap; _sub.Foreground = Theme.Get("OxWarn");
            _buttons.Children.Clear(); _buttons.Children.Add(Btn("Close", () => Close(0)));
        }
        finally { _tick.Stop(); }
        return made;
    }
    private static string Plain(Exception e) => e switch
    {
        NeedsPassword or PdfTools.WrongPassword or ArchiveProblem => e.Message,
        UnauthorizedAccessException => "Windows didn't allow writing here. Try another folder.",
        IOException io when io.HResult == unchecked((int)0x80070070) => "The drive is full.",
        _ => e.Message,
    };

    private async void Close(int seconds)
    {
        if (seconds > 0) await Task.Delay(seconds * 1000);
        Finished?.Invoke(this);
    }

    private static Button Btn(string text, Action run, bool accent = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 4, 14, 5), CornerRadius = new CornerRadius(8), FontSize = 12.5 };
        if (accent) { b.Background = Theme.Get("OxAcc"); b.Foreground = Theme.Get("OxAccText"); b.BorderThickness = new Thickness(0); }
        b.Click += (_, _) => run();
        return b;
    }
}
