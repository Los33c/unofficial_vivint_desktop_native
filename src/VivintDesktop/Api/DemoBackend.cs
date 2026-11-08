using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VivintDesktop.Api;

/// <summary>
/// Demo mode: answers every API call from an in-memory sample home, so the app
/// can be explored without a Vivint account. Nothing is sent over the network.
/// Commands (lock, arm, thermostat…) change the sample state and add history
/// events, and a little background activity keeps the feed moving.
/// </summary>
internal sealed class DemoBackend
{
    public const string PanelId = "100001";
    private const string Part = "1";

    private readonly object _gate = new();
    private readonly JsonObject _system;
    private readonly JsonArray _routines;
    private readonly JsonArray _legacyRules;
    private readonly JsonObject _awayLights;
    private readonly List<JsonObject> _history = new();
    private readonly Random _rng = new(7);
    private int _nextId = 5000;
    private DateTime _lastAmbient = DateTime.UtcNow;

    public DemoBackend()
    {
        DemoImages.Reset();
        _system = BuildSystem();
        _routines = BuildRoutines();
        _legacyRules = new JsonArray
        {
            Rule("r1", "Notify me when the system is disarmed", "notification", true),
            Rule("r2", "Notify me when a door is left open", "notification", true),
            Rule("r3", "Email me when the Front Door is unlocked", "notification", false),
        };
        _awayLights = new JsonObject
        {
            ["smart_deter_enabled"] = true,
            ["start"] = new JsonObject { ["type"] = "SUNSET" },
            ["end"] = new JsonObject { ["type"] = "TIME", ["hour"] = 23, ["minute"] = 0 },
        };
        SeedHistory();
    }

    // ================================================================ dispatch
    public async Task<HttpResponseMessage> HandleAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        await Task.Delay(_rng.Next(60, 180), ct).ConfigureAwait(false);   // feel like a network call
        var uri = new Uri(url);
        var path = uri.AbsolutePath.TrimEnd('/');
        var q = ParseQuery(uri.Query);
        JsonObject? b = null;
        if (!string.IsNullOrEmpty(body))
            try { b = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }

