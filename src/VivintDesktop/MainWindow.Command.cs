using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>
/// The command-center layout: security status band, compact controls rail, live event feed,
/// 24-hour timeline, slide-over panels for the less-used sections, and the Ctrl+K command bar.
/// </summary>
public partial class MainWindow
{
    // ================================================================== status band
    private static string BandColor(ArmedState a) => (a, Theme.IsDark) switch
    {
        (ArmedState.Disarmed, false) => "#2E7D32",
        (ArmedState.Disarmed, true) => "#1F6A35",
        (ArmedState.ArmedStay or ArmedState.ArmedAway, false) => "#B71C1C",
        (ArmedState.ArmedStay or ArmedState.ArmedAway, true) => "#8E1C1C",
        (ArmedState.ArmingAway or ArmedState.ArmingStay, _) => "#C26A00",
        (ArmedState.EntryDelayStay or ArmedState.EntryDelayAway, _) => "#D84E00",
        (ArmedState.Alarm or ArmedState.AlarmFire, _) => "#7F0000",
        _ => Theme.IsDark ? "#3A4250" : "#5F6B7A",
    };

    private void RenderBand(List<string> alerts)
    {
        if (_state is null) return;
        var parts = _state.Partitions.Count > 0 ? _state.Partitions : new List<Partition> { new() { Id = "1" } };
        var p = parts[0];
        var a = p.Armed;
        var color = BandColor(a);
        Band.Background = Hex(color);
        StateTitle.Text = StateText.Describe(a);
        BandIcon.Text = a is ArmedState.Alarm or ArmedState.AlarmFire ? "" : StateText.IsArmed(a) ? "" : "";

        var sub = new List<string> { _state.Name };
        sub.Add(alerts.Count > 0 ? "⚠ " + string.Join(" · ", alerts) : "Everything looks secure");
        foreach (var other in parts.Skip(1))
            sub.Add($"Partition {(other.Name.Length > 0 ? other.Name : other.Id)}: {StateText.Describe(other.Armed)}");
        StateSub.Text = string.Join("   ·   ", sub);

        ArmHost.Children.Clear();
        void Add(string label, int code, ArmedState current)
        {
            var b = MakeBtn(label, () => Arm(p, label, code), "ArmButton");
            if (a == current)
            {
                b.Background = Brushes.White;
                b.Foreground = Hex(color);
                b.IsHitTestVisible = false;
                b.Focusable = false;
            }
            ArmHost.Children.Add(b);
        }
        Add("Disarm", 0, ArmedState.Disarmed);
        if (a is not (ArmedState.Alarm or ArmedState.AlarmFire or ArmedState.EntryDelayAway or ArmedState.EntryDelayStay))
        {
            Add("Arm Stay", 3, ArmedState.ArmedStay);
            Add("Arm Away", 4, ArmedState.ArmedAway);
        }
    }

