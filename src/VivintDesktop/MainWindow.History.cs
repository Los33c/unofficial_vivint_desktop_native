using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VivintDesktop.Api;
using VivintDesktop.Video;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>
/// History panel (replaces the separate Activity and Clips pages): one list of events for a day,
/// filter chips, text + smart video search, and an embedded player — clips play in place.
/// </summary>
public partial class MainWindow
{
    private DateTime _histDay = DateTime.Today;
    private string _histCategory = "";        // "" = everything
    private string _histCamera = "";          // device id, "" = all
    private bool _histClipsOnly;
    private string _histText = "";
    private List<HistoryEvent> _histEvents = new();
    private List<HistoryEvent> _histShown = new();
    private HistoryEvent? _histSelected;
    private List<SearchHit>? _searchHits;
    private SearchHit? _hitSelected;
    private VideoSurface? _player;
    private VideoSearch? _search;
    private bool _historyLoaded;
    private int _histLoadSeq;

    private TextBox _histSearch = new();
    private TextBlock _histDayText = new();
    private Button _histTodayBtn = new();

    private static readonly (string key, string label)[] HistoryCategories =
    {
        ("", "Everything"), ("people", "People"), ("vehicles", "Vehicles"), ("packages", "Packages"),
        ("doorbell_rings", "Doorbell"), ("security", "Doors, locks & security"), ("thermostat", "Thermostat"),
    };

