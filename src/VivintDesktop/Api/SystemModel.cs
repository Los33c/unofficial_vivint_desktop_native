using System.Text.Json;

namespace VivintDesktop.Api;

public enum DeviceCategory
{
    Panel, Lock, Garage, Thermostat, Camera, Switch, Dimmer, Sensor, Other
}

/// <summary>Panel security state (SecurityState in the app; wire values match vivintpy).</summary>
public enum ArmedState
{
    Unknown = -1,
    Disarmed = 0,
    ArmingAway = 1,       // exit delay
    ArmingStay = 2,       // exit delay
    ArmedStay = 3,
    ArmedAway = 4,
    EntryDelayStay = 5,
    EntryDelayAway = 6,
    Alarm = 7,
    AlarmFire = 8,
    Disabled = 11,
    WalkTest = 12,
}

/// <summary>Garage door "s" values on the wire.</summary>
public enum GarageState { Unknown = 0, Closed = 1, Closing = 2, Stopped = 3, Opening = 4, Open = 5 }

/// <summary>Thermostat operating mode ("om").</summary>
public enum ThermostatMode { Off = 0, Heat = 1, Cool = 2, Auto = 3, EmergencyHeat = 4 }

public static class StateText
{
    public static ArmedState ParseArmed(int v) => v switch
    {
        >= 0 and <= 8 => (ArmedState)v,
        9 or 11 => ArmedState.Disabled,
        10 or 12 => ArmedState.WalkTest,
        _ => ArmedState.Unknown,
    };

    public static string Describe(ArmedState s) => s switch
    {
        ArmedState.Disarmed => "Disarmed",
        ArmedState.ArmingAway => "Arming Away…",
        ArmedState.ArmingStay => "Arming Stay…",
        ArmedState.ArmedStay => "Armed Stay",
        ArmedState.ArmedAway => "Armed Away",
        ArmedState.EntryDelayStay or ArmedState.EntryDelayAway => "Entry delay — disarm now",
        ArmedState.Alarm => "ALARM",
        ArmedState.AlarmFire => "FIRE ALARM",
        ArmedState.Disabled => "Security disabled",
        ArmedState.WalkTest => "Walk test",
        _ => "Unknown",
    };

    public static bool IsArmed(ArmedState s) => s is ArmedState.ArmedStay or ArmedState.ArmedAway
        or ArmedState.EntryDelayAway or ArmedState.EntryDelayStay or ArmedState.Alarm or ArmedState.AlarmFire;
}

