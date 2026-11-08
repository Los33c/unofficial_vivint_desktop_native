using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VivintDesktop.Api;

/// <summary>
/// Smart video search ("person at the front door yesterday").
/// Same flow as the phone app:
///  1. Vivint id token → token-exchange service → Firebase custom token
///  2. Firebase signInWithCustomToken (project dvr-search-production) → Firebase id token
///  3. POST https://api.search.vivint.ai/query
///  4. Results are written to Firestore search/{system}/queries/{batch}/results; we poll them over REST.
/// </summary>
public sealed class VideoSearch : IDisposable
{
    private const string ExchangeUrl = "https://token-exchange-164791117116.us-central1.run.app/?custom-token=true";
    private const string FirebaseKey = "AIzaSyChkX-D2Ps1a8e_DYLMB7sM4y6FSl2QuZs";
    private const string Project = "dvr-search-production";
    private const string Bucket = "dvr-search-thumbnails";
    private const string QueryUrl = "https://api.search.vivint.ai/query";

    private readonly VivintClient _client;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _fbToken;
    /// <summary>Last raw exchange, for diagnostics.</summary>
    public string? LastDebug { get; private set; }
    private DateTimeOffset _fbExpiry;

    public VideoSearch(VivintClient client)
    {
        _client = client;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Base.UserAgent);
    }

    private async Task<string> FirebaseTokenAsync(CancellationToken ct)
    {
        if (_client.DemoMode) throw new InvalidOperationException("Video search isn't available in demo mode.");
        if (_fbToken is not null && DateTimeOffset.UtcNow < _fbExpiry) return _fbToken;
        await _client.EnsureFreshAsync(ct);
        var id = _client.IdToken ?? throw new InvalidOperationException("Not signed in.");

        using var ex = new HttpRequestMessage(HttpMethod.Get, ExchangeUrl);
        ex.Headers.Authorization = new AuthenticationHeaderValue("Bearer", id);
        using var exResp = await _http.SendAsync(ex, ct);
        var exBody = await exResp.Content.ReadAsStringAsync(ct);
        if (!exResp.IsSuccessStatusCode) throw new InvalidOperationException($"Search sign-in failed (HTTP {(int)exResp.StatusCode}).");
        var custom = JsonNode.Parse(exBody)?["custom-token"]?.GetValue<string>()
                     ?? throw new InvalidOperationException("Search sign-in returned no token.");

        var signIn = new JsonObject { ["token"] = custom, ["returnSecureToken"] = true };
        using var si = await _http.PostAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={FirebaseKey}",
            new StringContent(signIn.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var siBody = await si.Content.ReadAsStringAsync(ct);
        if (!si.IsSuccessStatusCode) throw new InvalidOperationException($"Search sign-in failed (Firebase HTTP {(int)si.StatusCode}).");
        var j = JsonNode.Parse(siBody)!;
        _fbToken = j["idToken"]!.GetValue<string>();
        var secs = int.TryParse(j["expiresIn"]?.GetValue<string>(), out var s) ? s : 3600;
        _fbExpiry = DateTimeOffset.UtcNow.AddSeconds(secs - 120);
        return _fbToken;
    }

    /// <summary>Run a search and wait (up to ~40 s) for results.</summary>
    public async Task<List<SearchHit>> SearchAsync(string panelId, string text, DateTimeOffset? from, DateTimeOffset? to,
        int limit = 40, CancellationToken ct = default)
    {
        var token = await FirebaseTokenAsync(ct);
        var batch = Guid.NewGuid().ToString().ToLowerInvariant();
        var q = new JsonObject
        {
            ["queryId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
            ["queryText"] = text,
            ["limit"] = limit,
        };
        if (from is { } f && to is { } t)
            q["input"] = new JsonObject { ["timeRange"] = new JsonObject { ["start"] = Iso(f), ["end"] = Iso(t) } };
        var body = new JsonObject
        {
            ["systemId"] = panelId,
            ["batchId"] = batch,
            ["householdUtcOffset"] = (int)Math.Round(TimeZoneInfo.Local.BaseUtcOffset.TotalHours),
            ["batchQueries"] = new JsonArray(q),
            ["version"] = "1.0.0",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, QueryUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Search failed (HTTP {(int)resp.StatusCode}). {Trim(respBody)}");
        if (respBody.Contains("\"flagged\"")) throw new InvalidOperationException("That search can't be run. Try different words.");

        var url = $"https://firestore.googleapis.com/v1/projects/{Project}/databases/(default)/documents/search/{panelId}/queries/{batch}/results";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(1500, ct);
            using var fr = new HttpRequestMessage(HttpMethod.Get, url);
            fr.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var fresp = await _http.SendAsync(fr, ct);
            var fbody = await fresp.Content.ReadAsStringAsync(ct);
            LastDebug = $"query: {Trim(respBody)} | firestore {(int)fresp.StatusCode}: {Trim(fbody)}";
            if (!fresp.IsSuccessStatusCode) continue;
            var hits = ParseResults(fbody);
            if (hits is not null) return hits;
        }
        return new();
    }

    /// <summary>Raw Firestore REST GET below the project's documents root (diagnostics).</summary>
    public async Task<string> FirestoreGetAsync(string path, CancellationToken ct = default)
    {
        var token = await FirebaseTokenAsync(ct);
        using var r = new HttpRequestMessage(HttpMethod.Get, $"https://firestore.googleapis.com/v1/projects/{Project}/databases/(default)/documents/{path}");
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(r, ct);
        return $"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}";
    }

    /// <summary>Download a result thumbnail (gs://bucket/path → Firebase Storage).</summary>
    public async Task<byte[]?> ThumbnailAsync(string gsUri, CancellationToken ct = default)
    {
        var prefix = $"gs://{Bucket}/";
        if (!gsUri.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = await FirebaseTokenAsync(ct);
        var path = Uri.EscapeDataString(gsUri[prefix.Length..]);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://firebasestorage.googleapis.com/v0/b/{Bucket}/o/{path}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Firebase", token);
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
    }

    /// <summary>null = no results document yet; empty list = finished with nothing found.</summary>
    private static List<SearchHit>? ParseResults(string body)
    {
        var root = JsonNode.Parse(body);
        if (root?["documents"] is not JsonArray docs || docs.Count == 0) return null;
        var hits = new List<SearchHit>();
        foreach (var d in docs)
        {
            var fields = Plain(d?["fields"] is JsonObject f ? new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = f.DeepClone() } } : null);
            if (fields?["responses"] is not JsonArray responses) continue;
            foreach (var r in responses)
                if (r?["docs"] is JsonArray items)
                    foreach (var it in items)
                    {
                        if (it is not JsonObject o) continue;
                        DateTimeOffset? when = o["timestamp"] is JsonObject ts && ts["seconds"] is { } sec
                            && long.TryParse(sec.ToString(), out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
                        if (when is null && o["timestamp"] is JsonValue tv && DateTimeOffset.TryParse(tv.ToString(), out var p)) when = p;
                        hits.Add(new SearchHit(
                            o["deviceUuid"]?.ToString() ?? "",
                            when,
                            o["uri"]?.ToString() ?? "",
                            double.TryParse(o["distance"]?.ToString(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var dist) ? dist : 0,
                            o["isGoodMatch"]?.ToString() == "true"));
                    }
        }
        return hits.OrderBy(h => h.Distance).ToList();
    }

    /// <summary>Convert Firestore's typed REST values ({stringValue:…}, {mapValue:{fields}} …) to plain JSON.</summary>
    private static JsonNode? Plain(JsonNode? v)
    {
        if (v is not JsonObject o) return null;
        if (o["mapValue"] is JsonObject m)
        {
            var r = new JsonObject();
            if (m["fields"] is JsonObject fs)
                foreach (var (k, fv) in fs) r[k] = Plain(fv);
            return r;
        }
        if (o["arrayValue"] is JsonObject a)
        {
            var r = new JsonArray();
            if (a["values"] is JsonArray vs) foreach (var x in vs) r.Add(Plain(x));
            return r;
        }
        if (o["stringValue"] is { } s) return JsonValue.Create(s.GetValue<string>());
        if (o["integerValue"] is { } i) return JsonValue.Create(long.Parse(i.GetValue<string>()));
        if (o["doubleValue"] is { } d) return JsonValue.Create(d.GetValue<double>());
        if (o["booleanValue"] is { } b) return JsonValue.Create(b.GetValue<bool>());
        if (o["timestampValue"] is { } t) return JsonValue.Create(t.GetValue<string>());
        return null;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}

/// <summary>One search match: which camera (uuid), when, and a thumbnail.</summary>
public sealed record SearchHit(string CameraUuid, DateTimeOffset? When, string ThumbnailUri, double Distance, bool GoodMatch);
