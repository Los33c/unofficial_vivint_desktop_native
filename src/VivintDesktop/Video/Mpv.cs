using System.Runtime.InteropServices;
using System.Text;

namespace VivintDesktop.Video;

/// <summary>Minimal P/Invoke surface of libmpv (client.h, API v2) used by the app.</summary>
internal static class LibMpv
{
    private const string Dll = "libmpv-2.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_client_api_version();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out int data);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong id, byte[] name, int format);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    public const int FormatFlag = 3, FormatDouble = 5;

    public const int EventShutdown = 1, EventStartFile = 6, EventEndFile = 7, EventFileLoaded = 8,
        EventClientMessage = 16, EventVideoReconfig = 17, EventPlaybackRestart = 21, EventPropertyChange = 22;

    public const int EndReasonEof = 0, EndReasonStop = 2, EndReasonQuit = 3, EndReasonError = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event { public int EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndFileData { public int Reason; public int Error; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyData { public IntPtr Name; public int Format; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClientMessageData { public int NumArgs; public IntPtr Args; }

    public static byte[] U8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref b, b.Length + 1);   // NUL terminator
        return b;
    }

    public static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}

/// <summary>
/// One libmpv instance rendering into a native window (mpv's "wid" embedding),
/// with a background thread pumping its events.
/// </summary>
internal sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private readonly Thread _events;
    private volatile bool _disposed;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once mpv has shut down and released the window.</summary>
    public Task Closed => _closed.Task;

    /// <summary>A new file started loading.</summary>
    public event Action? Started;
    /// <summary>The first frame after a (re)start is ready to display.</summary>
    public event Action? FirstFrame;
    /// <summary>Playback ended: (isError, message).</summary>
    public event Action<bool, string?>? Ended;
    public event Action? Clicked;
    public event Action<bool>? PauseChanged;

    public MpvPlayer(IntPtr hostWindow)
    {
        _ctx = LibMpv.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("Could not start the mpv video engine.");

        // Render into our window, on the GPU, with hardware decoding.
        Opt("wid", hostWindow.ToInt64().ToString());
        Opt("vo", "gpu-next");
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "auto-safe");
        // Embedded player: no mpv UI, no config files, no keyboard handling.
        Opt("config", "no");
        Opt("osc", "no");
        Opt("load-scripts", "no");
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("osd-level", "1");
        Opt("osd-bar", "no");
        Opt("keep-open", "yes");          // clips stay on their last frame
        Opt("idle", "yes");
        Opt("force-window", "no");
        Opt("terminal", "no");
        Opt("msg-level", "all=no");
        Opt("audio-display", "no");
        Opt("ytdl", "no");
        Opt("screenshot-format", "png");
        // RTSP over TCP (the Vivint relays only speak TCP).
        Opt("demuxer-lavf-o", "rtsp_transport=tcp");
        Opt("network-timeout", "15");

        Check(LibMpv.mpv_initialize(_ctx), "initialize");

        Command("keybind", "MBTN_LEFT", "script-message vd-click");
        LibMpv.mpv_observe_property(_ctx, 1, LibMpv.U8("pause"), LibMpv.FormatFlag);

        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    // ------------------------------------------------------------------ API
    /// <summary>Play a live stream with low-latency settings.</summary>
    public void PlayLive(string url)
    {
        Prop("cache", "no");
        Prop("cache-pause", "no");
        Prop("video-latency-hacks", "yes");
        Prop("demuxer-lavf-analyzeduration", "0.5");
        Prop("demuxer-lavf-o-add", "fflags=+nobuffer");
        Prop("pause", "no");
        Command("loadfile", url, "replace");
    }

    /// <summary>Play a recorded clip (normal buffering, seekable).</summary>
    public void PlayFile(string url)
    {
        Prop("cache", "auto");
        Prop("cache-pause", "yes");
        Prop("video-latency-hacks", "no");
        Prop("demuxer-lavf-analyzeduration", "0");
        Prop("demuxer-lavf-o", "rtsp_transport=tcp");
        Prop("pause", "no");
        Command("loadfile", url, "replace");
    }

    public void Stop() => Command("stop");

    public bool Mute { set => Prop("mute", value ? "yes" : "no"); }
    public bool Pause { set => Prop("pause", value ? "yes" : "no"); }

