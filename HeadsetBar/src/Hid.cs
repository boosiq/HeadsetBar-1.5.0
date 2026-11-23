using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

// Devices with their own battery protocol over USB. Every request here is a read.
static class Hid
{
    class Proto
    {
        public Kind Kind;
        public string Name;        // null = what the device calls itself
        public int Vid;
        public int[] Pids;         // null = any product of the vendor
        public int Page, MinOut;   // usage page 0 = any vendor-defined page
        public double PerMinute;            // fastest believable change of the level; 0 = show as is
        public Func<Channel, bool?> Alive;  // cheap link check, optional; null = no answer
        public Func<Channel, Device> Query; // null = no answer
    }

    // shortcut: headset protocols follow HeadsetControl's documentation and were never run on real hardware.
    static readonly Proto[] Protos =
    {
        // VXE / ATK / VGN mice
        // The same mouse is vendor 373B behind its dongle and vendor 3554 on its cable.
        MouseProto(0x373B), MouseProto(0x3554),        // Ajazz AK820, on its cable and on its receiver. It has no readable level (the cable answers FF, the receiver 0), and its
        // vendor interfaces also accept screen data, so nothing is ever sent to it: only its presence is shown.
        new Proto { Kind = Kind.Keyboard, Name = "Клавиатура AK820", Vid = 0x0C45, Pids = new[] { 0x8009, 0x800A }, Page = 0xFF13, MinOut = 65,
            Query = c => new Device { Percent = -1, Charging = true } },
        new Proto { Kind = Kind.Keyboard, Name = "Клавиатура AK820", Vid = 0x0C45, Pids = new[] { 0xFDFD }, Page = 0xFF60, MinOut = 33,
            Query = c => c.Out == 33 ? new Device { Percent = -1 } : null },        // HyperX Cloud II Wireless
        new Proto { Vid = 0x03F0, Pids = new[] { 0x0696, 0x018B }, Page = 0xFF90, MinOut = 5, PerMinute = 3, Query = c =>
        {
            var l = c.Ask(new byte[] { 6, 0xFF, 0xBB, 2 }, r => r[0] == 6 && r[2] == 0xBB && r[3] == 2);
            var s = c.Ask(new byte[] { 6, 0xFF, 0xBB, 3 }, r => r[0] == 6 && r[2] == 0xBB && r[3] == 3);
            return l == null || s == null ? null : Make(l[7], s[4] == 1); // half an answer would make the bolt blink
        } },
        // HyperX Cloud Alpha Wireless
        new Proto { Vid = 0x03F0, Pids = new[] { 0x098D }, MinOut = 31, PerMinute = 3, Query = c =>
        {
            Func<byte, byte[]> ask = sub => c.Ask(new byte[] { 0x21, 0xBB, sub }, r => r[0] == 0x21 && r[1] == 0xBB && r[2] == sub);
            var on = ask(3);
            if (on == null || on[3] == 1) return null;
            var l = ask(0x0B);
            var s = ask(0x0C);
            return l == null || s == null ? null : Make(l[3], s[3] == 1);
        } },
        // SteelSeries Arctis 7
        new Proto { Vid = 0x1038, Pids = new[] { 0x1260, 0x12AD, 0x1252, 0x1280 }, Page = 0xFF43, MinOut = 2, PerMinute = 3, Query = c =>
        {
            var r = c.Ask(new byte[] { 6, 0x18 }, x => x[0] == 6 && x[1] == 0x18);
            return r == null ? null : Make(r[2], false);
        } },
        // SteelSeries Arctis 7+
        new Proto { Vid = 0x1038, Pids = new[] { 0x220E, 0x2212, 0x2216, 0x2236 }, Page = 0xFFC0, MinOut = 2, Query = c =>
        {
            var r = c.Ask(new byte[] { 0, 0xB0 }, x => x[0] == 0xB0);
            return r == null || r[1] == 1 ? null : Make(r[2] * 25, r[3] == 1);
        } },
        // SteelSeries Arctis Nova 7, four-step level and percent variants
        new Proto { Vid = 0x1038, Pids = new[] { 0x2202, 0x2206, 0x220A, 0x223A, 0x227A, 0x22A4 }, Page = 0xFFC0, MinOut = 2, Query = c => Nova7(c, 25) },
        new Proto { Vid = 0x1038, Pids = new[] { 0x22A1, 0x227E, 0x2258, 0x229E, 0x22AD, 0x22A9, 0x22A5 }, Page = 0xFFC0, MinOut = 2, PerMinute = 3, Query = c => Nova7(c, 1) },
        // SteelSeries Arctis Nova 5
        new Proto { Vid = 0x1038, Pids = new[] { 0x2232, 0x2253 }, Page = 0xFFC0, MinOut = 2, PerMinute = 3, Query = c =>
        {
            var r = c.Ask(new byte[] { 0, 0xB0 }, x => x[0] == 0xB0);
            return r == null || r[1] == 2 ? null : Make(r[3], r[4] == 1);
        } },
        // Corsair Void, HS70 and relatives
        new Proto { Vid = 0x1B1C, Pids = new[] { 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 },
            Page = 0xFFC5, MinOut = 2, PerMinute = 3, Query = c =>
        {
            var r = c.Ask(new byte[] { 0xC9, 0x64 }, x => x[0] == 0x64);
            return r == null || r[4] == 0 ? null : Make(r[2] & 0x7F, r[4] == 4 || r[4] == 5);
        } },
        // Logitech G PRO / PRO X Wireless, G535
        new Proto { Vid = 0x046D, Pids = new[] { 0x0AA7, 0x0AAA, 0x0ABA }, Page = 0xFF43, MinOut = 20, PerMinute = 3, Query = c =>
            Logitech(c, 6, new[] { 4150, 3830, 3780, 3740, 3670, 3320 }) },
        new Proto { Vid = 0x046D, Pids = new[] { 0x0AC4 }, Page = 0xFF43, MinOut = 20, PerMinute = 3, Query = c =>
            Logitech(c, 5, new[] { 4175, 3817, 3766, 3730, 3664, 3310 }) },
    };

