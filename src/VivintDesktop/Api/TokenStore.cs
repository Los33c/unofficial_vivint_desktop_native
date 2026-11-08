using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VivintDesktop.Api;

/// <summary>
/// Persists the OAuth refresh token so the app can stay signed in between
/// launches (like the phone app). The token is encrypted with Windows DPAPI,
/// scoped to the current Windows user, under %APPDATA%\VivintDesktop\session.bin.
/// </summary>
public static class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VivintDesktop.session.v1");

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VivintDesktop", "session.bin");

    public static void Save(string refreshToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, data);
        }
        catch { /* persistence is best effort */ }
    }

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var data = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy,
                DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(data);
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* ignore */ }
    }
}
