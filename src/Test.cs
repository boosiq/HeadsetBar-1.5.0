using System;
static class Test
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL " + what); }
    static void Main()
    {
        var r = new byte[17]; r[0] = 8; r[1] = 4; r[6] = 73; r[7] = 1;
        var d = Hid.ParseMouse(r);
        Check(d != null && d.Percent == 73 && d.Charging, "parse");
        r[6] = 101; Check(Hid.ParseMouse(r) == null, "over 100");
        r[6] = 50; r[1] = 3; Check(Hid.ParseMouse(r) == null, "other command");
        var o = new System.Collections.Generic.List<System.Drawing.Rectangle>();
        Check(Widget.FreeRight(1000, 60, o) == 1000, "free");
        o.Add(new System.Drawing.Rectangle(800, 0, 100, 48)); // adjacent to the next one
        o.Add(new System.Drawing.Rectangle(900, 0, 80, 48));
        Check(Widget.FreeRight(1000, 60, o) == 800, "stacked obstacles");
        var v = new[] { 4150, 3830, 3780, 3740, 3670, 3320 };
        Check(Hid.FromVoltage(4200, v) == 100 && Hid.FromVoltage(3830, v) == 50 && Hid.FromVoltage(3990, v) == 75 && Hid.FromVoltage(3000, v) == 0, "voltage");
        var on = new byte[17]; on[6] = 1;
        Check(Hid.IsOnline(on) && !Hid.IsOnline(new byte[17]) && !Hid.IsOnline(null), "link");
        var now = DateTime.UtcNow;
        Check(Hid.StillFresh(now.AddSeconds(-15), now) && !Hid.StillFresh(now.AddSeconds(-21), now), "mouse goes away");
        // captured from the mouse on its cable
        var m = new byte[] { 8, 4, 0, 0, 0, 2, 0x50, 1, 0x0F, 0xB8, 0, 0, 0, 0, 0, 0, 0x2F };
        d = Hid.ParseMouse(m); Check(d.Percent == 81 && d.Charging, "exact percent");
        Check(Hid.MousePercent(3988, 75, true) == 72 && Hid.MousePercent(0, 45, false) == 45 && Hid.MousePercent(4200, 95, true) == 99, "voltage edges");
        Check(Hid.MousePercent(3000, 1, false) == 1, "empty");
        // plugging in, unplugging and a voltage sag must not move the number at once
        var t0 = new DateTime(2026, 1, 1); var u = new Gauge();
        for (int i = 0; i < 5; i++) u.Update(62, false, t0.AddSeconds(i));
        for (int i = 5; i < 10; i++) Check(u.Update(86, true, t0.AddSeconds(i)) == 62, "plugged in");
        for (int i = 0; i <= 10; i++) u.Update(86, true, t0.AddMinutes(1 + i));
        int c = u.Update(86, true, t0.AddMinutes(12)); Check(c >= 73 && c <= 75, "charging " + c);
        for (int i = 1; i < 6; i++) Check(Math.Abs(u.Update(60, false, t0.AddMinutes(12).AddSeconds(i)) - c) <= 1, "unplugged");
        for (int i = 1; i < 6; i++) Check(u.Update(3, false, t0.AddMinutes(12).AddSeconds(6 + i)) >= c - 1, "sag");
        // overnight gap: one sagged reading is ignored, steady ones are believed
        var t1 = t0.AddHours(12); Check(u.Update(3, false, t1) >= c - 1, "one reading after a pause");
        u.Update(50, false, t1.AddSeconds(5)); Check(u.Update(50, false, t1.AddSeconds(10)) == 50, "after a pause");
        // a link that flaps while the mouse is switching on: once shown, never grey and never gone
        var sn = new Hid.Seen { Device = new Device { Percent = 60 }, At = t0 };
        bool?[] link = { true, false, null, true, false, false, null, true, false };
        for (int i = 0; i < 36; i++)
        {
            var shown = Hid.Decide(null, link[i % link.Length], sn, t0.AddSeconds(3 * i));
            Check(shown != null && !shown.Offline && shown.Percent == 60, "flapping " + i);
        }
        var offAt = sn.At;
        Check(Hid.Decide(null, false, sn, offAt.AddSeconds(21)).Offline, "really off");
        Check(Hid.Decide(null, null, sn, offAt.AddSeconds(99)).Offline, "off, dongle silent: grey, not gone");
        Check(Hid.Decide(null, null, null, t0) == null, "never heard of");
        // headset: no link query at all, only missing answers
        var hs = new Hid.Seen { Device = new Device { Kind = Kind.Headset, Percent = 80 }, At = t0 };
        Check(Hid.Decide(null, null, hs, t0.AddSeconds(9)).Percent == 80 && Hid.Decide(null, null, hs, t0.AddSeconds(30)).Offline, "headset");
        // cable to dongle and back: one entry, no grey moment
        var ct = new System.Collections.Generic.Dictionary<string, Hid.Seen> { { "M", new Hid.Seen { Device = new Device { Name = "M", Percent = 70, Charging = true }, At = t0 } } };
        Func<bool, int, bool, Device> mk = (off, pc, ch) => new Device { Name = "M", Kind = Kind.Mouse, Offline = off, Percent = pc, Charging = ch };
        var one = Hid.Merge(new System.Collections.Generic.List<Device> { mk(true, -1, false), mk(false, 70, true) }, ct, t0);
        Check(one.Count == 1 && one[0].Charging, "cable wins");
        one = Hid.Merge(new System.Collections.Generic.List<Device> { mk(true, -1, false) }, ct, t0.AddSeconds(3));
        Check(one.Count == 1 && one[0].Percent == 70 && !one[0].Charging && !one[0].Offline, "just unplugged");
        Check(Hid.Merge(new System.Collections.Generic.List<Device> { mk(true, -1, false) }, ct, t0.AddSeconds(30))[0].Offline, "unplugged and off");
        Console.WriteLine("ok");
        var t = System.Diagnostics.Stopwatch.StartNew();
        foreach (var x in Battery.ReadAll(true)) Console.WriteLine(x.Kind + " | " + x.Name + " | " + x.Percent + "% charging=" + x.Charging);
        Console.WriteLine(t.ElapsedMilliseconds + " ms");
    }
}