/// <summary>A device parsed out of the legacy system JSON (par[].d[]).</summary>
public sealed class Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TypeString { get; init; }
    public required DeviceCategory Category { get; init; }
    public string PartitionId { get; init; } = "1";

    public bool? State { get; init; }       // s as bool: locked / on / sensor tripped
    public int? IntState { get; init; }     // s as int (garage door)
    public int? Level { get; init; }        // val: dimmer 0-100
    public int? Battery { get; init; }      // bl
    public bool? Online { get; init; }      // ol
    public bool Bypassed { get; init; }     // b (sensors)
    public bool Tampered { get; init; }     // ta
    public int? EquipmentType { get; init; } // eqt (sensors)

    // thermostat (°C on the wire)
    public double? Temperature { get; init; }   // val
    public double? Humidity { get; init; }      // hmdt
    public double? CoolSetpoint { get; init; }  // csp
    public double? HeatSetpoint { get; init; }  // hsp
    public int? Mode { get; init; }             // om
    public int? FanMode { get; init; }          // fm

    public string? ThumbnailDate { get; init; } // ctd (cameras)
    public bool Privacy { get; init; }          // cpri (cameras)
    public JsonElement Raw { get; init; }

    /// <summary>First URL in a camera stream field (ciu/cius/cetu/cetus/ceu/ceus…).</summary>
    public string? StreamUrl(string key)
    {
        if (Raw.ValueKind != JsonValueKind.Object || !Raw.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } u) return u;
        return null;
    }

    public GarageState Garage => IntState is { } g && Enum.IsDefined(typeof(GarageState), g)
        ? (GarageState)g : GarageState.Unknown;

    public bool GarageIsOpen => Garage is GarageState.Open or GarageState.Opening or GarageState.Stopped;

    public string SensorKind => EquipmentType switch
    {
        1 => "Door/Window",
        2 => "Motion",
        3 => "Glass break",
        4 => "Smoke",
        5 => "Carbon monoxide",
        6 => "Freeze",
        7 => "Heat",
        8 => "Water",
        10 => "Temperature",
        11 => "Emergency",
        _ => "Sensor",
    };

    public string StateText => Category switch
    {
        DeviceCategory.Lock => State switch { true => "Locked", false => "Unlocked", _ => "Unknown" },
        DeviceCategory.Garage => Garage switch
        {
            GarageState.Open => "Open",
            GarageState.Closed => "Closed",
            GarageState.Opening => "Opening…",
            GarageState.Closing => "Closing…",
            GarageState.Stopped => "Stopped (partly open)",
            _ => "Unknown",
        },
        DeviceCategory.Switch => State == true ? "On" : "Off",
        DeviceCategory.Dimmer => (Level ?? 0) > 0 || State == true ? $"On · {Level ?? 100}%" : "Off",
        DeviceCategory.Sensor => (EquipmentType, State) switch
        {
            (_, null) => "—",
            (2, true) => "Motion detected",
            (2, false) => "No motion",
            (8, true) => "Water detected!",
            (8, false) => "Dry",
            (4 or 5 or 7 or 6 or 3 or 11, true) => "Triggered!",
            (4 or 5 or 7 or 6 or 3 or 11, false) => "OK",
            (_, true) => "Open",
            (_, false) => "Closed",
        },
        DeviceCategory.Camera => Online == false ? "Offline" : "Online",
        _ => Online == false ? "Offline" : "",
    };

    /// <summary>True when the state deserves attention (open, unlocked, triggered…).</summary>
    public bool Alert => Category switch
    {
        DeviceCategory.Lock => State == false,
        DeviceCategory.Garage => GarageIsOpen,
        DeviceCategory.Sensor => State == true && EquipmentType != 2,
        _ => false,
    } || Online == false || Tampered;
}

public sealed class Partition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public ArmedState Armed { get; init; } = ArmedState.Unknown;
}

/// <summary>A system (panel) the user has access to, from /api/authuser.</summary>
public sealed record SystemRef(string PanelId, string Name, bool IsAdmin)
{
    public string Label => Name;
    public override string ToString() => Name;

    public static List<SystemRef> FromAuthUser(JsonElement user)
    {
        var list = new List<SystemRef>();
        JsonElement root = user;
        if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("u", out var u)) root = u;
        if (root.ValueKind != JsonValueKind.Object) return list;
        foreach (var key in new[] { "system", "systems" })
        {
            if (!root.TryGetProperty(key, out var systems) || systems.ValueKind != JsonValueKind.Array) continue;
            foreach (var s in systems.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var pid = Json.Str(s, "panid", "panelId", "id", "_id");
                if (pid is null || list.Any(x => x.PanelId == pid)) continue;
                var name = Json.Str(s, "sn", "name", "nm") ?? $"Panel {pid}";
                var admin = Json.TryBoolish(s, "ad", out var a) && a;
                list.Add(new SystemRef(pid, name, admin));
            }
        }
        return list;
    }

    public static string? UserName(JsonElement user)
    {
        var root = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("u", out var u) ? u : user;
        if (root.ValueKind != JsonValueKind.Object) return null;
        return Json.Str(root, "n", "name", "fn") ?? Json.Str(root, "e", "email");
    }
}

/// <summary>The whole system: panel + partitions + flattened device list.</summary>
public sealed class SystemState
{
    public required string PanelId { get; init; }
    public string Name { get; init; } = "Home";
    public List<Partition> Partitions { get; } = new();
    public List<Device> Devices { get; } = new();

    public ArmedState Armed =>
        Partitions.Count > 0 ? Partitions[0].Armed : ArmedState.Unknown;

    public static SystemState Parse(JsonElement root, string panelId, string name)
    {
        var sys = new SystemState { PanelId = panelId, Name = name };
        JsonElement system = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("system", out var s))
            system = s;

