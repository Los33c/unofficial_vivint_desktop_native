using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace VivintDesktop.Api;

/// <summary>
/// Made-up camera pictures for demo mode, drawn with GDI+ (thread-safe, unlike
/// WPF rendering), so nothing is downloaded.
/// </summary>
internal static class DemoImages
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VivintDesktop", "demo");
    private static readonly ConcurrentDictionary<string, string> Files = new();

    /// <summary>Forget stills from an earlier demo run (event ids repeat between runs).</summary>
    public static void Reset()
    {
        Files.Clear();
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>JPEG bytes of a camera still.</summary>
    public static byte[] CameraJpeg(string camera, DateTime local, bool person = false, bool osd = true)
    {
        using var bmp = Draw(camera, local, person, 640, 360, osd);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    /// <summary>A still written to disk, as a file:/// URL (event thumbnails are loaded by URL).</summary>
    public static string CameraFileUri(string camera, DateTime local, string id, bool person) => Files.GetOrAdd(id, _ =>
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, id + ".jpg");
        if (!File.Exists(path))
            File.WriteAllBytes(path, CameraJpeg(camera, local, person));
        return new Uri(path).AbsoluteUri;
    });

    private static Bitmap Draw(string camera, DateTime local, bool person, int w, int h, bool osd)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var night = local.Hour is < 7 or >= 19;
        Color C(int r, int gr, int b) => night
            ? Color.FromArgb((r + gr + b) / 3 / 3, (r + gr + b) / 3 / 3 + 6, (r + gr + b) / 3 / 3 + 10)
            : Color.FromArgb(r, gr, b);

        var horizon = (int)(h * 0.48);
        using (var sky = new LinearGradientBrush(new Rectangle(0, 0, w, horizon + 1), C(122, 165, 204), C(206, 222, 234), 90f))
            g.FillRectangle(sky, 0, 0, w, horizon);
        using (var ground = new LinearGradientBrush(new Rectangle(0, horizon, w, h - horizon), C(96, 124, 78), C(62, 84, 52), 90f))
            g.FillRectangle(ground, 0, horizon, w, h - horizon);

        var kind = camera.Contains("Drive", StringComparison.OrdinalIgnoreCase) ? 1
                 : camera.Contains("Back", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (kind == 0)
        {
            // porch: walkway + door frame
            using var walk = new SolidBrush(C(176, 168, 152));
            g.FillPolygon(walk, new[] { new Point(w / 2 - 40, horizon), new Point(w / 2 + 40, horizon), new Point(w / 2 + 210, h), new Point(w / 2 - 210, h) });
            using var post = new SolidBrush(C(232, 228, 220));
            g.FillRectangle(post, 0, 0, 46, h);
            g.FillRectangle(post, w - 46, 0, 46, h);
            using var house = new SolidBrush(C(150, 120, 96));
            g.FillRectangle(house, w / 2 + 120, horizon - 120, 200, 120);
            using var roof = new SolidBrush(C(84, 64, 56));
            g.FillPolygon(roof, new[] { new Point(w / 2 + 104, horizon - 120), new Point(w / 2 + 220, horizon - 190), new Point(w / 2 + 336, horizon - 120) });
        }
        else if (kind == 1)
        {
            // driveway + garage
            using var drive = new SolidBrush(C(128, 128, 132));
            g.FillPolygon(drive, new[] { new Point(w / 2 - 70, horizon), new Point(w / 2 + 90, horizon), new Point(w, h), new Point(60, h) });
            using var line = new Pen(C(200, 200, 196), 3);
            g.DrawLine(line, w / 2 + 10, horizon, w / 2 + 160, h);
            using var house = new SolidBrush(C(196, 186, 168));
            g.FillRectangle(house, 40, horizon - 110, 230, 110);
            using var garage = new SolidBrush(C(232, 230, 226));
            g.FillRectangle(garage, 80, horizon - 70, 150, 70);
            using var car = new SolidBrush(C(54, 74, 112));
            g.FillRectangle(car, w / 2 + 40, horizon + 60, 150, 54);
            g.FillRectangle(car, w / 2 + 70, horizon + 30, 90, 34);
        }
        else
        {
            // backyard: fence + tree
            using var fence = new SolidBrush(C(170, 140, 104));
            for (int x = 0; x < w; x += 26) g.FillRectangle(fence, x, horizon - 70, 20, 80);
            using var trunk = new SolidBrush(C(94, 70, 50));
            g.FillRectangle(trunk, w - 170, horizon - 60, 22, 120);
            using var leaves = new SolidBrush(C(70, 110, 60));
            g.FillEllipse(leaves, w - 240, horizon - 190, 160, 150);
            using var patio = new SolidBrush(C(186, 176, 160));
            g.FillRectangle(patio, 0, h - 90, w, 90);
        }

        if (person)
        {
            using var body = new SolidBrush(C(40, 48, 66));
            var px = w / 2 - 20 + (camera.Length * 37 % 140) - 70;
            var py = horizon + 20;
            g.FillEllipse(body, px + 8, py - 54, 30, 32);
            g.FillRectangle(body, px, py - 22, 46, 90);
            using var box = new Pen(Color.FromArgb(230, 255, 214, 10), 2);
            g.DrawRectangle(box, px - 14, py - 66, 74, 150);
        }

        // vignette + camera OSD
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-w / 4, -h / 3, w * 3 / 2, h * 5 / 3);
            using var vig = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(150, 0, 0, 0) } };
            g.FillRectangle(vig, 0, 0, w, h);
        }
        if (!osd) return bmp;
        using var font = new Font("Consolas", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        var stamp = local.ToString("yyyy-MM-dd  HH:mm:ss");
        var size = g.MeasureString(stamp, font);
        using var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        using var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.DrawString(stamp, font, shadow, w - size.Width - 11, 11);
        g.DrawString(stamp, font, white, w - size.Width - 12, 10);
        g.DrawString("DEMO", font, shadow, 13, h - 25);
        g.DrawString("DEMO", font, white, 12, h - 26);
        return bmp;
    }
}
