using System.Net.Http;
using System.Text.Json;

namespace VivintDesktop.Api;

/// <summary>People (panel users, PINs, lock access, invites), account security (2FA),
/// integrations status and the panel photo frame.</summary>
public sealed partial class HomeServices
{
    // ------------------------------------------------------------ panel users
    private static string Users(string panelId) => $"/panel-user-api/v1/systems/{panelId}/users";

    /// <summary>Create a panel user (POST /panel-user-api/v1/systems/{id}/users). PINs are 4 digits.</summary>
    public async Task<(ApiResult result, int? userId)> CreatePanelUserAsync(string panelId, string name, string? pin, string? lockPin, bool admin)
    {
        var body = new Dictionary<string, object> { ["system_id"] = panelId, ["name"] = name, ["admin"] = admin };
        if (!string.IsNullOrEmpty(pin)) body["code"] = pin;
        if (!string.IsNullOrEmpty(lockPin)) body["lock_code"] = lockPin;
        var r = await _c.SendAsync(HttpMethod.Post, Api, Users(panelId), body: body);
        int? id = r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j && Json.TryInt(j, "user_id", out var u) ? u : null;
        return (r, id);
    }

    public Task<ApiResult> DeletePanelUserAsync(string panelId, string userId)
        => _c.SendAsync(HttpMethod.Delete, Api, $"{Users(panelId)}/{userId}");

    public Task<ApiResult> SetPanelPinAsync(string panelId, string userId, string pin)
        => _c.SendAsync(HttpMethod.Put, Api, $"{Users(panelId)}/{userId}/code", body: new Dictionary<string, object>
        { ["system_id"] = Num(panelId), ["user_id"] = Num(userId), ["code"] = pin });

    public Task<ApiResult> SetLockPinAsync(string panelId, string userId, string pin)
        => _c.SendAsync(HttpMethod.Put, Api, $"{Users(panelId)}/{userId}/lock-code", body: new Dictionary<string, object>
        { ["system_id"] = Num(panelId), ["user_id"] = Num(userId), ["lock_code"] = pin });

    public Task<ApiResult> SetLockAccessAsync(string panelId, string userId, string lockId, bool allowed)
        => _c.SendAsync(HttpMethod.Post, Api, $"{Users(panelId)}/{userId}:{(allowed ? "add-lock" : "remove-lock")}",
            body: new Dictionary<string, object> { ["system_id"] = Num(panelId), ["user_id"] = Num(userId), ["lock_id"] = lockId });

    /// <summary>Rename a system user (legacy PUT systems/{id}/users/{uid} {"n"}).</summary>
    public Task<ApiResult> RenameUserAsync(string panelId, string userId, string name)
        => _c.SendAsync(HttpMethod.Put, Api, $"systems/{panelId}/users/{userId}", body: new Dictionary<string, object> { ["n"] = name });

    /// <summary>Email an app invite to a panel user (POST invite-passcode).</summary>
    public Task<ApiResult> SendInviteAsync(string panelId, string userId, string name, string email, string? message)
    {
        var body = new Dictionary<string, object> { ["panid"] = Num(panelId), ["uid"] = Num(userId), ["n"] = name, ["e"] = email };
        if (!string.IsNullOrWhiteSpace(message)) body["me"] = message;
        return _c.SendAsync(HttpMethod.Post, Api, "invite-passcode", body: body);
    }

    // ------------------------------------------------------------ 2FA (account level, TOTP)
    /// <summary>Start 2FA setup; returns the otpauth:// URL (holds the secret for an authenticator app).</summary>
    public async Task<string?> StartMfaSetupAsync()
    {
        var r = await _c.SendAsync(HttpMethod.Post, Api, "/platform-user-api/v0/platformusers/2fa/account");
        return r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j ? Json.Str(j, "accountUrl") : null;
    }

    public Task<ApiResult> CancelMfaSetupAsync()
        => _c.SendAsync(HttpMethod.Delete, Api, "/platform-user-api/v0/platformusers/2fa/account");

    public Task<ApiResult> EnableMfaAsync(string code)
        => _c.SendAsync(HttpMethod.Post, Api, "/platform-user-api/v0/platformusers/2fa/enable", body: new Dictionary<string, object> { ["code"] = code });

    public Task<ApiResult> DisableMfaAsync()
        => _c.SendAsync(HttpMethod.Post, Api, "/platform-user-api/v0/platformusers/2fa/disable");

