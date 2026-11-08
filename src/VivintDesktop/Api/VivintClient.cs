using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VivintDesktop.Api;

/// <summary>
/// Authentication + transport for the Vivint cloud.
///
/// Flow (standard OAuth2 Authorization Code + PKCE against the app's IdP):
///  1. GET  {issuer}/.well-known/openid-configuration
///  2. Open {authorization_endpoint}?... in an embedded browser so the user can
///     sign in on Vivint's own pages (this is where email/2FA challenges happen).
///  3. The IdP redirects to vivint://app/oauth_redirect?code=... — we capture it.
///  4. POST {token_endpoint}  grant_type=authorization_code + code_verifier -> tokens
///  5. Call https://www.vivintsky.com/api/...  with  Authorization: Bearer {token}
///
/// The refresh token is saved (DPAPI-encrypted) so later launches sign in
/// silently; tokens are refreshed automatically before they expire.
/// </summary>
public sealed class VivintClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _idp;        // discovery / token
    private readonly HttpClient _api;        // vivint API calls
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private readonly string _deviceId;

    // discovered endpoints
    private string _authorizationEndpoint = "";
    private string _tokenEndpoint = "";
    private string _endSessionEndpoint = "";

    // PKCE / CSRF state for the in-flight authorization
    private string _codeVerifier = "";
    private string _state = "";

    // tokens
    private string? _accessToken;
    private string? _refreshToken;
    private string? _idToken;
    private DateTimeOffset _accessExpiry = DateTimeOffset.MinValue;

    // Which token a host accepts as the bearer. The legacy /api surface is
    // known (vivintpy) to accept the id_token; other services the access
    // token. We start with a guess and learn from 401s.
    private readonly ConcurrentDictionary<string, bool> _hostUsesIdToken = new();

    public bool Authenticated => DemoMode || _accessToken is not null || _idToken is not null;

    // Demo mode: every request is answered by an in-memory sample home; nothing leaves the PC.
    private DemoBackend? _demo;
    public bool DemoMode => _demo is not null;

    /// <summary>Switch to demo mode (sample data, no Vivint account, no network).</summary>
    public void StartDemo()
    {
        ClearTokens();
        _demo = new DemoBackend();
    }
    public string? IdToken => _idToken;
    public string RedirectUri => Base.RedirectUri;

    /// <summary>Save the refresh token so the next launch signs in silently.</summary>
    public bool RememberMe { get; set; } = true;

    /// <summary>Raised when the session can no longer be refreshed.</summary>
    public event Action? SessionExpired;

    public VivintClient()
    {
        _deviceId = DeviceId.Get();

        HttpClient Make()
        {
            var h = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            var c = new HttpClient(h) { Timeout = Base.DefaultTimeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(Base.UserAgent);
            return c;
        }

        _idp = Make();
        _api = Make();
    }

    // ------------------------------------------------------------------ login
    /// <summary>
    /// Discover endpoints and return the authorization URL to load in a browser.
    /// Generates a fresh PKCE verifier and CSRF state for this attempt.
    /// </summary>
    public async Task<string> BuildAuthorizeUrlAsync(CancellationToken ct = default)
    {
        await DiscoverAsync(ct).ConfigureAwait(false);

        _codeVerifier = RandomUrlToken(32);
        _state = RandomUrlToken(16);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_codeVerifier)));

        var q = new StringBuilder();
        void Add(string k, string v) => q.Append(q.Length == 0 ? '?' : '&')
            .Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        Add("response_type", "code");
        Add("client_id", Base.ClientId);
        Add("redirect_uri", Base.RedirectUri);
        Add("scope", Base.Scope);
        Add("state", _state);
        Add("code_challenge", challenge);
        Add("code_challenge_method", "S256");
        Add("device_id", _deviceId);
        return _authorizationEndpoint + q;
    }

    /// <summary>True if a browser navigation target is the OAuth redirect URI.</summary>
    public bool IsRedirect(string uri) =>
        uri.StartsWith(Base.RedirectUri, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finish login from the intercepted redirect (vivint://app/oauth_redirect?code=...).
    /// Validates state, then exchanges the code (with the PKCE verifier) for tokens.
    /// </summary>
    public async Task CompleteLoginFromRedirectAsync(string redirectUri, CancellationToken ct = default)
    {
        var query = redirectUri.Contains('?') ? redirectUri[(redirectUri.IndexOf('?') + 1)..] : "";
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        var pairs = new Dictionary<string, string>();
        foreach (var p in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var a = p.Split('=', 2);
            pairs[Uri.UnescapeDataString(a[0])] = a.Length > 1 ? Uri.UnescapeDataString(a[1].Replace('+', ' ')) : "";
        }

        if (pairs.TryGetValue("error", out var err))
            throw new AuthException($"Sign-in failed: {err}" +
                (pairs.TryGetValue("error_description", out var d) ? $" — {d}" : ""));
        if (!pairs.TryGetValue("code", out var code) || code.Length == 0)
            throw new AuthException("No authorization code was returned.");
        if (pairs.TryGetValue("state", out var st) && _state.Length > 0 && st != _state)
            throw new AuthException("State mismatch — sign-in was not completed safely.");

        await ExchangeCodeAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Try to resume a saved session using the stored refresh token.
    /// Returns false if there is none or it is no longer valid.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        var saved = TokenStore.Load();
        if (saved is null) return false;
        try
        {
            await DiscoverAsync(ct).ConfigureAwait(false);
            _refreshToken = saved;
            return await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        if (_tokenEndpoint.Length > 0 && _authorizationEndpoint.Length > 0) return;
        var url = Base.OpenIdIssuer.TrimEnd('/') + "/.well-known/openid-configuration";
        using var resp = await _idp.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Could not read OpenID configuration (HTTP {(int)resp.StatusCode}).");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _authorizationEndpoint = root.GetProperty("authorization_endpoint").GetString() ?? "";
        _tokenEndpoint = root.GetProperty("token_endpoint").GetString() ?? "";
        if (root.TryGetProperty("end_session_endpoint", out var es))
            _endSessionEndpoint = es.GetString() ?? "";
        if (_authorizationEndpoint.Length == 0 || _tokenEndpoint.Length == 0)
            throw new AuthException("OpenID configuration is missing required endpoints.");
    }

    private async Task ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = Base.ClientId,
            ["redirect_uri"] = Base.RedirectUri,
            ["code"] = code,
        };
        if (_codeVerifier.Length > 0)
            form["code_verifier"] = _codeVerifier;
        using var resp = await _idp.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(form), ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Token exchange failed (HTTP {(int)resp.StatusCode}): {body}");
        StoreTokens(body);
    }

    private void StoreTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("access_token", out var at)) _accessToken = at.GetString();
        if (r.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } rts)
            _refreshToken = rts;
        if (r.TryGetProperty("id_token", out var it) && it.GetString() is { Length: > 0 } its)
            _idToken = its;
        var expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        if (RememberMe && _refreshToken is not null) TokenStore.Save(_refreshToken);
        else if (!RememberMe) TokenStore.Clear();
    }

    /// <summary>Exchange the refresh token for new tokens. False if it was rejected.</summary>
    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is null || _tokenEndpoint.Length == 0) return false;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = Base.ClientId,
            ["refresh_token"] = _refreshToken,
        };
        using var resp = await _idp.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(form), ct)
            .ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            StoreTokens(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return true;
        }
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // refresh token revoked / expired
            _refreshToken = null;
            TokenStore.Clear();
        }
        return false;
    }

    /// <summary>Refresh the tokens if they are about to expire (used before handing the id token to another service).</summary>
    public Task EnsureFreshAsync(CancellationToken ct = default) => EnsureFreshTokenAsync(false, ct);

    private async Task EnsureFreshTokenAsync(bool force, CancellationToken ct)
    {
        if (DemoMode) return;
        if (!Authenticated) throw new AuthException("Not signed in.");
        if (!force && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-60)) return;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-60)) return;
            bool ok;
            try { ok = await RefreshAsync(ct).ConfigureAwait(false); }
            catch (HttpRequestException) { ok = false; } // offline: keep current tokens
            // No usable refresh token and the current one is dead → session is over.
            if (!ok && _refreshToken is null && (force || DateTimeOffset.UtcNow >= _accessExpiry))
            {
                ClearTokens();
                SessionExpired?.Invoke(); // may be raised off the UI thread
                throw new AuthException("Your Vivint session has expired. Please sign in again.");
            }
        }
        finally { _tokenLock.Release(); }
    }

    private void ClearTokens()
    {
        _accessToken = _refreshToken = _idToken = null;
        _accessExpiry = DateTimeOffset.MinValue;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (_demo is not null) { _demo = null; return; }   // demo never touched the saved session
        try
        {
            if (_endSessionEndpoint.Length > 0 && _idToken is not null)
            {
                var url = _endSessionEndpoint +
                    $"?id_token_hint={Uri.EscapeDataString(_idToken)}&client_id={Base.ClientId}";
                using var _ = await _idp.GetAsync(url, ct).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
        ClearTokens();
        TokenStore.Clear();
        _hostUsesIdToken.Clear();
        foreach (Cookie c in _cookies.GetAllCookies()) c.Expired = true;
    }

    // -------------------------------------------------------------- transport
    /// <summary>
    /// Core request used by every generated endpoint and by the API explorer.
    /// <paramref name="relativeOrAbsolute"/> is either a full URL or a path that
    /// is resolved against <paramref name="baseUrl"/> (Retrofit rules).
    /// </summary>
    public async Task<ApiResult> SendAsync(
        HttpMethod method,
        string baseUrl,
        string relativeOrAbsolute,
        IReadOnlyDictionary<string, string>? query = null,
        object? body = null,
        AuthMode auth = AuthMode.Bearer,
        CancellationToken ct = default)
    {
        var url = BuildUrl(baseUrl, relativeOrAbsolute, query);
        string? json = body is null ? null : body as string ?? JsonSerializer.Serialize(body);
        using var resp = await SendCoreAsync(method, url, json, auth, null, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ApiResult.From((int)resp.StatusCode, text);
    }

    /// <summary>GET that returns raw bytes (images such as camera thumbnails).</summary>
    public async Task<(int status, byte[] data, string? contentType)> GetBytesAsync(
        string baseUrl, string path, IReadOnlyDictionary<string, string>? query = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var url = BuildUrl(baseUrl, path, query);
        using var resp = await SendCoreAsync(HttpMethod.Get, url, null, AuthMode.Bearer, headers, ct)
            .ConfigureAwait(false);
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ((int)resp.StatusCode, data, resp.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string url, string? json,
        AuthMode auth, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (_demo is { } demo) return await demo.HandleAsync(method, url, json, ct).ConfigureAwait(false);
        if (auth != AuthMode.Bearer)
            return await _api.SendAsync(Build(method, url, json, null, headers), ct).ConfigureAwait(false);

        await EnsureFreshTokenAsync(false, ct).ConfigureAwait(false);
        var host = new Uri(url).Host;
        bool useId = _hostUsesIdToken.TryGetValue(host, out var known)
            ? known
            : host.EndsWith("vivintsky.com", StringComparison.OrdinalIgnoreCase) && _idToken is not null;

        var resp = await _api.SendAsync(Build(method, url, json, TokenFor(useId), headers), ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) { _hostUsesIdToken.TryAdd(host, useId); return resp; }

        // 1) the host may want the other token
        var alt = TokenFor(!useId);
        if (alt is not null && alt != TokenFor(useId))
        {
            var r2 = await _api.SendAsync(Build(method, url, json, alt, headers), ct).ConfigureAwait(false);
            if (r2.StatusCode != HttpStatusCode.Unauthorized)
            {
                resp.Dispose();
                _hostUsesIdToken[host] = !useId;
                return r2;
            }
            r2.Dispose();
        }

        // 2) the token may have been revoked early — refresh once and retry
        await EnsureFreshTokenAsync(true, ct).ConfigureAwait(false);
        var r3 = await _api.SendAsync(Build(method, url, json, TokenFor(useId), headers), ct).ConfigureAwait(false);
        if (r3.StatusCode != HttpStatusCode.Unauthorized) { resp.Dispose(); return r3; }
        r3.Dispose();
        return resp;
    }

    private string? TokenFor(bool idToken) => idToken ? _idToken ?? _accessToken : _accessToken ?? _idToken;

    /// <summary>
    /// One unary gRPC call over HTTP/2. Returns the gRPC status (0 = OK), its message,
    /// and the serialized reply. Retries once with a refreshed token on UNAUTHENTICATED.
    /// </summary>
    public async Task<(int status, string message, byte[] body)> GrpcUnaryAsync(string url, byte[] request, CancellationToken ct = default)
    {
        const int Unauthenticated = 16;
        await EnsureFreshTokenAsync(false, ct).ConfigureAwait(false);
        var result = await GrpcSendAsync(url, request, ct).ConfigureAwait(false);
        if (result.status != Unauthenticated) return result;
        await EnsureFreshTokenAsync(true, ct).ConfigureAwait(false);
        return await GrpcSendAsync(url, request, ct).ConfigureAwait(false);
    }

    private async Task<(int status, string message, byte[] body)> GrpcSendAsync(string url, byte[] request, CancellationToken ct)
    {
        // gRPC framing: 1-byte compressed flag + 4-byte big-endian length + message.
        var framed = new byte[5 + request.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1), request.Length);
        request.CopyTo(framed, 5);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(framed),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        req.Headers.TryAddWithoutValidation("TE", "trailers");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(false));

        using var resp = await _api.SendAsync(req, ct).ConfigureAwait(false);
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (resp.StatusCode == HttpStatusCode.Unauthorized ? 16 : 2, $"HTTP {(int)resp.StatusCode}", Array.Empty<byte>());

        // A call that fails before any reply sends grpc-status in the headers, not the trailers.
        string? Meta(string name) =>
            resp.TrailingHeaders.TryGetValues(name, out var t) ? t.FirstOrDefault()
            : resp.Headers.TryGetValues(name, out var h) ? h.FirstOrDefault() : null;

        var status = int.TryParse(Meta("grpc-status"), out var s) ? s : 2;
        var message = Uri.UnescapeDataString(Meta("grpc-message") ?? "");
        if (status != 0) return (status, message, Array.Empty<byte>());
        if (data.Length < 5) return (0, message, Array.Empty<byte>());
        if (data[0] != 0) return (2, "Compressed gRPC reply isn't supported.", Array.Empty<byte>());

        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(1));
        if (length < 0 || length > data.Length - 5) return (2, "Truncated gRPC reply.", Array.Empty<byte>());
        return (0, message, data.AsSpan(5, length).ToArray());
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, string? json, string? bearer,
        IReadOnlyDictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(method, url);
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (headers is not null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (json is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private static string BuildUrl(string baseUrl, string path, IReadOnlyDictionary<string, string>? query)
    {
        string url;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = path;
        }
        else
        {
            // Resolve exactly like Retrofit: a leading '/' path is host-absolute
            // (replaces the base path); a relative path is appended to the base path.
            var b = baseUrl.EndsWith('/') || new Uri(baseUrl).AbsolutePath.Length <= 1
                ? baseUrl
                : baseUrl + "/";
            url = new Uri(new Uri(b), path).ToString();
        }
        if (query is { Count: > 0 })
        {
            var sb = new StringBuilder(url);
            sb.Append(url.Contains('?') ? '&' : '?');
            bool first = true;
            foreach (var (k, v) in query)
            {
                if (!first) sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            }
            url = sb.ToString();
        }
        return url;
    }

    // ------------------------------------------------- home-control shortcuts
    // Legacy VivintSky scheme used by the phone app (SecurityApi / LockApi /
    // GarageDoorApi / thermostat DTOs in the decompiled app; bodies match vivintpy).

    public Task<ApiResult> AuthUserAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, Base.VivintskyApi, "authuser", ct: ct);

    public Task<ApiResult> GetSystemAsync(string panelId, bool includeRules = false, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, Base.VivintskyApi, $"systems/{panelId}",
            new Dictionary<string, string> { ["includerules"] = includeRules ? "true" : "false" }, ct: ct);

    /// <summary>PUT {panel}/{partition}/armedstates — state 0=disarm, 3=stay, 4=away.</summary>
    public Task<ApiResult> SetArmedStateAsync(string panelId, string partitionId, int state,
        bool forceArm = false, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, Base.VivintskyApi, $"{panelId}/{partitionId}/armedstates",
            body: new Dictionary<string, object>
            {
                ["system"] = NumOrString(panelId),
                ["partitionId"] = NumOrString(partitionId),
                ["armState"] = state,
                ["forceArm"] = forceArm,
            }, ct: ct);

    private Task<ApiResult> DeviceCmdAsync(string panel, string part, string collection,
        string id, Dictionary<string, object> body, CancellationToken ct)
    {
        body["_id"] = NumOrString(id);
        return SendAsync(HttpMethod.Put, Base.VivintskyApi, $"{panel}/{part}/{collection}/{id}",
            body: body, ct: ct);
    }

    public Task<ApiResult> SetLockAsync(string panel, string part, string id, bool locked, CancellationToken ct = default)
        => DeviceCmdAsync(panel, part, "locks", id, new() { ["s"] = locked }, ct);

    /// <summary>Garage: s=2 to close, s=4 to open (GarageDoorState wire values).</summary>
    public Task<ApiResult> SetGarageAsync(string panel, string part, string id, bool open, CancellationToken ct = default)
        => DeviceCmdAsync(panel, part, "door", id,
            new() { ["s"] = open ? (int)GarageState.Opening : (int)GarageState.Closing }, ct);

    public Task<ApiResult> SetSwitchAsync(string panel, string part, string id, bool on, CancellationToken ct = default)
        => DeviceCmdAsync(panel, part, "switches", id, new() { ["s"] = on }, ct);

    public Task<ApiResult> SetDimmerAsync(string panel, string part, string id, int level, CancellationToken ct = default)
        => DeviceCmdAsync(panel, part, "switches", id, new() { ["val"] = Math.Clamp(level, 0, 100) }, ct);

    /// <summary>Thermostat: any of csp/hsp (°C), om (mode), fm (fan mode).</summary>
    public Task<ApiResult> SetThermostatAsync(string panel, string part, string id,
        double? coolC = null, double? heatC = null, int? mode = null, int? fan = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (coolC is { } c) body["csp"] = Math.Round(c, 1);
        if (heatC is { } h) body["hsp"] = Math.Round(h, 1);
        if (mode is { } m) body["om"] = m;
        if (fan is { } f) body["fm"] = f;
        return DeviceCmdAsync(panel, part, "thermostats", id, body, ct);
    }

    /// <summary>Bypass / un-bypass a sensor zone.</summary>
    public Task<ApiResult> SetSensorBypassAsync(string panel, string part, string id, bool bypass, CancellationToken ct = default)
        => DeviceCmdAsync(panel, part, "sensors", id, new() { ["b"] = bypass }, ct);

    /// <summary>History feed: GET {panel}/1/history?lim=N → {"his":[...]}</summary>
    public Task<ApiResult> GetHistoryAsync(string panelId, int limit = 50, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, Base.VivintskyApi, $"{panelId}/1/history",
            new Dictionary<string, string> { ["lim"] = limit.ToString() }, ct: ct);

    /// <summary>Latest camera still (JPEG bytes).</summary>
    public Task<(int status, byte[] data, string? contentType)> GetCameraThumbnailAsync(
        string panelId, string cameraId, CancellationToken ct = default)
        => GetBytesAsync(Base.VivintskyApi, $"{panelId}/1/{cameraId}/camera-thumbnail", ct: ct);

    private static object NumOrString(string s) => long.TryParse(s, out var n) ? n : s;

    public void Dispose()
    {
        _idp.Dispose();
        _api.Dispose();
        _tokenLock.Dispose();
    }

    // ----------------------------------------------------------------- PKCE
    private static string RandomUrlToken(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
