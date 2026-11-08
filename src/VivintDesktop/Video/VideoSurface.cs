using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VivintDesktop.Video;

/// <summary>
/// A video area that plays a live camera (auto-reconnecting) or a recorded clip with mpv.
/// mpv renders on the GPU into a native child window; until the first frame arrives the
/// window is hidden and a WPF poster (last snapshot) + status text show instead.
/// </summary>
public sealed class VideoSurface : Grid, IDisposable
{
    private readonly MpvHost _host = new() { Visibility = Visibility.Hidden };
    private readonly Image _poster = new() { Stretch = Stretch.Uniform, Opacity = 0.55 };
    private readonly TextBlock _badge;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly TaskCompletionSource<MpvPlayer> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MpvPlayer? _mpv;

    private Func<Task<(Uri uri, string route)>>? _liveSource;
    private Func<Task<(Uri uri, string route)>>? _resumeSource;   // kept across Stop() for Resume
    private string _title = "";
    private string _badgeText = "";
    private bool _muted = true;
    private bool _paused;
    private bool _disposed;
    private int _failures;
    private int _generation;   // ignores events from a previous load

    /// <summary>Raised when the user clicks the video.</summary>
    public event EventHandler? Clicked;
    /// <summary>Raised (on the UI thread) when playback state changes.</summary>
    public event EventHandler? StateChanged;

    public bool IsLive => _liveSource is not null;
    /// <summary>True while a live stream is playing or (re)connecting.</summary>
    public bool IsStreaming => _liveSource is not null;
    public bool IsPaused => _paused;
    public bool IsPlaying => _host.Visibility == Visibility.Visible && !_paused;

    public VideoSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;

