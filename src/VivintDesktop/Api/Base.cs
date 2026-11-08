namespace VivintDesktop.Api;

/// <summary>
/// Host / base-URL constants, taken from the decompiled Vivint Android app
/// (com.vivint.vivintsky 26.10.400): PlatformConfig + the OAuth Dagger modules.
/// Production platform host is vivintsky.com; the OpenID issuer is id.vivint.com.
/// </summary>
public static class Base
{
    // Core legacy platform ("/api" surface): login bootstrap, authuser, systems,
    // arming, locks, thermostats, history, cameras.
    public const string Vivintsky = "https://www.vivintsky.com";
    public const string VivintskyApi = Vivintsky + "/api/";

    // OpenID / OAuth issuer.
    public const string OpenIdIssuer = "https://id.vivint.com/";

    // Platform micro-services ("{service}.platform.vivint.com").
    public const string She = "https://she.platform.vivint.com";
    public const string SheApps = "https://she-apps.platform.vivint.com";
    public const string VideoApps = "https://video-apps.platform.vivint.com";
    public const string Energy = "https://energy.platform.vivint.com";

    // Other first-party hosts.
    public const string Apio = "https://apio.vivint.com/";
    public const string Moments = "https://api.moments.vivint.ai/";
    public const string Exchange = "https://exchange.run.vivint.ai";
    public const string Search = "https://search.vivint.ai";
    public const string Metrics = "https://telegraf.metrics.vivint.ai";

    // Third-party.
    public const string GoogleMaps = "https://maps.googleapis.com/maps/api/";
    public const string Smartsheet = "https://api.smartsheet.com/2.0/";
    public const string SpacemonkeyAccounts = "https://accounts.spacemonkey.com";

    // OAuth client parameters (from CloudModule_ProvideOAuthClient*Factory).
    public const string ClientId = "android";
    public const string Scope = "openid email profile devices";
    public const string RedirectUri = "vivint://app/oauth_redirect";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public const string UserAgent = "VivintDesktop/1.0 (personal API client)";
}

/// <summary>How a request is authorised.</summary>
public enum AuthMode
{
    /// <summary>Attach the OAuth bearer access token (default for vivint hosts).</summary>
    Bearer,
    /// <summary>Rely on the session cookie (legacy flows).</summary>
    Cookie,
    /// <summary>No auth header (public endpoints, e.g. Google Maps).</summary>
    None
}
