using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using VivintDesktop.Api;

namespace VivintDesktop.Video;

/// <summary>
/// Live video plumbing.
///
/// Each camera's system data carries RTSP stream URLs:
///   ciu / cius   — on the home LAN, via the panel (rtsp://{panel-ip}:8554/Video-N[_SD])
///   cetu / cetus — through Vivint's cloud relay (rtsp://v.vivintsky.com:443/…), RTSP inside TLS
///   ceu / ceus   — older cloud relay (rtsp://livevid.vivint.com:443/…)
/// The phone app adds the panel's camera login (GET panel-login/{panel}) to the URL.
/// The relay speaks RTSP inside TLS with an rtsp:// URL, so cloud streams go through a tiny
/// loopback tunnel: mpv → 127.0.0.1:port (plain) → TLS → relay.
/// </summary>
public static class VideoEngine
{
    // libmpv is a large DLL; load it once, off the UI thread.
    private static readonly Lazy<bool> LazyMpv = new(() => { LibMpv.mpv_client_api_version(); return true; },
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, TlsTunnel> Tunnels = new();
    // One shared reachability check per host:port (all tiles await the same probe).
    private static readonly ConcurrentDictionary<string, (Task<bool> probe, DateTime at)> Reach = new();

    public static bool IsCreated => LazyMpv.IsValueCreated;

    /// <summary>Load libmpv off the UI thread so the first video doesn't stall the window.</summary>
    public static Task WarmUpAsync() => Task.Run(() => _ = LazyMpv.Value);

    /// <summary>
    /// Pre-resolve everything a live stream needs (engine, camera login, LAN-vs-cloud route,
    /// cloud TLS tunnel) so opening the Cameras tab starts streaming immediately.
    /// </summary>
    public static async Task PrepareAsync(HomeServices svc, string panelId, IEnumerable<Device> cameras)
    {
        if (svc.Client.DemoMode) { await WarmUpAsync(); return; }
        var warm = WarmUpAsync();
        var creds = svc.GetPanelCredentialsAsync(panelId);
        var probes = cameras.SelectMany(c => new[] { c.StreamUrl("cius"), c.StreamUrl("ciu") })
            .Where(u => u is not null && Uri.TryCreate(u, UriKind.Absolute, out _))
            .Select(u => new Uri(u!)).Select(u => CanReachAsync(u.Host, u.Port));
        await Task.WhenAll(probes.Append(warm).Append(creds));
    }

    /// <summary>Build the playable live URL for a camera, preferring the LAN when it is reachable.</summary>
    public static async Task<(Uri uri, string route)> LiveUriAsync(HomeServices svc, string panelId, Device cam, bool hd)
    {
        // Demo cameras show a drawn still (see VideoSurface.ShowDemoStill).
        if (svc.Client.DemoMode) return (new Uri("demo://camera/" + Uri.EscapeDataString(cam.Name)), "Demo");
        var credsTask = svc.GetPanelCredentialsAsync(panelId);

        var lan = cam.StreamUrl(hd ? "ciu" : "cius");
        Uri? lu = lan is not null && Uri.TryCreate(lan, UriKind.Absolute, out var l) ? l : null;
        var lanOk = lu is not null ? CanReachAsync(lu.Host, lu.Port) : Task.FromResult(false);
        var (user, pass) = await credsTask;
        if (lu is not null && await lanOk)
            return (WithCreds(lu, user, pass), "Home network");

        // v.vivintsky.com relay (RTSP inside TLS) first; the older relay only as a fallback.
        foreach (var key in hd ? new[] { "cetu", "ceu" } : new[] { "cetus", "ceus" })
        {
            var cloud = cam.StreamUrl(key);
            if (cloud is null || !Uri.TryCreate(cloud, UriKind.Absolute, out var cu)) continue;
            if (key.StartsWith("ceu") && !await CanReachAsync(cu.Host, cu.Port)) continue;
            var tunnel = Tunnels.GetOrAdd($"{cu.Host}:{cu.Port}", _ => new TlsTunnel(cu.Host, cu.Port));
            var local = new Uri($"rtsp://127.0.0.1:{tunnel.LocalPort}{cu.AbsolutePath}");
            return (WithCreds(local, user, pass), "Vivint cloud");
        }
        throw new InvalidOperationException("This camera has no reachable live stream right now.");
    }

    private static Uri WithCreds(Uri u, string user, string pass)
        => new($"{u.Scheme}://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@{u.Host}:{u.Port}{u.PathAndQuery}");

    private static Task<bool> CanReachAsync(string host, int port)
    {
        var key = $"{host}:{port}";
        var now = DateTime.UtcNow;
        var entry = Reach.AddOrUpdate(key,
            _ => (Probe(host, port), now),
            (_, old) => now - old.at < TimeSpan.FromMinutes(10) ? old : (Probe(host, port), now));
        return entry.probe;
    }

    private static async Task<bool> Probe(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public static void Shutdown()
    {
        foreach (var t in Tunnels.Values) t.Dispose();
        Tunnels.Clear();
    }
}

/// <summary>Loopback listener that forwards each connection to host:port over TLS.</summary>
internal sealed class TlsTunnel : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int LocalPort { get; }

    public TlsTunnel(string host, int port)
    {
        _host = host;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient local;
            try { local = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = PumpAsync(local);
        }
    }

    private async Task PumpAsync(TcpClient local)
    {
        using (local)
        using (var remote = new TcpClient())
        {
            try
            {
                local.NoDelay = remote.NoDelay = true;
                await remote.ConnectAsync(_host, _port, _cts.Token);
                await using var ssl = new SslStream(remote.GetStream(), false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, _cts.Token);
                var ls = local.GetStream();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var up = ls.CopyToAsync(ssl, linked.Token);
                var down = ssl.CopyToAsync(ls, linked.Token);
                await Task.WhenAny(up, down);
                linked.Cancel();
            }
            catch { /* connection ended */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