        _badge = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 8, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 },
        };
        _status = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 },
        };
        Children.Add(_poster);
        Children.Add(_badge);
        Children.Add(_status);
        Children.Add(_host);   // on top; hidden until video is ready

        // Clicks while the video window is hidden land here; on video they come from mpv.
        var pressed = false;
        MouseLeftButtonDown += (_, _) => pressed = true;
        MouseLeave += (_, _) => pressed = false;
        MouseLeftButtonUp += (_, _) => { if (pressed) Clicked?.Invoke(this, EventArgs.Empty); pressed = false; };

        _host.HandleCreated += OnHostHandle;
        _retry.Tick += async (_, _) => { _retry.Stop(); await StartLiveAsync(); };
    }

    private void OnHostHandle(IntPtr hwnd)
    {
        try
        {
            var mpv = new MpvPlayer(hwnd) { Mute = _muted };
            mpv.FirstFrame += () => Ui(OnFirstFrame);
            mpv.Ended += (err, msg) => Ui(() => OnEnded(err, msg));
            mpv.Clicked += () => Ui(() => Clicked?.Invoke(this, EventArgs.Empty));
            mpv.PauseChanged += p => Ui(() => { _paused = p; StateChanged?.Invoke(this, EventArgs.Empty); });
            _mpv = mpv;
            _ready.TrySetResult(mpv);
        }
        catch (Exception ex)
        {
            _status.Text = "Video engine failed to start: " + ex.Message;
            _ready.TrySetException(ex);
        }
    }

    private void Ui(Action a)
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(() => { if (!_disposed) a(); });
    }

    private void OnFirstFrame()
    {
        _failures = 0;
        _status.Text = "";
        _host.Visibility = Visibility.Visible;
        _mpv?.SetOverlayText(_badgeText);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCover(string status)
    {
        _host.Visibility = Visibility.Hidden;
        _poster.Visibility = _poster.Source is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = status;
    }

    /// <summary>Image to show (dimmed) while the stream connects.</summary>
    public void SetPoster(ImageSource? image) => _poster.Source = image;

    public void SetTitle(string title)
    {
        _title = title;
        UpdateBadge(IsLive ? "LIVE" : "");
    }

    private void UpdateBadge(string suffix)
    {
        _badgeText = suffix.Length > 0 ? $"{_title}  ·  {suffix}" : _title;
        _badge.Text = _badgeText;
        _mpv?.SetOverlayText(_badgeText);
    }

    // ---------------------------------------------------------------- live
    /// <summary>Play a live camera. <paramref name="source"/> resolves a fresh URL on each (re)connect.</summary>
    public async Task PlayLiveAsync(Func<Task<(Uri uri, string route)>> source)
    {
        _liveSource = _resumeSource = source;
        _failures = 0;
        await StartLiveAsync();
    }

    /// <summary>Restart the last live stream after <see cref="Stop"/>, reusing this player.</summary>
    public Task ResumeAsync() =>
        _resumeSource is not null && _liveSource is null ? PlayLiveAsync(_resumeSource) : Task.CompletedTask;

    private async Task StartLiveAsync()
    {
        if (_disposed || _liveSource is null) return;
        var gen = ++_generation;
        ShowCover("Connecting…");
        try
        {
            var mpvTask = _ready.Task;
            var (uri, route) = await _liveSource();
            if (uri.Scheme == "demo")
            {
                if (_disposed || _liveSource is null || gen != _generation) return;
                UpdateBadge($"LIVE · {route}");
                ShowDemoStill(Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')), person: false);
                return;
            }
            var mpv = await mpvTask;
            if (_disposed || _liveSource is null || gen != _generation) return;
            UpdateBadge($"LIVE · {route}");
            mpv.PlayLive(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            _status.Text = ex.Message;
            ScheduleRetry();
        }
    }

    /// <summary>Demo mode has no video: show a drawn camera picture instead.</summary>
    private void ShowDemoStill(string camera, bool person)
    {
        using var stream = new MemoryStream(Api.DemoImages.CameraJpeg(camera, DateTime.Now, person));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        _poster.Source = image;
        _poster.Opacity = 1;
        ShowCover("");
    }

    private void OnEnded(bool error, string? message)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (IsLive)
        {
            ShowCover(error ? "Connection lost — reconnecting…" : "Stream ended — reconnecting…");
            ScheduleRetry();
        }
        else if (error)
        {
            ShowCover("This clip couldn't be played." + (message is null ? "" : $" ({message})"));
        }
    }

    private void ScheduleRetry()
    {
        if (_disposed || _liveSource is null) return;
        _failures++;
        _retry.Interval = TimeSpan.FromSeconds(Math.Min(30, 3 * _failures));
        _retry.Stop();
        _retry.Start();
    }

    // ---------------------------------------------------------------- clips
    public async Task PlayUrlAsync(string url, string label)
    {
        _liveSource = null;
        _retry.Stop();
        var gen = ++_generation;
        UpdateBadge(label);
        if (url.StartsWith("demo:", StringComparison.Ordinal)) { ShowDemoStill(label, person: true); return; }
        ShowCover("Loading clip…");
        var mpv = await _ready.Task;
        if (_disposed || gen != _generation) return;
        mpv.PlayFile(url);
    }

    public void TogglePause()
    {
        if (IsLive || _mpv is null) return;
        if (_mpv.IsEof) { _mpv.PercentPos = 0; _mpv.Pause = false; }
        else _mpv.Pause = !_paused;
    }

    /// <summary>Clip length in seconds (0 for live).</summary>
    public double Duration => _mpv?.Duration ?? 0;

    /// <summary>Clip position 0..1.</summary>
    public double Position
    {
        get => (_mpv?.PercentPos ?? 0) / 100.0;
        set { if (_mpv is not null) _mpv.PercentPos = Math.Clamp(value, 0, 1) * 100; }
    }

    // ---------------------------------------------------------------- misc
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (_mpv is not null) _mpv.Mute = value; }
    }

    public void ShowMessage(string text) => ShowCover(text);

    public bool TakeSnapshot(string path) => _mpv?.Screenshot(path) ?? false;

    public void Stop()
    {
        _liveSource = null;
        _retry.Stop();
        _generation++;
        _mpv?.Stop();
        ShowCover("");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _liveSource = null;
        _retry.Stop();
        var mpv = _mpv;
        _mpv = null;
        if (mpv is null) { _host.Dispose(); return; }
        // Destroy the native window only after mpv has let go of it.
        mpv.Dispose();
        mpv.Closed.ContinueWith(_ => Dispatcher.BeginInvoke(() => _host.Dispose()));
    }
}

/// <summary>Native child window that mpv renders into.</summary>
internal sealed class MpvHost : HwndHost
{
    public event Action<IntPtr>? HandleCreated;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
        var hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 16, 16, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        HandleCreated?.Invoke(hwnd);
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
