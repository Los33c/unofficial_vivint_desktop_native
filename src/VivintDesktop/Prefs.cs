using System.IO;
using System.Text.Json;

namespace VivintDesktop;

/// <summary>User preferences, saved as JSON in %APPDATA%\VivintDesktop\prefs.json.</summary>
internal static class Prefs
{
    private sealed class Data
    {
        public bool Notifications { get; set; } = true;
        public bool NotifySecurity { get; set; } = true;
        public bool NotifyLocks { get; set; } = true;
        public bool NotifyGarage { get; set; } = true;
        public bool NotifyDoors { get; set; } = true;
        public bool NotifyMotion { get; set; }
        public bool NotifyDoorbell { get; set; } = true;
        public bool NotifyCameraEvents { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool RealTime { get; set; } = true;
        public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VivintDesktop", "prefs.json");

    private static readonly Data D = Load();

    private static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public static bool Notifications { get => D.Notifications; set { D.Notifications = value; Save(); } }
    public static bool NotifySecurity { get => D.NotifySecurity; set { D.NotifySecurity = value; Save(); } }
    public static bool NotifyLocks { get => D.NotifyLocks; set { D.NotifyLocks = value; Save(); } }
    public static bool NotifyGarage { get => D.NotifyGarage; set { D.NotifyGarage = value; Save(); } }
    public static bool NotifyDoors { get => D.NotifyDoors; set { D.NotifyDoors = value; Save(); } }
    public static bool NotifyMotion { get => D.NotifyMotion; set { D.NotifyMotion = value; Save(); } }
    public static bool NotifyDoorbell { get => D.NotifyDoorbell; set { D.NotifyDoorbell = value; Save(); } }
    public static bool NotifyCameraEvents { get => D.NotifyCameraEvents; set { D.NotifyCameraEvents = value; Save(); } }
    public static bool MinimizeToTray { get => D.MinimizeToTray; set { D.MinimizeToTray = value; Save(); } }
    public static bool RealTime { get => D.RealTime; set { D.RealTime = value; Save(); } }
    public static ThemeChoice Theme { get => D.Theme; set { D.Theme = value; Save(); } }
}