        // Partitions (par[]) carry the armed state (s) and device list (d[]).
        if (system.ValueKind == JsonValueKind.Object && system.TryGetProperty("par", out var par)
            && par.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in par.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var pid = Json.Str(p, "parid", "partitionId") ?? "1";
                var armed = Json.TryInt(p, "s", out var sv) ? StateText.ParseArmed(sv) : ArmedState.Unknown;
                sys.Partitions.Add(new Partition { Id = pid, Name = Json.Str(p, "n", "name") ?? "", Armed = armed });
                if (p.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array)
                    foreach (var dev in d.EnumerateArray())
                        AddDevice(sys, dev, pid);
            }
        }
        // Some payloads also carry a flat devices array.
        foreach (var key in new[] { "devices", "d" })
            if (system.ValueKind == JsonValueKind.Object && system.TryGetProperty(key, out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                foreach (var dev in arr.EnumerateArray())
                    AddDevice(sys, dev, sys.Partitions.FirstOrDefault()?.Id ?? "1");

        // Fallback: sweep the tree for anything that looks like a device.
        if (sys.Devices.Count == 0)
            SweepDevices(sys, root, sys.Partitions.FirstOrDefault()?.Id ?? "1");

        return sys;
    }

    private static void SweepDevices(SystemState sys, JsonElement el, string partitionId, int depth = 0)
    {
        if (depth > 12) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
                    && (el.TryGetProperty("_id", out _) || el.TryGetProperty("id", out _)))
                    AddDevice(sys, el, partitionId);
                foreach (var p in el.EnumerateObject())
                    SweepDevices(sys, p.Value, partitionId, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    SweepDevices(sys, item, partitionId, depth + 1);
                break;
        }
    }

    private static void AddDevice(SystemState sys, JsonElement dev, string partitionId)
    {
        if (dev.ValueKind != JsonValueKind.Object) return;
        var type = Json.Str(dev, "t") ?? "";
        if (type.Length == 0) return;
        var id = Json.Str(dev, "_id", "id");
        if (id is null || sys.Devices.Any(x => x.Id == id)) return;

        if (Json.TryBoolish(dev, "hidden", out var hid) && hid) return;
        if (type.EndsWith("_service", StringComparison.OrdinalIgnoreCase)) return;
        var cat = Categorize(type);
        // Garage door "s" is an int enum; everything else is boolish.
        int? intState = Json.TryInt(dev, "s", out var si) ? si : null;
        bool? state = Json.TryBoolish(dev, "s", out var st) ? st : null;
        if (cat == DeviceCategory.Garage) state = null;

        double? temp = null;
        if (cat == DeviceCategory.Thermostat && Json.TryDouble(dev, "val", out var tv)) temp = tv;
        if (Json.TryDouble(dev, "current_temperature", out var ctv)) temp = ctv;

        sys.Devices.Add(new Device
        {
            Id = id,
            Name = Json.Str(dev, "n", "name") ?? type,
            TypeString = type,
            Category = cat,
            PartitionId = Json.Str(dev, "parid") ?? partitionId,
            State = state,
            IntState = intState,
            Level = cat == DeviceCategory.Dimmer && Json.TryDouble(dev, "val", out var lv) ? (int)Math.Round(lv) : null,
            Battery = Json.TryInt(dev, "bl", out var bl) ? bl : null,
            Online = Json.TryBoolish(dev, "ol", out var ol) ? ol : null,
            Bypassed = Json.TryBoolish(dev, "b", out var b) && b,
            Tampered = Json.TryBoolish(dev, "ta", out var ta) && ta,
            EquipmentType = Json.TryInt(dev, "eqt", out var eqt) ? eqt : null,
            Temperature = temp,
            Humidity = Json.TryDouble(dev, "hmdt", out var h) ? h : null,
            CoolSetpoint = Json.TryDouble(dev, "csp", out var csp) ? csp : null,
            HeatSetpoint = Json.TryDouble(dev, "hsp", out var hsp) ? hsp : null,
            Mode = Json.TryInt(dev, "om", out var om) ? om : null,
            FanMode = Json.TryInt(dev, "fm", out var fm) ? fm : null,
            ThumbnailDate = Json.Str(dev, "ctd"),
            Privacy = Json.TryBoolish(dev, "cpri", out var cp) && cp,
            Raw = dev.Clone(),
        });
    }

    public static DeviceCategory Categorize(string t)
    {
        t = t.ToLowerInvariant();
        if (t.Contains("door_lock") || t == "lock_device") return DeviceCategory.Lock;
        if (t.Contains("garage_door")) return DeviceCategory.Garage;
        if (t.Contains("thermostat")) return DeviceCategory.Thermostat;
        if (t.Contains("camera") || t.Contains("doorbell")) return DeviceCategory.Camera;
        if (t.Contains("multilevel_switch") || t.Contains("dimmer")) return DeviceCategory.Dimmer;
        if (t.Contains("binary_switch") || t.Contains("outlet")) return DeviceCategory.Switch;
        if (t.Contains("sensor")) return DeviceCategory.Sensor;
        if (t.Contains("panel") || t.Contains("primary_touch_link")) return DeviceCategory.Panel;
        return DeviceCategory.Other;
    }
}

