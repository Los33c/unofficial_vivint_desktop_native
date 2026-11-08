using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VivintDesktop.Api;
using VivintDesktop.Video;
using VivintDesktop.Views;

namespace VivintDesktop;

public partial class MainWindow : Window
{
    private readonly VivintClient _client;
    private readonly HomeServices _svc;

    private List<SystemRef> _systems = new();
    private SystemState? _state;
    private JsonElement? _systemJson;
    private List<Routine> _routines = new();

    // refresh bookkeeping
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private int _busy;           // commands in flight
    private int _interacting;    // user is dragging a slider / editing a setpoint
    private bool _refreshing;
    private CancellationTokenSource? _followUp;

    // camera stills cached per device id (keyed by the thumbnail timestamp)
    private readonly Dictionary<string, (string? stamp, BitmapSource? image)> _thumbs = new();

    // units: the API speaks °C; show °F in non-metric regions
    private readonly bool _fahrenheit = !RegionInfo.CurrentRegion.IsMetric;

    // live camera tiles
    private readonly List<VideoSurface> _tiles = new();
    private bool _tilesHd;

    private bool _automationsLoaded;

    /// <summary>The user chose "Sign out".</summary>
    public event Action? SignedOut;

    public MainWindow(VivintClient client)
    {
        InitializeComponent();
        _client = client;
        _svc = new HomeServices(client);
        InitTray();
        Theme.ApplyTitleBar(this);
        UpdateThemeButton();
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;

        _poll.Tick += async (_, _) =>
        {
            if (_busy == 0 && _interacting == 0 && WindowState != WindowState.Minimized)
            {
                await RefreshAsync(quiet: true);
                await LoadFeedAsync();
            }
        };
        // Don't stream video while minimized or hidden in the tray.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) PauseCameraTiles();
            else if (IsVisible) _ = ShowCameraTilesAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) PauseCameraTiles();
            else if (WindowState != WindowState.Minimized) _ = ShowCameraTilesAsync();
        };
        BuildPanelButtons();
        if (client.DemoMode)
        {
            DemoBadge.Visibility = Visibility.Visible;
            LogoutBtn.ToolTip = "Exit demo";
            System.Windows.Automation.AutomationProperties.SetName(LogoutBtn, "Exit demo");
            Title += " — Demo";
        }
        SizeChanged += (_, _) =>
        {
            if (PanelOverlay.Visibility == Visibility.Visible) PanelSheet.Width = Math.Max(640, Math.Min(1040, ActualWidth - 160));
        };
        Loaded += async (_, _) => { await LoadSystemsAsync(); _poll.Start(); };
        Closed += (_, _) => { _poll.Stop(); _followUp?.Cancel(); DisposeCameraTiles(); };
    }

    private void SetStatus(string msg, bool error = false)
    {
        StatusText.Text = msg;
        StatusText.Foreground = error ? Res("Danger") : Res("Muted");
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private static Brush Hex(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    // ===================================================== systems
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(quiet: false);
        await LoadFeedAsync();
        if (Tabs.SelectedItem == HistoryTab) await LoadHistoryAsync();
        else if (Tabs.SelectedItem == AutomationsTab) await LoadAutomationsAsync();
        else if (Tabs.SelectedItem == SettingsTab) await LoadSettingsAsync();
        else if (Tabs.SelectedItem == PeopleTab) LoadPeopleTab();
    }

    private async Task LoadSystemsAsync()
    {
        SetStatus("Loading your account…");
        try
        {
            var res = await _client.AuthUserAsync();
            if (!res.Ok || res.Json is not { } j)
            {
                SetStatus($"Could not load your account (HTTP {res.Status}).", error: true);
                ShowEmpty($"Vivint returned HTTP {res.Status} for your account. Try Refresh, or sign out and back in.");
                return;
            }
            _systems = SystemRef.FromAuthUser(j);
            StartRealtime(j);
            UserText.Text = SystemRef.UserName(j) ?? "";
            UserInitial.Text = UserText.Text.Length > 0 ? UserText.Text[..1].ToUpperInvariant() : "";
            SystemCombo.ItemsSource = _systems;
            SystemCombo.Visibility = _systems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (_systems.Count == 0)
            {
                SetStatus("No systems on this account.");
                ShowEmpty("Your Vivint account doesn't have any systems you can access.");
                return;
            }
            var last = Settings.LastPanel;
            SystemCombo.SelectedIndex = Math.Max(0, _systems.FindIndex(s => s.PanelId == last)); // loads it
        }
        catch (AuthException) { /* App handles session expiry */ }
        catch (Exception ex) { SetStatus("Error: " + ex.Message, error: true); ShowEmpty("Couldn't reach Vivint: " + ex.Message); }
    }

    private async void SystemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SystemCombo.SelectedItem is not SystemRef s) return;
        Settings.LastPanel = s.PanelId;
        _state = null;
        _systemJson = null;
        _thumbs.Clear();
        ResetChangeBaseline();
        _routines = new();
        _historyLoaded = _automationsLoaded = _settingsLoaded = _peopleLoaded = false;
        _histEvents = new();
        _histSelected = null;
        HistoryToolbar.Children.Clear();
        HistoryList.Children.Clear();
        DisposeCameraTiles();
        _feed = new();
        _feedFocus = null;
        ClosePanel();
        await RefreshAsync(quiet: false);
        _ = LoadRoutinesAsync();
        _ = LoadFeedAsync();
        _ = LoadTimelineAsync();
        await ShowCameraTilesAsync();
    }

    private SystemRef? Selected => SystemCombo.SelectedItem as SystemRef;

    private async Task RefreshAsync(bool quiet)
    {
        if (Selected is not { } s || _refreshing) return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        if (!quiet) SetStatus($"Loading {s.Name}…");
        try
        {
            var res = await _client.GetSystemAsync(s.PanelId);
            if (!res.Ok || res.Json is not { } j)
            {
                SetStatus($"Could not load {s.Name} (HTTP {res.Status}).", error: true);
                if (_state is null) ShowEmpty($"Vivint returned HTTP {res.Status} for this system.\n{Trim(res.RawBody, 300)}");
                return;
            }
            _systemJson = j;
            var first = _state is null;
            _state = SystemState.Parse(j, s.PanelId, s.Name);
            DetectChanges(_state);
            if (first) PrewarmVideo();
            RenderHome();
            UpdatedText.Text = $"Updated {DateTime.Now:h:mm:ss tt}";
            if (!quiet) SetStatus($"{_state.Name}: {_state.Devices.Count} devices.");
        }
        catch (AuthException) { }
        catch (Exception ex)
        {
            if (!quiet || _state is null) SetStatus("Error: " + ex.Message, error: true);
            else UpdatedText.Text = "Offline — retrying";
        }
        finally
        {
            _refreshing = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private void ShowEmpty(string text)
    {
        ArmHost.Children.Clear();
        StateTitle.Text = "Unavailable";
        StateSub.Text = "";
        ShortcutBar.Children.Clear();
        SummaryText.Text = "";
        HomeContent.Children.Clear();
        HomeContent.Children.Add(Muted(text, 14));
    }

    private TextBlock Muted(string text, double size = 13) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted"), FontSize = size,
        Margin = new Thickness(2, 8, 0, 0),
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"),
        Margin = new Thickness(2, 18, 0, 10),
    };

    private Device? FindDevice(string id) => _state?.Devices.FirstOrDefault(d => d.Id == id);

    // ===================================================== home render
    private void RenderHome()
    {
        if (_state is null) return;
        var offset = HomeScroll.VerticalOffset;

        RenderShortcutBar();

        // summary of things that need attention
        var alerts = new List<string>();
        int unlocked = _state.Devices.Count(d => d.Category == DeviceCategory.Lock && d.State == false);
        int garages = _state.Devices.Count(d => d.Category == DeviceCategory.Garage && d.GarageIsOpen);
        int open = _state.Devices.Count(d => d.Category == DeviceCategory.Sensor && d.State == true && d.EquipmentType != 2);
        int lowBatt = _state.Devices.Count(d => d.Battery is > 0 and <= 20);
        int offline = _state.Devices.Count(d => d.Online == false && d.Category != DeviceCategory.Other);
        if (unlocked > 0) alerts.Add($"{unlocked} lock{S(unlocked)} unlocked");
        if (garages > 0) alerts.Add($"{garages} garage door{S(garages)} open");
        if (open > 0) alerts.Add($"{open} sensor{S(open)} open/triggered");
        if (lowBatt > 0) alerts.Add($"{lowBatt} low batter{(lowBatt == 1 ? "y" : "ies")}");
        if (offline > 0) alerts.Add($"{offline} device{S(offline)} offline");
        SummaryText.Visibility = Visibility.Collapsed;   // shown in the status band instead
        RenderBand(alerts);
        RenderControls();

        Dispatcher.BeginInvoke(() => HomeScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private static string S(int n) => n == 1 ? "" : "s";

    private void RenderShortcutBar()
    {
        ShortcutBar.Children.Clear();
        var shortcuts = _routines.Where(r => r.IsShortcut && r.Enabled).ToList();
        if (shortcuts.Count == 0) return;
        foreach (var r in shortcuts)
        {
            var b = MakeBtn("▶  " + r.Name, () => RunShortcutAsync(r));
            b.Margin = new Thickness(0, 0, 6, 6);
            ShortcutBar.Children.Add(b);
        }
    }

    // ----------------------------------------------------- device cards
    private Border BuildCard(Device d)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = d.Name, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Res("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // state line
        if (d.Category != DeviceCategory.Thermostat)
        {
            var good = d.Category switch
            {
                DeviceCategory.Lock => d.State == true,
                DeviceCategory.Garage => d.Garage == GarageState.Closed,
                DeviceCategory.Sensor => d.State == false,
                DeviceCategory.Switch or DeviceCategory.Dimmer => false,
                DeviceCategory.Camera => d.Online != false && !d.Privacy,
                _ => d.Online != false,
            };
            var text = d.Category switch
            {
                DeviceCategory.Sensor => $"{d.StateText} · {d.SensorKind}",
                DeviceCategory.Camera when d.Privacy => "Privacy mode on",
                _ => d.StateText,
            };
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = d.Alert ? Res("Danger") : good ? Res("Ok") : Res("Muted"),
                FontWeight = d.Alert ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        // detail line
        var details = new List<string>();
        if (d.Battery is { } b) details.Add($"Battery {b}%");
        if (d.Online == false && d.Category != DeviceCategory.Camera) details.Add("Offline");
        if (d.Bypassed) details.Add("Bypassed");
        if (d.Tampered) details.Add("Tamper!");
        if (details.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", details), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Foreground = d.Battery is > 0 and <= 20 || d.Tampered ? Res("Warn") : Res("Muted"),
            });

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var width = 260.0;
        var pid = _state!.PanelId;

        switch (d.Category)
        {
            case DeviceCategory.Lock:
                actions.Children.Add(d.State == true
                    ? MakeBtn("Unlock", () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, false), $"Unlocking {d.Name}"))
                    : MakeBtn("Lock", () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, true), $"Locking {d.Name}"), "PrimaryButton"));
                break;

            case DeviceCategory.Garage:
                actions.Children.Add(d.GarageIsOpen
                    ? MakeBtn("Close", () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, false), $"Closing {d.Name}", confirm: true), "PrimaryButton")
                    : MakeBtn("Open", () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, true), $"Opening {d.Name}", confirm: true)));
                break;

            case DeviceCategory.Switch:
                actions.Children.Add(MakeBtn(d.State == true ? "Turn off" : "Turn on",
                    () => Do(() => _client.SetSwitchAsync(pid, d.PartitionId, d.Id, d.State != true), $"Switching {d.Name}"),
                    d.State == true ? null : "PrimaryButton"));
                break;

            case DeviceCategory.Dimmer:
                BuildDimmer(d, actions);
                break;

            case DeviceCategory.Thermostat:
                width = 300;
                BuildThermostat(d, panel, actions);
                break;

            case DeviceCategory.Camera:
                width = 300;
                BuildCameraCard(d, panel, actions);
                break;

            case DeviceCategory.Sensor:
                width = 230;
                actions.Children.Add(MakeBtn(d.Bypassed ? "Un-bypass" : "Bypass",
                    () => Do(() => _client.SetSensorBypassAsync(pid, d.PartitionId, d.Id, !d.Bypassed),
                        $"{(d.Bypassed ? "Un-bypassing" : "Bypassing")} {d.Name}", confirm: !d.Bypassed), "GhostButton"));
                break;
        }
        if (actions.Children.Count > 0) panel.Children.Add(actions);

        var card = new Border { Style = (Style)FindResource("Card"), Width = width, Child = panel };
        if (d.Alert) card.BorderBrush = Res("DangerSoft");
        return card;
    }

    // ----------------------------------------------------- camera card (home)
    private void BuildCameraCard(Device d, StackPanel panel, Panel actions)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, Cursor = Cursors.Hand };
        var placeholder = new TextBlock { Text = "Loading image…", Foreground = Res("Muted"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var play = new TextBlock { Text = "▶", FontSize = 34, Foreground = Hex("#CCFFFFFF"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var frame = new Border
        {
            Height = 152, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6),
            Background = Res("Sunken"), ClipToBounds = true, Cursor = Cursors.Hand, ToolTip = "Watch live",
            Child = new Grid { Children = { placeholder, image, play } },
        };
        frame.MouseLeftButtonUp += (_, _) => OpenCamera(d);
        panel.Children.Add(frame);

        async Task Load(bool force)
        {
            if (!force && _thumbs.TryGetValue(d.Id, out var c) && c.stamp == d.ThumbnailDate && c.image is not null)
            {
                image.Source = c.image;
                placeholder.Visibility = Visibility.Collapsed;
                return;
            }
            placeholder.Text = "Loading image…";
            placeholder.Visibility = Visibility.Visible;
            try
            {
                var (status, data, type) = await _client.GetCameraThumbnailAsync(_state!.PanelId, d.Id);
                if (status is >= 200 and < 300 && data.Length > 0 && (type is null || type.StartsWith("image")))
                {
                    var bmp = Decode(data);
                    _thumbs[d.Id] = (d.ThumbnailDate, bmp);
                    image.Source = bmp;
                    placeholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    placeholder.Text = d.Privacy ? "Privacy mode" : "No snapshot yet";
                    _thumbs[d.Id] = (d.ThumbnailDate, null);
                }
            }
            catch (AuthException) { }
            catch (Exception ex) { placeholder.Text = "Image unavailable"; placeholder.ToolTip = ex.Message; }
        }

        if (_thumbs.TryGetValue(d.Id, out var cached) && cached.stamp == d.ThumbnailDate && cached.image is null)
            placeholder.Text = d.Privacy ? "Privacy mode" : "No snapshot yet";
        else
            _ = Load(false);

        actions.Children.Add(MakeBtn("● Watch live", () => { OpenCamera(d); return Task.CompletedTask; }, "PrimaryButton"));
        actions.Children.Add(MakeBtn("Recordings", () => { OpenCamera(d); return Task.CompletedTask; }));
    }

    private static BitmapSource Decode(byte[] data)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(data);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void OpenCamera(Device cam, HistoryEvent? clip = null)
    {
        if (_state is null) return;
        var w = new CameraWindow(_svc, _state.PanelId, cam, () => FindDevice(cam.Id), clip) { Owner = this };
        w.Show();
    }

    private void BuildDimmer(Device d, Panel actions)
    {
        var pid = _state!.PanelId;
        var on = (d.Level ?? 0) > 0 || d.State == true;
        actions.Children.Add(MakeBtn(on ? "Off" : "On",
            () => Do(() => _client.SetDimmerAsync(pid, d.PartitionId, d.Id, on ? 0 : 100), $"Switching {d.Name}"),
            on ? null : "PrimaryButton"));

        var label = new TextBlock { Text = $"{d.Level ?? 0}%", Width = 38, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Muted"), TextAlignment = TextAlignment.Right };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Width = 120, Value = d.Level ?? 0, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), IsSnapToTickEnabled = true, TickFrequency = 1, IsMoveToPointEnabled = true,
        };
        slider.ValueChanged += (_, _) => label.Text = $"{(int)slider.Value}%";
        bool held = false;
        slider.PreviewMouseLeftButtonDown += (_, _) => { if (!held) { held = true; _interacting++; } };
        async Task Commit()
        {
            if (held) { held = false; _interacting--; }
            var v = (int)slider.Value;
            if (v == (d.Level ?? 0)) return;
            await Do(() => _client.SetDimmerAsync(pid, d.PartitionId, d.Id, v), $"Setting {d.Name} to {v}%");
        }
        slider.PreviewMouseLeftButtonUp += async (_, _) => await Commit();
        slider.LostMouseCapture += async (_, _) => { if (held) await Commit(); };
        slider.KeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) await Commit(); };
        actions.Children.Add(slider);
        actions.Children.Add(label);
    }

    // ----------------------------------------------------- thermostat
    private string FormatTemp(double? c) => c is not { } v ? "—"
        : _fahrenheit ? $"{v * 9 / 5 + 32:0}°" : $"{v:0.#}°";
    private double ToDisplay(double c) => _fahrenheit ? Math.Round(c * 9 / 5 + 32) : Math.Round(c * 2) / 2;
    private double FromDisplay(double v) => _fahrenheit ? (v - 32) * 5 / 9 : v;

    private void BuildThermostat(Device d, StackPanel panel, Panel actions)
    {
        var pid = _state!.PanelId;
        var mode = (ThermostatMode)(d.Mode ?? 0);

        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        big.Children.Add(new TextBlock { Text = FormatTemp(d.Temperature), FontSize = 34, FontWeight = FontWeights.Light,
            Foreground = Res("Text") });
        var sub = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        sub.Children.Add(new TextBlock { Text = "Inside" + (_fahrenheit ? " (°F)" : " (°C)"), Foreground = Res("Muted"), FontSize = 11 });
        if (d.Humidity is { } h)
            sub.Children.Add(new TextBlock { Text = $"Humidity {h:0}%", Foreground = Res("Muted"), FontSize = 11 });
        if (d.Online == false)
            sub.Children.Add(new TextBlock { Text = "Offline", Foreground = Res("Danger"), FontSize = 11 });
        big.Children.Add(sub);
        panel.Children.Add(big);

        // mode
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        modeRow.Children.Add(new TextBlock { Text = "Mode", Width = 44, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Muted") });
        var modes = new[] { ThermostatMode.Off, ThermostatMode.Heat, ThermostatMode.Cool, ThermostatMode.Auto, ThermostatMode.EmergencyHeat };
        var combo = new ComboBox { Width = 130, ItemsSource = modes.Select(m => m == ThermostatMode.EmergencyHeat ? "Emergency heat" : m.ToString()).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(modes, mode)) };
        combo.DropDownOpened += (_, _) => _interacting++;
        combo.DropDownClosed += (_, _) => _interacting--;
        combo.SelectionChanged += async (_, _) =>
        {
            var m = modes[combo.SelectedIndex];
            if (m == mode) return;
            await Do(() => _client.SetThermostatAsync(pid, d.PartitionId, d.Id, mode: (int)m), $"Setting {d.Name} to {m}");
        };
        modeRow.Children.Add(combo);

        var fanOn = d.FanMode is > 0;
        var fan = MakeBtn(fanOn ? "Fan: On" : "Fan: Auto",
            () => Do(() => _client.SetThermostatAsync(pid, d.PartitionId, d.Id, fan: fanOn ? 0 : 1), $"Setting {d.Name} fan"), "GhostButton");
        modeRow.Children.Add(fan);
        panel.Children.Add(modeRow);

        // set points
        if (mode is ThermostatMode.Heat or ThermostatMode.Auto or ThermostatMode.EmergencyHeat)
            panel.Children.Add(SetpointRow(d, "Heat to", d.HeatSetpoint, "Heat", heat: true));
        if (mode is ThermostatMode.Cool or ThermostatMode.Auto)
            panel.Children.Add(SetpointRow(d, "Cool to", d.CoolSetpoint, "Cool", heat: false));
        if (d.Raw.ValueKind == JsonValueKind.Object && d.Raw.TryGetProperty("scht", out _))
            actions.Children.Add(MakeBtn("Comfort settings", () => ComfortSettingsAsync(d), "GhostButton"));
    }

    private FrameworkElement SetpointRow(Device d, string label, double? currentC, string color, bool heat)
    {
        var pid = _state!.PanelId;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 54, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Muted") });

        double? value = currentC is { } c ? ToDisplay(c) : null;
        var text = new TextBlock { Text = value is { } v0 ? $"{v0:0.#}°" : "—", Width = 52, FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = Res(color), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        bool pending = false;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (pending) { pending = false; _interacting--; }
            if (value is not { } v) return;
            var cVal = FromDisplay(v);
            await Do(() => heat
                    ? _client.SetThermostatAsync(pid, d.PartitionId, d.Id, heatC: cVal)
                    : _client.SetThermostatAsync(pid, d.PartitionId, d.Id, coolC: cVal),
                $"Setting {d.Name} {(heat ? "heat" : "cool")} to {v:0.#}°");
        };
        void Step(double delta)
        {
            value = (value ?? ToDisplay(heat ? 20 : 24)) + delta;
            text.Text = $"{value:0.#}°";
            if (!pending) { pending = true; _interacting++; }
            debounce.Stop();
            debounce.Start();
        }
        var step = _fahrenheit ? 1.0 : 0.5;
        var minus = MakeBtn("−", () => { Step(-step); return Task.CompletedTask; });
        var plus = MakeBtn("+", () => { Step(step); return Task.CompletedTask; });
        minus.MinWidth = plus.MinWidth = 34;
        minus.Padding = plus.Padding = new Thickness(8, 2, 8, 2);
        row.Children.Add(minus);
        row.Children.Add(text);
        row.Children.Add(plus);
        return row;
    }


    // ----------------------------------------------------- command helpers
    private Button MakeBtn(string text, Func<Task> onClick, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4) };
        if (style is not null) b.Style = (Style)FindResource(style);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await onClick(); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    /// <summary>Run a device/security command, report the result, then re-poll until the panel catches up.</summary>
    private async Task Do(Func<Task<ApiResult>> call, string busy, bool confirm = false)
    {
        if (confirm && !Dialog.Confirm(this, $"{busy}?", "This is sent to your system right away.", "Yes")) return;
        _busy++;
        SetStatus(busy + "…");
        try
        {
            var res = await call();
            if (res.Ok)
            {
                SetStatus(busy + " — sent.");
            }
            else
            {
                var why = DescribeFailure(res);
                SetStatus($"{busy} failed: {why}", error: true);
                Dialog.Info(this, $"{busy} failed", why, warning: true);
            }
        }
        catch (AuthException) { return; }
        catch (Exception ex)
        {
            SetStatus($"{busy} failed: {ex.Message}", error: true);
            Dialog.Info(this, $"{busy} failed", ex.Message, warning: true);
        }
        finally { _busy--; }

        // Devices report their new state a few seconds after the command.
        _followUp?.Cancel();
        var cts = _followUp = new CancellationTokenSource();
        try
        {
            foreach (var delay in new[] { 1500, 4000, 8000 })
            {
                await Task.Delay(delay, cts.Token);
                if (_interacting == 0) await RefreshAsync(quiet: true);
            }
        }
        catch (TaskCanceledException) { }
    }

    private static string DescribeFailure(ApiResult res)
    {
        string? msg = null;
        if (res.Json is { ValueKind: JsonValueKind.Object } j)
            msg = Json.Str(j, "msg", "message", "error_description", "error", "detail");
        msg ??= Trim(res.RawBody, 300);
        return res.Status switch
        {
            401 or 403 => $"Not allowed (HTTP {res.Status}). {msg}",
            404 => $"Device not found (HTTP 404). {msg}",
            409 => $"The panel rejected the request — check for open sensors or a busy panel (HTTP 409). {msg}",
            _ => $"HTTP {res.Status}. {msg}",
        };
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ===================================================== security
    private Task Arm(Partition p, string label, int code)
    {
        if (_state is null) return Task.CompletedTask;
        if (code != 0 && _state.Devices.Any(d => d.Category == DeviceCategory.Sensor && d.State == true
                                                  && d.EquipmentType != 2 && !d.Bypassed))
        {
            var open = string.Join(", ", _state.Devices
                .Where(d => d.Category == DeviceCategory.Sensor && d.State == true && d.EquipmentType != 2 && !d.Bypassed)
                .Select(d => d.Name).Take(6));
            if (!Dialog.Confirm(this, $"{label} with sensors open?", $"These are open: {open}. They'll be bypassed while armed.",
                    label, danger: true))
                return Task.CompletedTask;
            return Do(() => _client.SetArmedStateAsync(_state.PanelId, p.Id, code, forceArm: true), label);
        }
        return Do(() => _client.SetArmedStateAsync(_state.PanelId, p.Id, code), label, confirm: true);
    }


    // ===================================================== tabs
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        ShowPage();
        await LoadCurrentTabAsync();
    }

    /// <summary>Show the selected page; the others stay loaded but hidden.</summary>
    private void ShowPage()
    {
        var open = Tabs.SelectedItem != HomeTab && Tabs.SelectedItem != CamerasTab;
        if (!open) StopHistoryPlayback();
        ShowPanelOverlay(open);
        HistoryPage.Visibility = Tabs.SelectedItem == HistoryTab ? Visibility.Visible : Visibility.Collapsed;
        AutomationsPage.Visibility = Tabs.SelectedItem == AutomationsTab ? Visibility.Visible : Visibility.Collapsed;
        PeoplePage.Visibility = Tabs.SelectedItem == PeopleTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = Tabs.SelectedItem == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
        if (Tabs.SelectedItem != HistoryTab) StopHistoryPlayback();
        PageTitle.Text = (Tabs.SelectedItem as TabItem)?.Header as string ?? "";
    }

    private async Task LoadCurrentTabAsync()
    {
        if (Tabs.SelectedItem == HistoryTab && !_historyLoaded) await LoadHistoryAsync();
        else if (Tabs.SelectedItem == AutomationsTab && !_automationsLoaded) await LoadAutomationsAsync();
        else if (Tabs.SelectedItem == SettingsTab && !_settingsLoaded) await LoadSettingsAsync();
        else if (Tabs.SelectedItem == PeopleTab && !_peopleLoaded) LoadPeopleTab();
    }

    // ===================================================== cameras (live grid)
    private string _tilesKey = "";   // which system / camera set / quality the tiles were built for

    private List<Device> Cameras() => _state?.Devices.Where(d => d.Category == DeviceCategory.Camera)
        .OrderBy(d => d.Name).ToList() ?? new();

    /// <summary>Start loading everything live video needs, in the background, once a system is loaded.</summary>
    private void PrewarmVideo()
    {
        if (_state is null) return;
        var cams = Cameras();
        if (cams.Count == 0) return;
        _ = VideoEngine.PrepareAsync(_svc, _state.PanelId, cams).ContinueWith(t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Show the live grid: reuse existing players, only (re)connect what isn't streaming.</summary>
    private async Task ShowCameraTilesAsync()
    {
        if (_state is null) return;
        var cams = Cameras();
        var key = $"{_state.PanelId}|{_tilesHd}|{string.Join(",", cams.Select(c => c.Id))}";
        if (key != _tilesKey || _tiles.Count == 0)
        {
            await BuildCameraTilesAsync(cams, key);
            return;
        }
        var resumed = 0;
        foreach (var t in _tiles.Where(t => !t.IsStreaming))
        {
            resumed++;
            _ = t.ResumeAsync();
        }
        SetStatus(resumed == 0 ? $"Streaming {cams.Count} camera{S(cams.Count)}." : $"Reconnecting {resumed} camera{S(resumed)}…");
    }

    private async Task BuildCameraTilesAsync(List<Device> cams, string key)
    {
        DisposeCameraTiles();
        _tilesKey = key;
        CameraGrid.RowDefinitions.Clear();
        CameraGrid.ColumnDefinitions.Clear();
        if (cams.Count == 0)
        {
            CameraGrid.Children.Add(Muted("There are no cameras on this system.", 14));
            return;
        }
        // The video engine loads ~280 plugins; never do that on the UI thread.
        if (!VideoEngine.IsCreated)
        {
            CameraGrid.Children.Add(Muted("Starting video…", 14));
            await VideoEngine.WarmUpAsync();
            CameraGrid.Children.Clear();
            if (_tilesKey != key) return;   // superseded while warming up
        }

        int cols = cams.Count == 1 ? 1 : cams.Count <= 4 ? 2 : 3;
        int rows = (cams.Count + cols - 1) / cols;
        for (int i = 0; i < cols; i++) CameraGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows; i++) CameraGrid.RowDefinitions.Add(new RowDefinition());

        var pid = _state!.PanelId;
        var hd = _tilesHd;
        for (int i = 0; i < cams.Count; i++)
        {
            var cam = cams[i];
            var surface = new VideoSurface();
            surface.SetTitle(cam.Name);
            if (_thumbs.TryGetValue(cam.Id, out var th) && th.image is not null) surface.SetPoster(th.image);
            surface.Clicked += (_, _) => OpenCamera(FindDevice(cam.Id) ?? cam);
            var frame = new Border
            {
                Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8), ClipToBounds = true,
                Background = Brushes.Black, Child = surface,
            };
            Grid.SetColumn(frame, i % cols);
            Grid.SetRow(frame, i / cols);
            CameraGrid.Children.Add(frame);
            _tiles.Add(surface);
            if (cam.Privacy) surface.ShowMessage("Privacy mode is on.");
            else if (cam.Online == false) surface.ShowMessage("Camera offline.");
            else _ = surface.PlayLiveAsync(() => VideoEngine.LiveUriAsync(_svc, pid, FindDevice(cam.Id) ?? cam, hd));
        }
        SetStatus($"Streaming {cams.Count} camera{S(cams.Count)} ({(hd ? "HD" : "SD")}).");
    }

    /// <summary>Stop streaming but keep the players and their video windows for a fast resume.</summary>
    private void PauseCameraTiles()
    {
        foreach (var t in _tiles) t.Stop();
    }

    private void DisposeCameraTiles()
    {
        foreach (var t in _tiles) t.Dispose();
        _tiles.Clear();
        _tilesKey = "";
        CameraGrid.Children.Clear();
    }

    private async void CamQuality_Click(object sender, RoutedEventArgs e)
    {
        _tilesHd = !_tilesHd;
        CamQualityBtn.Content = _tilesHd ? "Quality: HD" : "Quality: SD";
        await ShowCameraTilesAsync();
    }

    private async void CamReconnect_Click(object sender, RoutedEventArgs e)
    {
        DisposeCameraTiles();
        await ShowCameraTilesAsync();
    }

    private static string Pretty(string s)
    {
        s = s.Replace('_', ' ').Replace('-', ' ');
        return s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    }

    // ===================================================== shortcuts & automations
    private async Task LoadRoutinesAsync()
    {
        if (Selected is not { } s) return;
        try
        {
            _routines = await _svc.GetRoutinesAsync(s.PanelId);
            RenderShortcutBar();
        }
        catch (AuthException) { }
        catch { /* shortcuts are optional */ }
    }

    private Task RunShortcutAsync(Routine r)
    {
        if (_state is null) return Task.CompletedTask;
        var pid = _state.PanelId;
        return Do(() => _svc.ExecuteRoutineAsync(pid, r.Id), $"Running “{r.Name}”");
    }

    private static async Task<T?> Safe<T>(Func<Task<T>> f)
    {
        try { return await f(); }
        catch (AuthException) { throw; }
        catch { return default; }
    }

    private static JsonElement? FindPanelDevice(JsonElement root)
    {
        var sys = root.TryGetProperty("system", out var s) ? s : root;
        if (!sys.TryGetProperty("par", out var par) || par.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in par.EnumerateArray())
            if (p.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var dev in d.EnumerateArray())
                    if (Json.Str(dev, "t") == "primary_touch_link_device") return dev;
        return null;
    }

    private FrameworkElement KeyValue(string k, string v)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var key = new TextBlock { Text = k, Width = 140, Foreground = Res("Muted") };
        DockPanel.SetDock(key, Dock.Left);
        dp.Children.Add(key);
        dp.Children.Add(new TextBlock { Text = v, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
        return dp;
    }

    // ===================================================== sign out
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_client.DemoMode)
        {
            if (!Dialog.Confirm(this, "Exit demo?", "You'll go back to the sign-in screen.", "Exit demo")) return;
        }
        else if (!Dialog.Confirm(this, "Sign out?", "You'll need to sign in to Vivint again on this PC.", "Sign out")) return;
        _poll.Stop();
        DisposeCameraTiles();
        await _client.LogoutAsync();
        SignedOut?.Invoke();
    }
}

/// <summary>Tiny persisted preferences (last selected system).</summary>
internal static class Settings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VivintDesktop", "last_panel");

    public static string? LastPanel
    {
        get { try { return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : null; } catch { return null; } }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, value ?? "");
            }
            catch { /* ignore */ }
        }
    }
}
