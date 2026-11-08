using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VivintDesktop.Api;

/// <summary>
/// Calls behind the editing features: routines, rules, clips, device settings,
/// Away Lights, comfort targets, panel settings and maintenance mode.
/// Wire formats follow the decompiled app's Moshi adapters and were checked
/// against GET responses from the live service.
/// </summary>
public sealed partial class HomeServices
{
    private static readonly HttpMethod Patch = new("PATCH");

    // ------------------------------------------------------------ routines (cloudrules v2)
    /// <summary>Raw routine objects, for editing.</summary>
    public async Task<List<JsonObject>> GetRoutinesRawAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/routines");
        return Objects(r, "routines");
    }

    public async Task<RoutineCatalog> GetRoutineCatalogAsync(string panelId)
    {
        var t = _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/triggers");
        var a = _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/actions");
        var c = _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/conditions");
        await Task.WhenAll(t, a, c);
        return new RoutineCatalog(Objects(t.Result, "triggers"), Objects(a.Result, "actions"), Objects(c.Result, "conditions"));
    }

    public async Task<List<JsonObject>> GetSuggestedRoutinesAsync(string panelId)
        => Objects(await _c.SendAsync(HttpMethod.Get, Root, $"cloudrules/v2/systems/{panelId}/suggested"), "routines");

    /// <summary>Only the fields of the app's EditShortcutDto are sent.</summary>
    public static JsonObject EditBody(JsonObject routine)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "name", "category", "actions", "enabled", "triggers", "isShortcut", "conditions" })
            if (routine.TryGetPropertyValue(k, out var v) && v is not null) o[k] = v.DeepClone();
        return o;
    }

    public Task<ApiResult> CreateRoutineAsync(string panelId, JsonObject routine)
        => _c.SendAsync(HttpMethod.Post, Root, $"cloudrules/v2/systems/{panelId}/routines", body: EditBody(routine).ToJsonString());

    public Task<ApiResult> UpdateRoutineAsync(string panelId, string routineId, JsonObject routine)
        => _c.SendAsync(HttpMethod.Put, Root, $"cloudrules/v2/systems/{panelId}/routines/{routineId}", body: EditBody(routine).ToJsonString());

    public Task<ApiResult> DeleteRoutineAsync(string panelId, string routineId)
        => _c.SendAsync(HttpMethod.Delete, Root, $"cloudrules/v2/systems/{panelId}/routines/{routineId}");

    // ------------------------------------------------------------ legacy rules (notifications etc.)
    public async Task<List<LegacyRule>> GetLegacyRulesAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"systems/{panelId}/cloudrules");
        var list = new List<LegacyRule>();
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j || !j.TryGetProperty("r", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (Json.TryBoolish(e, "del", out var del) && del) continue;
            var cat = e.TryGetProperty("cat", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
                ? c[0].GetString() ?? "" : "";
            list.Add(new LegacyRule(Json.Str(e, "_id") ?? "", Json.Str(e, "n") ?? "Rule", cat,
                !Json.TryBoolish(e, "s", out var s) || s));
        }
        return list;
    }

    public Task<ApiResult> SetLegacyRuleEnabledAsync(string panelId, string ruleId, bool on)
        => _c.SendAsync(HttpMethod.Put, Api, $"systems/{panelId}/cloudrules/{ruleId}", body: new Dictionary<string, object> { ["s"] = on });

    public Task<ApiResult> DeleteLegacyRuleAsync(string panelId, string ruleId)
        => _c.SendAsync(HttpMethod.Delete, Api, $"systems/{panelId}/cloudrules/{ruleId}");

    // ------------------------------------------------------------ clips
    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Recorded clips for one camera in a time range (GET {panel}/0/history/{cam}?stad&amp;stod&amp;t=clips).</summary>
    public async Task<List<HistoryEvent>> GetClipsBetweenAsync(string panelId, string cameraId, DateTimeOffset from, DateTimeOffset to)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"{panelId}/0/history/{cameraId}", new Dictionary<string, string>
        {
            ["stad"] = Stamp(from), ["stod"] = Stamp(to), ["lim"] = "1000", ["t"] = "clips", ["collapse"] = "1",
        });
        if (!r.Ok) throw new InvalidOperationException($"HTTP {r.Status}");
        return r.Json is { } j ? HistoryEvent.Parse(j) : new();
    }

    /// <summary>System history for a time range (GET {panel}/1/history?stad&amp;stod), optionally one filter category.</summary>
    public async Task<List<HistoryEvent>> GetHistoryRangeAsync(string panelId, DateTimeOffset from, DateTimeOffset to, int limit, string? category)
    {
        var q = new Dictionary<string, string> { ["stad"] = Stamp(from), ["stod"] = Stamp(to), ["lim"] = limit.ToString() };
        if (category is { Length: > 0 }) q["included_filter_categories"] = category;
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"{panelId}/1/history", q);
        if (!r.Ok) throw new InvalidOperationException($"HTTP {r.Status}");
        return r.Json is { } j ? HistoryEvent.Parse(j) : new();
    }

    /// <summary>PATCH {panel}/clip-batch-delete {"delete":[{historyDay, historyId}]}.</summary>
    public Task<ApiResult> DeleteClipsAsync(string panelId, IEnumerable<HistoryEvent> clips)
        => _c.SendAsync(Patch, Api, $"{panelId}/clip-batch-delete", body: new Dictionary<string, object>
        {
            ["delete"] = clips.Select(c => new Dictionary<string, string> { ["historyDay"] = c.HistoryDay, ["historyId"] = c.Id }).ToList(),
        });

    // ------------------------------------------------------------ activity overview
    public async Task<JsonElement?> GetActivityOverviewAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/history-api/v2/systems/{panelId}/history/activity-feed-overview");
        return r.Ok ? r.Json : null;
    }

    // ------------------------------------------------------------ sensors
    public async Task<JsonElement?> GetSensorAsync(string panelId, string sensorId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/sensor-api/v2/system/{panelId}/sensor/{sensorId}");
        return r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("wireless_sensor", out var s) ? s : null;
    }

    public Task<ApiResult> RenameSensorAsync(string panelId, string sensorId, string name)
        => _c.SendAsync(HttpMethod.Post, Api, $"/sensor-api/v2/system/{panelId}/sensors/{sensorId}:update-name",
            body: new Dictionary<string, object> { ["name"] = name });

    /// <summary>PUT sensor settings (any of chime_enabled, voice, bypassed, sensor_beep_alerts_enabled …).</summary>
    public Task<ApiResult> UpdateSensorAsync(string panelId, string sensorId, Dictionary<string, object> settings)
        => _c.SendAsync(HttpMethod.Put, Api, $"/sensor-api/v2/system/{panelId}/sensors/{sensorId}", body: settings);

    // ------------------------------------------------------------ cameras
    /// <summary>Cameras with deter (spotlight) status: GET /api/systems/{id}/cameras.</summary>
    public async Task<List<JsonElement>> GetCameraDetailsAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"/api/systems/{panelId}/cameras");
        var list = new List<JsonElement>();
        if (r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("cameras", out var a) && a.ValueKind == JsonValueKind.Array)
            list.AddRange(a.EnumerateArray());
        return list;
    }

    // ------------------------------------------------------------ keypads / outlets (device-api capabilities)
    public Task<ApiResult> SetCapabilityAsync(string panelId, string deviceId, string path, object body)
        => _c.SendAsync(HttpMethod.Put, Api, $"device-api/v1/systems/{panelId}/devices/{deviceId}/capabilities/{path}", body: body);

    // ------------------------------------------------------------ Away Lights (smart deter)
    public async Task<AwayLights?> GetAwayLightsAsync(string panelId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Root, $"smartdeter/{panelId}");
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j) return null;
        static AwayTime T(JsonElement j, string k) => j.TryGetProperty(k, out var t) && t.ValueKind == JsonValueKind.Object
            ? new AwayTime(Json.Str(t, "type") ?? "TIME", Json.TryInt(t, "hour", out var h) ? h : 0, Json.TryInt(t, "minute", out var m) ? m : 0)
            : new AwayTime("SUNSET", 0, 0);
        return new AwayLights(Json.TryBoolish(j, "smart_deter_enabled", out var en) && en, T(j, "start"), T(j, "end"));
    }

    private static Dictionary<string, object> TimeBody(AwayTime t) => t.Type == "TIME"
        ? new() { ["type"] = "TIME", ["hour"] = t.Hour, ["minute"] = t.Minute }
        : new() { ["type"] = t.Type };

    public Task<ApiResult> SetAwayLightsEnabledAsync(string panelId, bool on)
        => _c.SendAsync(HttpMethod.Post, Root, $"smartdeter/{panelId}", body: new Dictionary<string, object> { ["smart_deter_enabled"] = on });

    public async Task<ApiResult> SetAwayLightsTimesAsync(string panelId, AwayTime start, AwayTime end)
    {
        var r = await _c.SendAsync(HttpMethod.Post, Root, $"smartdeter/{panelId}", body: new Dictionary<string, object> { ["start"] = TimeBody(start) });
        if (!r.Ok) return r;
        return await _c.SendAsync(HttpMethod.Post, Root, $"smartdeter/{panelId}", body: new Dictionary<string, object> { ["end"] = TimeBody(end) });
    }

    // ------------------------------------------------------------ thermostat comfort targets
    /// <summary>PUT {panel}/1/thermostats/{id} {scht:{home,away,sleep,vacation}, scct:{…}} (°C).</summary>
    public Task<ApiResult> SetComfortTargetsAsync(string panelId, string thermostatId,
        Dictionary<string, double> heat, Dictionary<string, double> cool)
        => _c.SendAsync(HttpMethod.Put, Api, $"{panelId}/1/thermostats/{thermostatId}", body: new Dictionary<string, object>
        {
            ["_id"] = Num(thermostatId),
            ["scht"] = heat.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
            ["scct"] = cool.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1)),
        });

    // ------------------------------------------------------------ panel settings / maintenance
    /// <summary>PATCH /panel-api/v1/systems/{id}/settings with any of panel_volume_level, doorbell_volume_level,
    /// play_panel_clicks, panel_beeps_and_alerts_volume_level, panel_led_brightness.</summary>
    public Task<ApiResult> UpdatePanelSettingsAsync(string panelId, Dictionary<string, object> settings)
        => _c.SendAsync(Patch, Api, $"/panel-api/v1/systems/{panelId}/settings", body: settings);

    /// <summary>Maintenance mode on = security monitoring off for a while (POST …/disable-security {timeout_seconds}).</summary>
    public Task<ApiResult> StartMaintenanceAsync(string panelId, int seconds)
        => _c.SendAsync(HttpMethod.Post, Api, $"/panel-api/v1/system/{panelId}/disable-security",
            body: new Dictionary<string, object> { ["timeout_seconds"] = seconds });

    /// <summary>End maintenance mode (POST …/enable-security, no body).</summary>
    public Task<ApiResult> EndMaintenanceAsync(string panelId)
        => _c.SendAsync(HttpMethod.Post, Api, $"/panel-api/v1/system/{panelId}/enable-security");

    // ------------------------------------------------------------ helpers
    private static List<JsonObject> Objects(ApiResult r, string key)
    {
        var list = new List<JsonObject>();
        if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Object } j || !j.TryGetProperty(key, out var a)
            || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in a.EnumerateArray())
            if (JsonNode.Parse(e.GetRawText()) is JsonObject o) list.Add(o);
        return list;
    }
}

public sealed record RoutineCatalog(List<JsonObject> Triggers, List<JsonObject> Actions, List<JsonObject> Conditions);
public sealed record LegacyRule(string Id, string Name, string Category, bool Enabled);
public sealed record AwayTime(string Type, int Hour, int Minute)
{
    public override string ToString() => Type switch
    {
        "SUNSET" => "Sunset",
        "SUNRISE" => "Sunrise",
        _ => new DateTime(2000, 1, 1, Hour, Minute, 0).ToString("h:mm tt"),
    };
}
public sealed record AwayLights(bool Enabled, AwayTime Start, AwayTime End);
