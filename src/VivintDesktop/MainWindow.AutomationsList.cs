using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>Automations panel: Shortcuts | Automations | Notifications, each a list of rows with real on/off switches.</summary>
public partial class MainWindow
{
    private int _autoSection;                       // 0 shortcuts, 1 automations, 2 notifications
    private List<LegacyRule> _legacyRules = new();
    private List<JsonObject> _suggested = new();

    private async Task LoadAutomationsAsync()
    {
        if (Selected is not { } s) return;
        var pid = s.PanelId;
        AutomationsContent.Children.Clear();
        AutomationsContent.Children.Add(Muted("Loading…"));
        try
        {
            var rawT = _svc.GetRoutinesRawAsync(pid);
            var catT = Safe(() => _svc.GetRoutineCatalogAsync(pid));
            var sugT = Safe(() => _svc.GetSuggestedRoutinesAsync(pid));
            var rulesT = Safe(() => _svc.GetLegacyRulesAsync(pid));
            await Task.WhenAll(rawT, catT, sugT, rulesT);
            _routinesRaw = rawT.Result;
            _catalog = catT.Result;
            _suggested = sugT.Result ?? new();
            _legacyRules = rulesT.Result ?? new();
            _routines = await _svc.GetRoutinesAsync(pid);
            _automationsLoaded = true;
            RenderShortcutBar();
            RenderAutomations();
        }
        catch (AuthException) { }
        catch (Exception ex)
        {
            AutomationsContent.Children.Clear();
            AutomationsContent.Children.Add(Muted("Could not load shortcuts and automations: " + ex.Message));
        }
    }

    private void RenderAutomations()
    {
        var pid = _state?.PanelId ?? Selected?.PanelId ?? "";
        AutomationsContent.Children.Clear();
        var shortcuts = _routines.Where(r => r.IsShortcut).ToList();
        var autos = _routines.Where(r => !r.IsShortcut).OrderByDescending(a => a.Enabled).ThenBy(a => a.Name).ToList();

        var head = new DockPanel();
        if (_catalog is not null && _autoSection < 2)
        {
            var add = MakeBtn(_autoSection == 0 ? "+ New shortcut" : "+ New automation",
                () => EditRoutineAsync(null, shortcut: _autoSection == 0), "PrimaryButton");
            add.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(add, Dock.Right);
            head.Children.Add(add);
        }
        head.Children.Add(Segmented("auto", _autoSection,
            ($"Shortcuts · {shortcuts.Count}", () => { _autoSection = 0; Dispatcher.BeginInvoke(RenderAutomations); }),
            ($"Automations · {autos.Count}", () => { _autoSection = 1; Dispatcher.BeginInvoke(RenderAutomations); }),
            ($"Notifications · {_legacyRules.Count}", () => { _autoSection = 2; Dispatcher.BeginInvoke(RenderAutomations); })));
        AutomationsContent.Children.Add(head);

        if (_autoSection == 0)
        {
            AutomationsContent.Children.Add(SectionNote("One-click scenes. They also appear in the controls column and the command bar."));
            AutomationsContent.Children.Add(shortcuts.Count == 0 ? Muted("No shortcuts yet.") : ListCard(shortcuts.Select(r => (UIElement)RoutineRow(r, pid))));
            var have = _routines.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sugg = _suggested.Where(x => !have.Contains(x["name"]?.ToString() ?? "")).ToList();
            if (sugg.Count > 0)
            {
                AutomationsContent.Children.Add(SectionTitle("Suggested"));
                AutomationsContent.Children.Add(ListCard(sugg.Select(x =>
                {
                    var copy = x;
                    return (UIElement)ListRow(IconTile(""), x["name"]?.ToString() ?? "", x["description"]?.ToString(),
                        MakeBtn("Add", async () =>
                        {
                            copy["isShortcut"] = true;
                            copy["enabled"] = true;
                            await EditRoutineAsync(copy, shortcut: true, isNew: true);
                        }, "GhostButton"));
                })));
            }
        }
        else if (_autoSection == 1)
        {
            AutomationsContent.Children.Add(SectionNote("They run on their own when something happens. Switch one off to pause it without deleting it."));
            AutomationsContent.Children.Add(autos.Count == 0 ? Muted("No automations.") : ListCard(autos.Select(r => (UIElement)RoutineRow(r, pid))));
        }
        else
        {
            AutomationsContent.Children.Add(SectionNote("Older \"notify me when…\" rules, grouped by area."));
            foreach (var g in _legacyRules.GroupBy(r => r.Category.Length > 0 ? r.Category : "Other").OrderBy(g => g.Key))
            {
                AutomationsContent.Children.Add(SectionTitle(g.Key));
                AutomationsContent.Children.Add(ListCard(g.OrderBy(r => r.Name).Select(r =>
                {
                    var rule = r;
                    return (UIElement)ListRow(null, r.Name, null, Switch(r.Enabled, async on =>
                    {
                        var res = await _svc.SetLegacyRuleEnabledAsync(pid, rule.Id, on);
                        if (!res.Ok) { Dialog.Info(this, "Couldn't change the rule", DescribeFailure(res), warning: true); throw new InvalidOperationException(); }
                        SetStatus($"\"{rule.Name}\" turned {(on ? "on" : "off")}.");
                    }));
                })));
            }
            if (_legacyRules.Count == 0) AutomationsContent.Children.Add(Muted("No notification rules."));
        }
        SetStatus($"{shortcuts.Count} shortcuts, {autos.Count} automations, {_legacyRules.Count} notification rules.");
    }

