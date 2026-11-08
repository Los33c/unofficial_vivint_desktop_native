using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VivintDesktop.Api;
using Forms = System.Windows.Forms;

namespace VivintDesktop;

/// <summary>
/// System-tray icon and desktop alerts. Alerts come from comparing each new
/// system snapshot with the previous one (fed by polling and by the real-time
/// channel), so they work whichever update path delivered the change.
/// </summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private Dictionary<string, string> _lastSeen = new();
    private Dictionary<string, ArmedState> _lastArmed = new();
    private bool _haveBaseline;

    private void InitTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "Vivint Desktop",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Shield,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Vivint Desktop", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Disarm", null, (_, _) => Dispatcher.Invoke(() => TrayArm(0, "Disarm")));
        menu.Items.Add("Arm Stay", null, (_, _) => Dispatcher.Invoke(() => TrayArm(3, "Arm Stay")));
        menu.Items.Add("Arm Away", null, (_, _) => Dispatcher.Invoke(() => TrayArm(4, "Arm Away")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        _tray.ContextMenuStrip = menu;

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && Prefs.MinimizeToTray) Hide();
        };
        Closed += (_, _) => { _tray.Visible = false; _tray.Dispose(); _tray = null; };
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void TrayArm(int code, string label)
    {
        if (_state is null) return;
        RestoreFromTray();
        var p = _state.Partitions.FirstOrDefault() ?? new Partition { Id = "1" };
        await Arm(p, label, code);
    }

    private void Toast(string title, string text, bool warning = false)
    {
        if (_tray is null || !Prefs.Notifications) return;
        _tray.ShowBalloonTip(6000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    /// <summary>Compare the new snapshot with the previous one and raise alerts for meaningful changes.</summary>
    private void DetectChanges(SystemState s)
    {
        var seen = new Dictionary<string, string>();
        foreach (var d in s.Devices)
            seen[d.Id] = d.Category switch
            {
                DeviceCategory.Lock => d.State == true ? "locked" : d.State == false ? "unlocked" : "",
                DeviceCategory.Garage => d.Garage.ToString(),
                DeviceCategory.Sensor => d.State == true ? "open" : "closed",
                _ => "",
            };
        var armed = s.Partitions.ToDictionary(p => p.Id, p => p.Armed);

        if (_haveBaseline)
        {
            foreach (var (pid, a) in armed)
                if (_lastArmed.TryGetValue(pid, out var was) && was != a)
                {
                    var alarm = a is ArmedState.Alarm or ArmedState.AlarmFire;
                    if (alarm || Prefs.NotifySecurity)
                        Toast(alarm ? "ALARM" : "Security", $"{s.Name}: {StateText.Describe(a)}", alarm);
                }

            foreach (var d in s.Devices)
            {
                if (!seen.TryGetValue(d.Id, out var now) || now.Length == 0) continue;
                if (!_lastSeen.TryGetValue(d.Id, out var before) || before == now || before.Length == 0) continue;
                switch (d.Category)
                {
                    case DeviceCategory.Lock when Prefs.NotifyLocks:
                        Toast(d.Name, now == "locked" ? "Locked" : "Unlocked", now == "unlocked");
                        break;
                    case DeviceCategory.Garage when Prefs.NotifyGarage
                                                    && d.Garage is GarageState.Open or GarageState.Closed:
                        Toast(d.Name, d.Garage == GarageState.Open ? "Opened" : "Closed", d.Garage == GarageState.Open);
                        break;
                    case DeviceCategory.Sensor when now == "open":
                        var motion = d.EquipmentType == 2;
                        if (motion ? Prefs.NotifyMotion : Prefs.NotifyDoors)
                            Toast(d.Name, motion ? "Motion detected" : $"{d.SensorKind} opened",
                                StateText.IsArmed(s.Partitions.FirstOrDefault()?.Armed ?? ArmedState.Unknown));
                        break;
                }
            }
        }
        _lastSeen = seen;
        _lastArmed = armed;
        _haveBaseline = true;
    }

    // ---------------------------------------------------------------- real time
    private RealtimeChannel? _realtime;
    private CancellationTokenSource? _pushDebounce;
    private string _lastEventId = "";

    private void StartRealtime(JsonElement authUser)
    {
        _realtime?.Dispose();
        _realtime = null;
        if (!Prefs.RealTime || RealtimeChannel.BroadcastChannel(authUser) is not { Length: > 0 } mbc) return;
        _realtime = new RealtimeChannel(_client, mbc);
        _realtime.Message += m => { if (Meaningful(m)) Dispatcher.BeginInvoke(OnPush); };
        _realtime.Connection += on => Dispatcher.BeginInvoke(() =>
        {
            LiveBadge.Visibility = Visibility.Visible;
            LiveText.Text = on ? "● Live" : "○ Reconnecting";
            LiveText.SetResourceReference(TextBlock.ForegroundProperty, on ? "Ok" : "Warn");
            LiveBadge.SetResourceReference(Border.BackgroundProperty, on ? "OkSoft" : "Surface2");
            LiveText.ToolTip = on ? "Real-time updates connected" : "Real-time updates lost — polling every 15 s";
            _poll.Interval = TimeSpan.FromSeconds(on ? 60 : 15);
        });
        _realtime.Start();
        Closed += (_, _) => _realtime?.Dispose();
    }

    // Camera radios and the thermostat report signal/telemetry values every few seconds; those don't need a refresh.
    private static readonly HashSet<string> NoiseKeys = new()
    {
        "_id", "act", "sas", "neas", "ccas", "rssi", "wast", "hmdt", "os", "fs", "ts", "vpnta", "vpntf",
        "vpntlf", "vpntls", "cell_signal_strength", "pnlconn", "ol_updated_time", "plctx", "pnlctx",
    };

    /// <summary>True if a push message carries a change worth refreshing for (not just telemetry).</summary>
    private static bool Meaningful(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return true;
        if (!m.TryGetProperty("da", out var da) || da.ValueKind != JsonValueKind.Object) return true;
        if (!da.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Array)
            return da.EnumerateObject().Any(p => !NoiseKeys.Contains(p.Name));
        foreach (var dev in d.EnumerateArray())
            if (dev.ValueKind == JsonValueKind.Object && dev.EnumerateObject().Any(p => !NoiseKeys.Contains(p.Name)))
                return true;
        return false;
    }

    /// <summary>Something changed on the system: refresh soon (coalescing bursts) and look for new camera events.</summary>
    private async void OnPush()
    {
        _pushDebounce?.Cancel();
        var cts = _pushDebounce = new CancellationTokenSource();
        try { await Task.Delay(400, cts.Token); } catch (TaskCanceledException) { return; }
        if (_busy == 0 && _interacting == 0) await RefreshAsync(quiet: true);
        await LoadFeedAsync();
    }

    /// <summary>Toast camera events (doorbell rings, people, packages) newer than the last batch seen.</summary>
    private void NotifyNewEvents(List<HistoryEvent> latest)
    {
        if (latest.Count == 0) return;
        if (_lastEventId.Length == 0) { _lastEventId = latest[0].Id; return; }
        foreach (var e in latest.TakeWhile(e => e.Id != _lastEventId).Take(10).Reverse())
        {
            var msg = e.Message.Trim();
            var doorbell = msg.Contains("doorbell", StringComparison.OrdinalIgnoreCase)
                           || msg.Contains("rang", StringComparison.OrdinalIgnoreCase);
            if ((doorbell && Prefs.NotifyDoorbell) || (e.HasClip && Prefs.NotifyCameraEvents))
                Toast(e.Device.Length > 0 ? e.Device : "Camera", msg);
        }
        _lastEventId = latest[0].Id;
    }

    private void ResetChangeBaseline()
    {
        _haveBaseline = false;
        _lastEventId = "";
        _lastSeen.Clear();
        _lastArmed.Clear();
    }
}
