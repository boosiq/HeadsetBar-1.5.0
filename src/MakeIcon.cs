using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

// Draws the application icon at build time.
static class MakeIcon
{
    static byte[] Png(int n)
    {
        using (var bmp = new Bitmap(n, n))
        using (var g = Graphics.FromImage(bmp))
        using (var ms = new MemoryStream())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(n * .03f, n * .03f, n * .94f, n * .94f);
            using (var p = Ui.Round(r, n * .22f))
            using (var b = new LinearGradientBrush(r, Color.FromArgb(22, 130, 88), Color.FromArgb(6, 48, 38), 60f)) g.FillPath(b, p);
            Ui.Glyph(g, "", n * .44f, Color.White, new RectangleF(0, n * .08f, n, n * .5f));
            Ui.Battery(g, new RectangleF(n * .24f, n * .63f, n * .54f, n * .22f), 75, false, new Theme { Fg = Color.White }, n / 64f);
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
    static void Main(string[] a)
    {
        int[] sizes = { 16, 32, 48, 256 };
        var png = sizes.Select(Png).ToArray();
        using (var w = new BinaryWriter(File.Create(a[0])))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int at = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)sizes[i]); w.Write((byte)sizes[i]); w.Write((short)0); w.Write((short)1); w.Write((short)32);
                w.Write(png[i].Length); w.Write(at);
                at += png[i].Length;
            }
            foreach (var p in png) w.Write(p);
        }
    }
}