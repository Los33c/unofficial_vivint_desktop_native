using System.Net.Http;
using System.Text.Json;

namespace VivintDesktop.Api;

/// <summary>
/// Higher-level calls behind the app's screens (cameras, clips, shortcuts,
/// system, people, insights). Paths and hosts were confirmed against the live
/// service; shapes follow the decompiled app's DTOs.
/// </summary>
public sealed partial class HomeServices
{
    private readonly VivintClient _c;
    private readonly Dictionary<string, (string user, string pass)> _panelCreds = new();

    public HomeServices(VivintClient client) => _c = client;
    public VivintClient Client => _c;

    private const string Api = Base.VivintskyApi;          // https://www.vivintsky.com/api/
    private const string Root = Base.Vivintsky + "/";      // cloudrules lives at the host root

    // ------------------------------------------------------------ cameras
    /// <summary>Panel RTSP credentials (GET panel-login/{panel} → {n, pswd}); cached per panel.</summary>
    public async Task<(string user, string pass)> GetPanelCredentialsAsync(string panelId, bool refresh = false)
    {
        if (!refresh && _panelCreds.TryGetValue(panelId, out var c)) return c;
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"panel-login/{panelId}");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j)
            throw new InvalidOperationException($"Could not get camera access from the panel (HTTP {r.Status}).");
        var creds = (Json.Str(j, "n") ?? "", Json.Str(j, "pswd") ?? "");
        _panelCreds[panelId] = creds;
        return creds;
    }

    /// <summary>Recent recorded events/clips for one camera (GET {panel}/0/history/{camera}).</summary>
    public async Task<List<HistoryEvent>> GetCameraClipsAsync(string panelId, string cameraId, int limit = 40)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"{panelId}/0/history/{cameraId}",
            new Dictionary<string, string> { ["lim"] = limit.ToString() });
        return r.Ok && r.Json is { } j ? HistoryEvent.Parse(j) : new();
    }

    /// <summary>Signed MP4 URL for a recorded clip (GET token/{eventId} → {clipUrl}).</summary>
    public async Task<string?> GetClipUrlAsync(string eventId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"token/{eventId}");
        return r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j ? Json.Str(j, "clipUrl") : null;
    }

    // ------------------------------------------------------------ routines
    public async Task<List<Routine>> GetRoutinesAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/routines");
        if (!r.Ok || r.Json is not { } j) return new();
        return Routine.Parse(j);
    }

    public Task<ApiResult> ExecuteRoutineAsync(string panelId, string routineId)
        => _c.SendAsync(HttpMethod.Post, Root, $"cloudrules/v2/systems/{panelId}/execute-routine/{routineId}");

    // ------------------------------------------------------------ system
    public async Task<PanelUpdate?> GetPanelUpdateAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"systems/{panelId}/system-update");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j) return null;
        return new PanelUpdate(Json.TryBoolish(j, "av", out var av) && av,
            Json.Str(j, "csv") ?? "", Json.Str(j, "asv") ?? "");
    }

    public Task<ApiResult> InstallPanelUpdateAsync(string panelId)
        => _c.SendAsync(HttpMethod.Post, Api, $"systems/{panelId}/system-update");

    public Task<ApiResult> RebootPanelAsync(string panelId)
        => _c.SendAsync(HttpMethod.Post, Api, $"systems/{panelId}/reboot-panel");

    public Task<ApiResult> AcknowledgePanelAlertsAsync(string panelId)
        => _c.SendAsync(HttpMethod.Post, Api, $"ack-panel-alerts/1/{panelId}");

    /// <summary>Home system health problems (diagnostics engine).</summary>
    public async Task<List<string>> GetHealthProblemsAsync(string panelId)
    {
        var list = new List<string>();
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/diagnostics-engine-public/systems/{panelId}");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j) return list;
        if (j.TryGetProperty("problems", out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var e in p.EnumerateArray())
                list.Add(e.ValueKind == JsonValueKind.Object
                    ? Json.Str(e, "title", "description", "message", "name", "type") ?? e.GetRawText()
                    : e.ToString());
        return list;
    }

    public async Task<List<Insight>> GetInsightsAsync(string panelId)
    {
        var list = new List<Insight>();
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/insights/v1/{panelId}");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j) return list;
        if (!j.TryGetProperty("insightsData", out var d) || d.ValueKind != JsonValueKind.Object) return list;
        var period = Json.Str(d, "shortDate") ?? "";
        if (d.TryGetProperty("insights", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var i in arr.EnumerateArray())
                list.Add(new Insight(
                    Json.Str(i, "displayName", "category") ?? "",
                    Json.Str(i, "summaryMetricName") ?? "",
                    Json.Str(i, "summaryMetricValue") ?? "",
                    Json.Str(i, "summaryMetricSubheader") ?? "",
                    period));
        return list;
    }

    public async Task<List<SensorInfo>> GetSensorsAsync(string panelId)
    {
        var list = new List<SensorInfo>();
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/sensor-api/v2/system/{panelId}/sensors");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j) return list;
        foreach (var p in j.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var s in p.Value.EnumerateArray())
                list.Add(new SensorInfo(
                    Json.Str(s, "name") ?? "Sensor",
                    PrettyCategory(Json.Str(s, "category") ?? ""),
                    int.TryParse(Json.Str(s, "battery_level"), out var b) ? b : null,
                    !Json.TryBoolish(s, "online", out var ol) || ol,
                    Json.Str(s, "firmware") ?? "")
                {
                    Id = Json.Str(s, "id") ?? "",
                    Chime = Json.Str(s, "chime") is { } ch && ch != "0",
                    Voice = Json.TryBoolish(s, "voice_enabled", out var ve) && ve,
                });
        }
        return list;
    }

    public async Task<List<string>> GetHistoryCategoriesAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/history-api/v2/systems/{panelId}/history/filter-categories");
        var list = new List<string>();
        if (r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j
            && j.TryGetProperty("valid_filter_categories", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var e in a.EnumerateArray())
                if (e.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    public async Task<List<HistoryEvent>> GetHistoryAsync(string panelId, int limit, string? category)
    {
        var q = new Dictionary<string, string> { ["lim"] = limit.ToString() };
        if (category is { Length: > 0 }) q["included_filter_categories"] = category;
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"{panelId}/1/history", q);
        if (!r.Ok) throw new InvalidOperationException($"HTTP {r.Status}");
        return r.Json is { } j ? HistoryEvent.Parse(j) : new();
    }

    private static string PrettyCategory(string c)
    {
        c = c.Replace("SENSOR_CATEGORY_", "").Replace('_', ' ').ToLowerInvariant();
        return c.Length == 0 ? "" : char.ToUpper(c[0]) + c[1..];
    }

    private static object Num(string s) => long.TryParse(s, out var n) ? n : s;
}

public sealed record PanelUpdate(bool Available, string Current, string AvailableVersion);
public sealed record Insight(string Title, string Metric, string Value, string Detail, string Period);
public sealed record SensorInfo(string Name, string Category, int? Battery, bool Online, string Firmware)
{
    public string Id { get; init; } = "";
    public bool Chime { get; init; }
    public bool Voice { get; init; }
}

/// <summary>A user on the system (system.u[] in the system payload).</summary>
public sealed record PanelUser(string Name, bool Admin, bool HasPanelPin, bool HasLockPin, int LockCount, bool Registered)
{
    public string Id { get; init; } = "";
    public bool PrimaryAdmin { get; init; }
    public List<string> LockIds { get; init; } = new();

    public static List<PanelUser> Parse(JsonElement systemRoot)
    {
        var list = new List<PanelUser>();
        var sys = systemRoot.ValueKind == JsonValueKind.Object && systemRoot.TryGetProperty("system", out var s) ? s : systemRoot;
        if (sys.ValueKind != JsonValueKind.Object || !sys.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in u.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var locks = e.TryGetProperty("lids", out var l) && l.ValueKind == JsonValueKind.Array ? l.GetArrayLength() : 0;
            list.Add(new PanelUser(
                Json.Str(e, "n") ?? "User",
                Json.TryBoolish(e, "ad", out var ad) && ad,
                Json.TryBoolish(e, "hasPanelPin", out var pp) && pp,
                Json.TryBoolish(e, "hasLockPin", out var lp) && lp,
                locks,
                Json.TryBoolish(e, "reg", out var rg) && rg)
            {
                Id = Json.Str(e, "_id") ?? "",
                PrimaryAdmin = Json.TryBoolish(e, "pad", out var pad) && pad,
                LockIds = l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Select(x => x.ToString()).ToList() : new(),
            });
        }
        return list;
    }
}

/// <summary>A cloud routine: a one-tap "shortcut" or an automation (trigger → actions).</summary>
public sealed record Routine(string Id, string Name, bool IsShortcut, bool Enabled, string Trigger, string Actions)
{
    public static List<Routine> Parse(JsonElement j)
    {
        var list = new List<Routine>();
        JsonElement arr = j;
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("routines", out var r)) arr = r;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = Json.Str(e, "id", "_id");
            if (id is null) continue;
            var trig = Names(e, "triggers");
            var acts = Names(e, "actions");
            var name = Json.Str(e, "name", "n");
            if (string.IsNullOrWhiteSpace(name))
                name = trig.Length == 0 ? acts
                    : trig.StartsWith("When", StringComparison.OrdinalIgnoreCase) ? trig : $"When: {trig}";
            list.Add(new Routine(id, name ?? "Routine",
                Json.TryBoolish(e, "isShortcut", out var sc) && sc,
                !Json.TryBoolish(e, "enabled", out var en) || en,
                trig, acts));
        }
        return list;
    }

    private static string Names(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array) return "";
        var names = new List<string>();
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var n = Json.Str(x, "name") ?? "";
            var devs = x.TryGetProperty("devices", out var d) && d.ValueKind == JsonValueKind.Array
                ? string.Join(", ", d.EnumerateArray().Select(v => Json.Str(v, "name", "n")).Where(v => v is not null))
                : "";
            names.Add(devs.Length > 0 ? $"{n} ({devs})" : n);
        }
        return string.Join("; ", names.Where(n => n.Length > 0));
    }
}
