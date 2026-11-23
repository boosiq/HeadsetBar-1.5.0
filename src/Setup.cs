using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("HeadsetBar Setup")]
[assembly: AssemblyVersion("1.5.0.0")]

// Per-user installer: no administrator rights. The same file, copied next to the app, uninstalls it.
static class Setup
{
    public const string Version = "1.5.0";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HeadsetBar";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\HeadsetBar");
    public static readonly string App = Path.Combine(Dir, "HeadsetBar.exe");
    static readonly string Uninstaller = Path.Combine(Dir, "Uninstall.exe");
    static readonly string Link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "HeadsetBar.lnk");

    public static bool Installed { get { return File.Exists(App); } }
    // An update keeps the user's earlier choice.
    public static bool Autostart
    {
        get
        {
            if (!Installed) return true;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HeadsetBar") != null;
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = Array.IndexOf(args, "/S") >= 0, remove = Array.IndexOf(args, "/uninstall") >= 0;
        if (!silent)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(remove));
            return 0;
        }
        try
        {
            if (remove) Uninstall(delegate { });
            else { Install(Autostart, delegate { }); Process.Start(App); }
            return 0;
        }
        catch (Exception) { return 1; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName("HeadsetBar"))
            using (p)
                try
                {
                    // Asked politely first: on Windows 10 the app gives the taskbar its space back when it closes.
                    PostMessage(FindWindow(null, "HeadsetBar.Devices"), 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (p.WaitForExit(2000)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (System.ComponentModel.Win32Exception) { } // another user's copy
                catch (InvalidOperationException) { } // closed in between
    }

    public static void Install(bool autostart, Action<int> progress)
    {
        progress(10);
        StopApp();
        progress(35);
        Directory.CreateDirectory(Dir);
        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("HeadsetBar.exe"))
        using (var dst = File.Create(App)) src.CopyTo(dst);
        var self = Application.ExecutablePath;
        if (!string.Equals(self, Uninstaller, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Uninstaller, true);
        progress(60);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        var link = shell.CreateShortcut(Link);
        link.TargetPath = App;
        link.Save();
        progress(80);

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            if (autostart) k.SetValue("HeadsetBar", "\"" + App + "\"");
            else k.DeleteValue("HeadsetBar", false);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "HeadsetBar");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("DisplayIcon", App);
            k.SetValue("Publisher", "HeadsetBar");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Uninstaller + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + Uninstaller + "\" /uninstall /S");
            k.SetValue("NoModify", 1);
            k.SetValue("NoRepair", 1);
        }
        progress(100);
    }

    public static void Uninstall(Action<int> progress)
    {
        progress(20);
        StopApp();
        progress(50);
        File.Delete(Link);
        File.Delete(App);
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue("HeadsetBar", false);
        Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKey(@"Software\HeadsetBar", false); // saved levels
        progress(100);
        // A running exe cannot delete itself: retry until this window is closed.
        // rd without /s removes the folder only if nothing else is in it.
        Process.Start(new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,60) do @if exist \"" + Uninstaller + "\" (ping -n 3 127.0.0.1 >nul & del \"" + Uninstaller + "\" 2>nul) else (rd \"" + Dir + "\" 2>nul & exit)")
        { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }
}

class SetupForm : Form
{
    readonly Theme t = Theme.Get("AppsUseLightTheme");
    readonly float s;
    readonly bool remove;
    readonly Label title = new Label(), text = new Label();
    readonly Toggle auto = new Toggle(), run = new Toggle();
    readonly PushButton ok = new PushButton { Primary = true }, cancel = new PushButton();
    readonly Bar bar = new Bar();
    bool done, busy;

    public SetupForm(bool remove)
    {
        this.remove = remove;
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Text = remove ? "Удаление HeadsetBar" : "Установка HeadsetBar";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(X(600), X(360));
        DoubleBuffered = true;
        BackColor = t.Bg;
        ForeColor = t.Fg;
        Font = new Font("Segoe UI", 14 * s, GraphicsUnit.Pixel);

        Place(title, 236, 36, 336, 40);
        title.Font = new Font("Segoe UI Semibold", 26 * s, GraphicsUnit.Pixel);
        Place(text, 238, 84, 334, 76);
        text.ForeColor = t.Dim;
        Place(auto, 236, 172, 336, 40);
        Place(run, 236, 214, 336, 40);
        Place(bar, 238, 184, 334, 8);
        Place(ok, 316, 300, 140, 36);
        Place(cancel, 466, 300, 106, 36);
        foreach (var c in new ThemedControl[] { auto, run, ok, cancel, bar }) { c.T = t; c.S = s; }
        bar.TabStop = false;

        auto.Text = "Запускать вместе с Windows";
        auto.On = Setup.Autostart;
        run.Text = "Запустить после установки";
        run.On = true;
        cancel.Text = "Отмена";
        title.Text = remove ? "Удалить HeadsetBar?" : "HeadsetBar";
        text.Text = remove
            ? "Виджет исчезнет с панели задач, автозапуск будет отключён."
            : "Заряд наушников, мыши и клавиатуры на панели задач, слева от системного трея.";
        ok.Text = remove ? "Удалить" : Setup.Installed ? "Обновить" : "Установить";
        auto.Visible = run.Visible = !remove;
        bar.Visible = false;
        ok.Click += delegate { if (done) Close(); else Start(); };
        cancel.Click += delegate { Close(); };
        AcceptButton = ok;
        CancelButton = cancel;
    }

    int X(float v) { return (int)Math.Round(v * s); }
    void Place(Control c, int x, int y, int w, int h)
    {
        c.SetBounds(X(x), X(y), X(w), X(h));
        c.BackColor = t.Bg;
        Controls.Add(c);
    }