    // ================================================================== controls rail
    private void RenderControls()
    {
        if (_state is null) return;
        HomeContent.Children.Clear();
        var devs = _state.Devices;
        var order = new[] { DeviceCategory.Lock, DeviceCategory.Garage, DeviceCategory.Thermostat, DeviceCategory.Dimmer, DeviceCategory.Switch };
        foreach (var cat in order)
            foreach (var d in devs.Where(x => x.Category == cat).OrderBy(x => x.Name))
                HomeContent.Children.Add(cat == DeviceCategory.Thermostat ? ThermostatBlock(d) : ControlRow(d));

        // sensors: one summary row, with the ones that need attention listed
        var sensors = devs.Where(d => d.Category == DeviceCategory.Sensor).ToList();
        if (sensors.Count > 0)
        {
            var attention = sensors.Where(d => (d.State == true && d.EquipmentType != 2) || d.Bypassed || d.Tampered
                                               || d.Online == false || d.Battery is > 0 and <= 20).ToList();
            var head = RowShell(attention.Count > 0 ? "" : "",
                $"{sensors.Count} sensor{S(sensors.Count)}",
                attention.Count == 0 ? "All closed and online" : $"{attention.Count} need{(attention.Count == 1 ? "s" : "")} attention",
                attention.Count > 0 ? Res("Warn") : Res("Ok"), null);
            HomeContent.Children.Add(head);
            foreach (var d in attention)
            {
                var why = d.State == true ? "Open" : d.Bypassed ? "Bypassed" : d.Tampered ? "Tamper" : d.Online == false ? "Offline" : $"Battery {d.Battery}%";
                var pid = _state.PanelId;
                var bypass = MakeBtn(d.Bypassed ? "Un-bypass" : "Bypass",
                    () => Do(() => _client.SetSensorBypassAsync(pid, d.PartitionId, d.Id, !d.Bypassed),
                        $"{(d.Bypassed ? "Un-bypassing" : "Bypassing")} {d.Name}", confirm: !d.Bypassed), "GhostButton");
                var row = RowShell("", d.Name, why, Res("Warn"), bypass);
                row.Margin = new Thickness(22, 0, 0, 2);
                HomeContent.Children.Add(row);
            }
        }
        if (HomeContent.Children.Count == 0) HomeContent.Children.Add(Muted("No controllable devices on this system."));
    }

