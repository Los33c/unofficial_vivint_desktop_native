using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>
/// Settings panel (replaces the System and Account pages): a section index on the left,
/// one scrolling column of sections on the right. Changes save as you make them.
/// </summary>
public partial class MainWindow
{
    private bool _settingsLoaded;
    private readonly List<(string title, FrameworkElement anchor, Button link)> _settingsSections = new();

    private async Task LoadSettingsAsync()
    {
        if (Selected is not { } s) return;
        var pid = s.PanelId;
        SettingsContent.Children.Clear();
        SettingsIndex.Children.Clear();
        _settingsSections.Clear();
        SettingsContent.Children.Add(Muted("Loading…"));
        try
        {
            var updateT = Safe(() => _svc.GetPanelUpdateAsync(pid));
            var healthT = Safe(() => _svc.GetHealthProblemsAsync(pid));
            var insightsT = Safe(() => _svc.GetInsightsAsync(pid));
            var sensorsT = Safe(() => _svc.GetSensorsAsync(pid));
            var awayT = Safe(() => _svc.GetAwayLightsAsync(pid));
            var userT = Safe(() => _client.AuthUserAsync());
            var integT = Safe(() => _svc.GetIntegrationsAsync(pid));
            var svcNum = _systemJson is { } sj && sj.TryGetProperty("system", out var sys0) ? Json.Str(sys0, "svnum", "arid") : null;
            var frameT = svcNum is null ? Task.FromResult<(bool, JsonElement?)>((false, null)) : Safe(() => _svc.GetPhotoFrameAsync(svcNum));
            await Task.WhenAll(updateT, healthT, insightsT, sensorsT, awayT, userT, integT, frameT);
            _settingsLoaded = true;
            SettingsContent.Children.Clear();

            var panelDev = _systemJson is { } j1 ? FindPanelDevice(j1) : null;
            var sys = _systemJson is { } j2 && j2.TryGetProperty("system", out var sy) ? sy : default;

            // ---------------- overview
            var rows = new List<UIElement>();
            UIElement Kv(string k, string v, Brush? b = null) => ListRow(null, k, null, new TextBlock { Text = v, Foreground = b ?? Res("Text") });
            rows.Add(Kv("Panel", panelDev is { } pd && Json.TryBoolish(pd, "ol", out var ol) && !ol ? "Offline" : "Online",
                panelDev is { } pd0 && Json.TryBoolish(pd0, "ol", out var ol0) && !ol0 ? Res("Danger") : Res("Ok")));
            if (sys.ValueKind == JsonValueKind.Object && sys.TryGetProperty("sinfo", out var si) && si.ValueKind == JsonValueKind.Object
                && si.TryGetProperty("srv", out var srv))
                rows.Add(Kv("Monitoring", Pretty(Json.Str(srv, "lvl") ?? "")));
            if (sys.ValueKind == JsonValueKind.Object && Json.Str(sys, "wisd") is { } wifi) rows.Add(Kv("Wi-Fi network", wifi));
            if (panelDev is { } pd2 && Json.Str(pd2, "cell_signal_strength") is { } cs) rows.Add(Kv("Cell signal", $"{cs} dBm"));
            var upd = updateT.Result;
            if (upd is not null)
                rows.Add(ListRow(null, "Firmware", upd.Available ? $"Update available: {upd.AvailableVersion}" : null,
                    upd.Available
                        ? MakeBtn("Install update", () => Do(() => _svc.InstallPanelUpdateAsync(pid), "Installing the panel update", confirm: true), "PrimaryButton")
                        : new TextBlock { Text = upd.Current, Foreground = Res("Text") }));
            rows.Add(ListRow(null, "Panel ID", null, new TextBlock { Text = pid, Foreground = Res("Muted") }));
            var problems = healthT.Result ?? new();
            rows.Add(ListRow(IconTile(problems.Count == 0 ? "" : "", problems.Count == 0 ? Res("Ok") : Res("Warn")),
                problems.Count == 0 ? "No problems detected" : $"{problems.Count} problem{S(problems.Count)} detected",
                problems.Count == 0 ? null : string.Join("\n", problems), null, subBrush: Res("Warn")));
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            actions.Children.Add(MakeBtn("Reboot panel", async () =>
            {
                if (!Dialog.Confirm(this, "Reboot the panel?", "It will be offline — unable to arm or report alarms — for a few minutes.", "Reboot", danger: true)) return;
                await Do(() => _svc.RebootPanelAsync(pid), "Rebooting the panel");
            }));
            actions.Children.Add(MakeBtn("Clear panel alerts", () => Do(() => _svc.AcknowledgePanelAlertsAsync(pid), "Clearing panel alerts"), "GhostButton"));
            AddSection("Overview", s.Name, ListCard(rows), actions);

            // ---------------- sound & screen
            if (panelDev is { } pdev) AddSection("Sound & screen", "Changes save when you let go of a slider.", SoundCard(pid, pdev));

            // ---------------- away lights
            AddSection("Away Lights", "Turns lights on and off while you're away so the house looks lived in.", AwayLightsCard(pid, awayT.Result));

            // ---------------- maintenance
            AddSection("Maintenance mode", "Turns off alarm monitoring for a while, e.g. while a technician works on sensors.", MaintenanceCard(pid));

            // ---------------- sensors
            var sensors = sensorsT.Result ?? new();
            if (sensors.Count > 0)
            {
                var sRows = sensors.OrderBy(x => x.Name).Select(sn =>
                {
                    var bits = new List<string> { sn.Category, sn.Online ? "Online" : "Offline" };
                    if (sn.Battery is { } b) bits.Add($"Battery {b}%");
                    if (sn.Firmware.Length > 0) bits.Add($"FW {sn.Firmware}");
                    var info = sn;
                    UIElement? trail = sn.Id.Length > 0 ? MoreMenu(("Rename and options…", () => SensorSettingsAsync(info), false)) : null;
                    return (UIElement)ListRow(IconTile(sn.Online ? "" : "", sn.Online ? Res("Muted") : Res("Warn")), sn.Name,
                        string.Join(" · ", bits), trail, sn.Id.Length > 0 ? () => _ = SensorSettingsAsync(info) : null,
                        subBrush: !sn.Online || sn.Battery is <= 20 ? Res("Warn") : null);
                });
                AddSection("Sensors", null, ListCard(sRows));
            }

            // ---------------- cameras (settings are fetched when one is opened)
            var cameras = _state?.Devices.Where(d => d.Category == DeviceCategory.Camera).OrderBy(d => d.Name).ToList() ?? new();
            if (cameras.Count > 0)
            {
                var cRows = cameras.Select(cam => (UIElement)ListRow(null, cam.Name,
                    cam.Online == false ? "Offline" : "Online",
                    MoreMenu(("Camera settings…", () => CameraSettingsAsync(cam), false),
                             ("Restart camera…", () => RestartCameraAsync(cam), true)),
                    () => _ = CameraSettingsAsync(cam),
                    subBrush: cam.Online == false ? Res("Warn") : null));
                AddSection("Cameras", "Video quality, night vision, recording and more for each camera.", ListCard(cRows));
            }

            // ---------------- insights
            var insights = insightsT.Result ?? new();
            if (insights.Count > 0)
            {
                var wrap = new WrapPanel();
                foreach (var i in insights)
                {
                    var p = new StackPanel();
                    p.Children.Add(new TextBlock { Text = i.Title, Foreground = Res("Muted"), FontSize = 12 });
                    p.Children.Add(new TextBlock { Text = i.Value, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Res("Text") });
                    p.Children.Add(new TextBlock { Text = i.Metric, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
                    wrap.Children.Add(new Border { Style = (Style)FindResource("Card"), Width = 178, Child = p, Padding = new Thickness(14, 10, 14, 10) });
                }
                AddSection("Weekly insights", insights[0].Period, wrap);
            }

            // ---------------- account security
            var u = userT.Result is { Ok: true, Json: { } uj } && uj.TryGetProperty("u", out var ux) ? ux : default;
            var mfa = u.ValueKind == JsonValueKind.Object && Json.TryBoolish(u, "mfa_enabled", out var m) && m;
            AddSection("Account security", null, ListCard(new UIElement[]
            {
                ListRow(IconTile("", mfa ? Res("Ok") : Res("Warn")), "Two-step verification",
                    mfa ? "On — sign-in asks for a code from your authenticator app." : "Off — anyone with your password can sign in.",
                    mfa ? MakeBtn("Turn off", DisableMfaAsync, "GhostButton") : MakeBtn("Set up", SetupMfaAsync, "PrimaryButton"),
                    subBrush: mfa ? null : Res("Warn")),
            }));

            // ---------------- connected services
            var integ = integT.Result ?? new();
            AddSection("Connected services", "Link services from the Vivint phone app; you can disconnect them here.",
                ListCard(integ.Select(i => (UIElement)ListRow(null, i.Name, i.Status,
                    i.Connected && i.DisconnectPath is not null
                        ? MoreMenu(("Disconnect", async () =>
                        {
                            if (!Dialog.Confirm(this, $"Disconnect {i.Name}?", "You'd need the Vivint phone app to link it again.", "Disconnect", danger: true)) return;
                            await Do(() => _svc.DisconnectIntegrationAsync(i), $"Disconnecting {i.Name}");
                            await LoadSettingsAsync();
                        }, true))
                        : null,
                    subBrush: i.Connected ? Res("Ok") : null))));

            // ---------------- photo frame
            AddSection("Panel photo frame", "Show your own photos on the panel's screen when it's idle.",
                svcNum is null ? Muted("Not available for this system.") : await PhotoFrameCardAsync(svcNum, frameT.Result));

            // ---------------- appearance
            AddSection("Appearance", null, Segmented("theme", Theme.Choice switch { ThemeChoice.Light => 0, ThemeChoice.Dark => 1, _ => 2 },
                ("Light", () => Theme.Set(ThemeChoice.Light)),
                ("Dark", () => Theme.Set(ThemeChoice.Dark)),
                ("Match Windows", () => Theme.Set(ThemeChoice.System))));

            // ---------------- this PC
            UIElement Pref(string title, string? sub, bool value, Action<bool> set) =>
                ListRow(null, title, sub, Switch(value, v => { set(v); return Task.CompletedTask; }));
            AddSection("Notifications & this PC", null, ListCard(new[]
            {
                Pref("Real-time updates", "Instant updates instead of checking every 15 seconds.", Prefs.RealTime, v =>
                {
                    Prefs.RealTime = v;
                    if (!v) { _realtime?.Dispose(); _realtime = null; LiveBadge.Visibility = Visibility.Collapsed; _poll.Interval = TimeSpan.FromSeconds(15); }
                    else if (userT.Result is { Ok: true, Json: { } aj }) StartRealtime(aj);
                }),
                Pref("Minimize to the system tray", null, Prefs.MinimizeToTray, v => Prefs.MinimizeToTray = v),
                Pref("Desktop notifications", "Master switch for everything below.", Prefs.Notifications, v => Prefs.Notifications = v),
                Pref("Arming, disarming and alarms", null, Prefs.NotifySecurity, v => Prefs.NotifySecurity = v),
                Pref("Locks", null, Prefs.NotifyLocks, v => Prefs.NotifyLocks = v),
                Pref("Garage doors", null, Prefs.NotifyGarage, v => Prefs.NotifyGarage = v),
                Pref("Doors and windows opening", null, Prefs.NotifyDoors, v => Prefs.NotifyDoors = v),
                Pref("Motion sensors", null, Prefs.NotifyMotion, v => Prefs.NotifyMotion = v),
                Pref("Doorbell", null, Prefs.NotifyDoorbell, v => Prefs.NotifyDoorbell = v),
                Pref("Every new camera clip", null, Prefs.NotifyCameraEvents, v => Prefs.NotifyCameraEvents = v),
                ListRow(null, "Test notification", null, MakeBtn("Send", () => { Toast("Vivint Desktop", "Notifications are working."); return Task.CompletedTask; })),
            }));

            SettingsScroll.ScrollChanged -= SettingsScrolled;
            SettingsScroll.ScrollChanged += SettingsScrolled;
            HighlightSection(0);
            SetStatus("Settings loaded.");
        }
        catch (AuthException) { }
        catch (Exception ex)
        {
            SettingsContent.Children.Clear();
            SettingsContent.Children.Add(Muted("Could not load settings: " + ex.Message));
        }
    }

    private void AddSection(string title, string? note, params FrameworkElement[] content)
    {
        var header = new StackPanel { Margin = new Thickness(0, _settingsSections.Count == 0 ? 6 : 22, 0, 0) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), Margin = new Thickness(2, 0, 0, note is null ? 10 : 2) });
        if (note is not null) header.Children.Add(SectionNote(note));
        SettingsContent.Children.Add(header);
        foreach (var c in content) SettingsContent.Children.Add(c);

        var link = new Button { Content = title, Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 7, 12, 7) };
        link.SetResourceReference(Button.ForegroundProperty, "Muted");
        var index = _settingsSections.Count;
        link.Click += (_, _) =>
        {
            var y = header.TransformToAncestor(SettingsContent).Transform(new Point(0, 0)).Y;
            SettingsScroll.ScrollToVerticalOffset(y);
            HighlightSection(index);
        };
        SettingsIndex.Children.Add(link);
        _settingsSections.Add((title, header, link));
    }

    private void SettingsScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_settingsSections.Count == 0 || e.VerticalChange == 0) return;
        var current = 0;
        for (int i = 0; i < _settingsSections.Count; i++)
        {
            var y = _settingsSections[i].anchor.TransformToAncestor(SettingsContent).Transform(new Point(0, 0)).Y;
            if (y <= SettingsScroll.VerticalOffset + 60) current = i;
        }
        if (SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 2) current = _settingsSections.Count - 1;
        HighlightSection(current);
    }

    private void HighlightSection(int index)
    {
        for (int i = 0; i < _settingsSections.Count; i++)
        {
            var b = _settingsSections[i].link;
            b.SetResourceReference(Button.BackgroundProperty, i == index ? "AccentSoft" : "Transparent");
            if (i != index) b.Background = Brushes.Transparent;
            b.SetResourceReference(Button.ForegroundProperty, i == index ? "AccentText" : "Muted");
            b.FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ================================================================== sections
    /// <summary>Panel volumes and brightness — each slider saves on release.</summary>
    private FrameworkElement SoundCard(string pid, JsonElement panel)
    {
        int Get(string k, int d) => Json.TryInt(panel, k, out var v) ? v : d;
        var rows = new List<UIElement>();
        UIElement SliderRow(string label, string readKey, string writeKey, int max)
        {
            var start = Get(readKey, max);
            var val = new TextBlock { Width = 46, TextAlignment = TextAlignment.Right, Foreground = Res("Muted") };
            string Pct(double v) => $"{v / max * 100:0}%";
            val.Text = Pct(start);
            var sl = new Slider { Minimum = 0, Maximum = max, Value = start, Width = 280, IsSnapToTickEnabled = true, TickFrequency = 1, IsMoveToPointEnabled = true };
            var last = start;
            sl.ValueChanged += (_, _) => val.Text = Pct(sl.Value);
            async Task Commit()
            {
                var v = (int)sl.Value;
                if (v == last) return;
                last = v;
                val.Text = "Saving…";
                var r = await _svc.UpdatePanelSettingsAsync(pid, new Dictionary<string, object> { [writeKey] = v });
                val.Text = r.Ok ? "Saved" : "Failed";
                await Task.Delay(1200);
                val.Text = Pct(sl.Value);
            }
            sl.PreviewMouseLeftButtonUp += async (_, _) => await Commit();
            sl.LostMouseCapture += async (_, _) => await Commit();
            sl.KeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) await Commit(); };
            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            trail.Children.Add(sl);
            trail.Children.Add(val);
            return ListRow(null, label, null, trail);
        }
        rows.Add(SliderRow("Panel volume", "panel_volume_level", "panel_volume_level", 100));
        rows.Add(SliderRow("Beeps & alerts volume", "beeps_and_alerts_volume_level", "panel_beeps_and_alerts_volume_level", 100));
        rows.Add(SliderRow("Doorbell chime volume", "doorbell_volume_level", "doorbell_volume_level", 100));
        if (!Json.TryBoolish(panel, "supports_setting_led_brightness", out var led) || led)
            rows.Add(SliderRow("Screen brightness", "led_brightness", "panel_led_brightness", 255));
        rows.Add(ListRow(null, "Tap sounds", "Play a click when the screen is tapped.",
            Switch(Json.TryBoolish(panel, "play_panel_clicks", out var pc) && pc, async v =>
            {
                var r = await _svc.UpdatePanelSettingsAsync(pid, new Dictionary<string, object> { ["play_panel_clicks"] = v });
                if (!r.Ok) throw new InvalidOperationException();
            })));
        return ListCard(rows);
    }

    private FrameworkElement AwayLightsCard(string pid, AwayLights? a)
    {
        if (a is null) return Muted("Away Lights isn't available on this system.");
        (StackPanel row, Func<AwayTime?> read) TimePick(AwayTime t)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var kind = new ComboBox { Width = 112, ItemsSource = new[] { "Sunset", "Sunrise", "Time" },
                SelectedIndex = t.Type switch { "SUNSET" => 0, "SUNRISE" => 1, _ => 2 } };
            var time = new TextBox { Width = 104, Text = new DateTime(2000, 1, 1, t.Hour, t.Minute, 0).ToString("h:mm tt") };
            time.Visibility = kind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            kind.SelectionChanged += (_, _) => time.Visibility = kind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            row.Children.Add(kind);
            row.Children.Add(time);
            return (row, () => kind.SelectedIndex switch
            {
                0 => new AwayTime("SUNSET", 0, 0),
                1 => new AwayTime("SUNRISE", 0, 0),
                _ => DateTime.TryParse("2000-01-01 " + time.Text.Trim(), out var d) ? new AwayTime("TIME", d.Hour, d.Minute) : null,
            });
        }
        var start = TimePick(a.Start);
        var end = TimePick(a.End);
        var save = MakeBtn("Save times", async () =>
        {
            if (start.read() is not { } s || end.read() is not { } e) { Dialog.Info(this, "Away Lights", "Enter times like 11:00 PM."); return; }
            await Do(() => _svc.SetAwayLightsTimesAsync(pid, s, e), "Saving Away Lights times");
        }, "GhostButton");
        return ListCard(new UIElement[]
        {
            ListRow(IconTile(""), "Away Lights", a.Enabled ? "On" : "Off",
                Switch(a.Enabled, async v =>
                {
                    var r = await _svc.SetAwayLightsEnabledAsync(pid, v);
                    if (!r.Ok) { Dialog.Info(this, "Away Lights", DescribeFailure(r), warning: true); throw new InvalidOperationException(); }
                })),
            ListRow(null, "From", null, start.row),
            ListRow(null, "Until", null, end.row),
            ListRow(null, "", null, save),
        });
    }

    private FrameworkElement MaintenanceCard(string pid)
    {
        var dur = new ComboBox { Width = 130, ItemsSource = new[] { "30 minutes", "1 hour", "2 hours", "4 hours" }, SelectedIndex = 1 };
        var startBtn = MakeBtn("Start", async () =>
        {
            var secs = new[] { 1800, 3600, 7200, 14400 }[dur.SelectedIndex];
            if (!Dialog.Confirm(this, $"Turn off alarm monitoring for {dur.SelectedItem}?",
                    "If there's a break-in or fire during this time, it won't be reported to the monitoring center.", "Start", danger: true)) return;
            await Do(() => _svc.StartMaintenanceAsync(pid, secs), "Starting maintenance mode");
        });
        var trail = new StackPanel { Orientation = Orientation.Horizontal };
        trail.Children.Add(dur);
        trail.Children.Add(startBtn);
        return ListCard(new UIElement[]
        {
            ListRow(IconTile("", Res("Warn")), "Start maintenance mode", "Alarms aren't reported until it ends.", trail),
            ListRow(null, "End maintenance mode", "Turn monitoring back on now.",
                MakeBtn("End now", () => Do(() => _svc.EndMaintenanceAsync(pid), "Ending maintenance mode"), "PrimaryButton")),
        });
    }

    private async Task SensorSettingsAsync(SensorInfo sn)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var f = new Prompt($"Sensor — {sn.Name}")
            .Text("name", "Name", sn.Name, maxLength: 32)
            .Check("chime", "Chime when it opens", sn.Chime)
            .Check("voice", "Announce its name when it opens", sn.Voice);
        if (!f.Ask(this, "Save", v => v["name"].Length == 0 ? "Enter a name." : null)) return;
        var v = f.Values;
        if (v["name"] != sn.Name)
            await Do(() => _svc.RenameSensorAsync(pid, sn.Id, v["name"]), $"Renaming {sn.Name}");
        var chime = v["chime"] == "1";
        var voice = v["voice"] == "1";
        if (chime != sn.Chime || voice != sn.Voice)
            await Do(() => _svc.UpdateSensorAsync(pid, sn.Id, new Dictionary<string, object> { ["chime_enabled"] = chime, ["voice"] = voice }),
                $"Saving {v["name"]} settings");
        await LoadSettingsAsync();
    }

    // ---------------------------------------------------------------- camera settings
    private CameraSettingsService? _cameraSettings;
    private CameraSettingsService CameraSettingsApi => _cameraSettings ??= new CameraSettingsService(_client);

    private async Task CameraSettingsAsync(Device cam)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var api = CameraSettingsApi;

        CameraSettings cur;
        SetStatus($"Loading {cam.Name} settings…");
        try { cur = await api.GetAsync(pid, cam.Id); }
        catch (Exception ex)
        {
            SetStatus($"Couldn't load {cam.Name} settings.", error: true);
            Dialog.Info(this, $"Couldn't load {cam.Name} settings", ex.Message, warning: true);
            return;
        }
        SetStatus("");

        var f = new Prompt($"Camera — {cam.Name}")
            .Choice("quality", "Video quality", CameraSettings.QualityNames, cur.VideoQuality - 1)
            .Check("night", "Night vision", cur.NightVision)
            .Check("led", "Status light", cur.StatusLight)
            .Check("audio", "Record audio", cur.RecordAudio)
            .Check("person", "Record when a person is detected", cur.RecordOnPerson)
            .Check("quiet", "Quiet mode", cur.QuietMode)
            .Note("Not every camera model supports every option.");
        if (!f.Ask(this, "Save")) return;

        var v = f.Values;
        var changes = new List<(string label, Func<Task> send)>();
        var quality = int.Parse(v["quality"]) + 1;
        if (quality > 0 && quality != cur.VideoQuality)
            changes.Add(("video quality", () => api.SetVideoQualityAsync(pid, cam.Id, quality)));
        void Toggle(string key, bool now, string label, Func<string, string, bool, CancellationToken, Task> set)
        {
            var on = v[key] == "1";
            if (on != now) changes.Add((label, () => set(pid, cam.Id, on, default)));
        }
        Toggle("night", cur.NightVision, "night vision", api.SetNightVisionAsync);
        Toggle("led", cur.StatusLight, "status light", api.SetStatusLightAsync);
        Toggle("audio", cur.RecordAudio, "audio recording", api.SetRecordAudioAsync);
        Toggle("person", cur.RecordOnPerson, "person recording", api.SetRecordOnPersonAsync);
        Toggle("quiet", cur.QuietMode, "quiet mode", api.SetQuietModeAsync);
        if (changes.Count == 0) return;

        var saved = 0;
        foreach (var (label, send) in changes)
        {
            SetStatus($"Saving {cam.Name} {label}…");
            try { await send(); saved++; }
            catch (Exception ex)
            {
                SetStatus($"Couldn't save {cam.Name} {label}.", error: true);
                var kept = saved > 0 ? $"\n\nThe {saved} change{S(saved)} before it were saved." : "";
                Dialog.Info(this, $"Couldn't save {label}", ex.Message + kept, warning: true);
                return;
            }
        }
        SetStatus($"{cam.Name} settings saved.");
    }

    private async Task RestartCameraAsync(Device cam)
    {
        if (_state is null) return;
        if (!Dialog.Confirm(this, $"Restart {cam.Name}?", "It won't record or stream until it comes back online.", "Restart", danger: true)) return;
        SetStatus($"Restarting {cam.Name}…");
        try
        {
            await CameraSettingsApi.RebootAsync(_state.PanelId, cam.Id);
            SetStatus($"{cam.Name} is restarting.");
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't restart {cam.Name}.", error: true);
            Dialog.Info(this, $"Couldn't restart {cam.Name}", ex.Message, warning: true);
        }
    }

    // ---------------------------------------------------------------- thermostat comfort targets
    private static readonly (string key, string label)[] ComfortModes =
        { ("home", "Home"), ("away", "Away"), ("sleep", "Sleep"), ("vacation", "Vacation") };

    private async Task ComfortSettingsAsync(Device t)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        double? Cur(string group, string key) => t.Raw.ValueKind == JsonValueKind.Object && t.Raw.TryGetProperty(group, out var g)
            && Json.TryDouble(g, key, out var v) ? v : null;
        var unit = _fahrenheit ? "°F" : "°C";
        var f = new Prompt($"Comfort settings — {t.Name}",
            $"The temperatures Smart Comfort uses for each home state ({unit}). Heat = warm to at least, Cool = cool to at most.");
        foreach (var (key, label) in ComfortModes)
        {
            f.Text("h_" + key, $"{label} — heat to", Cur("scht", key) is { } h ? $"{ToDisplay(h):0.#}" : "");
            f.Text("c_" + key, $"{label} — cool to", Cur("scct", key) is { } c ? $"{ToDisplay(c):0.#}" : "");
        }
        if (!f.Ask(this, "Save", v =>
            {
                foreach (var (key, label) in ComfortModes)
                {
                    if (!double.TryParse(v["h_" + key], out var h) || !double.TryParse(v["c_" + key], out var c))
                        return $"Enter both temperatures for {label}.";
                    if (h >= c) return $"{label}: the heat temperature must be below the cool temperature.";
                }
                return null;
            })) return;
        var heat = ComfortModes.ToDictionary(m => m.key, m => FromDisplay(double.Parse(f.Values["h_" + m.key])));
        var cool = ComfortModes.ToDictionary(m => m.key, m => FromDisplay(double.Parse(f.Values["c_" + m.key])));
        await Do(() => _svc.SetComfortTargetsAsync(pid, t.Id, heat, cool), $"Saving {t.Name} comfort settings");
    }

    // ---------------------------------------------------------------- two-step verification
    private async Task SetupMfaAsync()
    {
        string? url;
        try { url = await _svc.StartMfaSetupAsync(); }
        catch (AuthException) { return; }
        if (url is null) { Dialog.Info(this, "Two-step verification", "Vivint didn't start the setup. Try again later.", warning: true); return; }
        var secret = System.Text.RegularExpressions.Regex.Match(url, "[?&]secret=([^&]+)").Groups[1].Value;
        var f = new Prompt("Set up two-step verification",
                "1. In your authenticator app (Microsoft Authenticator, Google Authenticator, 1Password…), add an account with this key:")
            .Text("key", "Setup key", Uri.UnescapeDataString(secret))
            .Note("2. Enter the 6-digit code the app shows to finish.")
            .Text("code", "Code", maxLength: 8);
        if (!f.Ask(this, "Turn on", v => v["code"].Length < 6 ? "Enter the 6-digit code from your authenticator app." : null))
        {
            try { await _svc.CancelMfaSetupAsync(); } catch { }
            return;
        }
        await Do(() => _svc.EnableMfaAsync(f.Values["code"]), "Turning on two-step verification");
        await LoadSettingsAsync();
    }

    private async Task DisableMfaAsync()
    {
        if (!Dialog.Confirm(this, "Turn off two-step verification?", "Your account will only be protected by your password.", "Turn off", danger: true)) return;
        await Do(() => _svc.DisableMfaAsync(), "Turning off two-step verification");
        await LoadSettingsAsync();
    }

    // ---------------------------------------------------------------- photo frame
    private async Task<FrameworkElement> PhotoFrameCardAsync(string svc, (bool exists, JsonElement? frame) data)
    {
        if (!data.exists)
            return ListCard(new UIElement[]
            {
                ListRow(IconTile(""), "Photo frame is off", "Turn it on to upload photos for the panel's screensaver.",
                    MakeBtn("Turn on", async () =>
                    {
                        await Do(() => _svc.EnablePhotoFrameAsync(svc), "Turning on the photo frame");
                        await LoadSettingsAsync();
                    }, "PrimaryButton")),
            });

        var fj = data.frame!.Value;
        var frame = fj.TryGetProperty("photoframe", out var pf) ? pf : default;
        string? album = frame.ValueKind == JsonValueKind.Object ? Json.Str(frame, "active_album_id") : null;
        if (album is null && fj.TryGetProperty("available_albums", out var aa) && aa.TryGetProperty("user_albums", out var ua)
            && ua.ValueKind == JsonValueKind.Array && ua.GetArrayLength() > 0)
            album = Json.Str(ua[0], "id");
        var count = frame.ValueKind == JsonValueKind.Object && Json.TryInt(frame, "photo_count", out var pc) ? pc : 0;
        var max = frame.ValueKind == JsonValueKind.Object && Json.TryInt(frame, "max_photo_count", out var mc) ? mc : 0;
        var dur = frame.ValueKind == JsonValueKind.Object && Json.TryInt(frame, "photo_duration_seconds", out var ds) ? ds : 10;
        if (album is null) return Muted("No album found.");

        var photos = await Safe(() => _svc.ListFramePhotosAsync(svc, album)) ?? new();
        var grid = new WrapPanel { Margin = new Thickness(14, 10, 14, 4) };
        foreach (var ph in photos)
        {
            var id = Json.Str(ph, "id") ?? "";
            var img = new Image { Width = 112, Height = 74, Stretch = Stretch.UniformToFill };
            if (Json.Str(ph, "presigned_thumbnail_url", "presigned_url") is { } t) img.Source = CameraWindow.Thumb(t, 224);
            var del = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "Remove photo",
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Hex("#99000000"), Foreground = Brushes.White, Padding = new Thickness(5), FontSize = 10 };
            del.Click += async (_, _) =>
            {
                if (!Dialog.Confirm(this, "Remove this photo?", "It will no longer show on the panel.", "Remove", danger: true)) return;
                await Do(() => _svc.DeleteFramePhotoAsync(svc, album, id), "Removing photo");
                await LoadSettingsAsync();
            };
            grid.Children.Add(new Border { Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Child = new Grid { Children = { img, del } } });
        }
        var add = MakeBtn("Add photos…", async () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Photos|*.jpg;*.jpeg;*.png", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames)
            {
                var bytes = await File.ReadAllBytesAsync(f);
                if (bytes.Length > 8_000_000) { Dialog.Info(this, "Photo too large", $"{Path.GetFileName(f)} is over 8 MB."); continue; }
                var mime = f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                await Do(() => _svc.UploadFramePhotoAsync(svc, album, bytes, mime), $"Uploading {Path.GetFileName(f)}");
            }
            await _svc.PublishFrameAsync(svc, album);
            await LoadSettingsAsync();
        }, "PrimaryButton");
        var more = MoreMenu(
            ("Seconds per photo…", async () =>
            {
                var f = new Prompt("Photo frame").Text("s", "Seconds each photo is shown", dur.ToString());
                if (!f.Ask(this, "Save", v => int.TryParse(v["s"], out var n) && n is >= 3 and <= 3600 ? null : "Enter 3–3600 seconds.")) return;
                await Do(() => _svc.SetFrameDurationAsync(svc, int.Parse(f.Values["s"])), "Saving photo frame timing");
                await LoadSettingsAsync();
            }, false),
            ("Send to panel now", () => Do(() => _svc.PublishFrameAsync(svc, album), "Sending photos to the panel"), false));
        var trail = new StackPanel { Orientation = Orientation.Horizontal };
        trail.Children.Add(add);
        trail.Children.Add(more);
        var stack = new List<UIElement>
        {
            ListRow(IconTile(""), $"{count}{(max > 0 ? $" of {max}" : "")} photos", $"Each shown for {dur} seconds", trail),
        };
        if (photos.Count > 0) stack.Add(grid);
        return ListCard(stack);
    }
}
