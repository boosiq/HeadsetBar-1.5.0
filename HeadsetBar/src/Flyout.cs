using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

class Row
{
    public string Text, Glyph;
    public Device Device;      // set: a device line
    public bool? On;           // set: a switch
    public Action Click;       // null: not selectable
    public bool KeepOpen;
}

// The panel above the taskbar that opens by clicking the widget.
class Flyout : Form
{
    readonly Func<List<Row>> build;
    readonly float s;
    readonly Theme t;
    List<Row> rows;
    int hot = -1;
    public static DateTime ClosedAt;

    public Flyout(Func<List<Row>> build, float scale, Rectangle widget, Rectangle bar)
    {
        this.build = build;
        s = scale;
        t = Theme.Get("SystemUsesLightTheme");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = t.Bg;
        Text = "HeadsetBar";
        Rebuild();
        var area = Screen.FromRectangle(widget).Bounds;
        int gap = (int)(10 * s);
        int x = Math.Max(area.Left + gap, Math.Min(widget.Left + widget.Width / 2 - Width / 2, area.Right - Width - gap));
        int y = bar.Top > area.Top ? bar.Top - Height - gap : bar.Bottom + gap;
        if (bar.Height > bar.Width)
        {
            x = bar.Left > area.Left ? bar.Left - Width - gap : bar.Right + gap;
            y = Math.Max(area.Top + gap, Math.Min(widget.Bottom - Height, area.Bottom - Height - gap));
        }
        Location = new Point(x, y);
    }

    public void Rebuild()
    {
        rows = build();
        AccessibleName = string.Join("; ", rows.Where(r => r.Device != null).Select(r => r.Device.Name + ", " + r.Device.Status));
        int h = (int)(52 * s) + rows.Sum(r => RowHeight(r)) + (int)(8 * s);
        if (Height != h || Width == 0) SetBounds(Left, Top + Height - h, (int)(320 * s), h); // grows upwards
        Invalidate();
    }

    public void Animate() { if (rows.Any(r => r.Device != null && r.Device.Charging)) Invalidate(); }

    int RowHeight(Row r) { return (int)((r.Device != null ? 58 : 38) * s); }

    Rectangle RowBounds(int index)
    {
        int y = (int)(48 * s);
        for (int i = 0; i < index; i++) y += RowHeight(rows[i]);
        return new Rectangle((int)(6 * s), y, Width - (int)(12 * s), RowHeight(rows[index]));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.RoundCorners(Handle);
    }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { ClosedAt = DateTime.UtcNow; base.OnFormClosed(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var title = new Font("Segoe UI Semibold", 15 * s, GraphicsUnit.Pixel))
        using (var text = new Font("Segoe UI", 13.5f * s, GraphicsUnit.Pixel))
        using (var small = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel))
        using (var line = new Pen(t.Line))
        {
            Put(g, "Заряд устройств", title, t.Fg, new RectangleF(16 * s, 12 * s, Width - 32 * s, 26 * s), false);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var b = RowBounds(i);
                if (r.Device == null && i > 0 && rows[i - 1].Device != null || r.Click != null && i > 0 && rows[i - 1].Click == null)
                    g.DrawLine(line, 16 * s, b.Top, Width - 16 * s, b.Top);
                if (i == hot)
                    using (var p = Ui.Round(RectangleF.Inflate(b, 0, -2 * s), 5 * s))
                    using (var br = new SolidBrush(t.Hover)) g.FillPath(br, p);
                float x = b.X + 10 * s;
                if (r.Device != null)
                {
                    var d = r.Device;
                    Ui.Icon(g, d.Glyph, 20 * s, d.Offline ? t.Dim : t.Fg, new RectangleF(x, b.Y, 28 * s, b.Height));
                    float tx = x + 40 * s, right = b.Right - 10 * s, bw = 30 * s;
                    string value = d.Percent >= 0 ? d.Percent + "%" : "";
                    float room = right - bw - (value == "" ? 0 : 52 * s) - tx - 6 * s;
                    Put(g, d.Name, text, t.Fg, new RectangleF(tx, b.Y + 8 * s, room, 22 * s), false);
                    Put(g, d.Status, small, d.Charging ? t.Green : t.Dim, new RectangleF(tx, b.Y + 29 * s, room, 20 * s), false);
                    if (d.Percent >= 0 || d.Charging) Ui.Battery(g, new RectangleF(right - bw, b.Y + (b.Height - 14 * s) / 2, bw, 14 * s), d.Percent, d.Charging, t, s, Ui.Phase);
                    if (value != "") Put(g, value, title, t.Level(d.Percent, d.Charging), new RectangleF(right - bw - 58 * s, b.Y, 52 * s, b.Height), true);
                }
                else
                {
                    if (r.Glyph != null) Ui.Glyph(g, r.Glyph, 15 * s, t.Dim, new RectangleF(x, b.Y, 28 * s, b.Height));
                    Put(g, r.Text, text, r.Click == null ? t.Dim : t.Fg, new RectangleF(r.Glyph == null ? x : x + 40 * s, b.Y, b.Width - 90 * s, b.Height), false);
                    if (r.On != null) Ui.Switch(g, new RectangleF(b.Right - 48 * s, b.Y + (b.Height - 20 * s) / 2, 38 * s, 20 * s), r.On.Value, t, s);
                }
            }
        }
    }

    static void Put(Graphics g, string s, Font f, Color c, RectangleF r, bool right)
    {
        TextRenderer.DrawText(g, s, f, Rectangle.Round(r), c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | (right ? TextFormatFlags.Right : TextFormatFlags.Left));
    }

    void Hot(int i)
    {
        if (i == hot) return;
        hot = i;
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        Hot(Enumerable.Range(0, rows.Count).Where(i => rows[i].Click != null && RowBounds(i).Contains(e.Location)).DefaultIfEmpty(-1).First());
    }
    protected override void OnMouseLeave(EventArgs e) { Hot(-1); }
    protected override void OnMouseUp(MouseEventArgs e) { OnMouseMove(e); Run(); }

    protected override bool ProcessCmdKey(ref Message m, Keys key)
    {
        if (key == Keys.Escape) Close();
        else if (key == Keys.Enter || key == Keys.Space) Run();
        else if (key == Keys.Down || key == Keys.Tab) Step(1);
        else if (key == Keys.Up || key == (Keys.Shift | Keys.Tab)) Step(-1);
        else return base.ProcessCmdKey(ref m, key);
        return true;
    }
    void Step(int by)
    {
        var can = Enumerable.Range(0, rows.Count).Where(i => rows[i].Click != null).ToList();
        int at = can.IndexOf(hot);
        Hot(can[at < 0 ? (by > 0 ? 0 : can.Count - 1) : (at + by + can.Count) % can.Count]);
    }
    void Run()
    {
        if (hot < 0 || hot >= rows.Count || rows[hot].Click == null) return;
        var r = rows[hot];
        if (!r.KeepOpen) Close();
        r.Click();
        if (r.KeepOpen) Rebuild();
    }
}
