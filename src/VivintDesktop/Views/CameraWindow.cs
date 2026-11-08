using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VivintDesktop.Api;
using VivintDesktop.Video;

namespace VivintDesktop.Views;

/// <summary>
/// Full camera view: HD live video with sound/snapshot/privacy controls and the
/// camera's recent recorded clips on the right (click one to play it).
/// </summary>
public sealed class CameraWindow : Window
{
    private readonly HomeServices _svc;
    private readonly string _panelId;
    private Device _cam;
    private readonly VideoSurface _video = new();
    private readonly StackPanel _clipList = new();
    private readonly TextBlock _clipHeader = new() { FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly TextBlock _status = new() { Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _liveBtn, _hdBtn, _muteBtn, _pauseBtn, _privacyBtn;
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsMoveToPointEnabled = true };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _hd = true;
    private bool _seeking;
    private readonly Func<Device?> _currentDevice;

    public CameraWindow(HomeServices svc, string panelId, Device camera, Func<Device?> currentDevice, HistoryEvent? playClip = null)
    {
        _svc = svc;
        _panelId = panelId;
        _cam = camera;
        _currentDevice = currentDevice;

        Title = camera.Name;
        Width = 1240; Height = 760; MinWidth = 720; MinHeight = 420;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Theme.Style(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _video.SetTitle(camera.Name);
        _video.Muted = true;

        // toolbar
        _liveBtn = Btn("● Live", async () => await GoLiveAsync(), "PrimaryButton");
        _hdBtn = Btn("HD", async () => { _hd = !_hd; UpdateButtons(); if (_video.IsLive) await GoLiveAsync(); });
        _muteBtn = Btn("Sound off", () => { _video.Muted = !_video.Muted; UpdateButtons(); return Task.CompletedTask; });
        _pauseBtn = Btn("Pause", () => { _video.TogglePause(); return Task.CompletedTask; });
        var snap = Btn("Snapshot", () => { Snapshot(); return Task.CompletedTask; });
        // Privacy mode can only be shown here: Vivint rejects camera-setting writes from this client (HTTP 404).
        _privacyBtn = new Button { Content = "Privacy mode: ON", IsEnabled = false, ToolTip = "Change privacy mode in the Vivint phone app." };

        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) => { _seeking = false; _video.Position = _seek.Value; };
        _clock.Tick += (_, _) =>
        {
            if (_video.IsLive || _seeking) return;
            if (_video.Duration > 0) _seek.Value = _video.Position;
            _pauseBtn.Content = _video.IsPaused ? "Play" : "Pause";
        };
        _video.StateChanged += (_, _) => UpdateButtons();

        var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new UIElement[] { _liveBtn, _hdBtn, _muteBtn, snap, _pauseBtn, _seek }) left.Children.Add(b);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(_privacyBtn, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(_privacyBtn);
        bar.Children.Add(_status);

        var videoFrame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Brushes.Black, Child = _video };
        var leftPane = new DockPanel { Margin = new Thickness(14, 14, 8, 14) };
        DockPanel.SetDock(bar, Dock.Bottom);
        leftPane.Children.Add(bar);
        leftPane.Children.Add(videoFrame);

        // clips sidebar
        var refreshClips = Btn("Refresh", LoadClipsAsync, "GhostButton");
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(refreshClips, Dock.Right);
        head.Children.Add(refreshClips);
        _clipHeader.Text = "Recent clips";
        _clipHeader.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_clipHeader);
        var side = new DockPanel { Margin = new Thickness(6, 14, 14, 14) };
        DockPanel.SetDock(head, Dock.Top);
        side.Children.Add(head);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _clipList });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.Children.Add(leftPane);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        Content = grid;

        Loaded += async (_, _) =>
        {
            _clock.Start();
            UpdateButtons();
            if (playClip is not null) await PlayClipAsync(playClip);
            else await GoLiveAsync();
            await LoadClipsAsync();
        };
        Closed += (_, _) => { _clock.Stop(); _video.Dispose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private Button Btn(string text, Func<Task> click, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        if (style is not null) b.Style = (Style)Application.Current.FindResource(style);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await click(); } finally { b.IsEnabled = true; } };
        return b;
    }

    private void UpdateButtons()
    {
        _hdBtn.Content = _hd ? "HD" : "SD";
        _muteBtn.Content = _video.Muted ? "🔇 Sound off" : "🔊 Sound on";
        var live = _video.IsLive;
        _liveBtn.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _hdBtn.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        _pauseBtn.Visibility = _seek.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _privacyBtn.Visibility = _cam.Privacy ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = live ? "" : _status.Text;
    }

    private async Task GoLiveAsync()
    {
        _cam = _currentDevice() ?? _cam;
        if (_cam.Privacy)
        {
            _video.Stop();
            _video.ShowMessage("Privacy mode is on — live video is turned off for this camera.");
            UpdateButtons();
            return;
        }
        var cam = _cam;
        var hd = _hd;
        await _video.PlayLiveAsync(() => VideoEngine.LiveUriAsync(_svc, _panelId, cam, hd));
        UpdateButtons();
    }

    private async Task PlayClipAsync(HistoryEvent ev)
    {
        _status.Text = "Opening clip…";
        try
        {
            var url = await _svc.GetClipUrlAsync(ev.Id);
            if (url is null) { _status.Text = "This clip is no longer available."; return; }
            await _video.PlayUrlAsync(url, $"{ev.Message.Trim()} · {ev.WhenText}");
            _video.Muted = false;
            _status.Text = $"Clip: {ev.WhenText}";
            UpdateButtons();
        }
        catch (AuthException) { }
        catch (Exception ex) { _status.Text = "Clip failed: " + ex.Message; }
    }

    private async Task LoadClipsAsync()
    {
        _clipHeader.Text = "Recent clips — loading…";
        try
        {
            var clips = (await _svc.GetCameraClipsAsync(_panelId, _cam.Id, 50)).Where(c => c.HasClip).ToList();
            _clipList.Children.Clear();
            foreach (var c in clips) _clipList.Children.Add(ClipRow(c));
            _clipHeader.Text = clips.Count > 0 ? $"Recent clips ({clips.Count})" : "No recent clips";
        }
        catch (AuthException) { }
        catch (Exception ex) { _clipHeader.Text = "Clips unavailable: " + ex.Message; }
    }

    private FrameworkElement ClipRow(HistoryEvent c)
    {
        var img = new Image { Width = 112, Height = 63, Stretch = Stretch.UniformToFill };
        if (c.ThumbnailUrl is { } t) img.Source = Thumb(t, 224);
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = c.Message.Trim(), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = c.WhenText, Foreground = Theme.Brush("Muted"), FontSize = 11 });
        var row = new DockPanel();
        row.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Brushes.Black, Child = img });
        row.Children.Add(text);
        var card = new Border
        {
            Background = Theme.Brush("Surface"), BorderBrush = Theme.Brush("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand, Child = row, ToolTip = "Play this clip",
        };
        card.MouseLeftButtonUp += async (_, _) => await PlayClipAsync(c);
        return card;
    }

    internal static BitmapImage? Thumb(string url, int decodeWidth)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(url);
            b.DecodePixelWidth = decodeWidth;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            return b;
        }
        catch { return null; }
    }

    private void Snapshot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Vivint");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(_cam.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        _status.Text = _video.TakeSnapshot(path) ? $"Saved {path}" : "Snapshot failed (is video playing?)";
    }
}
