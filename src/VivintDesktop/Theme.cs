using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace VivintDesktop;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light/dark theme. Every color the UI uses is a brush resource on the application;
/// switching themes replaces those resources (XAML uses DynamicResource, so it updates
/// live) and raises <see cref="Changed"/> so code-built pages can re-render.
/// Also colors the Windows title bar to match.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    private static readonly Dictionary<string, (string light, string dark)> Palette = new()
    {
        ["Bg"]          = ("#F3F5F8", "#0E1116"),
        ["Surface"]     = ("#FFFFFF", "#171B22"),
        ["Surface2"]    = ("#EEF1F5", "#212732"),   // controls, secondary buttons
        ["SurfaceHover"]= ("#E4E8EE", "#2A313D"),
        ["Sunken"]      = ("#E6EAF0", "#0A0D11"),   // image placeholders
        ["Border"]      = ("#E1E5EB", "#2A303B"),
        ["BorderStrong"]= ("#C9CFD8", "#3A4250"),
        ["Text"]        = ("#18202B", "#E7EAF0"),
        ["Muted"]       = ("#667085", "#98A2B3"),
        ["Sidebar"]     = ("#FFFFFF", "#12161C"),
        ["Header"]      = ("#14213D", "#0B0F17"),
        ["Accent"]      = ("#2563EB", "#3B82F6"),
        ["AccentHover"] = ("#1D4ED8", "#60A5FA"),
        ["AccentSoft"]  = ("#E8EFFD", "#1C2A45"),
        ["AccentText"]  = ("#1D4ED8", "#93BBFF"),
        ["OnAccent"]    = ("#FFFFFF", "#FFFFFF"),
        ["Ok"]          = ("#15803D", "#4ADE80"),
        ["OkSoft"]      = ("#E7F5EC", "#173022"),
        ["Warn"]        = ("#B45309", "#FBBF24"),
        ["Danger"]      = ("#C62828", "#F87171"),
        ["DangerSoft"]  = ("#F2B8B5", "#5A2427"),
        ["Neutral"]     = ("#5F6B7A", "#7B8696"),
        ["Heat"]        = ("#C2410C", "#FB923C"),
        ["Cool"]        = ("#1D4ED8", "#60A5FA"),
        ["Scroll"]      = ("#C3C9D2", "#3A4250"),
    };

    public static ThemeChoice Choice => Prefs.Theme;

    /// <summary>Apply the saved choice at startup and follow Windows when set to System.</summary>
    public static void Init()
    {
        Apply(Prefs.Theme, raise: false);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Prefs.Theme == ThemeChoice.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
        };
    }

    public static void Set(ThemeChoice choice)
    {
        Prefs.Theme = choice;
        Apply(choice);
    }

    /// <summary>Light → Dark → System → Light …</summary>
    public static ThemeChoice Next(ThemeChoice c) => c switch
    {
        ThemeChoice.Light => ThemeChoice.Dark, ThemeChoice.Dark => ThemeChoice.System, _ => ThemeChoice.Light,
    };

    private static void Apply(ThemeChoice choice, bool raise = true)
    {
        var dark = choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => WindowsUsesDark() };
        IsDark = dark;
        var res = Application.Current.Resources;
        foreach (var (key, (l, d)) in Palette)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? d : l));
            b.Freeze();
            res[key] = b;
        }
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        if (raise) Changed?.Invoke();
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static bool WindowsUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- title bar
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light Windows title bar for this window (Windows 10 20H1+/11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero)
        {
            w.SourceInitialized -= OnSourceInitialized;
            w.SourceInitialized += OnSourceInitialized;
            return;
        }
        var on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(h, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        // Windows 11: caption color to match the sidebar/background.
        var color = IsDark ? 0x00161210 : 0x00FFFFFF;             // COLORREF 0x00BBGGRR
        _ = DwmSetWindowAttribute(h, 35, ref color, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) ApplyTitleBar(w);
    }

    /// <summary>Give a code-built window the theme's background/foreground and title bar.</summary>
    public static void Style(Window w)
    {
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        ApplyTitleBar(w);
    }
}
