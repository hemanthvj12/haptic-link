using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HapticLink.App;

/// <summary>
/// HapticLink's icon, drawn in code: a rounded square (purple to blue when the bridge is on, grey otherwise) with a white
/// controller shape and vibration waves on both sides.
/// </summary>
internal static class IconMaker
{
    public static Icon Create(int size, bool active)
    {
        using var bitmap = Draw(size, active);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public static Bitmap Draw(int size, bool active)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = size;

        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, s - 1, s - 1), s * 0.22f))
        using (var fill = active
                   ? new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(0x7C, 0x5C, 0xFF), Color.FromArgb(0x2E, 0x8B, 0xFF))
                   : new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(0x7A, 0x7A, 0x7A), Color.FromArgb(0x50, 0x50, 0x50)))
            g.FillPath(fill, path);

        // Controller: a capsule body with two grips.
        using var white = new SolidBrush(Color.White);
        var body = new RectangleF(s * 0.30f, s * 0.38f, s * 0.40f, s * 0.24f);
        using (var capsule = RoundedRect(body, body.Height / 2)) g.FillPath(white, capsule);
        g.FillEllipse(white, s * 0.28f, s * 0.44f, s * 0.16f, s * 0.22f);
        g.FillEllipse(white, s * 0.56f, s * 0.44f, s * 0.16f, s * 0.22f);

        // Waves: two arcs on each side.
        using var pen = new Pen(Color.White, Math.Max(1f, s * 0.065f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cy = s * 0.50f;
        for (int i = 0; i < 2; i++)
        {
            float r = s * (0.13f + i * 0.09f);
            g.DrawArc(pen, s * 0.24f - r, cy - r, r * 2, r * 2, 150, 60);   // left
            g.DrawArc(pen, s * 0.76f - r, cy - r, r * 2, r * 2, -30, 60);   // right
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Writes a multi-size .ico (PNG frames), e.g. Assets\app.ico.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = sizes.Select(size =>
        {
            using var bmp = Draw(size, active: true);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return (size, data: ms.ToArray());
        }).ToList();
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);
    }
}