    // One gauge per name, shared by the dongle and the cable, so plugging in does not change the number.
    // shortcut: two mice of this family at once would share a gauge, key it by serial number if that happens.
    static readonly Dictionary<string, Gauge> gauges = new Dictionary<string, Gauge>();
    static int Steady(Device d, double perMinute)
    {
        Gauge g;
        if (!gauges.TryGetValue(d.Name, out g)) gauges[d.Name] = g = Gauge.Load(d.Name, DateTime.UtcNow);
        int v = g.Update(d.Percent, d.Charging, DateTime.UtcNow, perMinute);
        g.Save(d.Name);
        return v;
    }

    static Proto MouseProto(int vid)
    {
        return new Proto { Kind = Kind.Mouse, Name = "Беспроводная мышь", Vid = vid, Page = 0xFF02, MinOut = 17, PerMinute = 1,
            // The dongle answers this itself, without waking the mouse.
            Alive = c =>
            {
                var r = c.Ask(MouseRequest(3), x => x[0] == 8 && x[1] == 3);
                return r == null ? (bool?)null : IsOnline(r);
            },
            Query = c => ParseMouse(c.Ask(MouseRequest(4), r => r[0] == 8 && r[1] == 4)) };
    }

    // The mouse itself rounds to 5 %. This is the table its own software (VGN VHUB) uses: one entry per 5 %.
    static readonly int[] MouseMillivolts = { 3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880, 3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110 };
    public static int MousePercent(int mv, int rounded, bool charging)
    {
        if (mv < 2500 || mv > 4500) return rounded; // no voltage in the reply
        int top = MouseMillivolts.Length - 1;
        if (mv >= MouseMillivolts[top]) return charging ? 99 : 100;
        int i = Array.FindIndex(MouseMillivolts, v => mv < v);
        if (i == 0) return 1;
        double part = (double)(mv - MouseMillivolts[i - 1]) / (MouseMillivolts[i] - MouseMillivolts[i - 1]);
        return Math.Max(1, (int)Math.Round((i - 1 + part) * 5));
    }

    static Device Nova7(Channel c, int step)
    {
        var r = c.Ask(new byte[] { 0, 0xB0 }, x => x[0] == 0xB0);
        return r == null || r[3] == 0 ? null : Make(r[2] * step, r[3] == 1 || r[3] == 2);
    }

    static Device Logitech(Channel c, byte feature, int[] millivolts)
    {
        var r = c.Ask(new byte[] { 0x11, 0xFF, feature, 0x0D }, x => x[0] == 0x11 && (x[2] == feature || x[2] == 0xFF));
        return r == null || r[2] == 0xFF ? null : Make(FromVoltage(r[4] << 8 | r[5], millivolts), r[6] == 3);
    }

    public static int FromVoltage(int mv, int[] table)
    {
        int[] pct = { 100, 50, 30, 20, 5, 0 };
        if (mv >= table[0]) return 100;
        for (int i = 1; i < table.Length; i++)
            if (mv >= table[i])
                return pct[i] + (mv - table[i]) * (pct[i - 1] - pct[i]) / (table[i - 1] - table[i]);
        return 0;
    }