    // ---------------------------------------------------------------- toolbar
    private void BuildHistoryToolbar()
    {
        HistoryToolbar.Children.Clear();

        // search + day navigation
        // fresh controls each build (an element can only have one parent; keeps handlers from stacking up)
        _histSearch = new TextBox { Text = _histText };
        _histDayText = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 96,
            TextAlignment = TextAlignment.Center, Foreground = Res("Text") };
        _histTodayBtn = new Button { Content = "Today", Style = (Style)FindResource("GhostButton") };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        var prev = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "Previous day" };
        var next = new Button { Content = "", Style = (Style)FindResource("IconButton"), ToolTip = "Next day" };
        System.Windows.Automation.AutomationProperties.SetName(prev, "Previous day");
        System.Windows.Automation.AutomationProperties.SetName(next, "Next day");
        prev.Click += async (_, _) => { _histDay = _histDay.AddDays(-1); await LoadHistoryAsync(); };
        next.Click += async (_, _) => { if (_histDay < DateTime.Today) { _histDay = _histDay.AddDays(1); await LoadHistoryAsync(); } };
        _histTodayBtn.Click += async (_, _) => { _histDay = DateTime.Today; await LoadHistoryAsync(); };
        nav.Children.Add(prev);
        nav.Children.Add(_histDayText);
        nav.Children.Add(next);
        nav.Children.Add(_histTodayBtn);
        DockPanel.SetDock(nav, Dock.Right);
        top.Children.Add(nav);

        var hint = new TextBlock { Text = "Filter, or Enter to search video", Foreground = Res("Muted"),
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = _histText.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        _histSearch.TextChanged += (_, _) =>
        {
            hint.Visibility = _histSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _histText = _histSearch.Text.Trim();
            if (_searchHits is null) RenderHistoryList();
        };
        _histSearch.KeyDown += async (_, e) => { if (e.Key == Key.Enter && _histText.Length > 0) await RunSearchAsync(); };
        var searchBox = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        searchBox.Children.Add(_histSearch);
        searchBox.Children.Add(hint);
        top.Children.Add(searchBox);
        HistoryToolbar.Children.Add(top);

        // category chips (single choice, like the API's filter)
        var cats = new WrapPanel();
        var catChips = new List<System.Windows.Controls.Primitives.ToggleButton>();
        foreach (var (key, label) in HistoryCategories)
        {
            var k = key;
            System.Windows.Controls.Primitives.ToggleButton? chip = null;
            chip = Chip(label, _histCategory == key, async on =>
            {
                foreach (var c in catChips) c.IsChecked = c == chip;
                _histCategory = k;
                await LoadHistoryAsync();
            });
            catChips.Add(chip);
            cats.Children.Add(chip);
        }
        HistoryToolbar.Children.Add(cats);

        // camera chips + clips-only
        var cams = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var camChips = new List<System.Windows.Controls.Primitives.ToggleButton>();
        foreach (var (id, name) in new[] { ("", "All cameras") }.Concat(Cameras().Select(c => (c.Id, c.Name))))
        {
            var cid = id;
            System.Windows.Controls.Primitives.ToggleButton? chip = null;
            chip = Chip(name, _histCamera == id, on =>
            {
                foreach (var c in camChips) c.IsChecked = c == chip;
                _histCamera = cid;
                RenderHistoryList();
            });
            camChips.Add(chip);
            cams.Children.Add(chip);
        }
        var clipsOnly = Switch(_histClipsOnly, v => { _histClipsOnly = v; RenderHistoryList(); return Task.CompletedTask; }, "Clips only");
        clipsOnly.Margin = new Thickness(10, 0, 0, 6);
        cams.Children.Add(clipsOnly);
        HistoryToolbar.Children.Add(cams);
    }

    // ---------------------------------------------------------------- data
    private async Task LoadHistoryAsync()
    {
        if (Selected is not { } s) return;
        if (HistoryToolbar.Children.Count == 0) BuildHistoryToolbar();
        _searchHits = null;
        _histDayText.Text = _histDay == DateTime.Today ? "Today" : _histDay == DateTime.Today.AddDays(-1) ? "Yesterday" : _histDay.ToString("ddd, MMM d");
        _histTodayBtn.Visibility = _histDay == DateTime.Today ? Visibility.Collapsed : Visibility.Visible;
        if (_histSelected is null) ShowHistoryDetail(null);
        var seq = ++_histLoadSeq;
        HistoryList.Children.Clear();
        HistoryList.Children.Add(Muted("  Loading…"));
        SetStatus("Loading history…");
        try
        {
            var from = new DateTimeOffset(_histDay);
            var to = from.AddDays(1);
            var list = await _svc.GetHistoryRangeAsync(s.PanelId, from, to, 1000, _histCategory);
            if (seq != _histLoadSeq) return;
            _histEvents = list.OrderByDescending(e => e.When).ToList();
            _historyLoaded = true;
            RenderHistoryList();
            SetStatus($"{_histEvents.Count} event{S(_histEvents.Count)} on {_histDay:ddd MMM d}.");
        }
        catch (AuthException) { }
        catch (Exception ex)
        {
            HistoryList.Children.Clear();
            HistoryList.Children.Add(Muted("  Could not load history: " + ex.Message));
        }
    }

    private void RenderHistoryList()
    {
        HistoryList.Children.Clear();
        if (_searchHits is not null) { RenderSearchHits(); return; }
        IEnumerable<HistoryEvent> items = _histEvents;
        if (_histCamera.Length > 0) items = items.Where(e => e.DeviceId == _histCamera);
        if (_histClipsOnly) items = items.Where(e => e.HasClip);
        if (_histText.Length > 0)
            items = items.Where(e => e.Message.Contains(_histText, StringComparison.OrdinalIgnoreCase)
                                     || e.Device.Contains(_histText, StringComparison.OrdinalIgnoreCase));
        _histShown = items.ToList();
        if (_histShown.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock { Text = "Nothing here for this day and filter.", Foreground = Res("Muted"), Margin = new Thickness(16) });
            return;
        }
        foreach (var e in _histShown) HistoryList.Children.Add(HistoryRow(e));
        if (_histSelected is not null && !_histShown.Contains(_histSelected)) ShowHistoryDetail(null);
    }

    private Border HistoryRow(HistoryEvent e)
    {
        var lead = new StackPanel { Orientation = Orientation.Horizontal };
        lead.Children.Add(new TextBlock { Text = e.When?.ToLocalTime().ToString("h:mm tt") ?? "", Width = 62, FontSize = 12,
            Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
        if (e.ThumbnailUrl is { } t)
            lead.Children.Add(new Border { Width = 64, Height = 38, CornerRadius = new CornerRadius(5), ClipToBounds = true, Background = Res("Sunken"),
                Child = new Image { Stretch = Stretch.UniformToFill, Source = CameraWindow.Thumb(t, 128) } });
        else
            lead.Children.Add(new Border { Width = 64, Child = IconTile(EventGlyph(e), Res("Muted"), 30) });

        UIElement? trail = e.HasClip
            ? Pill(e.ClipSeconds is { } secs ? $"▶ {secs}s" : "▶ clip", "AccentSoft", "AccentText")
            : null;
        var row = ListRow(lead, e.Message.Trim(), e.Device, trail, () => SelectHistoryEvent(e));
        row.Tag = e;
        if (ReferenceEquals(e, _histSelected) || (e.Id.Length > 0 && e.Id == _histSelected?.Id)) row.Background = Res("AccentSoft");
        row.MouseLeave += (_, _) => { if (row.Tag == _histSelected) row.Background = Res("AccentSoft"); };
        return row;
    }

    private static string EventGlyph(HistoryEvent e)
    {
        var m = e.Message.ToLowerInvariant();
        if (m.Contains("lock")) return "";
        if (m.Contains("door") || m.Contains("window")) return "";
        if (m.Contains("armed") || m.Contains("disarm")) return "";
        if (m.Contains("thermostat") || m.Contains("set point")) return "";
        if (m.Contains("online") || m.Contains("offline")) return "";
        return "";
    }

    // ---------------------------------------------------------------- selection + player
    private async void SelectHistoryEvent(HistoryEvent e)
    {
        _histSelected = e;
        _hitSelected = null;
        foreach (var child in HistoryList.Children.OfType<Border>())
            child.Background = child.Tag == (object)e ? Res("AccentSoft") : Brushes.Transparent;
        ShowHistoryDetail(e);
        if (e.HasClip) await PlayInPanelAsync(e);
        else ShowStill(e.ThumbnailUrl, e.ThumbnailUrl is null ? "No video for this event." : null);
    }

    private async Task EnsurePlayerAsync()
    {
        if (_player is not null) return;
        if (!VideoEngine.IsCreated) await VideoEngine.WarmUpAsync();
        _player = new VideoSurface();
        HistoryPlayerHost.Children.Clear();
        HistoryPlayerHost.Children.Add(_player);
    }

    private async Task PlayInPanelAsync(HistoryEvent e)
    {
        await EnsurePlayerAsync();
        _player!.Visibility = Visibility.Visible;
        _player.ShowMessage("Opening clip…");
        try
        {
            var url = await _svc.GetClipUrlAsync(e.Id);
            if (url is null) { _player.ShowMessage("This clip is no longer available."); return; }
            if (_histSelected != e) return;   // user moved on
            await _player.PlayUrlAsync(url, $"{e.Message.Trim()} · {e.WhenText}");
            _player.Muted = false;
        }
        catch (AuthException) { }
        catch (Exception ex) { _player.ShowMessage("Clip failed: " + ex.Message); }
    }

    /// <summary>Show a still image (or a message) in the player area.</summary>
    private void ShowStill(string? imageUrl, string? message, byte[]? bytes = null)
    {
        _player?.Stop();
        if (_player is not null) _player.Visibility = Visibility.Collapsed;
        foreach (var img in HistoryPlayerHost.Children.OfType<FrameworkElement>().Where(c => c is not VideoSurface).ToList())
            HistoryPlayerHost.Children.Remove(img);
        if (bytes is not null || imageUrl is not null)
            HistoryPlayerHost.Children.Add(new Image { Stretch = Stretch.Uniform, Source = bytes is not null ? Decode(bytes) : CameraWindow.Thumb(imageUrl!, 960) });
        if (message is not null)
            HistoryPlayerHost.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
    }

    private void ShowHistoryDetail(HistoryEvent? e)
    {
        HistoryDetail.Children.Clear();
        if (e is null)
        {
            ShowStill(null, "Pick an event to see it here. Events with ▶ have video.");
            return;
        }
        foreach (var img in HistoryPlayerHost.Children.OfType<FrameworkElement>().Where(c => c is not VideoSurface).ToList())
            HistoryPlayerHost.Children.Remove(img);
        HistoryDetail.Children.Add(new TextBlock { Text = e.Message.Trim(), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
        HistoryDetail.Children.Add(new TextBlock { Text = $"{e.When?.ToLocalTime():dddd, MMMM d · h:mm:ss tt}{(e.Device.Length > 0 ? " · " + e.Device : "")}",
            Foreground = Res("Muted"), Margin = new Thickness(0, 2, 0, 10) });
        var bar = new WrapPanel();
        var cam = FindDevice(e.DeviceId);
        if (cam is { Category: DeviceCategory.Camera })
            bar.Children.Add(MakeBtn("Watch live", () => { OpenCamera(cam); return Task.CompletedTask; }));
        if (e.HasClip && e.HistoryDay.Length > 0)
        {
            var del = MakeBtn("Delete clip", () => DeleteHistoryClipAsync(e), "GhostButton");
            del.SetResourceReference(Button.ForegroundProperty, "Danger");
            bar.Children.Add(del);
        }
        bar.Children.Add(new TextBlock { Text = "↑ ↓ to move · Del to delete", Foreground = Res("Muted"), FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        HistoryDetail.Children.Add(bar);
    }

    private async Task DeleteHistoryClipAsync(HistoryEvent e)
    {
        if (_state is null) return;
        if (!Dialog.Confirm(this, "Delete this clip?", $"“{e.Message.Trim()}” at {e.When?.ToLocalTime():h:mm tt} will be removed permanently.",
                "Delete", danger: true)) return;
        var pid = _state.PanelId;
        _player?.Stop();
        var idx = _histShown.IndexOf(e);
        await Do(() => _svc.DeleteClipsAsync(pid, new[] { e }), "Deleting clip");
        _histEvents.Remove(e);
        RenderHistoryList();
        if (_histShown.Count > 0) SelectHistoryEvent(_histShown[Math.Clamp(idx, 0, _histShown.Count - 1)]);
        else ShowHistoryDetail(null);
    }

    private async void HistoryKey(KeyEventArgs e)
    {
        if (_searchHits is not null || _histShown.Count == 0 || _histSearch.IsKeyboardFocused) return;
        var i = _histSelected is null ? -1 : _histShown.IndexOf(_histSelected);
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            var n = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, _histShown.Count - 1);
            if (n == i) return;
            SelectHistoryEvent(_histShown[n]);
            if (HistoryList.Children.OfType<Border>().FirstOrDefault(b => b.Tag == (object)_histShown[n]) is { } row) row.BringIntoView();
        }
        else if (e.Key == Key.Delete && _histSelected is { HasClip: true } sel)
        {
            e.Handled = true;
            await DeleteHistoryClipAsync(sel);
        }
    }

    /// <summary>Open History on an event's day and play it (used by the feed and the timeline).</summary>
    private void PlayHistoryClip(HistoryEvent h) => _ = OpenHistoryAtAsync(h);

    private async Task OpenHistoryAtAsync(HistoryEvent h)
    {
        _histDay = h.When?.ToLocalTime().Date ?? DateTime.Today;
        _histCategory = "";
        _histCamera = "";
        HistoryToolbar.Children.Clear();     // rebuild chips with the reset filters
        OpenPanel(HistoryTab);
        await LoadHistoryAsync();
        var match = _histEvents.FirstOrDefault(x => x.Id == h.Id) ?? h;
        SelectHistoryEvent(match);
        if (HistoryList.Children.OfType<Border>().FirstOrDefault(b => b.Tag == (object)match) is { } row) row.BringIntoView();
    }

    /// <summary>Open History filtered to one camera's clips.</summary>
    private async Task ShowCameraClipsAsync(Device cam)
    {
        _histCamera = cam.Id;
        _histClipsOnly = true;
        _histDay = DateTime.Today;
        HistoryToolbar.Children.Clear();
        OpenPanel(HistoryTab);
        await LoadHistoryAsync();
    }

    // ---------------------------------------------------------------- smart video search
    private async Task SearchVideoAsync(string q)
    {
        OpenPanel(HistoryTab);
        if (HistoryToolbar.Children.Count == 0) BuildHistoryToolbar();
        _histSearch.Text = q;
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (_state is null || _histText.Length == 0) return;
        var q = _histText;
        _search ??= new VideoSearch(_client);
        HistoryList.Children.Clear();
        HistoryList.Children.Add(new TextBlock { Text = $"Searching video for “{q}”… this can take up to a minute.", Foreground = Res("Muted"), Margin = new Thickness(16) });
        SetStatus("Searching video…");
        try
        {
            var to = new DateTimeOffset(_histDay).AddDays(1);
            _searchHits = await _search.SearchAsync(_state.PanelId, q, to.AddDays(-7), to);
            RenderHistoryList();
            SetStatus($"{_searchHits.Count} video match{(_searchHits.Count == 1 ? "" : "es")} for “{q}”.");
        }
        catch (AuthException) { }
        catch (Exception ex)
        {
            _searchHits = new();
            RenderHistoryList();
            SetStatus("Video search failed: " + ex.Message, error: true);
        }
    }

    private void RenderSearchHits()
    {
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 6) };
        var back = MakeBtn("Back to events", () => { _searchHits = null; RenderHistoryList(); return Task.CompletedTask; }, "GhostButton");
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);
        head.Children.Add(new TextBlock { Text = $"Video matches for “{_histText}” · last 7 days", FontWeight = FontWeights.SemiBold,
            Foreground = Res("Text"), VerticalAlignment = VerticalAlignment.Center });
        HistoryList.Children.Add(head);
        if (_searchHits!.Count == 0)
        {
            HistoryList.Children.Add(new TextBlock { Text = "No matches. Video search only covers footage your cameras have indexed, and may not be enabled for every system.",
                Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 14) });
            return;
        }
        foreach (var h in _searchHits)
        {
            var cam = CameraByUuid(h.CameraUuid);
            var img = new Image { Stretch = Stretch.UniformToFill };
            var lead = new Border { Width = 96, Height = 54, CornerRadius = new CornerRadius(5), ClipToBounds = true, Background = Res("Sunken"), Child = img };
            _ = LoadHitThumbAsync(h, img);
            var row = ListRow(lead, $"{h.When?.ToLocalTime():ddd MMM d, h:mm tt}", (cam?.Name ?? "Camera") + (h.GoodMatch ? " · strong match" : ""), null,
                () => _ = SelectHitAsync(h, cam));
            HistoryList.Children.Add(row);
        }
    }

    private async Task LoadHitThumbAsync(SearchHit h, Image img)
    {
        try { if (await _search!.ThumbnailAsync(h.ThumbnailUri) is { Length: > 0 } b) img.Source = Decode(b); }
        catch { /* no thumbnail */ }
    }

    /// <summary>Show a search match: its still, and play the clip recorded closest to that moment if there is one.</summary>
    private async Task SelectHitAsync(SearchHit h, Device? cam)
    {
        _hitSelected = h;
        _histSelected = null;
        HistoryDetail.Children.Clear();
        HistoryDetail.Children.Add(new TextBlock { Text = $"{cam?.Name ?? "Camera"} · {h.When?.ToLocalTime():dddd, MMMM d · h:mm tt}",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("Text") });
        byte[]? still = null;
        try { still = await _search!.ThumbnailAsync(h.ThumbnailUri); } catch { }
        ShowStill(null, null, still);
        if (cam is null || h.When is not { } t || _state is null) return;
        try
        {
            var clips = await _svc.GetClipsBetweenAsync(_state.PanelId, cam.Id, t.AddMinutes(-3), t.AddMinutes(3));
            var best = clips.Where(c => c.HasClip).OrderBy(c => Math.Abs(((c.When ?? t) - t).TotalSeconds)).FirstOrDefault();
            if (best is not null && _hitSelected == h)
            {
                HistoryDetail.Children.Add(MakeBtnLeft("▶ Play the clip from this moment", async () =>
                {
                    _histSelected = best;
                    foreach (var img in HistoryPlayerHost.Children.OfType<FrameworkElement>().Where(c => c is not VideoSurface).ToList())
                        HistoryPlayerHost.Children.Remove(img);
                    await PlayInPanelAsync(best);
                }, "PrimaryButton"));
            }
            else HistoryDetail.Children.Add(SectionNote("No recorded clip around this moment; open the camera to look at its recordings."));
        }
        catch { /* best effort */ }
    }

    private Device? CameraByUuid(string uuid) => Cameras().FirstOrDefault(c =>
        c.Raw.ValueKind == JsonValueKind.Object && Json.Str(c.Raw, "uuid") is { } u && u.Equals(uuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Called when the panel closes: stop playback so audio doesn't continue in the background.</summary>
    private void StopHistoryPlayback() => _player?.Stop();
}