    async void Start()
    {
        busy = true;
        ok.Enabled = cancel.Enabled = auto.Visible = run.Visible = false;
        bar.Visible = true;
        title.Text = remove ? "Удаление…" : "Установка…";
        bool autostart = auto.On;
        Action<int> progress = v => BeginInvoke((Action)(() => { bar.Value = v; bar.Invalidate(); }));
        try
        {
            await Task.Run(() => { if (remove) Setup.Uninstall(progress); else Setup.Install(autostart, progress); });
            if (!remove && run.On) Process.Start(Setup.App);
            done = true;
            title.Text = "Готово";
            text.Text = remove ? "HeadsetBar удалён с этого компьютера."
                : run.On ? "Заряд показан слева от системного трея. Нажмите на него, чтобы увидеть подробности."
                : "Запустить HeadsetBar можно из меню «Пуск».";
            ok.Text = "Закрыть";
            cancel.Visible = false;
            ok.Left = cancel.Right - ok.Width;
        }
        catch (Exception e)
        {
            title.Text = "Не получилось";
            text.Text = e.Message;
            ok.Text = "Повторить";
            cancel.Enabled = true;
            bar.Visible = false;
        }
        busy = false;
        ok.Enabled = true;
        ok.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.DarkTitle(Handle, !t.Light);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var side = new Rectangle(0, 0, X(200), ClientSize.Height);
        using (var b = new LinearGradientBrush(side, Color.FromArgb(13, 92, 64), Color.FromArgb(5, 34, 28), 65f)) g.FillRectangle(b, side);
        // A picture of what gets installed.
        var dark = new Theme { Fg = Color.White, Green = Color.FromArgb(86, 211, 100), Amber = Color.FromArgb(242, 184, 75), Red = Color.FromArgb(255, 123, 114) };
        string[] glyphs = { "", "", "" };
        int[] levels = { 85, 60, 25 };
        for (int i = 0; i < 3; i++)
        {
            var card = new RectangleF(30 * s, (74 + i * 74) * s, 140 * s, 60 * s);
            using (var p = Ui.Round(card, 10 * s))
            using (var b = new SolidBrush(Color.FromArgb(34, 255, 255, 255))) g.FillPath(b, p);
            Ui.Icon(g, glyphs[i], 24 * s, Color.White, new RectangleF(card.X + 12 * s, card.Y, 36 * s, card.Height));
            Ui.Battery(g, new RectangleF(card.X + 68 * s, card.Y + 19 * s, 50 * s, 22 * s), levels[i], i == 1, dark, 1.5f * s);
        }
        TextRenderer.DrawText(g, "Версия " + Setup.Version, Font, new Rectangle(0, ClientSize.Height - X(38), side.Width, X(24)), Color.FromArgb(170, 205, 190), TextFormatFlags.HorizontalCenter);
    }
}

// Real buttons underneath, so keyboard, focus and screen readers keep working.
class ThemedControl : Button
{
    public Theme T;
    public float S = 1;
    bool hover;
    protected bool Hover { get { return hover; } }
    public ThemedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(T.Bg);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Draw(e.Graphics, new RectangleF(S, S, Width - 2 * S - 1, Height - 2 * S - 1));
        if (Focused && ShowFocusCues)
            using (var pen = new Pen(T.Fg, 1.6f * S))
            using (var p = Ui.Round(new RectangleF(S, S, Width - 2 * S - 1, Height - 2 * S - 1), 6 * S)) e.Graphics.DrawPath(pen, p);
    }
    protected virtual void Draw(Graphics g, RectangleF r) { }
}

class PushButton : ThemedControl
{
    public bool Primary;
    protected override void Draw(Graphics g, RectangleF r)
    {
        var back = Primary ? (Hover ? ControlPaint.Light(T.Accent, .2f) : T.Accent) : Hover ? T.Line : T.Hover;
        var fore = Primary ? T.OnAccent : T.Fg;
        if (!Enabled) { back = T.Hover; fore = T.Dim; }
        using (var p = Ui.Round(r, 6 * S))
        using (var b = new SolidBrush(back)) g.FillPath(b, p);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class Toggle : ThemedControl
{
    bool on;
    public bool On { get { return on; } set { on = value; AccessibleDescription = on ? "Включено" : "Выключено"; Invalidate(); } }
    public Toggle() { AccessibleRole = AccessibleRole.CheckButton; }
    protected override void OnClick(EventArgs e) { On = !On; base.OnClick(e); }
    protected override void Draw(Graphics g, RectangleF r)
    {
        if (Hover)
            using (var p = Ui.Round(r, 6 * S))
            using (var b = new SolidBrush(T.Hover)) g.FillPath(b, p);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(2 * S), 0, Width - (int)(60 * S), Height), T.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        Ui.Switch(g, new RectangleF(r.Right - 48 * S, r.Y + (r.Height - 20 * S) / 2, 40 * S, 20 * S), On, T, S);
    }
}

class Bar : ThemedControl
{
    public int Value;
    public Bar() { SetStyle(ControlStyles.Selectable, false); Cursor = Cursors.Default; AccessibleRole = AccessibleRole.ProgressBar; }
    protected override void Draw(Graphics g, RectangleF r)
    {
        using (var p = Ui.Round(r, r.Height / 2))
        using (var b = new SolidBrush(T.Line)) g.FillPath(b, p);
        if (Value <= 0) return;
        using (var p = Ui.Round(new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * Value / 100f), r.Height), r.Height / 2))
        using (var b = new SolidBrush(T.Accent)) g.FillPath(b, p);
    }
}
