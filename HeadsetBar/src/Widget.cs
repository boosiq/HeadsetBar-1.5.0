using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("HeadsetBar")]
[assembly: System.Reflection.AssemblyVersion("1.5.0.0")]

// Windows tells only top-level windows that a device came or went.
class DeviceWatcher : NativeWindow
{
    static DeviceWatcher keep; // otherwise collected
    readonly Action changed, quit;
    public DeviceWatcher(Action changed, Action quit)
    {
        this.quit = quit;
        this.changed = changed;
        keep = this;
        CreateHandle(new CreateParams { Caption = "HeadsetBar.Devices", Style = unchecked((int)0x80000000) }); // WS_POPUP, never shown
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0219) changed(); // WM_DEVICECHANGE
        if (m.Msg == 0x0010) { quit(); return; } // WM_CLOSE, sent by the installer
        base.WndProc(ref m);
    }
}

// A child window of the taskbar, so it follows auto-hide, fullscreen apps and Explorer itself.
class Widget : NativeWindow
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    Flyout flyout;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile List<Device> devices = new List<Device>();
    volatile object widgetsButton; // Rectangle of the Windows 11 weather button, or null
    string drawn;
    int placed, thickness; // length along the taskbar, and its other dimension
    bool vertical;

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "HeadsetBar.SingleInstance", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            var w = new Widget();
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += delegate { w.Tick(); };
            timer.Start();
            var frames = new System.Windows.Forms.Timer { Interval = 100 };
            frames.Tick += delegate { w.Frame(); };
            frames.Start();
            new Thread(w.Poll) { IsBackground = true }.Start();
            new DeviceWatcher(() => w.wake.Set(), w.Quit);
            SystemEvents.PowerModeChanged += delegate { w.wake.Set(); };
            Application.Run();
        }
    }

    void Poll()
    {
        // A device needs a few seconds after plugging in before it answers, so a change is followed by several quick checks.
        int burst = 0;
        var widgetsAt = DateTime.MinValue;
        while (true)
        {
            if (DateTime.UtcNow - widgetsAt > TimeSpan.FromSeconds(6))
            {
                try { widgetsButton = FindWidgetsButton(); } catch (Exception) { widgetsButton = null; }
                widgetsAt = DateTime.UtcNow;
            }
            try
            {
                Hid.Listen(() => wake.Set());
                devices = Battery.ReadAll(burst > 0);
            }
            catch (Exception) { }
            if (burst > 0) burst--;
            if (!wake.WaitOne(burst > 0 ? 800 : 3000)) continue;
            burst = 6;
            Thread.Sleep(150); // a plug or unplug arrives as a dozen notifications
            wake.Reset();
        }
    }
    // The weather button is drawn by XAML and has no window of its own.
    static object FindWidgetsButton()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || Legacy) return null;
        var b = AutomationElement.FromHandle(bar).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
        if (b == null) return null;
        var r = b.Current.BoundingRectangle;
        return r.IsEmpty ? null : (object)new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    List<Row> Rows()
    {
        var rows = devices.Select(d => new Row { Device = d }).ToList();
        if (rows.Count == 0) rows.Add(new Row { Text = "Нет устройств на связи", Glyph = "" });
        bool auto;
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) auto = k != null && k.GetValue("HeadsetBar") != null;
        rows.Add(new Row { Text = "Запускать вместе с Windows", Glyph = "", On = auto, KeepOpen = true, Click = () =>
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (auto) k.DeleteValue("HeadsetBar", false);
                else k.SetValue("HeadsetBar", "\"" + Application.ExecutablePath + "\"");
        } });
        rows.Add(new Row { Text = "Обновить сейчас", Glyph = "", KeepOpen = true, Click = () => wake.Set() });
        rows.Add(new Row { Text = "Выход", Glyph = "", Click = Quit });
        return rows;
    }

    void OpenFlyout()
    {
        // The click that closed the panel must not open it again.
        if (flyout != null || DateTime.UtcNow - Flyout.ClosedAt < TimeSpan.FromMilliseconds(300)) return;
        var bar = FindWindow("Shell_TrayWnd", null);
        RECT b, w;
        if (!GetWindowRect(bar, out b) || !GetWindowRect(Handle, out w)) return;
        flyout = new Flyout(Rows, GetDpiForWindow(bar) / 96f, Rectangle.FromLTRB(w.L, w.T, w.R, w.B), Rectangle.FromLTRB(b.L, b.T, b.R, b.B));
        flyout.FormClosed += delegate { flyout = null; };
        flyout.Show();
        SetForegroundWindow(flyout.Handle); // so that a click elsewhere closes it
    }
    // Redraws only; the position is checked by Tick.
    void Frame()
    {
        var list = devices;
        if (drawn == null || Handle == IntPtr.Zero || !list.Any(d => d.Charging) || Key(list) != drawn) return;
        using (var bmp = Draw(list, thickness, drawnScale, drawnTheme, vertical)) Push(bmp);
        if (flyout != null) flyout.Animate();
    }
    float drawnScale;
    Theme drawnTheme;
    string Key(List<Device> list)
    {
        return thickness + "|" + vertical + "|" + drawnScale + "|" + drawnTheme.Light + "|" + string.Join(";", list.Select(d => d.Name + d.Kind + d.Percent + d.Charging + d.Offline));
    }

    void Tick()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero) return; // Explorer is restarting
        if (Handle != IntPtr.Zero && !IsWindow(Handle)) ReleaseHandle();
        if (Handle == IntPtr.Zero)
        {
            CreateHandle(new CreateParams
            {
                Caption = "HeadsetBar", Parent = bar,
                Style = 0x40000000 | 0x10000000,  // WS_CHILD | WS_VISIBLE
                ExStyle = 0x00080000 | 0x08000000 // WS_EX_LAYERED | WS_EX_NOACTIVATE
            });
            drawn = null;
        }

        RECT br, tr;
        GetWindowRect(bar, out br);
        var tray = FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out tr)) return;
        vertical = br.B - br.T > br.R - br.L;
        thickness = vertical ? br.R - br.L : br.B - br.T;
        float scale = GetDpiForWindow(bar) / 96f;
        drawnTheme = Theme.Get("SystemUsesLightTheme");
        drawnScale = scale;

        var list = devices;
        var key = Key(list);
        Bitmap bmp = key == drawn ? null : Draw(list, thickness, scale, drawnTheme, vertical);
        int length = bmp == null ? placed : vertical ? bmp.Height : bmp.Width;

        // From here on "left" means towards the start of the taskbar: up, when it is vertical.
        int start = vertical ? br.T : br.L;
        int edge = vertical ? tr.T : tr.L;
        if (Legacy) edge = Math.Min(edge, TaskListEdge(bar));
        edge = FreeRight(edge, length, Obstacles(bar, br));
        int pos = Math.Max(start, edge - length);
        if (bmp != null)
        {
            using (bmp) Push(bmp);
            drawn = key;
            if (flyout != null) flyout.Rebuild();
        }
        placed = length;
        if (Legacy) ReserveUpTo(pos, (int)(120 * scale));
        // Re-asserted every tick: Explorer raises its XAML layer above children after some changes.
        SetWindowPos(Handle, IntPtr.Zero, vertical ? 0 : pos - start, vertical ? pos - start : 0,
            vertical ? thickness : length, vertical ? length : thickness, 0x10 | 0x40); // NOACTIVATE | SHOWWINDOW
    }

    // ---- Windows 10: the list of open windows takes all the room up to the tray and has to be shortened ----

    static readonly bool Legacy = Environment.OSVersion.Version.Build < 22000;
    IntPtr taskList;
    int taskNatural, taskReserved = int.MinValue, rebarEdge = int.MinValue;

    // Where Explorer itself wants the list to end.
    int TaskListEdge(IntPtr bar)
    {
        var rebar = FindWindowEx(bar, IntPtr.Zero, "ReBarWindow32", null);
        var list = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        RECT r, l;
        if (list == IntPtr.Zero || !GetWindowRect(rebar, out r) || !GetWindowRect(list, out l)) { taskList = IntPtr.Zero; return int.MaxValue; }
        int edge = vertical ? l.B : l.R, outer = vertical ? r.B : r.R;
        // A shorter list counts as Explorer's own choice only after its container changed, or our change would feed on itself.
        if (list != taskList || outer != rebarEdge || edge > taskReserved) taskNatural = edge == taskReserved ? Math.Min(taskNatural, outer) : edge;
        taskList = list;
        rebarEdge = outer;
        return taskNatural;
    }

    void ReserveUpTo(int edge, int minimum)
    {
        RECT l;
        if (taskList == IntPtr.Zero || !GetWindowRect(taskList, out l)) return;
        if (edge - (vertical ? l.T : l.L) < minimum || (vertical ? l.B : l.R) == edge) return;
        taskReserved = edge;
        // Asynchronous, so a busy Explorer cannot hang this program.
        SetWindowPos(taskList, IntPtr.Zero, 0, 0, vertical ? l.R - l.L : edge - l.L, vertical ? edge - l.T : l.B - l.T, 0x2 | 0x4 | 0x10 | 0x4000); // NOMOVE | NOZORDER | NOACTIVATE | ASYNC
    }

    public void Quit()
    {
        if (taskList != IntPtr.Zero) ReserveUpTo(taskNatural, 0);
        DestroyHandle();
        Application.Exit();
    }
    // Moves left past everything that already occupies the spot.
    public static int FreeRight(int right, int width, List<Rectangle> obstacles)
    {
        foreach (var o in obstacles.OrderByDescending(o => o.Right))
            if (o.Left < right && o.Right > right - width) right = o.Left;
        return right;
    }

    List<Rectangle> Obstacles(IntPtr bar, RECT br)
    {
        var result = new List<Rectangle>();
        uint shell, mine = GetCurrentProcessId();
        GetWindowThreadProcessId(bar, out shell);
        EnumProc add = (hwnd, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            RECT r;
            int cloaked = 0;
            if (pid == shell || pid == mine || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out r)) return true;
            // Only small windows lying inside the taskbar; overlays covering all of it are not widgets.
            if (r.T < br.T - 2 || r.B > br.B + 2 || r.L < br.L - 2 || r.R > br.R + 2 || r.B - r.T < 4 || r.R - r.L < 4
                || (vertical ? r.B - r.T > (br.B - br.T) / 2 : r.R - r.L > (br.R - br.L) / 2)) return true;
            DwmGetWindowAttribute(hwnd, 14, out cloaked, 4);
            // Invisible tooltips and IME bars are not click targets.
            if (cloaked == 0 && (GetWindowLong(hwnd, -20) & 0x20) == 0)
                result.Add(vertical ? Rectangle.FromLTRB(r.T, 0, r.B, 1) : Rectangle.FromLTRB(r.L, 0, r.R, 1));
            return true;
        };
        EnumWindows(add, IntPtr.Zero);
        EnumChildWindows(bar, add, IntPtr.Zero);
        var wb = widgetsButton;
        // Cached, so ignore it unless it sits on this taskbar's right half.
        if (wb != null && !vertical && ((Rectangle)wb).Left > (br.L + br.R) / 2) result.Add((Rectangle)wb);
        return result;
    }

    // Vertical taskbar: icon, battery and percentage under each other.
    static Bitmap DrawVertical(List<Device> list, int w, float s, Theme t)
    {
        float icon = 20 * s, battery = 14 * s, text = 16 * s, pad = 6 * s, gap = 10 * s;
        Func<Device, float> height = d => icon + (d.Percent >= 0 || d.Charging ? battery : 0) + (d.Percent >= 0 || !d.Charging && !d.Offline ? text : 0);
        float h = 2 * pad + (list.Count == 0 ? icon : list.Sum(height) + (list.Count - 1) * gap);
        var bmp = new Bitmap(w, (int)Math.Ceiling(h), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.Clear(Color.FromArgb(1, 128, 128, 128));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            float y = pad, phase = Ui.Phase, bw = Math.Min(25 * s, w - 4 * s);
            if (list.Count == 0) Ui.Glyph(g, "", 16 * s, t.Dim, new RectangleF(0, y, w, icon));
            foreach (var d in list)
            {
                float at = y;
                Ui.Icon(g, d.Glyph, 16 * s, d.Offline ? t.Dim : t.Fg, new RectangleF(0, at, w, icon));
                at += icon;
                if (d.Percent >= 0 || d.Charging)
                {
                    Ui.Battery(g, new RectangleF((w - bw) / 2, at + s, bw, 12 * s), d.Percent, d.Charging, t, s, phase);
                    at += battery;
                }
                if (d.Percent >= 0 || !d.Charging && !d.Offline)
                    using (var b = new SolidBrush(t.Level(d.Percent, d.Charging)))
                        g.DrawString(d.Percent >= 0 ? d.Percent + "%" : "—", font, b, new RectangleF(0, at, w, text), fmt);
                y += height(d) + gap;
            }
        }
        return bmp;
    }
    static Bitmap Draw(List<Device> list, int h, float s, Theme t, bool vertical)
    {
        if (vertical) return DrawVertical(list, h, s, t);
        float icon = 18 * s, battery = 25 * s, pad = 8 * s, gap = 5 * s;
        using (var font = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel))
        using (var fmt = new StringFormat(StringFormat.GenericTypographic))
        {
            Func<Device, string> value = d => d.Percent >= 0 ? d.Percent + "%" : d.Charging || d.Offline ? "" : "—";
            var widths = new List<float>();
            using (var tmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                foreach (var d in list) widths.Add(value(d) == "" ? 0 : gap + g.MeasureString(value(d), font, 9999, fmt).Width);
            }
            float w = list.Count == 0 ? icon + 2 * pad : widths.Sum() + list.Count * (icon + gap) + list.Count(d => d.Percent >= 0 || d.Charging) * battery + (list.Count - 1) * 12 * s + 2 * pad;
            var bmp = new Bitmap((int)Math.Ceiling(w), h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 128, 128, 128)); // fully transparent pixels would not receive clicks
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                float x = pad, phase = Ui.Phase;
                if (list.Count == 0) Ui.Glyph(g, "", 16 * s, t.Dim, new RectangleF(x, 0, icon, h));
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    Ui.Icon(g, d.Glyph, 16 * s, d.Offline ? t.Dim : t.Fg, new RectangleF(x, 0, icon, h));
                    x += icon + gap;
                    bool known = d.Percent >= 0 || d.Charging;
                    if (known) Ui.Battery(g, new RectangleF(x, (h - 12 * s) / 2, battery, 12 * s), d.Percent, d.Charging, t, s, phase);
                    if (known) x += battery;
                    var size = g.MeasureString("0%", font, 9999, fmt);
                    using (var b = new SolidBrush(t.Level(d.Percent, d.Charging))) g.DrawString(value(d), font, b, x + gap, (h - size.Height) / 2, fmt);
                    x += widths[i] + 12 * s;
                }
            }
            return bmp;
        }
    }
    void Push(Bitmap bmp)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var hb = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(dc, hb);
        var size = new Size(bmp.Width, bmp.Height);
        var zero = new Point();
        int blend = 0x01FF0000; // AC_SRC_OVER, alpha 255, AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, dc, ref zero, 0, ref blend, 2);
        SelectObject(dc, old);
        DeleteObject(hb);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0202 || m.Msg == 0x0205) // left or right button up
        {
            OpenFlyout();
            return;
        }
        base.WndProc(ref m);
    }

    struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr d, IntPtr pos, ref Size s, IntPtr src, ref Point p, int k, ref int b, int f);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr d, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr d);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);
}