    /// <summary>One routine: switch, a sentence ("When … → …"), Run (shortcuts) and a ⋯ menu.</summary>
    private Border RoutineRow(Routine r, string pid)
    {
        var sw = Switch(r.Enabled, async on =>
        {
            if (Raw(r.Id) is not { } raw) throw new InvalidOperationException();
            raw["enabled"] = on;
            var res = await _svc.UpdateRoutineAsync(pid, r.Id, raw);
            if (!res.Ok) { Dialog.Info(this, "Couldn't change the routine", DescribeFailure(res), warning: true); throw new InvalidOperationException(); }
            var i = _routinesRaw.FindIndex(x => x["id"]?.ToString() == r.Id);
            if (i >= 0) _routinesRaw[i]["enabled"] = on;
            SetStatus($"\"{r.Name}\" turned {(on ? "on" : "off")}.");
        }, tooltip: "On / off");

        string title, sub;
        if (r.IsShortcut) { title = r.Name; sub = r.Actions; }
        else
        {
            var when = r.Trigger.Length > 0 ? r.Trigger : r.Name.Replace("When: ", "");
            title = when.StartsWith("When ", StringComparison.OrdinalIgnoreCase) ? when : "When: " + when;
            sub = "→ " + r.Actions;
        }

        var trail = new StackPanel { Orientation = Orientation.Horizontal };
        if (r.IsShortcut) trail.Children.Add(MakeBtn("▶ Run", () => RunShortcutAsync(r)));
        var menu = new List<(string, Func<Task>, bool)>();
        if (_catalog is not null && Raw(r.Id) is { } rr) menu.Add(("Edit…", () => EditRoutineAsync(rr, r.IsShortcut), false));
        menu.Add(("Delete", async () =>
        {
            if (!Dialog.Confirm(this, $"Delete \"{r.Name}\"?", "This can't be undone.", "Delete", danger: true)) return;
            await Do(() => _svc.DeleteRoutineAsync(pid, r.Id), $"Deleting \"{r.Name}\"");
            await LoadAutomationsAsync();
        }, true));
        trail.Children.Add(MoreMenu(menu.ToArray()));
        var row = ListRow(sw, title, sub, trail);
        if (!r.Enabled) row.Opacity = 0.7;
        return row;
    }
}
