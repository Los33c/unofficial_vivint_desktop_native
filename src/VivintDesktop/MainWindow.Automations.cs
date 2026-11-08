using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>Shortcuts &amp; Automations tab: create, edit, enable/disable and delete routines;
/// add suggested ones; turn notification/legacy rules on and off.</summary>
public partial class MainWindow
{
    private List<JsonObject> _routinesRaw = new();
    private RoutineCatalog? _catalog;

    private JsonObject? Raw(string id) => _routinesRaw.FirstOrDefault(x => x["id"]?.ToString() == id)?.DeepClone().AsObject();

    private async Task EditRoutineAsync(JsonObject? routine, bool shortcut, bool isNew = false)
    {
        if (_catalog is null || _state is null) return;
        var pid = _state.PanelId;
        var dlg = new RoutineEditor(_catalog, routine, _fahrenheit, newShortcut: shortcut) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } result) return;
        var id = isNew || routine is null ? null : routine["id"]?.ToString();
        var name = result["name"]?.ToString() ?? "routine";
        await Do(() => id is null ? _svc.CreateRoutineAsync(pid, result) : _svc.UpdateRoutineAsync(pid, id, result),
            id is null ? $"Creating “{name}”" : $"Saving “{name}”");
        await LoadAutomationsAsync();
    }
}
