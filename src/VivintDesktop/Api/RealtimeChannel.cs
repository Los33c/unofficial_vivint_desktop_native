using System.Net.Http;
using System.Text.Json;

namespace VivintDesktop.Api;

/// <summary>
/// Real-time updates. The phone app subscribes to the PubNub channel
/// "PlatformChannel#{mbc}" (mbc = the account's broadcast channel from
/// /api/authuser) and periodically calls PUT /api/renew-channel to keep it
/// alive. This uses PubNub's plain HTTP long-poll subscribe protocol, so it
/// needs no SDK. Only the subscribe key is used: this client never publishes.
/// </summary>
public sealed class RealtimeChannel : IDisposable
{
    private const string SubscribeKey = "sub-c-6fb03d68-6a78-11e2-ae8f-12313f022c90";
    private const string Origin = "https://ps.pndsn.com";

    private readonly VivintClient _client;
    private readonly string _channel;
    private readonly string _uuid;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(330) };
    private CancellationTokenSource? _cts;

    /// <summary>A message arrived (raw PubNub payload). Raised on a worker thread.</summary>
    public event Action<JsonElement>? Message;
    /// <summary>Connected (true) or lost (false). Raised on a worker thread.</summary>
    public event Action<bool>? Connection;

    public bool Connected { get; private set; }

    public RealtimeChannel(VivintClient client, string broadcastChannel)
    {
        _client = client;
        _channel = "PlatformChannel#" + broadcastChannel;
        _uuid = "vivintdesktop-" + DeviceId.Get();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Base.UserAgent);
    }

    /// <summary>The broadcast channel id ("mbc") from the authuser payload.</summary>
    public static string? BroadcastChannel(JsonElement authUser)
    {
        var u = authUser.ValueKind == JsonValueKind.Object && authUser.TryGetProperty("u", out var x) ? x : authUser;
        return Json.Str(u, "mbc");
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => SubscribeLoop(ct), ct);
        _ = Task.Run(() => RenewLoop(ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        SetConnected(false);
    }

    private void SetConnected(bool on)
    {
        if (Connected == on) return;
        Connected = on;
        Connection?.Invoke(on);
    }

    private async Task RenewLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _client.SendAsync(HttpMethod.Put, Base.VivintskyApi, "renew-channel", ct: ct); }
            catch (OperationCanceledException) { return; }
            catch { /* next round */ }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SubscribeLoop(CancellationToken ct)
    {
        string tt = "0", tr = "";
        var backoff = 2;
        var url0 = $"{Origin}/v2/subscribe/{SubscribeKey}/{Uri.EscapeDataString(_channel)}/0";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"{url0}?tt={tt}&uuid={Uri.EscapeDataString(_uuid)}" + (tr.Length > 0 ? $"&tr={tr}" : "");
                using var resp = await _http.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("t", out var t))
                {
                    tt = Json.Str(t, "t") ?? tt;
                    tr = Json.Str(t, "r") ?? tr;
                }
                SetConnected(true);
                backoff = 2;
                if (root.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.Array)
                    foreach (var msg in m.EnumerateArray())
                        if (msg.TryGetProperty("d", out var d))
                        {
                            var payload = d.ValueKind == JsonValueKind.String && TryParse(d.GetString()!, out var p) ? p : d.Clone();
                            try { Message?.Invoke(payload); } catch { /* listener bug must not kill the loop */ }
                        }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                SetConnected(false);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { return; }
                backoff = Math.Min(backoff * 2, 60);
                tt = "0"; tr = "";
            }
        }
    }

    private static bool TryParse(string s, out JsonElement e)
    {
        try { using var d = JsonDocument.Parse(s); e = d.RootElement.Clone(); return true; }
        catch { e = default; return false; }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