    /// <summary>A rail row: icon, name, state line, and an optional action on the right.</summary>
    private FrameworkElement RowShell(string glyph, string name, string state, Brush stateBrush, FrameworkElement? action)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Res("Surface2"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = stateBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        if (glyph.Length == 0) icon.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = state, FontSize = 12, Foreground = stateBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(0, 4, 0, 4), BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private FrameworkElement ControlRow(Device d)
    {
        var pid = _state!.PanelId;
        var extra = d.Battery is { } b && b <= 30 ? $" · battery {b}%" : "";
        switch (d.Category)
        {
            case DeviceCategory.Lock:
                return RowShell(d.State == true ? "" : "", d.Name, (d.State == true ? "Locked" : "Unlocked") + extra,
                    d.State == true ? Res("Ok") : Res("Warn"),
                    d.State == true
                        ? MakeBtn("Unlock", () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, false), $"Unlocking {d.Name}"))
                        : MakeBtn("Lock", () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, true), $"Locking {d.Name}"), "PrimaryButton"));
            case DeviceCategory.Garage:
                return RowShell("", d.Name, d.StateText, d.GarageIsOpen ? Res("Warn") : Res("Ok"),
                    d.GarageIsOpen
                        ? MakeBtn("Close", () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, false), $"Closing {d.Name}", confirm: true), "PrimaryButton")
                        : MakeBtn("Open", () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, true), $"Opening {d.Name}", confirm: true)));
            case DeviceCategory.Dimmer:
            {
                var wrap = new StackPanel();
                var on = (d.Level ?? 0) > 0 || d.State == true;
                wrap.Children.Add(RowShell("", d.Name, on ? $"On · {d.Level ?? 0}%" : "Off", on ? Res("AccentText") : Res("Muted"),
                    MakeBtn(on ? "Off" : "On", () => Do(() => _client.SetDimmerAsync(pid, d.PartitionId, d.Id, on ? 0 : 100), $"Switching {d.Name}"),
                        on ? null : "PrimaryButton")));
                var actions = new WrapPanel { Margin = new Thickness(44, 0, 0, 4) };
                BuildDimmer(d, actions);
                if (actions.Children.Count > 1) actions.Children.RemoveAt(0);   // on/off is already in the row
                wrap.Children.Add(actions);
                return wrap;
            }
            default:
                return RowShell("", d.Name, d.State == true ? "On" : "Off", d.State == true ? Res("AccentText") : Res("Muted"),
                    MakeBtn(d.State == true ? "Turn off" : "Turn on",
                        () => Do(() => _client.SetSwitchAsync(pid, d.PartitionId, d.Id, d.State != true), $"Switching {d.Name}"),
                        d.State == true ? null : "PrimaryButton"));
        }
    }

    private FrameworkElement ThermostatBlock(Device d)
    {
        var panel = new StackPanel();
        var actions = new WrapPanel();
        BuildThermostat(d, panel, actions);
        var top = new DockPanel();
        top.Children.Add(new TextBlock { Text = d.Name, FontWeight = FontWeights.SemiBold, Foreground = Res("Text") });
        var box = new StackPanel();
        box.Children.Add(top);
        box.Children.Add(panel);
        if (actions.Children.Count > 0) box.Children.Add(actions);
        return new Border { Child = box, Padding = new Thickness(0, 8, 0, 8), BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    // ================================================================== live feed
    private List<HistoryEvent> _feed = new();
    private DateTimeOffset? _feedFocus;

    private async Task LoadFeedAsync()
    {
        if (Selected is not { } s) return;
        try
        {
            // "security" covers doors, locks, arming and camera detections; doorbell rings are their own category.
            var sec = _svc.GetHistoryAsync(s.PanelId, 300, "security");
            var bell = Safe(() => _svc.GetHistoryAsync(s.PanelId, 20, "doorbell_rings"));
            await Task.WhenAll(sec, bell);
            var latest = sec.Result.Concat(bell.Result ?? new())
                .GroupBy(e => e.Id).Select(g => g.First())
                .OrderByDescending(e => e.When).ToList();
            NotifyNewEvents(latest);
            _feed = latest;
            RenderFeed();
            RenderTimeline();
        }
        catch (AuthException) { }
        catch { /* the feed is best effort */ }
    }

    private void RenderFeed()
    {
        FeedList.Children.Clear();
        var items = _feed.AsEnumerable();
        if (_feedFocus is { } f)
        {
            items = items.Where(e => e.When is { } w && Math.Abs((w - f).TotalMinutes) <= 30);
            FeedTitle.Text = $"Around {f.ToLocalTime():h:mm tt}";
            FeedAllBtn.Content = "Show latest";
        }
        else
        {
            FeedTitle.Text = "Live feed";
            FeedAllBtn.Content = "All activity";
        }
        var list = items.ToList();
        if (list.Count == 0) { FeedList.Children.Add(Muted(_feedFocus is null ? "No recent events." : "Nothing happened within 30 minutes of that time.")); return; }
        foreach (var e in list.Take(80)) FeedList.Children.Add(FeedRow(e));
    }

    private FrameworkElement FeedRow(HistoryEvent e)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        if (e.ThumbnailUrl is { } t)
        {
            var img = new Image { Width = 72, Height = 42, Stretch = Stretch.UniformToFill, Source = CameraWindow.Thumb(t, 144) };
            var th = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Background = Res("Sunken"), Child = img, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(th, Dock.Left);
            row.Children.Add(th);
        }
        else
        {
            var dot = new Border { Width = 18, Margin = new Thickness(0, 0, 8, 0),
                Child = new Ellipse { Width = 7, Height = 7, Fill = Res("BorderStrong"), HorizontalAlignment = HorizontalAlignment.Center } };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Message.Trim(), Foreground = Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = Ago(e.When) + (e.Device.Length > 0 ? $" · {e.Device}" : "");
        text.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        var b = new Border { Child = row, Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        if (e.HasClip)
        {
            b.Cursor = Cursors.Hand;
            b.ToolTip = "Play clip";
            b.MouseEnter += (_, _) => b.Background = Res("Surface2");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) => PlayHistoryClip(e);
        }
        return b;
    }

    private static string Ago(DateTimeOffset? when)
    {
        if (when is not { } w) return "";
        var d = DateTimeOffset.Now - w;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24 && w.ToLocalTime().Date == DateTime.Today) return w.ToLocalTime().ToString("h:mm tt");
        return w.ToLocalTime().ToString("ddd h:mm tt");
    }

    private void FeedAll_Click(object sender, RoutedEventArgs e)
    {
        if (_feedFocus is not null) { _feedFocus = null; RenderFeed(); return; }
        OpenPanel(HistoryTab);
    }

    // ================================================================== 24 h timeline
    private readonly List<Canvas> _tickLanes = new();

    private Task LoadTimelineAsync() => Task.CompletedTask;   // built from the feed's history (see LoadFeedAsync)

    private static (DateTimeOffset from, DateTimeOffset to) Window24() => (DateTimeOffset.Now.AddHours(-24), DateTimeOffset.Now);

    private static readonly System.Text.RegularExpressions.Regex ChangeRx =
        new(@"^The (?<n>.+?) was (?<s>locked|unlocked|opened|closed)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Turn history into state bars for the last 24 h: before the first change in the window the device
    /// was in the opposite state; with no change at all it was in its current state the whole time.
    /// </summary>
    private void RenderTimeline()
    {
        TimelineHost.Children.Clear();
        _tickLanes.Clear();
        if (_state is null) return;
        var (from, to) = Window24();
        TimelineRange.Text = "click a bar to see what happened then · click a tick to play the clip";
        var events = _feed.Where(e => e.When is { } w && w >= from).OrderBy(e => e.When).ToList();

        List<(DateTimeOffset s, DateTimeOffset e, Brush b, string tip)> Build(List<(DateTimeOffset t, string state)> changes, string current)
        {
            var segs = new List<(DateTimeOffset, DateTimeOffset, Brush, string)>();
            var state = changes.Count > 0 ? Opposite(changes[0].state) : current;
            var t0 = from;
            foreach (var (t, st) in changes)
            {
                segs.Add((t0, t, StateBrush(state), $"{Pretty(state)}  {t0.ToLocalTime():h:mm tt} – {t.ToLocalTime():h:mm tt}"));
                state = st; t0 = t;
            }
            segs.Add((t0, to, StateBrush(state), $"{Pretty(state)}  since {t0.ToLocalTime():h:mm tt}"));
            return segs;
        }

        // security
        var p = _state.Partitions.FirstOrDefault();
        var cur = p?.Armed switch
        {
            ArmedState.ArmedAway => "armed_away", ArmedState.ArmedStay => "armed_stay", ArmedState.Disarmed => "disarmed", _ => "unknown",
        };
        var arm = events.Select(e => (t: e.When!.Value, m: e.Message.ToLowerInvariant()))
            .Where(x => x.m.Contains("disarm") || System.Text.RegularExpressions.Regex.IsMatch(x.m, @"\barmed\b"))
            .Select(x => (x.t, x.m.Contains("disarm") ? "disarmed" : x.m.Contains("away") ? "armed_away" : "armed_stay")).ToList();
        TimelineHost.Children.Add(Lane("Security", Build(arm, cur), null));

        // locks, then door/window sensors that changed in the window
        var parsed = events.Select(e => (e, m: ChangeRx.Match(e.Message.Trim()))).Where(x => x.m.Success).ToList();
        foreach (var d in _state.Devices.Where(d => d.Category == DeviceCategory.Lock).OrderBy(d => d.Name))
        {
            var ch = parsed.Where(x => x.m.Groups["n"].Value.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.e.When!.Value, x.m.Groups["s"].Value.ToLowerInvariant())).ToList();
            TimelineHost.Children.Add(Lane(d.Name, Build(ch, d.State == true ? "locked" : "unlocked"), null));
        }
        var doors = _state.Devices.Where(d => d.Category == DeviceCategory.Sensor && d.EquipmentType != 2)
            .Select(d => (d, ch: parsed.Where(x => x.m.Groups["n"].Value.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.e.When!.Value, x.m.Groups["s"].Value.ToLowerInvariant())).ToList()))
            .Where(x => x.ch.Count > 0).OrderByDescending(x => x.ch.Count).Take(4).ToList();
        foreach (var (d, ch) in doors)
            TimelineHost.Children.Add(Lane(d.Name, Build(ch, d.State == true ? "opened" : "closed"), null));

        var ticks = new Canvas { Height = 16 };
        _tickLanes.Add(ticks);
        TimelineHost.Children.Add(Lane("Camera events", new(), ticks));
        TimelineHost.Children.Add(Axis(from, to));
        Dispatcher.BeginInvoke(RenderTimelineTicks, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string Opposite(string s) => s switch
    {
        "locked" => "unlocked", "unlocked" => "locked", "opened" => "closed", "closed" => "opened",
        "disarmed" => "armed_stay", _ => "disarmed",
    };

    private Brush StateBrush(string state) => state switch
    {
        "armed_stay" or "armed_away" or "armed" => Hex("#C62828"),
        "unlocked" or "open" or "opened" => Hex("#E08A00"),
        "disarmed" or "locked" or "closed" or "off" => Hex(Theme.IsDark ? "#2F8F4E" : "#3C9A5A"),
        _ => Res("Neutral"),
    };

    /// <summary>One timeline row: label + bar. Clicking the bar focuses the feed on that moment.</summary>
    private FrameworkElement Lane(string name, List<(DateTimeOffset s, DateTimeOffset e, Brush b, string tip)> segs, Canvas? overlay)
    {
        var (from, to) = Window24();
        var total = (to - from).TotalSeconds;
        var canvas = overlay ?? new Canvas { Height = 12 };
        var host = new Border { Height = overlay is null ? 12 : 16, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = Res("Surface2"), Cursor = Cursors.Hand, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        void Layout()
        {
            if (overlay is not null) { RenderTimelineTicks(); return; }
            canvas.Children.Clear();
            var w = host.ActualWidth;
            // Draw resting states first so short unusual ones (unlocked, opened) stay visible on top.
            foreach (var (s, e, b, tip) in segs.OrderBy(x => (x.e - x.s).TotalSeconds < 3600 ? 1 : 0))
            {
                var x0 = Math.Max(0, (s - from).TotalSeconds / total * w);
                var x1 = Math.Min(w, (e - from).TotalSeconds / total * w);
                if (x1 <= x0) continue;
                var r = new Rectangle { Width = Math.Max(1.5, x1 - x0), Height = 12, Fill = b, ToolTip = tip };
                Canvas.SetLeft(r, x0);
                canvas.Children.Add(r);
            }
        }
        host.SizeChanged += (_, _) => Layout();
        host.MouseLeftButtonUp += (_, ev) =>
        {
            if (ev.OriginalSource is FrameworkElement { Tag: HistoryEvent }) return;   // a tick handles itself
            var x = ev.GetPosition(host).X;
            _feedFocus = from.AddSeconds(total * Math.Clamp(x / Math.Max(1, host.ActualWidth), 0, 1));
            RenderFeed();
        };

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var label = new TextBlock { Text = name, Width = 130, FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        row.Children.Add(host);
        return row;
    }

    private FrameworkElement Axis(DateTimeOffset from, DateTimeOffset to)
    {
        var canvas = new Canvas { Height = 14 };
        var host = new Border { Child = canvas, Margin = new Thickness(130, 2, 0, 0) };
        host.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            var w = host.ActualWidth;
            var total = (to - from).TotalSeconds;
            var t = from.ToLocalTime();
            var first = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset).AddHours(1);
            while (first.Hour % 3 != 0) first = first.AddHours(1);
            for (var h = first; h < to; h = h.AddHours(3))
            {
                var x = (h - from).TotalSeconds / total * w;
                if (x > w - 40) break;
                var tb = new TextBlock { Text = h.ToString("h tt").ToLowerInvariant(), FontSize = 11, Foreground = Res("Muted") };
                Canvas.SetLeft(tb, x - 10);
                canvas.Children.Add(tb);
            }
            var now = new TextBlock { Text = "now", FontSize = 11, Foreground = Res("Muted") };
            Canvas.SetLeft(now, w - 22);
            canvas.Children.Add(now);
        };
        return host;
    }

    /// <summary>Clip events as ticks on the "Camera events" lane; click one to play it.</summary>
    private void RenderTimelineTicks()
    {
        var (from, to) = Window24();
        var total = (to - from).TotalSeconds;
        foreach (var lane in _tickLanes)
        {
            lane.Children.Clear();
            var w = (lane.Parent as FrameworkElement)?.ActualWidth ?? 0;
            if (w <= 0) continue;
            foreach (var e in _feed.Where(e => e.HasClip && e.When is { } t && t >= from))
            {
                var x = (e.When!.Value - from).TotalSeconds / total * w;
                var tick = new Rectangle
                {
                    Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = Res("Accent"), Tag = e, Cursor = Cursors.Hand,
                    ToolTip = $"{e.When.Value.ToLocalTime():h:mm tt} · {e.Message.Trim()} · {e.Device}",
                };
                tick.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; PlayHistoryClip(e); };
                Canvas.SetLeft(tick, Math.Clamp(x - 1.5, 0, w - 3));
                lane.Children.Add(tick);
            }
        }
    }

    // ================================================================== slide-over panels
    private static readonly string[] PanelOrder = { "HistoryTab", "AutomationsTab", "PeopleTab", "SettingsTab" };

    private void BuildPanelButtons()
    {
        PanelButtons.Children.Clear();
        foreach (var name in PanelOrder)
        {
            var tab = (TabItem)FindName(name);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = tab.Tag as string, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            content.Children.Add(new TextBlock { Text = tab.Header as string, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 2, 0) };
            b.SetResourceReference(Button.ForegroundProperty, "Text");
            System.Windows.Automation.AutomationProperties.SetName(b, tab.Header as string ?? name);
            b.Click += (_, _) => OpenPanel(tab);
            PanelButtons.Children.Add(b);
        }
    }

    private void OpenPanel(TabItem tab)
    {
        if (Tabs.SelectedItem == tab && PanelOverlay.Visibility == Visibility.Visible) return;
        Tabs.SelectedItem = tab;   // Tabs_SelectionChanged → ShowPage + load
    }

    private void ClosePanel()
    {
        if (PanelOverlay.Visibility != Visibility.Visible) return;
        Tabs.SelectedItem = HomeTab;
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();
    private void PanelBackdrop_Click(object sender, MouseButtonEventArgs e) => ClosePanel();

    private void ShowPanelOverlay(bool open)
    {
        var was = PanelOverlay.Visibility == Visibility.Visible;
        PanelOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PanelSheet.Width = Math.Max(640, Math.Min(1040, ActualWidth - 160));
        if (open && !was)
            PanelSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(80, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        UpdateAirspace();
    }

    /// <summary>Live video is drawn by native child windows that would cover WPF overlays; hide the wall while one is open.</summary>
    private void UpdateAirspace()
    {
        var covered = PanelOverlay.Visibility == Visibility.Visible || CommandOverlay.Visibility == Visibility.Visible;
        CameraGrid.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        // the History player is a native window too; it would cover the command bar
        HistoryPlayerHost.Visibility = CommandOverlay.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    // ================================================================== command bar (Ctrl+K)
    private sealed record Command(string Title, string Hint, string Glyph, Func<Task> Run, string Keywords = "");
    private List<Command> _commands = new();
    private List<Command> _shown = new();
    private int _cmdIndex;

    private void CommandBtn_Click(object sender, RoutedEventArgs e) => OpenCommandBar();
    private void CommandBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseCommandBar();

    private void OpenCommandBar()
    {
        _commands = BuildCommands();
        CommandBox.Text = "";
        CommandOverlay.Visibility = Visibility.Visible;
        UpdateAirspace();
        FilterCommands();
        CommandBox.Focus();
    }

    private void CloseCommandBar()
    {
        CommandOverlay.Visibility = Visibility.Collapsed;
        UpdateAirspace();
    }

    private List<Command> BuildCommands()
    {
        var list = new List<Command>();
        if (_state is not null)
        {
            var p = _state.Partitions.FirstOrDefault() ?? new Partition { Id = "1" };
            list.Add(new("Disarm", "Security", "", () => Arm(p, "Disarm", 0), "off security"));
            list.Add(new("Arm Stay", "Security", "", () => Arm(p, "Arm Stay", 3), "home night security"));
            list.Add(new("Arm Away", "Security", "", () => Arm(p, "Arm Away", 4), "leave security"));
            var pid = _state.PanelId;
            foreach (var d in _state.Devices.OrderBy(d => d.Name))
                switch (d.Category)
                {
                    case DeviceCategory.Lock:
                        list.Add(new($"Lock {d.Name}", d.State == true ? "Already locked" : "Lock", "",
                            () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, true), $"Locking {d.Name}"), "door"));
                        list.Add(new($"Unlock {d.Name}", d.State == false ? "Already unlocked" : "Lock", "",
                            () => Do(() => _client.SetLockAsync(pid, d.PartitionId, d.Id, false), $"Unlocking {d.Name}"), "door open"));
                        break;
                    case DeviceCategory.Garage:
                        list.Add(new($"Close {d.Name}", "Garage", "",
                            () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, false), $"Closing {d.Name}", confirm: true), "garage door"));
                        list.Add(new($"Open {d.Name}", "Garage", "",
                            () => Do(() => _client.SetGarageAsync(pid, d.PartitionId, d.Id, true), $"Opening {d.Name}", confirm: true), "garage door"));
                        break;
                    case DeviceCategory.Switch or DeviceCategory.Dimmer:
                        list.Add(new($"Turn on {d.Name}", "Light / switch", "",
                            () => Do(() => d.Category == DeviceCategory.Dimmer ? _client.SetDimmerAsync(pid, d.PartitionId, d.Id, 100)
                                : _client.SetSwitchAsync(pid, d.PartitionId, d.Id, true), $"Turning on {d.Name}"), "light"));
                        list.Add(new($"Turn off {d.Name}", "Light / switch", "",
                            () => Do(() => d.Category == DeviceCategory.Dimmer ? _client.SetDimmerAsync(pid, d.PartitionId, d.Id, 0)
                                : _client.SetSwitchAsync(pid, d.PartitionId, d.Id, false), $"Turning off {d.Name}"), "light"));
                        break;
                    case DeviceCategory.Camera:
                        var cam = d;
                        list.Add(new($"Watch {d.Name}", "Open live view", "", () => { OpenCamera(cam); return Task.CompletedTask; }, "camera live video"));
                        list.Add(new($"{d.Name} clips", "Recorded clips", "", () => ShowCameraClipsAsync(cam), "camera recordings video"));
                        break;
                }
        }
        foreach (var r in _routines.Where(r => r.IsShortcut && r.Enabled))
            list.Add(new($"Run {r.Name}", "Shortcut", "", () => RunShortcutAsync(r), "shortcut scene"));
        foreach (var name in PanelOrder)
        {
            var tab = (TabItem)FindName(name);
            list.Add(new($"Open {tab.Header}", "Section", tab.Tag as string ?? "", () => { OpenPanel(tab); return Task.CompletedTask; }, "go show"));
        }
        list.Add(new("Switch to light theme", "Appearance", "", () => { Theme.Set(ThemeChoice.Light); return Task.CompletedTask; }, "theme mode"));
        list.Add(new("Switch to dark theme", "Appearance", "", () => { Theme.Set(ThemeChoice.Dark); return Task.CompletedTask; }, "theme mode night"));
        list.Add(new("Match Windows theme", "Appearance", "", () => { Theme.Set(ThemeChoice.System); return Task.CompletedTask; }, "theme mode system"));
        list.Add(new("Refresh", "Reload everything", "", async () => { await RefreshAsync(false); await LoadFeedAsync(); await LoadTimelineAsync(); }, "reload update"));
        return list;
    }

    private void CommandBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CommandHint.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterCommands();
    }

    private void FilterCommands()
    {
        var q = CommandBox.Text.Trim();
        var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _shown = _commands
            .Select(c => (c, score: Score(c, words)))
            .Where(x => x.score > 0 || words.Length == 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.c).Take(8).ToList();

        // Free text → smart video search, always offered when something is typed.
        if (q.Length > 2)
            _shown.Add(new($"Search video for “{q}”", "Clips & search", "", () => SearchVideoAsync(q)));

        _cmdIndex = 0;
        RenderCommands();
    }

    private static int Score(Command c, string[] words)
    {
        if (words.Length == 0) return 1;
        var hay = (c.Title + " " + c.Hint + " " + c.Keywords).ToLowerInvariant();
        var score = 0;
        foreach (var w in words)
        {
            if (!hay.Contains(w)) return 0;
            score += c.Title.ToLowerInvariant().Contains(w) ? 3 : 1;
        }
        if (c.Title.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    private void RenderCommands()
    {
        CommandList.Children.Clear();
        for (int i = 0; i < _shown.Count; i++)
        {
            var c = _shown[i];
            var sel = i == _cmdIndex;
            var row = new DockPanel();
            var hint = new TextBlock { Text = c.Hint, FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(hint, Dock.Right);
            row.Children.Add(hint);
            var icon = new TextBlock { Text = c.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Width = 28,
                Foreground = sel ? Res("AccentText") : Res("Muted"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = c.Title, FontSize = 14, Foreground = sel ? Res("AccentText") : Res("Text"), VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { Child = row, Padding = new Thickness(10, 8, 12, 8), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                Background = sel ? Res("AccentSoft") : Brushes.Transparent };
            var index = i;
            b.MouseEnter += (_, _) => { if (_cmdIndex != index) { _cmdIndex = index; RenderCommands(); } };
            b.MouseLeftButtonUp += async (_, _) => await RunCommandAsync(c);
            CommandList.Children.Add(b);
        }
        if (_shown.Count == 0)
            CommandList.Children.Add(Muted("No matching commands."));
    }

    private async void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _cmdIndex = Math.Min(_cmdIndex + 1, Math.Max(0, _shown.Count - 1)); RenderCommands(); e.Handled = true; break;
            case Key.Up: _cmdIndex = Math.Max(_cmdIndex - 1, 0); RenderCommands(); e.Handled = true; break;
            case Key.Escape: CloseCommandBar(); e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (_cmdIndex < _shown.Count) await RunCommandAsync(_shown[_cmdIndex]);
                break;
        }
    }

    private async Task RunCommandAsync(Command c)
    {
        CloseCommandBar();
        try { await c.Run(); }
        catch (AuthException) { }
        catch (Exception ex) { SetStatus($"{c.Title} failed: {ex.Message}", error: true); }
    }

    // ================================================================== keyboard
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (CommandOverlay.Visibility == Visibility.Visible) CloseCommandBar(); else OpenCommandBar();
            e.Handled = true;
            return;
        }
        if (PanelOverlay.Visibility == Visibility.Visible && CommandOverlay.Visibility != Visibility.Visible
            && Tabs.SelectedItem == HistoryTab && e.Key is Key.Up or Key.Down or Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            HistoryKey(e);
            if (e.Handled) return;
        }
        if (e.Key == Key.Escape && CommandOverlay.Visibility != Visibility.Visible && PanelOverlay.Visibility == Visibility.Visible)
        {
            ClosePanel();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