    public double Duration => GetDouble("duration");
    public double PercentPos
    {
        get => GetDouble("percent-pos");
        set => Command("seek", value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute-percent");
    }

    public bool IsPaused => GetFlag("pause");
    public bool IsEof => GetFlag("eof-reached");

    public bool Screenshot(string path) => Command("screenshot-to-file", path, "video") >= 0;

    /// <summary>Text drawn on top of the video (top-left), e.g. "Driveway · LIVE".</summary>
    public void SetOverlayText(string text)
    {
        var esc = text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        var ass = $"{{\\an7\\fs15\\b1\\bord1.5\\shad0\\1c&HFFFFFF&\\3c&H000000&\\3a&H40&}}{esc}";
        Command("osd-overlay", "1", "ass-events", ass, "0", "540");
    }

    // ------------------------------------------------------------------ plumbing
    private void Opt(string name, string value) => LibMpv.mpv_set_option_string(_ctx, LibMpv.U8(name), LibMpv.U8(value));

    private void Prop(string name, string value)
    {
        if (_disposed || _ctx == IntPtr.Zero) return;
        LibMpv.mpv_set_property_string(_ctx, LibMpv.U8(name), LibMpv.U8(value));
    }

    private double GetDouble(string name)
    {
        if (_disposed || _ctx == IntPtr.Zero) return 0;
        return LibMpv.mpv_get_property(_ctx, LibMpv.U8(name), LibMpv.FormatDouble, out double v) >= 0 ? v : 0;
    }

    private bool GetFlag(string name)
    {
        if (_disposed || _ctx == IntPtr.Zero) return false;
        return LibMpv.mpv_get_property(_ctx, LibMpv.U8(name), LibMpv.FormatFlag, out int v) >= 0 && v != 0;
    }

    private int Command(params string[] args)
    {
        if (_disposed || _ctx == IntPtr.Zero) return -1;
        return RawCommand(_ctx, args);
    }

    private static int RawCommand(IntPtr ctx, string[] args)
    {
        var handles = new List<GCHandle>();
        try
        {
            var ptrs = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
            {
                var h = GCHandle.Alloc(LibMpv.U8(args[i]), GCHandleType.Pinned);
                handles.Add(h);
                ptrs[i] = h.AddrOfPinnedObject();
            }
            return LibMpv.mpv_command(ctx, ptrs);
        }
        finally
        {
            foreach (var h in handles) h.Free();
        }
    }

    private static void Check(int code, string what)
    {
        if (code < 0) throw new InvalidOperationException($"mpv {what} failed: {LibMpv.Str(LibMpv.mpv_error_string(code))}");
    }

    private void EventLoop()
    {
        var ctx = _ctx;
        while (true)
        {
            var p = LibMpv.mpv_wait_event(ctx, -1);
            if (p == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<LibMpv.Event>(p);
            try
            {
                switch (ev.EventId)
                {
                    case LibMpv.EventShutdown:
                        // Only this thread touches the handle after shutdown starts.
                        LibMpv.mpv_terminate_destroy(ctx);
                        _closed.TrySetResult();
                        return;
                    case LibMpv.EventStartFile:
                        Started?.Invoke();
                        break;
                    case LibMpv.EventPlaybackRestart:
                        FirstFrame?.Invoke();
                        break;
                    case LibMpv.EventEndFile when ev.Data != IntPtr.Zero:
                        var end = Marshal.PtrToStructure<LibMpv.EndFileData>(ev.Data);
                        if (end.Reason == LibMpv.EndReasonError)
                            Ended?.Invoke(true, LibMpv.Str(LibMpv.mpv_error_string(end.Error)));
                        else if (end.Reason == LibMpv.EndReasonEof)
                            Ended?.Invoke(false, null);
                        break;
                    case LibMpv.EventClientMessage when ev.Data != IntPtr.Zero:
                        var msg = Marshal.PtrToStructure<LibMpv.ClientMessageData>(ev.Data);
                        if (msg.NumArgs > 0 && LibMpv.Str(Marshal.ReadIntPtr(msg.Args)) == "vd-click")
                            Clicked?.Invoke();
                        break;
                    case LibMpv.EventPropertyChange when ev.Data != IntPtr.Zero:
                        var prop = Marshal.PtrToStructure<LibMpv.PropertyData>(ev.Data);
                        if (LibMpv.Str(prop.Name) == "pause" && prop.Format == LibMpv.FormatFlag && prop.Data != IntPtr.Zero)
                            PauseChanged?.Invoke(Marshal.ReadInt32(prop.Data) != 0);
                        break;
                }
            }
            catch { /* never let a handler kill the event pump */ }
        }
    }

    /// <summary>Ask mpv to quit; <see cref="Closed"/> completes when it is fully shut down.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        if (ctx != IntPtr.Zero) RawCommand(ctx, new[] { "quit" });   // → SHUTDOWN → event thread destroys
    }
}
