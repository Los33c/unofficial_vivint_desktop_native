using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VivintDesktop.Api;

namespace VivintDesktop.Views;

/// <summary>
/// Create or edit a cloud routine (a one-tap shortcut, or a trigger → actions automation).
/// Works on a copy of the routine JSON: fields this editor does not understand are kept as they were.
/// </summary>
public sealed class RoutineEditor : Window
{
    private readonly RoutineCatalog _cat;
    private readonly JsonObject _r;
    private readonly bool _fahrenheit;

    private readonly TextBox _name = new() { Width = 320 };
    private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 8, 0, 0) };
    private readonly RadioButton _isShortcut = new() { Content = "Shortcut (run with one click)", GroupName = "kind" };
    private readonly RadioButton _isAuto = new() { Content = "Automation (runs when something happens)", GroupName = "kind" };
    private readonly ComboBox _trigger = new() { Width = 360 };
    private readonly StackPanel _triggerDetail = new() { Margin = new Thickness(16, 6, 0, 0) };
    private readonly StackPanel _conditions = new();
    private readonly StackPanel _actions = new();
    private readonly StackPanel _triggerHost = new();

    private Func<JsonObject?>? _readTrigger;
    private readonly List<Func<JsonObject?>> _readConditions = new();
    private readonly List<Func<JsonObject?>> _readActions = new();

    /// <summary>The routine to save (EditShortcutDto fields), set when the user clicks Save.</summary>
    public JsonObject? Result { get; private set; }

    public RoutineEditor(RoutineCatalog catalog, JsonObject? routine, bool fahrenheit, bool newShortcut = false)
    {
        _cat = catalog;
        _fahrenheit = fahrenheit;
        _r = routine?.DeepClone().AsObject() ?? new JsonObject
        {
            ["isShortcut"] = newShortcut, ["enabled"] = true, ["category"] = "custom",
            ["actions"] = new JsonArray(), ["triggers"] = new JsonArray(),
        };

        Title = routine is null ? "New routine" : "Edit routine";
        Width = 640; Height = 760; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Style(this);
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(Label("Name"));
        _name.Text = _r["name"]?.ToString() ?? "";
        body.Children.Add(_name);
        _enabled.IsChecked = _r["enabled"]?.GetValue<bool>() ?? true;
        body.Children.Add(_enabled);

        body.Children.Add(Section("Type"));
        var isShortcut = _r["isShortcut"]?.GetValue<bool>() ?? false;
        _isShortcut.IsChecked = isShortcut;
        _isAuto.IsChecked = !isShortcut;
        _isShortcut.Checked += (_, _) => _triggerHost.Visibility = Visibility.Collapsed;
        _isAuto.Checked += (_, _) => _triggerHost.Visibility = Visibility.Visible;
        body.Children.Add(_isShortcut);
        body.Children.Add(_isAuto);

        // ---- trigger + conditions (automations only)
        _triggerHost.Visibility = isShortcut ? Visibility.Collapsed : Visibility.Visible;
        _triggerHost.Children.Add(Section("When"));
        var triggers = _cat.Triggers.Where(t => t["category"]?.ToString() != "manual").ToList();
        foreach (var t in triggers) _trigger.Items.Add(new ComboBoxItem { Content = t["name"]?.ToString(), Tag = t });
        var current = (_r["triggers"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["category"]?.ToString() != "manual");
        _trigger.SelectionChanged += (_, _) =>
        {
            if (_trigger.SelectedItem is ComboBoxItem { Tag: JsonObject t }) BuildTrigger(t, SameId(t, current) ? current : null);
        };
        _triggerHost.Children.Add(_trigger);
        _triggerHost.Children.Add(_triggerDetail);
        var idx = current is null ? -1 : triggers.FindIndex(t => SameId(t, current));
        _trigger.SelectedIndex = idx >= 0 ? idx : triggers.Count > 0 ? 0 : -1;

        _triggerHost.Children.Add(Section("Only if (optional)"));
        _triggerHost.Children.Add(_conditions);
        BuildConditions();
        body.Children.Add(_triggerHost);

        // ---- actions
        body.Children.Add(Section("Then"));
        body.Children.Add(_actions);
        foreach (var a in (_r["actions"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            AddAction(_cat.Actions.FirstOrDefault(c => SameId(c, a)) ?? a, a);
        var add = new ComboBox { Width = 300, Margin = new Thickness(0, 8, 0, 0) };
        add.Items.Add(new ComboBoxItem { Content = "+ Add an action…", IsEnabled = false });
        foreach (var a in _cat.Actions.Where(a => a["devices"] is JsonArray { Count: > 0 } && Supported(a)))
            add.Items.Add(new ComboBoxItem { Content = a["name"]?.ToString(), Tag = a });
        add.SelectedIndex = 0;
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedItem is ComboBoxItem { Tag: JsonObject a }) { AddAction(a, null); add.SelectedIndex = 0; }
        };
        body.Children.Add(add);

        // ---- buttons
        var save = new Button { Content = "Save", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 8, 18, 14) };
        bar.Children.Add(save);
        bar.Children.Add(cancel);

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = dock;
    }

    // ---------------------------------------------------------------- trigger
    private void BuildTrigger(JsonObject def, JsonObject? existing)
    {
        _triggerDetail.Children.Clear();
        var category = def["category"]?.ToString();
        var t = (existing ?? def).DeepClone().AsObject();

        if (category == "time" && def["name"]?.ToString()?.Contains("at time") == true)
        {
            var p = t["params"] as JsonObject;
            var hour = p?["time"]?["hour"]?.GetValue<int>() ?? 22;
            var minute = p?["time"]?["minute"]?.GetValue<int>() ?? 0;
            var days = (p?["days"] as JsonArray)?.Select(d => d!.GetValue<int>()).ToHashSet() ?? Enumerable.Range(0, 7).ToHashSet();
            var time = TimeBox(hour, minute);
            _triggerDetail.Children.Add(Row("At", time.panel));
            var dayBoxes = DayBoxes(days);
            _triggerDetail.Children.Add(dayBoxes.panel);
            _readTrigger = () =>
            {
                if (!time.read(out var h, out var m)) { Warn("Enter the time as h:mm, e.g. 10:30 PM."); return null; }
                var sel = dayBoxes.read();
                if (sel.Count == 0) { Warn("Pick at least one day."); return null; }
                t["params"] = new JsonObject
                {
                    ["time"] = new JsonObject { ["hour"] = h, ["minute"] = m },
                    ["days"] = new JsonArray(sel.Select(d => (JsonNode)d).ToArray()),
                };
                return t;
            };
            return;
        }

        if (def["devices"] is JsonArray { Count: > 0 } devs)
        {
            var chosen = (existing?["devices"] as JsonArray)?.OfType<JsonObject>().Select(d => d["id"]?.ToString()).ToHashSet();
            var boxes = new List<(CheckBox box, JsonObject dev)>();
            foreach (var d in devs.OfType<JsonObject>())
            {
                var cb = new CheckBox { Content = d["n"]?.ToString(), IsChecked = chosen is null || chosen.Contains(d["id"]?.ToString()), Margin = new Thickness(0, 2, 0, 2) };
                boxes.Add((cb, d));
                _triggerDetail.Children.Add(cb);
            }
            _readTrigger = () =>
            {
                var sel = boxes.Where(b => b.box.IsChecked == true).Select(b => StripDevice(b.dev, 1)).ToArray();
                if (sel.Length == 0) { Warn("Pick at least one device for the trigger."); return null; }
                t["devices"] = new JsonArray(sel);
                t["anyDevices"] = new JsonObject { ["status"] = 1 };
                return t;
            };
            return;
        }
        _readTrigger = () => t;
    }

    // ---------------------------------------------------------------- conditions
    private void BuildConditions()
    {
        var existing = (_r["conditions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        foreach (var c in _cat.Conditions)
        {
            var cur = existing.FirstOrDefault(e => SameId(e, c));
            var cb = new CheckBox { Content = c["name"]?.ToString(), IsChecked = cur is not null, Margin = new Thickness(0, 2, 0, 2) };
            _conditions.Children.Add(cb);
            var node = (cur ?? c).DeepClone().AsObject();
            if (c["category"]?.ToString() == "time")
            {
                var p = node["params"] as JsonObject;
                var start = TimeBox(p?["startTime"]?["hour"]?.GetValue<int>() ?? 22, p?["startTime"]?["minute"]?.GetValue<int>() ?? 0);
                var end = TimeBox(p?["endTime"]?["hour"]?.GetValue<int>() ?? 6, p?["endTime"]?["minute"]?.GetValue<int>() ?? 0);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 4) };
                row.Children.Add(start.panel);
                row.Children.Add(new TextBlock { Text = "and", Margin = new Thickness(6, 4, 6, 0) });
                row.Children.Add(end.panel);
                _conditions.Children.Add(row);
                _readConditions.Add(() =>
                {
                    if (cb.IsChecked != true) return null;
                    if (!start.read(out var sh, out var sm) || !end.read(out var eh, out var em)) { Warn("Enter the hours as h:mm."); return null; }
                    node["params"] = new JsonObject
                    {
                        ["startTime"] = new JsonObject { ["hour"] = sh, ["minute"] = sm },
                        ["endTime"] = new JsonObject { ["hour"] = eh, ["minute"] = em },
                    };
                    return node;
                });
            }
            else _readConditions.Add(() => cb.IsChecked == true ? node : null);
        }
    }

    // ---------------------------------------------------------------- actions
    private static bool Supported(JsonObject a) => a["category"]?.ToString() is "security" or "door" or "light" or "thermostat"
        or "garage" or "cameraprivacy" or "thermostatfan" or "cameraclip" or "triggerdeter" or "deter";

    private void AddAction(JsonObject def, JsonObject? existing)
    {
        var category = def["category"]?.ToString() ?? "";
        var node = (existing ?? def).DeepClone().AsObject();
        var card = new StackPanel();
        var border = new Border { Style = (Style)Application.Current.Resources["Card"], Child = card, Margin = new Thickness(0, 0, 0, 8) };

        var head = new DockPanel();
        var remove = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["GhostButton"] };
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        head.Children.Add(new TextBlock { Text = def["name"]?.ToString(), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(head);

        Func<JsonObject?> read;
        if (!Supported(def) || def["devices"] is not JsonArray { Count: > 0 } devs)
        {
            card.Children.Add(new TextBlock { Text = "This action can't be edited here; it will be kept as it is.", Foreground = Theme.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            read = () => node;
        }
        else
        {
            var states = StateChoices(category);
            var existingDevs = (existing?["devices"] as JsonArray)?.OfType<JsonObject>().ToDictionary(d => d["id"]?.ToString() ?? "", d => d) ?? new();
            var rows = new List<Func<JsonObject?>>();
            foreach (var d in devs.OfType<JsonObject>())
            {
                existingDevs.TryGetValue(d["id"]?.ToString() ?? "", out var cur);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var cb = new CheckBox { Content = d["n"]?.ToString(), Width = 170, IsChecked = existing is null || cur is not null, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(cb);
                Func<JsonObject, bool> apply;
                if (category == "thermostat")
                {
                    var heat = new TextBox { Width = 50, Text = Temp(cur?["hsp"]?.GetValue<double>() ?? 20) };
                    var cool = new TextBox { Width = 50, Text = Temp(cur?["csp"]?.GetValue<double>() ?? 24), Margin = new Thickness(0, 0, 0, 0) };
                    row.Children.Add(new TextBlock { Text = "Heat", Margin = new Thickness(0, 4, 4, 0) });
                    row.Children.Add(heat);
                    row.Children.Add(new TextBlock { Text = "Cool", Margin = new Thickness(10, 4, 4, 0) });
                    row.Children.Add(cool);
                    row.Children.Add(new TextBlock { Text = _fahrenheit ? "°F" : "°C", Margin = new Thickness(4, 4, 0, 0) });
                    apply = o =>
                    {
                        if (!double.TryParse(heat.Text, out var h) || !double.TryParse(cool.Text, out var c)) { Warn("Enter set points as numbers."); return false; }
                        o["hsp"] = Math.Round(ToC(h), 1);
                        o["csp"] = Math.Round(ToC(c), 1);
                        return true;
                    };
                }
                else if (category == "thermostatfan")
                {
                    var mins = new TextBox { Width = 50, Text = ((cur?["fd"]?.GetValue<long>() ?? 900) / 60).ToString() };
                    row.Children.Add(new TextBlock { Text = "for", Margin = new Thickness(0, 4, 4, 0) });
                    row.Children.Add(mins);
                    row.Children.Add(new TextBlock { Text = "minutes", Margin = new Thickness(4, 4, 0, 0) });
                    apply = o =>
                    {
                        if (!int.TryParse(mins.Text, out var m) || m <= 0) { Warn("Enter the fan time in minutes."); return false; }
                        o["fm"] = 1;
                        o["fd"] = m * 60L;
                        return true;
                    };
                }
                else if (states.Count > 0)
                {
                    var combo = new ComboBox { Width = 150 };
                    foreach (var (label, _) in states) combo.Items.Add(label);
                    var curS = cur?["s"]?.GetValue<int>();
                    var curP = cur?["cpri"]?.GetValue<bool>();
                    var sel = states.FindIndex(s => category == "cameraprivacy" ? curP == (s.value == 1) : s.value == curS);
                    combo.SelectedIndex = Math.Max(0, sel);
                    row.Children.Add(combo);
                    TextBox? level = null;
                    if (category == "light")
                    {
                        level = new TextBox { Width = 44, Text = cur?["val"]?.ToString() ?? "100", Margin = new Thickness(8, 0, 0, 0) };
                        row.Children.Add(level);
                        row.Children.Add(new TextBlock { Text = "%", Margin = new Thickness(3, 4, 0, 0) });
                    }
                    apply = o =>
                    {
                        var v = states[combo.SelectedIndex].value;
                        if (category == "cameraprivacy") o["cpri"] = v == 1; else o["s"] = v;
                        if (level is not null && v == 1) o["val"] = level.Text.Trim();
                        return true;
                    };
                }
                else apply = o => { o["s"] = 1; return true; };

                card.Children.Add(row);
                var devCopy = d;
                rows.Add(() =>
                {
                    if (cb.IsChecked != true) return null;
                    var o = StripDevice(devCopy, null);
                    return apply(o) ? o : throw new OperationCanceledException();
                });
            }

            var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var delay = new TextBox { Width = 50, Text = ((node["delay"]?.GetValue<int>() ?? 0) / 60).ToString() };
            delayRow.Children.Add(new TextBlock { Text = "Wait", Margin = new Thickness(0, 4, 6, 0) });
            delayRow.Children.Add(delay);
            delayRow.Children.Add(new TextBlock { Text = "minutes first (0 = right away)", Margin = new Thickness(6, 4, 0, 0), Foreground = Theme.Brush("Muted") });
            card.Children.Add(delayRow);

            read = () =>
            {
                var chosen = new List<JsonObject>();
                foreach (var r in rows)
                {
                    try { if (r() is { } o) chosen.Add(o); }
                    catch (OperationCanceledException) { return null; }
                }
                if (chosen.Count == 0) { Warn($"Pick at least one device for “{def["name"]}”, or remove it."); return null; }
                node["devices"] = new JsonArray(chosen.ToArray());
                if (!int.TryParse(delay.Text, out var dm) || dm < 0) { Warn("Enter the wait in minutes."); return null; }
                if (dm > 0) node["delay"] = dm * 60; else node.Remove("delay");
                node.Remove("status");
                return node;
            };
        }

        _readActions.Add(read);
        remove.Click += (_, _) => { _actions.Children.Remove(border); _readActions.Remove(read); };
        _actions.Children.Add(border);
    }

    private static List<(string label, int value)> StateChoices(string category) => category switch
    {
        "security" => new() { ("Disarm", 0), ("Arm Stay", 3), ("Arm Away", 4) },
        "door" => new() { ("Lock", 1), ("Unlock", 0) },
        "light" => new() { ("Turn on", 1), ("Turn off", 0) },
        "garage" => new() { ("Close", 1) },
        "cameraprivacy" => new() { ("Privacy on", 1), ("Privacy off", 0) },
        "deter" => new() { ("Deter on", 1), ("Deter off", 0) },
        "cameraclip" => new() { ("Record", 1) },
        "triggerdeter" => new() { ("Trigger", 1) },
        _ => new(),
    };

    // ---------------------------------------------------------------- save
    private void Save()
    {
        if (_name.Text.Trim().Length == 0 && _isShortcut.IsChecked == true) { Warn("Give the shortcut a name."); return; }
        var r = _r;
        if (_name.Text.Trim().Length > 0) r["name"] = _name.Text.Trim(); else r.Remove("name");
        r["enabled"] = _enabled.IsChecked == true;
        var shortcut = _isShortcut.IsChecked == true;
        r["isShortcut"] = shortcut;

        if (shortcut)
        {
            var manual = _cat.Triggers.FirstOrDefault(t => t["category"]?.ToString() == "manual");
            r["triggers"] = manual is null ? new JsonArray() : new JsonArray(manual.DeepClone());
            r.Remove("conditions");
        }
        else
        {
            if (_readTrigger?.Invoke() is not { } trig) { if (_readTrigger is null) Warn("Pick a trigger."); return; }
            r["triggers"] = new JsonArray(trig);
            var conds = new List<JsonNode>();
            foreach (var c in _readConditions)
            {
                var v = c();
                if (v is not null) conds.Add(v);
            }
            r["conditions"] = new JsonArray(conds.ToArray());
        }

        var acts = new List<JsonNode>();
        foreach (var a in _readActions)
        {
            if (a() is not { } v) return;
            acts.Add(v);
        }
        if (acts.Count == 0) { Warn("Add at least one action."); return; }
        r["actions"] = new JsonArray(acts.ToArray());
        Result = r;
        DialogResult = true;
    }

    // ---------------------------------------------------------------- helpers
    private static bool SameId(JsonObject a, JsonObject? b) => b is not null && a["id"]?.ToString() == b["id"]?.ToString();

    /// <summary>A device entry for a trigger/action: id, device_id, n, t (+ s when given).</summary>
    private static JsonObject StripDevice(JsonObject d, int? s)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "id", "device_id", "n", "t" })
            if (d[k] is { } v) o[k] = v.DeepClone();
        if (s is { } st) o["s"] = st;
        return o;
    }

    private string Temp(double c) => _fahrenheit ? Math.Round(c * 9 / 5 + 32).ToString() : c.ToString("0.#");
    private double ToC(double v) => _fahrenheit ? (v - 32) * 5 / 9 : v;

    private static (StackPanel panel, ReadTime read) TimeBox(int hour, int minute)
    {
        var box = new TextBox { Width = 104, Text = new DateTime(2000, 1, 1, hour, minute, 0).ToString("h:mm tt") };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(box);
        return (p, (out int h, out int m) =>
        {
            h = m = 0;
            if (!DateTime.TryParse("2000-01-01 " + box.Text.Trim(), out var t)) return false;
            h = t.Hour; m = t.Minute;
            return true;
        });
    }

    private delegate bool ReadTime(out int hour, out int minute);

    private static (WrapPanel panel, Func<List<int>> read) DayBoxes(HashSet<int> days)
    {
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var p = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var boxes = new List<CheckBox>();
        for (int i = 0; i < 7; i++)
        {
            var cb = new CheckBox { Content = names[i], IsChecked = days.Contains(i), Margin = new Thickness(0, 0, 10, 0) };
            boxes.Add(cb);
            p.Children.Add(cb);
        }
        return (p, () => boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => x.i).ToList());
    }

    private static FrameworkElement Row(string label, UIElement content)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        p.Children.Add(new TextBlock { Text = label, Width = 40, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(content);
        return p;
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 0, 0, 4) };
    private static TextBlock Section(string t) => new() { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
    private void Warn(string msg) => Dialog.Info(this, "Check the routine", msg);
}
