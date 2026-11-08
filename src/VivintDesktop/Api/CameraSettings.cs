using System.IO;

namespace VivintDesktop.Api;

/// <summary>The per-camera settings this app can read and change.</summary>
public sealed record CameraSettings
{
    /// <summary>1 = Low … 5 = Great; 0 = the camera didn't report one.</summary>
    public int VideoQuality { get; init; }
    public bool NightVision { get; init; }
    public bool StatusLight { get; init; }
    public bool RecordAudio { get; init; }
    public bool QuietMode { get; init; }
    public bool RecordOnPerson { get; init; }

    public static readonly string[] QualityNames = { "Low", "Fair", "Medium", "Good", "Great" };
}

/// <summary>
/// Camera settings over Vivint's <c>beam.Beam</c> gRPC service (the legacy REST route
/// PUT {panel}/{part}/cameras/{id} returns 404). Messages are small, so they are
/// encoded by hand here rather than pulling in protobuf tooling.
/// </summary>
public sealed class CameraSettingsService
{
    private const string Service = "https://grpc.vivintsky.com:50051/beam.Beam/";

    private readonly VivintClient _client;
    private readonly Dictionary<string, CameraSettings> _demo = new();

    public CameraSettingsService(VivintClient client) => _client = client;

    public async Task<CameraSettings> GetAsync(string panelId, string deviceId, CancellationToken ct = default)
    {
        if (_client.DemoMode) return DemoGet(deviceId);

        // GetCameraSettingsRequest { header = 3: SettingsRequestHeader { panel_device_id = 1: PanelDeviceIds } }
        var header = new ProtoWriter().Message(1, Ids(panelId, deviceId));
        var reply = await CallAsync("GetCameraSettings", new ProtoWriter().Message(3, header), ct).ConfigureAwait(false);
        return Parse(reply);
    }

    public Task SetVideoQualityAsync(string panel, string device, int quality, CancellationToken ct = default) =>
        SetAsync("SetVideoQuality", panel, device, quality, s => s with { VideoQuality = quality }, ct);

    public Task SetNightVisionAsync(string panel, string device, bool on, CancellationToken ct = default) =>
        SetAsync("SetNightVision", panel, device, on ? 1 : 0, s => s with { NightVision = on }, ct);

    public Task SetStatusLightAsync(string panel, string device, bool on, CancellationToken ct = default) =>
        SetAsync("SetStatusLight", panel, device, on ? 1 : 0, s => s with { StatusLight = on }, ct);

    public Task SetRecordAudioAsync(string panel, string device, bool on, CancellationToken ct = default) =>
        SetAsync("SetAudioRecording", panel, device, on ? 1 : 0, s => s with { RecordAudio = on }, ct);

    public Task SetQuietModeAsync(string panel, string device, bool on, CancellationToken ct = default) =>
        SetAsync("SetQuietMode", panel, device, on ? 1 : 0, s => s with { QuietMode = on }, ct);

    public Task SetRecordOnPersonAsync(string panel, string device, bool on, CancellationToken ct = default) =>
        SetAsync("SetRecordOnPersonDetected", panel, device, on ? 1 : 0, s => s with { RecordOnPerson = on }, ct);

    public async Task RebootAsync(string panel, string device, CancellationToken ct = default)
    {
        if (_client.DemoMode) return;
        await CallAsync("RebootCamera", Ids(panel, device), ct).ConfigureAwait(false);
    }

    // Every setter shares one shape: panel_id = 1, device_id = 2, value = 3.
    private async Task SetAsync(string method, string panel, string device, int value,
        Func<CameraSettings, CameraSettings> applyDemo, CancellationToken ct)
    {
        if (_client.DemoMode) { _demo[device] = applyDemo(DemoGet(device)); return; }
        await CallAsync(method, Ids(panel, device).Varint(3, (ulong)value), ct).ConfigureAwait(false);
    }

    private async Task<byte[]> CallAsync(string method, ProtoWriter request, CancellationToken ct)
    {
        var (status, message, body) = await _client.GrpcUnaryAsync(Service + method, request.ToArray(), ct).ConfigureAwait(false);
        if (status != 0)
            throw new InvalidOperationException($"Vivint rejected {method} (gRPC status {status}{(message.Length > 0 ? ": " + message : "")}).");
        return body;
    }

    private static ProtoWriter Ids(string panelId, string deviceId) => new ProtoWriter()
        .Varint(1, ulong.Parse(panelId))
        .Varint(2, ulong.Parse(deviceId));

    // Field numbers from GetCameraSettingsResponse; proto3 omits false/zero values.
    private static CameraSettings Parse(byte[] data)
    {
        var s = new CameraSettings();
        foreach (var (field, value) in ProtoReader.Varints(data))
            s = field switch
            {
                9 => s with { VideoQuality = (int)value },
                10 => s with { NightVision = value != 0 },
                13 => s with { StatusLight = value != 0 },
                14 => s with { RecordAudio = value != 0 },
                18 => s with { QuietMode = value != 0 },
                35 => s with { RecordOnPerson = value != 0 },
                _ => s,
            };
        return s;
    }

    private CameraSettings DemoGet(string deviceId) =>
        _demo.TryGetValue(deviceId, out var s) ? s
            : new CameraSettings { VideoQuality = 4, NightVision = true, StatusLight = true, RecordAudio = true, RecordOnPerson = true };
}

/// <summary>Minimal protobuf encoder: varint and nested-message fields only.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _out = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        if (value == 0) return this;   // proto3 default: not sent
        WriteRaw((ulong)(field << 3));
        WriteRaw(value);
        return this;
    }

    public ProtoWriter Message(int field, ProtoWriter inner)
    {
        var bytes = inner.ToArray();
        WriteRaw((ulong)((field << 3) | 2));
        WriteRaw((ulong)bytes.Length);
        _out.Write(bytes);
        return this;
    }

    public byte[] ToArray() => _out.ToArray();

    private void WriteRaw(ulong v)
    {
        while (v >= 0x80) { _out.WriteByte((byte)(v | 0x80)); v >>= 7; }
        _out.WriteByte((byte)v);
    }
}

/// <summary>Minimal protobuf decoder: yields top-level varint fields and skips the rest.</summary>
internal static class ProtoReader
{
    public static List<(int field, ulong value)> Varints(byte[] data)
    {
        var found = new List<(int, ulong)>();
        var i = 0;
        while (i < data.Length)
        {
            var tag = ReadVarint(data, ref i);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0: found.Add((field, ReadVarint(data, ref i))); break;
                case 1: i += 8; break;
                case 2: var len = ReadVarint(data, ref i); i = checked(i + (int)len); break;
                case 5: i += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type in field {field}.");
            }
            if (i > data.Length) throw new InvalidDataException("Truncated protobuf message.");
        }
        return found;
    }

    private static ulong ReadVarint(byte[] data, ref int i)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (i >= data.Length) throw new InvalidDataException("Truncated protobuf varint.");
            var b = data[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Protobuf varint too long.");
    }
}
