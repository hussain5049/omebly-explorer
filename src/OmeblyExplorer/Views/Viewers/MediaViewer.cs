// Video and music, played by Windows' own player (the formats Windows plays: MP4, MOV, MKV, WebM, AVI, WMV, MP3,
// M4A, FLAC, WAV and more). The controls are from the design: play, 5 seconds back and forward, the time, a seek
// bar, volume, speed, subtitles (a .srt or .vtt file with the same name) and full screen. Music shows its cover.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OmeblyExplorer.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace OmeblyExplorer.Views.Viewers;

public sealed class MediaViewer : ViewerBase
{
    private readonly bool _video;
    private readonly MediaPlayer _player = new() { AutoPlay = true };
    private readonly MediaPlayerElement _element = new() { AreTransportControlsEnabled = false, Stretch = Stretch.Uniform };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.001, IsThumbToolTipEnabled = false };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.01, Width = 100, Value = 0.8, IsThumbToolTipEnabled = false };
    private readonly TextBlock _time = new() { FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("#EEF6F2") };
    private readonly Button _play, _speed, _cc;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _dragging;
    private double _rate = 1;
    private int _w, _h;
    private TimeSpan _duration;
    private readonly StackPanel _musicInfo = new() { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _cover = new() { Width = 260, Height = 260, Stretch = Stretch.UniformToFill };

    public MediaViewer(bool video, bool autoPlay = true)
    {
        _video = video;
        _player.AutoPlay = autoPlay;
        _element.SetMediaPlayer(_player);
        _player.Volume = 0.8;
        var stage = new Grid { Margin = new Thickness(36, 24, 36, 12), CornerRadius = new CornerRadius(12), Background = video ? Ui.Brush("#FF000000") : new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        stage.RowDefinitions.Add(new RowDefinition());
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (video) stage.Children.Add(_element);
        else
        {
            stage.Children.Add(_element); _element.Visibility = Visibility.Collapsed;
            var art = new Border { CornerRadius = new CornerRadius(18), Background = Theme.Get("OxCard2"), Child = _cover, HorizontalAlignment = HorizontalAlignment.Center };
            _musicInfo.Children.Add(art);
            stage.Children.Add(_musicInfo);
        }
        _element.Tapped += (_, _) => TogglePlay();
        _element.DoubleTapped += (_, _) => { };
        // Controls bar.
        var bar = new Grid { Padding = new Thickness(18, 4, 18, 12), Background = Ui.Brush("#CC05090A") };
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _seek.Foreground = Theme.Get("OxAcc");
        _seek.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _dragging = true), true);
        _seek.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { _dragging = false; SeekTo(_seek.Value); }), true);
        _seek.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { if (_dragging) { _dragging = false; SeekTo(_seek.Value); } }), true);
        bar.Children.Add(_seek);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _play = Round("", "Play or pause (Space)", TogglePlay);
        left.Children.Add(_play);
        left.Children.Add(Light(IconButton("", "5 seconds back (←)", () => Skip(-5))));
        left.Children.Add(Light(IconButton("", "5 seconds forward (→)", () => Skip(5))));
        left.Children.Add(_time);
        row.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(Light(IconButton("", "Mute (M)", () => _player.IsMuted = !_player.IsMuted)));
        _volume.ValueChanged += (_, e) => _player.Volume = e.NewValue;
        right.Children.Add(_volume);
        _speed = new Button { Content = "1x", MinWidth = 44, Height = 32, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = Ui.Brush("#40FFFFFF"), BorderThickness = new Thickness(1), Foreground = Ui.Brush("#EEF6F2"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_speed, "Speed");
        _speed.Click += (_, _) => { var speeds = new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 }; _rate = speeds[(Array.IndexOf(speeds, _rate) + 1) % speeds.Length]; _player.PlaybackSession.PlaybackRate = _rate; _speed.Content = $"{_rate:0.##}x"; };
        right.Children.Add(_speed);
        _cc = Light(IconButton("", "Subtitles (C)", ToggleSubtitles)); _cc.Visibility = Visibility.Collapsed;
        right.Children.Add(_cc);
        Grid.SetColumn(right, 2); row.Children.Add(right);
        Grid.SetRow(row, 1); bar.Children.Add(row);
        Grid.SetRow(bar, 1); stage.Children.Add(bar);
        Children.Add(stage);
        _tick.Tick += (_, _) => DrawTime();
        _player.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() => { if (!_closed) _play.Content = new FontIcon { FontFamily = Icons, Glyph = s.PlaybackState == MediaPlaybackState.Playing ? "" : "", FontSize = 16 }; });
        _player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() => ShowMessage("This file can't be played here", $"Windows can't play it ({e.Error}). It may need a codec. Try Open in its usual app."));
        _player.MediaOpened += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _duration = s.PlaybackSession.NaturalDuration;
            _w = (int)s.PlaybackSession.NaturalVideoWidth; _h = (int)s.PlaybackSession.NaturalVideoHeight;
            RaiseInfo(); DrawTime();
        });
    }

    public override (string, string)[] Hints => new[] { ("Space", "play or pause"), ("← →", "5 seconds"), ("F", "full screen") };
    public override string Info(FileItem f)
    {
        var parts = new List<string>();
        if (_video && _w > 0) parts.Add($"{_w} × {_h}");
        if (_duration > TimeSpan.Zero) parts.Add(Clock(_duration));
        parts.Add(Format.Size(f.Size));
        return string.Join(" · ", parts);
    }

    public override async Task LoadAsync(FileItem file, CancellationToken ct)
    {
        var sf = await StorageFile.GetFileFromPathAsync(file.Path);
        ct.ThrowIfCancellationRequested();
        var source = MediaSource.CreateFromStorageFile(sf);
        // Subtitles: "film.srt" or "film.vtt" next to "film.mp4".
        if (_video)
        {
            foreach (var ext in new[] { ".srt", ".vtt" })
            {
                var sub = System.IO.Path.ChangeExtension(file.Path, ext);
                if (!File.Exists(sub)) continue;
                try { var subFile = await StorageFile.GetFileFromPathAsync(sub); source.ExternalTimedTextSources.Add(TimedTextSource.CreateFromStream(await subFile.OpenReadAsync())); _cc.Visibility = Visibility.Visible; } catch { }
                break;
            }
        }
        var item = new MediaPlaybackItem(source);
        item.TimedMetadataTracksChanged += (s, _) => { if (_subsOn) for (uint i = 0; i < s.TimedMetadataTracks.Count; i++) s.TimedMetadataTracks.SetPresentationMode(i, TimedMetadataTrackPresentationMode.PlatformPresented); };
        _item = item;
        _player.Source = item;
        _tick.Start();
        if (!_video)
        {
            try
            {
                var props = await sf.Properties.GetMusicPropertiesAsync();
                if (!string.IsNullOrWhiteSpace(props.Title)) _musicInfo.Children.Add(Center(Text(props.Title, 20, bold: true)));
                var who = string.Join(" · ", new[] { props.Artist, props.Album }.Where((x) => !string.IsNullOrWhiteSpace(x)));
                if (who.Length > 0) _musicInfo.Children.Add(Center(Text(who, 14, "OxMuted")));
            }
            catch { }
            var art = await Task.Run(() => Native.GetImage(file.Path, 512, Native.SIIGBF.BiggerSizeOk));
            if (art is { } p)
            {
                var wb = new WriteableBitmap(p.w, p.h);
                using (var s = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(wb.PixelBuffer)) s.Write(p.pixels, 0, p.pixels.Length);
                wb.Invalidate(); _cover.Source = wb;
            }
        }
    }
    private MediaPlaybackItem? _item;
    private bool _subsOn = true;
    private void ToggleSubtitles()
    {
        _subsOn = !_subsOn;
        if (_item == null) return;
        for (uint i = 0; i < _item.TimedMetadataTracks.Count; i++) _item.TimedMetadataTracks.SetPresentationMode(i, _subsOn ? TimedMetadataTrackPresentationMode.PlatformPresented : TimedMetadataTrackPresentationMode.Disabled);
    }

    private void TogglePlay() { if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play(); }
    private void Skip(double s) { var p = _player.PlaybackSession; p.Position = TimeSpan.FromSeconds(Math.Clamp(p.Position.TotalSeconds + s, 0, Math.Max(0, _duration.TotalSeconds - 0.1))); DrawTime(); }
    private void SeekTo(double fraction) { if (_duration > TimeSpan.Zero) _player.PlaybackSession.Position = TimeSpan.FromSeconds(fraction * _duration.TotalSeconds); }
    private void DrawTime()
    {
        if (_closed) return; // a tick or an opened event that was already queued when the viewer closed
        var pos = _player.PlaybackSession.Position;
        _time.Text = $"{Clock(pos)} / {Clock(_duration)}";
        if (!_dragging && _duration > TimeSpan.Zero) _seek.Value = pos.TotalSeconds / _duration.TotalSeconds;
    }
    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public override bool OnKey(VirtualKey key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Space or VirtualKey.K: TogglePlay(); return true;
            case VirtualKey.Left: Skip(shift ? -30 : -5); return true;
            case VirtualKey.Right: Skip(shift ? 30 : 5); return true;
            case VirtualKey.Up: _volume.Value = Math.Min(1, _volume.Value + 0.1); return true;
            case VirtualKey.Down: _volume.Value = Math.Max(0, _volume.Value - 0.1); return true;
            case VirtualKey.M: _player.IsMuted = !_player.IsMuted; return true;
            case VirtualKey.C: ToggleSubtitles(); return true;
        }
        return false;
    }

    private bool _closed;
    public override void Close()
    {
        _closed = true;
        _tick.Stop();
        try { _player.Pause(); _player.Source = null; _element.SetMediaPlayer(null); _player.Dispose(); } catch { }
    }

    private static Button Round(string glyph, string name, Action run)
    {
        var b = new Button { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Padding = new Thickness(0), Background = Ui.Brush("#FFFFFFFF"), Foreground = Ui.Brush("#FF05090A"), BorderThickness = new Thickness(0), Content = new FontIcon { FontFamily = Icons, Glyph = glyph, FontSize = 16 } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => run();
        return b;
    }
    private static Button Light(Button b) { b.Foreground = Ui.Brush("#EEF6F2"); return b; }
    private static TextBlock Center(TextBlock t) { t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center; return t; }
}
