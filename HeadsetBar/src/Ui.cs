using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Shared by the widget and the installer.
class Theme
{
    public bool Light;
    public Color Fg, Dim, Bg, Hover, Line, Accent, OnAccent, Green, Amber, Red;

    // "SystemUsesLightTheme" for the taskbar and its flyout, "AppsUseLightTheme" for windows.
    public static Theme Get(string value)
    {
        bool light;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            light = k != null && Convert.ToInt32(k.GetValue(value, 0)) == 1;
        return light
            ? new Theme { Light = true, Fg = Rgb(0x1B1B1B), Dim = Rgb(0x5C5C5C), Bg = Rgb(0xF6F6F6), Hover = Rgb(0xE7E7E7), Line = Rgb(0xDADADA),
                Accent = Rgb(0x0F7B4B), OnAccent = Color.White, Green = Rgb(0x12873F), Amber = Rgb(0x9A6400), Red = Rgb(0xC42B1C) }
            : new Theme { Fg = Color.White, Dim = Rgb(0xB4B4B4), Bg = Rgb(0x2A2A2A), Hover = Rgb(0x3A3A3A), Line = Rgb(0x454545),
                Accent = Rgb(0x4CC38A), OnAccent = Rgb(0x06281A), Green = Rgb(0x56D364), Amber = Rgb(0xF2B84B), Red = Rgb(0xFF7B72) };
    }
    static Color Rgb(int v) { return Color.FromArgb(255, Color.FromArgb(v)); }

    public Color Level(int percent, bool charging)
    {
        return charging ? Green : percent < 0 ? Dim : percent <= 20 ? Red : percent <= 35 ? Amber : Fg;
    }
}

static class Ui
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // One charging cycle; a still picture when the user has switched Windows animations off.
    public static float Phase
    {
        get
        {
            int on = 1;
            SystemParametersInfo(0x1042, 0, ref on, 0); // SPI_GETCLIENTAREAANIMATION
            return on == 0 ? 0 : (Environment.TickCount & int.MaxValue) % 1800 / 1800f;
        }
    }
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int b, ref int v, int w);

    // While charging, a wave runs up to the current level and the bolt pulses; phase is 0..1, 0 = still.
    public static void Battery(Graphics g, RectangleF r, int percent, bool charging, Theme t, float s, float phase = 0)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = new RectangleF(r.X + s / 2, r.Y + s / 2, r.Width - 3.5f * s, r.Height - s);
        var color = t.Level(percent, charging);
        using (var pen = new Pen(Color.FromArgb(charging ? 255 : 190, color), 1.2f * s))
        {
            if (percent < 0 && !charging) pen.DashPattern = new[] { 2f, 1.6f };
            using (var p = Round(body, 3 * s)) g.DrawPath(pen, p);
        }
        using (var nub = Round(new RectangleF(body.Right + 1.2f * s, r.Y + r.Height * .32f, 1.8f * s, r.Height * .36f), .9f * s))
        using (var b = new SolidBrush(Color.FromArgb(charging ? 255 : 190, color))) g.FillPath(b, nub);

        int level = Math.Max(percent, 0);
        var inner = RectangleF.Inflate(body, -2 * s, -2 * s);
        // Charging: a bright wave fills up to the level again and again over a dimmed fill.
        int shown = charging && percent < 0 ? 100 : level;
        float wave = charging && phase > 0 ? Math.Min(1, phase / .7f) : 1;
        foreach (var layer in charging ? new[] { new { Part = 1f, Alpha = 80 }, new { Part = wave, Alpha = 255 } } : new[] { new { Part = 1f, Alpha = 255 } })
        {
            float width = inner.Width * shown / 100f * layer.Part;
            if (width < 1) continue;
            using (var p = Round(new RectangleF(inner.X, inner.Y, width, inner.Height), 1.4f * s))
            using (var b = new SolidBrush(Color.FromArgb(layer.Alpha, color))) g.FillPath(b, p);
        }        if (!charging) return;
        // The bolt overlaps the case slightly; the outline separates it from the fill.
        float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2, h = r.Height * (1.25f + .16f * (float)Math.Sin(phase * 2 * Math.PI));
        var bolt = new[] { new PointF(.10f, -.5f), new PointF(-.34f, .08f), new PointF(-.03f, .08f), new PointF(-.10f, .5f), new PointF(.34f, -.1f), new PointF(.03f, -.1f) }
            .Select(q => new PointF(cx + q.X * h, cy + q.Y * h)).ToArray();
        using (var pen = new Pen(t.Light ? Color.White : Color.FromArgb(32, 32, 32), 2.2f * s) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, bolt);
        using (var b = new SolidBrush(t.Light ? Color.FromArgb(27, 27, 27) : Color.White)) g.FillPolygon(b, bolt);
    }

    public static void Glyph(Graphics g, string glyph, float size, Color c, RectangleF r)
    {
        using (var f = new Font("Segoe MDL2 Assets", size, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(c))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString(glyph, f, b, r, fmt);
        }
    }

    // The system keyboard glyph turns into noise at taskbar size, so this one is drawn.
    static void Keyboard(Graphics g, float size, Color c, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = size, h = size * .7f, u = w / 13f;
        var body = new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        using (var pen = new Pen(c, Math.Max(1, size * .075f)))
        using (var p = Round(body, size * .18f)) g.DrawPath(pen, p);
        using (var b = new SolidBrush(c))
        {
            float k = u * 1.6f, gap = (w - 4 * k) / 5;
            Action<float, float, float> key = (x, y, kw) => { using (var p = Round(new RectangleF(body.X + x, body.Y + y, kw, k), k * .3f)) g.FillPath(b, p); };
            for (int i = 0; i < 4; i++)
            {
                key(gap + i * (k + gap), h * .2f, k);
                if (i > 0 && i < 3) key(gap + i * (k + gap), h * .62f, k);
            }
            key(gap, h * .62f, k);
            key(gap + 3 * (k + gap), h * .62f, k);
            key(gap + k + gap, h * .62f, 2 * k + gap); // space bar
        }
    }

    public static void Icon(Graphics g, string glyph, float size, Color c, RectangleF r)
    {
        if (glyph == "") Keyboard(g, size, c, r);
        else Glyph(g, glyph, size, c, r);
    }

    public static void Switch(Graphics g, RectangleF r, bool on, Theme t, float s)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = Round(r, r.Height / 2))
            if (on) using (var b = new SolidBrush(t.Accent)) g.FillPath(b, p);
            else using (var pen = new Pen(t.Dim, 1.2f * s)) g.DrawPath(pen, p);
        float d = r.Height - 6 * s;
        using (var b = new SolidBrush(on ? t.OnAccent : t.Dim))
            g.FillEllipse(b, on ? r.Right - d - 3 * s : r.X + 3 * s, r.Y + 3 * s, d, d);
    }

    public static void DarkTitle(IntPtr h, bool dark) { int v = dark ? 1 : 0; if (DwmSetWindowAttribute(h, 20, ref v, 4) != 0) DwmSetWindowAttribute(h, 19, ref v, 4); } // 19 before Windows 10 2004
    public static void RoundCorners(IntPtr h) { int v = 2; DwmSetWindowAttribute(h, 33, ref v, 4); } // ignored on Windows 10
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
}