/// <summary>History event from GET {panel}/1/history ({"his":[...]}).</summary>
public sealed record HistoryEvent(DateTimeOffset? When, string Device, string Message, string Type)
{
    public string Id { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string? ThumbnailUrl { get; init; }   // thu (signed image URL)
    public string? ClipName { get; init; }       // clu — present when a video clip exists
    public string Icon { get; init; } = "";
    public string HistoryDay { get; init; } = "";   // hdt (needed to delete a clip)
    public int? ClipSeconds { get; init; }           // clen
    public bool HasClip => !string.IsNullOrEmpty(ClipName);

    public string WhenText => When?.ToLocalTime().ToString("ddd MMM d, h:mm tt") ?? "";

    public static List<HistoryEvent> Parse(JsonElement j)
    {
        var list = new List<HistoryEvent>();
        JsonElement arr = default;
        bool found = j.ValueKind == JsonValueKind.Array;
        if (found) arr = j;
        else if (j.ValueKind == JsonValueKind.Object)
            foreach (var k in new[] { "his", "history", "events", "items" })
                if (j.TryGetProperty(k, out arr) && arr.ValueKind == JsonValueKind.Array) { found = true; break; }
        if (!found) return list;

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            DateTimeOffset? when = null;
            var ts = Json.Str(e, "ts", "timestamp", "dt");
            if (ts is not null)
            {
                if (DateTimeOffset.TryParse(ts, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)) when = dto;
                else if (long.TryParse(ts, out var epoch))
                    when = epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            list.Add(new HistoryEvent(when,
                Json.Str(e, "dn", "deviceName") ?? "",
                Json.Str(e, "me", "message", "msg") ?? "",
                Json.Str(e, "evt", "type") ?? "")
            {
                Id = Json.Str(e, "_id", "id") ?? "",
                DeviceId = Json.Str(e, "did") ?? "",
                ThumbnailUrl = Json.Str(e, "thu"),
                ClipName = Json.Str(e, "clu"),
                Icon = Json.Str(e, "icon") ?? "",
                HistoryDay = Json.Str(e, "hdt") ?? "",
                ClipSeconds = Json.TryInt(e, "clen", out var clen) ? clen : null,
            });
        }
        return list;
    }
}

/// <summary>Lenient JSON accessors (the legacy API mixes numbers/strings/bools).</summary>
internal static class Json
{
    public static string? Str(JsonElement o, params string[] keys)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (o.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
        return null;
    }

    public static bool TryInt(JsonElement o, string k, out int val)
    {
        val = 0;
        if (!o.TryGetProperty(k, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out val)) return true;
            if (v.TryGetDouble(out var d)) { val = (int)Math.Round(d); return true; }
        }
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out val);
    }

    public static bool TryDouble(JsonElement o, string k, out double val)
    {
        val = 0;
        if (!o.TryGetProperty(k, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out val);
        return v.ValueKind == JsonValueKind.String &&
               double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out val);
    }

    public static bool TryBoolish(JsonElement o, string k, out bool val)
    {
        val = false;
        if (!o.TryGetProperty(k, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: val = true; return true;
            case JsonValueKind.False: val = false; return true;
            case JsonValueKind.Number when v.TryGetDouble(out var n): val = n != 0; return true;
            default: return false;
        }
    }
}