        lock (_gate)
        {
            Ambient();
            try { return Route(method.Method.ToUpperInvariant(), path, q, b); }
            catch (Exception ex) { return Json(HttpStatusCode.InternalServerError, new JsonObject { ["msg"] = ex.Message }); }
        }
    }

    private HttpResponseMessage Route(string m, string path, Dictionary<string, string> q, JsonObject? b)
    {
        Match x;
        bool Is(string pattern) => (x = Regex.Match(path, "^" + pattern + "$", RegexOptions.IgnoreCase)).Success;
        string G(int i) => x.Groups[i].Value;

        // ---------------------------------------------------------- reads
        if (m == "GET")
        {
            if (Is("/api/authuser")) return Json(AuthUser());
            if (Is(@"/api/systems/(\w+)")) return Json(new JsonObject { ["system"] = _system.DeepClone() });
            if (Is(@"/api/(\w+)/1/history")) return Json(new JsonObject { ["his"] = History(q, null) });
            if (Is(@"/api/(\w+)/0/history/(\w+)")) return Json(new JsonObject { ["his"] = History(q, G(2)) });
            if (Is(@"/api/(\w+)/1/(\w+)/camera-thumbnail")) return Image(DemoImages.CameraJpeg(CameraName(G(2)), DateTime.Now));
            if (Is(@"/api/token/(\w+)")) return Json(new JsonObject { ["clipUrl"] = "demo://clip/" + G(1) });
            if (Is(@"/api/panel-login/(\w+)")) return Json(new JsonObject { ["n"] = "demo", ["pswd"] = "demo" });
            if (Is(@"/cloudrules/v2/systems/(\w+)/routines")) return Json(new JsonObject { ["routines"] = _routines.DeepClone() });
            if (Is(@"/cloudrules/v2/systems/(\w+)/(triggers|actions|conditions)")) return Json(Catalog(G(2)));
            if (Is(@"/cloudrules/v2/systems/(\w+)/suggested")) return Json(new JsonObject { ["routines"] = new JsonArray() });
            if (Is(@"/api/systems/(\w+)/cloudrules")) return Json(new JsonObject { ["r"] = _legacyRules.DeepClone() });
            if (Is(@"/api/systems/(\w+)/system-update")) return Json(new JsonObject { ["av"] = false, ["csv"] = "4.12.3", ["asv"] = "4.12.3" });
            if (Is(@"/diagnostics-engine-public/systems/(\w+)"))
                return Json(new JsonObject { ["problems"] = new JsonArray(new JsonObject { ["title"] = "Back Door lock battery is low (18%)" }) });
            if (Is(@"/insights/v1/(\w+)")) return Json(Insights());
            if (Is(@"/sensor-api/v2/system/(\w+)/sensors")) return Json(new JsonObject { ["sensors"] = SensorList() });
            if (Is(@"/sensor-api/v2/system/(\w+)/sensor/(\w+)"))
                return Json(new JsonObject { ["wireless_sensor"] = SensorList().OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == G(2))?.DeepClone() });
            if (Is(@"/history-api/v2/systems/(\w+)/history/filter-categories"))
                return Json(new JsonObject { ["valid_filter_categories"] = new JsonArray("security", "doorbell_rings", "locks", "cameras", "climate") });
            if (Is(@"/smartdeter/(\w+)")) return Json((JsonObject)_awayLights.DeepClone());
            if (Is(@"/api/systems/(\w+)/cameras")) return Json(new JsonObject { ["cameras"] = new JsonArray() });
            return Json(HttpStatusCode.NotFound, new JsonObject { ["msg"] = "Not available in demo mode." });
        }

        // ---------------------------------------------------------- commands
        if (m == "PUT" && Is(@"/api/(\w+)/(\w+)/armedstates") && b is not null)
        {
            var state = b["armState"]?.GetValue<int>() ?? 0;
            Arm(state);
            return Ok();
        }
        if (m == "PUT" && Is(@"/api/(\w+)/(\w+)/(locks|door|switches|thermostats|sensors)/(\w+)") && b is not null)
        {
            if (Device(G(4)) is not { } dev) return Json(HttpStatusCode.NotFound, new JsonObject { ["msg"] = "No such device." });
            DeviceCommand(G(3), dev, b);
            return Ok();
        }
        if (m == "PUT" && Is(@"/api/systems/(\w+)/users/(\w+)") && b?["n"] is { } newName)
        {
            if (Users().FirstOrDefault(u => u["_id"]?.ToString() == G(2)) is { } user) user["n"] = newName.ToString();
            return Ok();
        }
        if (Is(@"/cloudrules/v2/systems/(\w+)/execute-routine/(\w+)"))
        {
            var r = _routines.OfType<JsonObject>().FirstOrDefault(o => o["id"]?.ToString() == G(2));
            if (r is not null) RunRoutine(r);
            return Ok();
        }
        if (m == "POST" && Is(@"/cloudrules/v2/systems/(\w+)/routines") && b is not null)
        {
            b["id"] = "rt" + _nextId++;
            _routines.Add(b);
            return Json(b);
        }
        if (Is(@"/cloudrules/v2/systems/(\w+)/routines/(\w+)"))
        {
            var idx = IndexOf(_routines, "id", G(2));
            if (idx < 0) return Json(HttpStatusCode.NotFound, new JsonObject());
            if (m == "DELETE") _routines.RemoveAt(idx);
            else if (b is not null) { b["id"] = G(2); _routines[idx] = b; }
            return Ok();
        }
        if (Is(@"/api/systems/(\w+)/cloudrules/(\w+)"))
        {
            var idx = IndexOf(_legacyRules, "_id", G(2));
            if (idx >= 0)
            {
                if (m == "DELETE") _legacyRules.RemoveAt(idx);
                else if (b?["s"] is { } s) ((JsonObject)_legacyRules[idx]!)["s"] = s.GetValue<bool>();
            }
            return Ok();
        }
        if (m == "POST" && Is(@"/smartdeter/(\w+)") && b is not null)
        {
            foreach (var (k, v) in b) _awayLights[k] = v?.DeepClone();
            return Ok();
        }
        if (m == "PATCH" && Is(@"/panel-api/v1/systems/(\w+)/settings") && b is not null)
        {
            var panel = Devices().First(d => d["t"]?.ToString() == "primary_touch_link_device");
            foreach (var (k, v) in b)
                panel[k == "panel_led_brightness" ? "led_brightness" : k == "panel_beeps_and_alerts_volume_level" ? "beeps_and_alerts_volume_level" : k] = v?.DeepClone();
            return Ok();
        }
        if (m == "PATCH" && Is(@"/api/(\w+)/clip-batch-delete") && b?["delete"] is JsonArray del)
        {
            var ids = del.Select(d => d?["historyId"]?.ToString()).ToHashSet();
            _history.RemoveAll(h => ids.Contains(h["_id"]?.ToString()));
            return Ok();
        }
        if (m == "POST" && Is(@"/sensor-api/v2/system/(\w+)/sensors/(\w+):update-name") && b?["name"] is { } sn)
        {
            if (Device(G(2)) is { } dev) dev["n"] = sn.ToString();
            return Ok();
        }

        // Everything else (PIN changes, invites, reboot…) just "works" in the demo.
        return Ok();
    }

    // ================================================================ state changes
    private void Arm(int state)
    {
        var par = Partition();
        var who = "Demo User";
        if (state == 0)
        {
            par["s"] = 0;
            AddEvent("System disarmed by " + who, "Security system", "", "security");
            return;
        }
        // Exit delay first, like the real panel.
        par["s"] = state == 4 ? 1 : 2;
        AddEvent($"System arming {(state == 4 ? "Away" : "Stay")} by {who}", "Security system", "", "security");
        Later(TimeSpan.FromSeconds(8), () =>
        {
            if (par["s"]?.GetValue<int>() is 1 or 2)
            {
                par["s"] = state;
                AddEvent($"System armed {(state == 4 ? "Away" : "Stay")}", "Security system", "", "security");
            }
        });
    }

    private void DeviceCommand(string collection, JsonObject dev, JsonObject b)
    {
        var name = dev["n"]?.ToString() ?? "Device";
        var id = dev["_id"]!.ToString();
        switch (collection)
        {
            case "locks" when b["s"] is { } s:
                var locked = s.GetValue<bool>();
                dev["s"] = locked;
                AddEvent($"{name} {(locked ? "locked" : "unlocked")} by Demo User", name, id, "security", "locks");
                break;
            case "door" when b["s"] is { } g:
                var opening = g.GetValue<int>() == (int)GarageState.Opening;
                dev["s"] = opening ? (int)GarageState.Opening : (int)GarageState.Closing;
                Later(TimeSpan.FromSeconds(6), () =>
                {
                    dev["s"] = opening ? (int)GarageState.Open : (int)GarageState.Closed;
                    AddEvent($"{name} {(opening ? "opened" : "closed")}", name, id, "security");
                });
                break;
            case "switches":
                if (b["val"] is { } val) { var v = val.GetValue<int>(); dev["val"] = v; dev["s"] = v > 0; }
                if (b["s"] is { } on) dev["s"] = on.GetValue<bool>();
                break;
            case "thermostats":
                foreach (var key in new[] { "csp", "hsp", "om", "fm", "scht", "scct" })
                    if (b[key] is { } v) dev[key] = v.DeepClone();
                break;
            case "sensors" when b["b"] is { } by:
                dev["b"] = by.GetValue<bool>();
                break;
        }
    }

    private void RunRoutine(JsonObject r)
    {
        var name = r["name"]?.ToString() ?? "Shortcut";
        AddEvent($"Shortcut “{name}” ran", "Shortcuts", "", "security");
        if (r["actions"] is not JsonArray actions) return;
        foreach (var a in actions.OfType<JsonObject>())
        {
            var kind = a["demo"]?.ToString();
            if (kind == "lock-all")
                foreach (var l in Devices().Where(d => d["t"]?.ToString() == "door_lock_device")) l["s"] = true;
            else if (kind == "lights-off")
                foreach (var l in Devices().Where(d => d["t"]?.ToString() is "binary_switch_device" or "multilevel_switch_device"))
                { l["s"] = false; if (l["val"] is not null) l["val"] = 0; }
            else if (kind == "lights-on")
                foreach (var l in Devices().Where(d => d["t"]?.ToString() is "binary_switch_device" or "multilevel_switch_device"))
                { l["s"] = true; if (l["val"] is not null) l["val"] = 100; }
            else if (kind == "arm-stay") Arm(3);
            else if (kind == "disarm") Arm(0);
        }
    }

    /// <summary>Occasional background events so the live feed isn't static.</summary>
    private void Ambient()
    {
        if (DateTime.UtcNow - _lastAmbient < TimeSpan.FromSeconds(75)) return;
        _lastAmbient = DateTime.UtcNow;
        var cams = Cameras().ToList();
        var cam = cams[_rng.Next(cams.Count)];
        var what = new[] { "Person detected", "Motion detected", "Vehicle detected" }[_rng.Next(3)];
        AddCameraEvent(cam, what, DateTimeOffset.UtcNow);
        cam["ctd"] = DateTimeOffset.UtcNow.ToString("o");
    }

    private void Later(TimeSpan delay, Action a) =>
        _ = Task.Delay(delay).ContinueWith(_ => { lock (_gate) a(); }, TaskScheduler.Default);

    // ================================================================ history
    private void SeedHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var cams = Cameras().ToList();
        var t = now.AddDays(-7);
        while (t < now.AddMinutes(-5))
        {
            t = t.AddMinutes(_rng.Next(25, 140));
            if (t > now.AddMinutes(-60)) break;   // leave the last hour to the hand-placed events below
            var local = t.ToLocalTime().Hour;
            if (local is < 6 or > 23 && _rng.Next(4) != 0) continue;   // quiet nights
            switch (_rng.Next(9))
            {
                case 0 or 1 or 2:
                    var cam = cams[_rng.Next(cams.Count)];
                    AddCameraEvent(cam, new[] { "Person detected", "Motion detected", "Vehicle detected", "Package detected" }[_rng.Next(4)], t);
                    break;
                case 3:
                    AddCameraEvent(cams[0], "Doorbell pressed", t, "doorbell_rings");
                    break;
                case 4:
                    var door = new[] { "Front Door", "Back Door", "Garage Entry" }[_rng.Next(3)];
                    AddEvent($"{door} opened", door, "", t, "security");
                    AddEvent($"{door} closed", door, "", t.AddSeconds(_rng.Next(8, 90)), "security");
                    break;
                case 5:
                    var l = _rng.Next(2) == 0 ? "Front Door Lock" : "Back Door Lock";
                    AddEvent($"{l} {(_rng.Next(2) == 0 ? "locked" : "unlocked")} by {(_rng.Next(2) == 0 ? "Alex" : "Jordan")}", l, "", t, "security", "locks");
                    break;
                case 6:
                    var stay = _rng.Next(2) == 0;
                    AddEvent(stay ? "System armed Stay by Alex" : "System armed Away by Jordan", "Security system", "", t, "security");
                    var off = t.AddMinutes(_rng.Next(20, 300));
                    AddEvent("System disarmed by Alex", "Security system", "", off < now.AddMinutes(-50) ? off : now.AddMinutes(-50), "security");
                    break;
                case 7:
                    AddEvent("Hallway Motion detected motion", "Hallway Motion", "36", t, "security");
                    break;
                default:
                    AddEvent($"Thermostat set to {(_rng.Next(2) == 0 ? "Heat 68°F" : "Auto")}", "Thermostat", "13", t, "climate");
                    break;
            }
        }
        // A few recent ones so the timeline and feed have something near "now".
        AddCameraEvent(cams[1], "Vehicle detected", now.AddMinutes(-42));
        AddCameraEvent(cams[0], "Package detected", now.AddMinutes(-18));
        AddEvent("Back Door Lock unlocked by Jordan", "Back Door Lock", "11", now.AddMinutes(-9), "security", "locks");
    }

    private void AddEvent(string message, string device, string deviceId, params string[] cats)
        => AddEvent(message, device, deviceId, DateTimeOffset.UtcNow, cats);

    private JsonObject AddEvent(string message, string device, string deviceId, DateTimeOffset when, params string[] cats)
    {
        var e = new JsonObject
        {
            ["_id"] = "ev" + _nextId++,
            ["ts"] = when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["me"] = message,
            ["dn"] = device,
            ["did"] = deviceId,
            ["hdt"] = when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["cats"] = new JsonArray(cats.Select(c => (JsonNode?)c).ToArray()),
        };
        _history.Add(e);
        return e;
    }

    private void AddCameraEvent(JsonObject cam, string what, DateTimeOffset when, string cat = "security")
    {
        var name = cam["n"]!.ToString();
        var e = AddEvent(what, name, cam["_id"]!.ToString(), when, cat, "cameras");
        e["evt"] = "camera";
        e["clu"] = "demo-clip";
        e["clen"] = 12 + _rng.Next(20);
        e["thu"] = DemoImages.CameraFileUri(name, when.LocalDateTime, e["_id"]!.ToString(),
            person: what.Contains("Person") || what.Contains("Doorbell") || what.Contains("Package"));
    }

    private JsonArray History(Dictionary<string, string> q, string? cameraId)
    {
        IEnumerable<JsonObject> list = _history;
        if (cameraId is not null) list = list.Where(e => e["did"]?.ToString() == cameraId);
        if (q.TryGetValue("included_filter_categories", out var cat) && cat.Length > 0)
            list = list.Where(e => e["cats"] is JsonArray a && a.Any(c => c?.ToString() == cat));
        if (q.TryGetValue("t", out var t) && t == "clips") list = list.Where(e => e["clu"] is not null);
        DateTime? Parse(string k) => q.TryGetValue(k, out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        var from = Parse("stad");
        var to = Parse("stod");
        DateTime When(JsonObject e) => DateTime.Parse(e["ts"]!.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (from is { } f) list = list.Where(e => When(e) >= f);
        if (to is { } tt) list = list.Where(e => When(e) <= tt);
        var lim = q.TryGetValue("lim", out var l) && int.TryParse(l, out var n) ? n : 50;
        return new JsonArray(list.OrderByDescending(When).Take(lim).Select(e => (JsonNode?)e.DeepClone()).ToArray());
    }

    // ================================================================ sample home
    private static JsonObject BuildSystem()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("o");
        JsonObject Cam(int id, string name, string type) => new()
        {
            ["_id"] = id, ["n"] = name, ["t"] = type, ["ol"] = true, ["cpri"] = false, ["ctd"] = stamp,
            ["uuid"] = $"00000000-0000-4000-8000-0000000000{id}",
        };
        JsonObject Sensor(int id, string name, int eqt, int battery, bool open = false) => new()
        {
            ["_id"] = id, ["n"] = name, ["t"] = "wireless_sensor", ["eqt"] = eqt, ["s"] = open, ["bl"] = battery, ["ol"] = true, ["b"] = false,
        };
        var devices = new JsonArray
        {
            new JsonObject
            {
                ["_id"] = 1, ["n"] = "Smart Hub", ["t"] = "primary_touch_link_device", ["ol"] = true,
                ["panel_volume_level"] = 60, ["beeps_and_alerts_volume_level"] = 70, ["doorbell_volume_level"] = 80,
                ["led_brightness"] = 200, ["play_panel_clicks"] = true, ["cell_signal_strength"] = "-71",
            },
            new JsonObject { ["_id"] = 10, ["n"] = "Front Door Lock", ["t"] = "door_lock_device", ["s"] = true, ["bl"] = 86, ["ol"] = true },
            new JsonObject { ["_id"] = 11, ["n"] = "Back Door Lock", ["t"] = "door_lock_device", ["s"] = false, ["bl"] = 18, ["ol"] = true },
            new JsonObject { ["_id"] = 12, ["n"] = "Garage Door", ["t"] = "garage_door_device", ["s"] = (int)GarageState.Closed, ["ol"] = true },
            new JsonObject
            {
                ["_id"] = 13, ["n"] = "Thermostat", ["t"] = "thermostat_device", ["val"] = 21.5, ["hmdt"] = 44,
                ["csp"] = 24.0, ["hsp"] = 20.0, ["om"] = (int)ThermostatMode.Auto, ["fm"] = 0, ["ol"] = true,
                ["scht"] = new JsonObject { ["home"] = 20.5, ["away"] = 17.0, ["sleep"] = 18.5, ["vacation"] = 15.5 },
                ["scct"] = new JsonObject { ["home"] = 24.0, ["away"] = 27.0, ["sleep"] = 23.5, ["vacation"] = 29.0 },
            },
            new JsonObject { ["_id"] = 14, ["n"] = "Porch Light", ["t"] = "binary_switch_device", ["s"] = false, ["ol"] = true },
            new JsonObject { ["_id"] = 15, ["n"] = "Living Room Lamp", ["t"] = "multilevel_switch_device", ["s"] = true, ["val"] = 60, ["ol"] = true },
            Cam(20, "Front Doorbell", "doorbell_camera_device"),
            Cam(21, "Driveway", "camera_device"),
            Cam(22, "Backyard", "camera_device"),
            Sensor(30, "Front Door", 1, 92),
            Sensor(31, "Back Door", 1, 88),
            Sensor(32, "Garage Entry", 1, 75),
            Sensor(33, "Living Room Window", 1, 64, open: true),
            Sensor(34, "Kitchen Smoke", 4, 97),
            Sensor(35, "Water Heater Leak", 8, 81),
            Sensor(36, "Hallway Motion", 2, 70),
        };
        return new JsonObject
        {
            ["panid"] = long.Parse(PanelId),
            ["sn"] = "Demo Home",
            ["wisd"] = "Demo-Home-WiFi",
            ["sinfo"] = new JsonObject { ["srv"] = new JsonObject { ["lvl"] = "professional_monitoring" } },
            ["par"] = new JsonArray(new JsonObject { ["parid"] = 1, ["n"] = "Home", ["s"] = 0, ["d"] = devices }),
            ["u"] = new JsonArray
            {
                new JsonObject { ["_id"] = 501, ["n"] = "Alex", ["ad"] = true, ["pad"] = true, ["hasPanelPin"] = true, ["hasLockPin"] = true, ["reg"] = true, ["lids"] = new JsonArray(10, 11) },
                new JsonObject { ["_id"] = 502, ["n"] = "Jordan", ["ad"] = true, ["hasPanelPin"] = true, ["hasLockPin"] = true, ["reg"] = true, ["lids"] = new JsonArray(10, 11) },
                new JsonObject { ["_id"] = 503, ["n"] = "Dog walker", ["ad"] = false, ["hasPanelPin"] = false, ["hasLockPin"] = true, ["reg"] = false, ["lids"] = new JsonArray(11) },
            },
        };
    }

    private static JsonArray BuildRoutines()
    {
        JsonObject Act(string name, string demo) => new() { ["name"] = name, ["demo"] = demo };
        return new JsonArray
        {
            new JsonObject { ["id"] = "rt1", ["name"] = "Good night", ["isShortcut"] = true, ["enabled"] = true,
                ["actions"] = new JsonArray(Act("Lock all doors", "lock-all"), Act("Turn off lights", "lights-off"), Act("Arm Stay", "arm-stay")) },
            new JsonObject { ["id"] = "rt2", ["name"] = "I'm home", ["isShortcut"] = true, ["enabled"] = true,
                ["actions"] = new JsonArray(Act("Disarm", "disarm"), Act("Turn on lights", "lights-on")) },
            new JsonObject { ["id"] = "rt3", ["name"] = "Lights off", ["isShortcut"] = true, ["enabled"] = true,
                ["actions"] = new JsonArray(Act("Turn off lights", "lights-off")) },
            new JsonObject { ["id"] = "rt4", ["name"] = "Porch light at sunset", ["isShortcut"] = false, ["enabled"] = true,
                ["triggers"] = new JsonArray(new JsonObject { ["name"] = "At sunset" }),
                ["actions"] = new JsonArray(new JsonObject { ["name"] = "Turn on", ["devices"] = new JsonArray(new JsonObject { ["name"] = "Porch Light" }) }) },
            new JsonObject { ["id"] = "rt5", ["name"] = "Lock up when armed Away", ["isShortcut"] = false, ["enabled"] = false,
                ["triggers"] = new JsonArray(new JsonObject { ["name"] = "When the system is armed Away" }),
                ["actions"] = new JsonArray(Act("Lock all doors", "lock-all")) },
        };
    }

    private static JsonObject Catalog(string kind) => new()
    {
        [kind] = kind switch
        {
            "triggers" => new JsonArray(new JsonObject { ["name"] = "At sunset" }, new JsonObject { ["name"] = "When the system is armed Away" }),
            "actions" => new JsonArray(new JsonObject { ["name"] = "Turn on" }, new JsonObject { ["name"] = "Lock all doors" }),
            _ => new JsonArray(),
        },
    };

    private static JsonObject AuthUser() => new()
    {
        ["u"] = new JsonObject
        {
            ["n"] = "Demo User",
            ["e"] = "demo@example.com",
            ["mfa_enabled"] = false,
            ["system"] = new JsonArray(new JsonObject { ["panid"] = long.Parse(PanelId), ["sn"] = "Demo Home", ["ad"] = true }),
        },
    };

    private static JsonObject Insights()
    {
        JsonObject I(string t, string m, string v, string d) => new()
        { ["displayName"] = t, ["summaryMetricName"] = m, ["summaryMetricValue"] = v, ["summaryMetricSubheader"] = d };
        var end = DateTime.Today;
        return new JsonObject
        {
            ["insightsData"] = new JsonObject
            {
                ["shortDate"] = $"{end.AddDays(-7):MMM d} – {end:MMM d}",
                ["insights"] = new JsonArray(
                    I("Security", "Times armed", "9", "2 more than last week"),
                    I("Cameras", "Person detections", "31", "Most at Front Doorbell"),
                    I("Climate", "Average indoor", "70°F", "Humidity 44%")),
            },
        };
    }

    private JsonArray SensorList()
    {
        var arr = new JsonArray();
        foreach (var d in Devices().Where(d => d["t"]?.ToString() == "wireless_sensor"))
            arr.Add(new JsonObject
            {
                ["id"] = d["_id"]!.ToString(),
                ["name"] = d["n"]!.ToString(),
                ["category"] = d["eqt"]!.GetValue<int>() switch
                {
                    1 => "SENSOR_CATEGORY_DOOR_WINDOW", 2 => "SENSOR_CATEGORY_MOTION", 4 => "SENSOR_CATEGORY_SMOKE",
                    8 => "SENSOR_CATEGORY_WATER", _ => "SENSOR_CATEGORY_OTHER",
                },
                ["battery_level"] = d["bl"]!.ToString(),
                ["online"] = true,
                ["firmware"] = "2.4.1",
                ["chime"] = d["eqt"]!.GetValue<int>() == 1 ? "1" : "0",
                ["voice_enabled"] = d["eqt"]!.GetValue<int>() == 1,
            });
        return arr;
    }

    private static JsonObject Rule(string id, string name, string cat, bool on) => new()
    { ["_id"] = id, ["n"] = name, ["cat"] = new JsonArray(cat), ["s"] = on };

    // ================================================================ helpers
    private JsonObject Partition() => (JsonObject)_system["par"]![0]!;
    private IEnumerable<JsonObject> Devices() => ((JsonArray)Partition()["d"]!).OfType<JsonObject>();
    private IEnumerable<JsonObject> Users() => ((JsonArray)_system["u"]!).OfType<JsonObject>();
    private IEnumerable<JsonObject> Cameras() => Devices().Where(d => d["t"]!.ToString().Contains("camera"));
    private JsonObject? Device(string id) => Devices().FirstOrDefault(d => d["_id"]?.ToString() == id);
    private string CameraName(string id) => Device(id)?["n"]?.ToString() ?? "Camera";

    private static int IndexOf(JsonArray a, string key, string id)
    {
        for (int i = 0; i < a.Count; i++) if (a[i]?[key]?.ToString() == id) return i;
        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = p.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return d;
    }

    private static HttpResponseMessage Ok() => Json(new JsonObject { ["ok"] = true });
    private static HttpResponseMessage Json(JsonObject o) => Json(HttpStatusCode.OK, o);
    private static HttpResponseMessage Json(HttpStatusCode code, JsonObject o) => new(code)
    {
        Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Image(byte[] jpeg)
    {
        var c = new ByteArrayContent(jpeg);
        c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = c };
    }
}