    // ------------------------------------------------------------ integrations
    public async Task<List<Integration>> GetIntegrationsAsync(string panelId)
    {
        var myq = _c.SendAsync(HttpMethod.Get, Api, $"/api/myq/oauth/{panelId}");
        var nest = _c.SendAsync(HttpMethod.Get, Api, $"systems/{panelId}/sdm_nest_mapping");
        var energy = _c.SendAsync(HttpMethod.Get, Api, $"/energy/v1/oauth/{panelId}");
        await Task.WhenAll(myq, nest, energy);

        static string Why(ApiResult r) => r.Json is { ValueKind: JsonValueKind.Object } j ? Json.Str(j, "msg", "message") ?? "" : "";
        var list = new List<Integration>
        {
            new("myQ garage", myq.Result.Ok, myq.Result.Ok ? "Linked" : "Not linked",
                myq.Result.Ok ? $"/api/myq/oauth/{panelId}" : null),
        };
        var nestCount = 0;
        if (nest.Result.Ok && nest.Result.Json is { } nj)
            foreach (var p in nj.ValueKind == JsonValueKind.Object ? nj.EnumerateObject() : default)
                if (p.Value.ValueKind == JsonValueKind.Array) nestCount += p.Value.GetArrayLength();
        list.Add(new("Google Nest", nest.Result.Ok,
            nest.Result.Ok ? $"Linked{(nestCount > 0 ? $" · {nestCount} device{(nestCount == 1 ? "" : "s")}" : "")}"
                : Why(nest.Result).Contains("auth", StringComparison.OrdinalIgnoreCase) ? "Not linked" : "Not available",
            nest.Result.Ok ? $"google-sdm-connection/{panelId}" : null));
        list.Add(new("Energy provider", energy.Result.Ok, energy.Result.Ok
                ? Json.Str(energy.Result.Json!.Value, "energy_provider") is { } ep ? $"Linked · {ep}" : "Linked"
                : "Not linked",
            energy.Result.Ok ? $"/energy/v1/oauth/{panelId}" : null));
        return list;
    }

    /// <summary>Disconnect an integration (DELETE on the path stored in <see cref="Integration.DisconnectPath"/>).</summary>
    public Task<ApiResult> DisconnectIntegrationAsync(Integration i)
        => _c.SendAsync(HttpMethod.Delete, Api, i.DisconnectPath!);

    // ------------------------------------------------------------ photo frame
    private static string Frame(string svc) => $"/photoframe/v0/{svc}";

    public async Task<(bool exists, JsonElement? frame)> GetPhotoFrameAsync(string serviceNumber)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, Frame(serviceNumber));
        if (r.Status == 404) return (false, null);
        if (!r.Ok) throw new InvalidOperationException($"HTTP {r.Status}");
        return (true, r.Json);
    }

    public Task<ApiResult> EnablePhotoFrameAsync(string svc) => _c.SendAsync(HttpMethod.Post, Api, $"{Frame(svc)}/enable");

    public async Task<List<JsonElement>> ListFramePhotosAsync(string svc, string albumId)
    {
        var r = await _c.SendAsync(HttpMethod.Get, Api, $"{Frame(svc)}/album/{albumId}/photos");
        var list = new List<JsonElement>();
        if (r.Ok && r.Json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("photos", out var a) && a.ValueKind == JsonValueKind.Array)
            list.AddRange(a.EnumerateArray());
        return list;
    }

    /// <summary>Upload a JPEG/PNG (base64 in JSON, as the app does).</summary>
    public Task<ApiResult> UploadFramePhotoAsync(string svc, string albumId, byte[] image, string mime)
        => _c.SendAsync(HttpMethod.Post, Api, $"{Frame(svc)}/album/{albumId}/photo", body: new Dictionary<string, object>
        { ["mime_type"] = mime, ["image"] = Convert.ToBase64String(image), ["rotation"] = "ROTATION_0" });

    public Task<ApiResult> DeleteFramePhotoAsync(string svc, string albumId, string photoId)
        => _c.SendAsync(HttpMethod.Delete, Api, $"{Frame(svc)}/album/{albumId}/photo/{photoId}");

    public Task<ApiResult> SetFrameDurationAsync(string svc, int seconds)
        => _c.SendAsync(HttpMethod.Put, Api, Frame(svc), body: new Dictionary<string, object>
        { ["photo_duration_seconds"] = seconds, ["update_mask"] = "photoDurationSeconds" });

    public Task<ApiResult> PublishFrameAsync(string svc, string albumId)
        => _c.SendAsync(HttpMethod.Post, Api, $"{Frame(svc)}/publish", body: new Dictionary<string, object>
        { ["album_id"] = albumId, ["album_type"] = "MY_PHOTOS" });

    public Task<ApiResult> DeletePhotoFrameAsync(string svc) => _c.SendAsync(HttpMethod.Delete, Api, Frame(svc));
}

public sealed record Integration(string Name, bool Connected, string Status, string? DisconnectPath);