    static byte[] MouseRequest(byte command)
    {
        var q = new byte[17];
        q[0] = 8; q[1] = command; q[16] = (byte)(0x55 - 8 - command);
        return q;
    }
    public static bool IsOnline(byte[] r) { return r != null && r[6] == 1; }

    // Anything out of range means the reply was not what we think it is.
    static Device Make(int percent, bool charging)
    {
        return percent < 0 || percent > 100 ? null : new Device { Percent = percent, Charging = charging };
    }

    public static Device ParseMouse(byte[] r)
    {
        return r == null || r[0] != 8 || r[1] != 4 || r[6] > 100 || r[6] == 0 && r[7] != 1 ? null
            : Make(r[6] == 100 ? 100 : MousePercent(r[8] << 8 | r[9], r[6], r[7] == 1), r[7] == 1);
    }

    public class Seen { public Device Device; public DateTime At, Asked; } // last alive, last answered
    static readonly Dictionary<string, Seen> last = new Dictionary<string, Seen>();

    // How long the last level stays on screen without contact.
    public static bool StillFresh(DateTime seen, DateTime now) { return now >= seen && now - seen < TimeSpan.FromSeconds(20); }

    // force: ask every device now instead of reusing an answer from the last seconds.
    // What to show for one device after one poll. A link that is coming up or going down flaps for several seconds,
    // so a device stays on until it has been silent for a while, and then goes grey instead of disappearing.
    // answer: the level, null = none; alive: what the dongle says, null = it said nothing; seen: the last contact.
    public static Device Decide(Device answer, bool? alive, Seen seen, DateTime now)
    {
        if (answer != null) return answer;
        if (seen != null)
        {
            if (alive == true) seen.At = now; // the link is up, only the level did not come through
            if (StillFresh(seen.At, now)) return seen.Device;
        }
        return seen != null || alive != null ? new Device { Percent = -1, Offline = true } : null;
    }

    public static List<Device> ReadAll(bool force)
    {
        var result = new List<Device>();
        foreach (var path in Paths())
        {
            var lower = path.ToLowerInvariant();
            foreach (var p in Protos.Where(p => lower.Contains("vid_" + p.Vid.ToString("x4")) &&
                (p.Pids == null || p.Pids.Any(x => lower.Contains("pid_" + x.ToString("x4"))))).ToList())
            {
                var now = DateTime.UtcNow;
                Seen seen;
                last.TryGetValue(path, out seen);
                Device answer = null;
                bool? alive = null;
                string name = p.Name ?? (seen == null ? null : seen.Device.Name);
                try
                {
                    using (var c = Channel.Open(path))
                    {
                        if (c != null)
                        {
                            if (c.Out < p.MinOut || (p.Page == 0 ? c.Page < 0xFF00 : c.Page != p.Page)) continue;
                            name = p.Name ?? c.Name;
                            if (p.Alive != null) alive = p.Alive(c);
                            bool recent = seen != null && !force && now - seen.Asked < TimeSpan.FromSeconds(15);
                            if (alive != false) answer = recent && (p.Alive == null || alive == true) ? seen.Device : p.Query(c);
                            if (answer != null && !recent)
                            {
                                answer.Kind = p.Kind;
                                answer.Name = name;
                                if (p.PerMinute > 0 && answer.Percent >= 0) answer.Percent = Steady(answer, p.PerMinute);
                                last[path] = seen = new Seen { Device = answer, Asked = now };
                            }
                            if (answer != null) seen.At = now;
                        }
                    }
                }
                catch (IOException) { } // common while Windows is rearranging USB devices
                catch (UnauthorizedAccessException) { }
                var d = Decide(answer, alive, seen, now);
                if (d == null || name == null) continue;
                d.Kind = p.Kind;
                d.Name = name;
                if (answer != null) contact[name] = new Seen { Device = d, At = now };
                result.Add(d);
            }
        }
        return Merge(result, contact, DateTime.UtcNow);
    }

    // Last contact by name: the same device may be reached through its dongle or through its cable.
    static readonly Dictionary<string, Seen> contact = new Dictionary<string, Seen>();

