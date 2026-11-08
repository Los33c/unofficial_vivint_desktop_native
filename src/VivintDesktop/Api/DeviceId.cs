using System.IO;

namespace VivintDesktop.Api;

/// <summary>
/// Stable per-install device identifier sent to the IdP as a trust hint
/// (the app sends "ANDROID_ID:&lt;id&gt;"; here we persist a GUID once).
/// </summary>
public static class DeviceId
{
    public static string Get()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VivintDesktop");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "device_id");
            if (File.Exists(file))
            {
                var v = File.ReadAllText(file).Trim();
                if (v.Length > 0) return v;
            }
            var id = "DESKTOP_ID:" + Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            return "DESKTOP_ID:" + Guid.NewGuid().ToString("N");
        }
    }
}