    public static List<Device> Merge(List<Device> found, Dictionary<string, Seen> contact, DateTime now)
    {
        var result = new List<Device>();
        foreach (var g in found.GroupBy(d => d.Kind + d.Name))
        {
            // Charging means the cable, and then the dongle has nothing to add.
            var d = g.OrderBy(x => x.Offline).ThenByDescending(x => x.Charging).First();
            Seen s;
            // Just unplugged and not yet back on the radio: keep the level for a moment.
            if (d.Offline && contact.TryGetValue(d.Name, out s) && StillFresh(s.At, now))
                d = new Device { Kind = d.Kind, Name = d.Name, Percent = s.Device.Percent };
            result.Add(d);
        }
        return result;
    }
    static string[] Paths()
    {
        Guid g; int n;
        HidD_GetHidGuid(out g);
        if (CM_Get_Device_Interface_List_SizeW(out n, ref g, null, 0) != 0) return new string[0];
        var buf = new char[n];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, n, 0) != 0) return new string[0];
        return new string(buf).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // ---- Notifications the devices send on their own ----

    static readonly HashSet<string> listening = new HashSet<string>();

    public static void Listen(Action changed)
    {
        foreach (var path in Paths())
        {
            var lower = path.ToLowerInvariant();
            bool mouse = lower.Contains("vid_373b") || lower.Contains("vid_3554"), keyboard = lower.Contains("vid_0c45&pid_fdfd");
            if (!mouse && !keyboard) continue;
            lock (listening) if (listening.Contains(path)) continue;
            var c = Channel.Open(path);
            if (c == null) continue;
            if (mouse ? c.Page != 0xFF02 || c.In != 17 : c.Page != 0xFFFF || c.In != 4) { c.Dispose(); continue; }
            lock (listening) listening.Add(path);
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var r = c.Next();
                        if (r == null) break;
                        // Replies to our own queries arrive here too and must not trigger another query.
                        if (mouse ? r[1] != 3 && r[1] != 4 : r[0] == 5 && r[1] == 0xA6) changed();
                    }
                }
                catch (IOException) { } // unplugged
                catch (OperationCanceledException) { }
                finally
                {
                    c.Dispose();
                    lock (listening) listening.Remove(path);
                    changed();
                }
            }) { IsBackground = true }.Start();
        }
    }

    class Channel : IDisposable
    {
        SafeFileHandle h;
        FileStream s;
        bool stuck; // timed out: this handle has a cancelled read
        public int Page, In, Out, FeatureLen;
        public string Name;

        public static Channel Open(string path)
        {
            var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            IntPtr p;
            if (h.IsInvalid || !HidD_GetPreparsedData(h, out p)) { h.Dispose(); return null; }
            var c = new ushort[32];
            HidP_GetCaps(p, c);
            HidD_FreePreparsedData(p);
            var name = new StringBuilder(128);
            HidD_GetProductString(h, name, 256);
            return new Channel { h = h, Page = c[1], In = c[2], Out = c[3], FeatureLen = c[4], Name = name.Length > 0 ? name.ToString() : "USB" };
        }

        public byte[] Ask(byte[] request, Func<byte[], bool> accept)
        {
            if (stuck || In == 0) return null;
            if (s == null) s = new FileStream(h, FileAccess.ReadWrite, 1, true); // unbuffered: HID has no flush
            var q = new byte[Out];
            request.CopyTo(q, 0);
            if (!Wait(s.WriteAsync(q, 0, Out))) return null;
            // Replies to other programs and unrelated events come first at times.
            var until = DateTime.UtcNow.AddMilliseconds(600);
            while (DateTime.UtcNow < until)
            {
                var r = new byte[In];
                if (!Wait(s.ReadAsync(r, 0, In))) return null;
                // Unnumbered reports carry a leading zero that the protocols do not count.
                if (r[0] == 0) r = r.Skip(1).ToArray();
                Array.Resize(ref r, 64);
                if (accept(r)) return r;
            }
            return null;
        }

        // Blocks until the device says something.
        public byte[] Next()
        {
            if (s == null) s = new FileStream(h, FileAccess.ReadWrite, 1, true);
            var r = new byte[In];
            return s.Read(r, 0, In) > 0 ? r : null;
        }

        bool Wait(Task t)
        {
            try
            {
                if (t.Wait(300)) return true;
                CancelIoEx(h, IntPtr.Zero);
                t.Wait(300);
            }
            catch (AggregateException) { }
            stuck = true;
            return false;
        }

        public void Dispose()
        {
            if (s != null) s.Dispose();
            h.Dispose();
        }
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder s, int n);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, [Out] ushort[] c);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string p, uint a, int s, IntPtr x, int c, uint f, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr o);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int n, ref Guid g, string d, int f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid g, string d, [Out] char[] b, int n, int f);
}
