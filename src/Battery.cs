using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

enum Kind { Headset, Mouse, Keyboard, Other }

class Device
{
    public string Name;
    public Kind Kind;
    public int Percent; // -1 = not known right now
    public bool Charging;
    public bool Offline; // paired, but asleep or switched off

    public string Glyph
    {
        get { return Kind == Kind.Headset ? "" : Kind == Kind.Mouse ? "" : Kind == Kind.Keyboard ? "" : ""; }
    }
    public string Status
    {
        get
        {
            return Offline ? Kind == Kind.Headset ? "Не на связи или выключены" : "Спит или выключена" : Percent < 0 ? (Charging ? "На кабеле, процент не передаёт" : "2.4G, процент не передаёт")
                : Charging ? (Percent == 100 ? "Заряжено" : "Заряжается")
                : Percent <= 20 ? "Пора зарядить" : "От батареи";
        }
    }
}
static class Battery
{
    // Worker thread only: HID reads can block for up to a second.
    static List<Device> windows = new List<Device>();
    static DateTime windowsAt;
    static readonly Dictionary<string, DateTime> contact = new Dictionary<string, DateTime>();

    public static List<Device> ReadAll(bool force)
    {
        if (force || DateTime.UtcNow - windowsAt > TimeSpan.FromSeconds(6))
        {
            var fresh = FromWindows();
            // A headset drops out for a moment while it switches between music and call modes.
            foreach (var d in fresh) contact[d.Kind + d.Name] = DateTime.UtcNow;
            fresh.AddRange(windows.Where(w => !fresh.Any(d => d.Name == w.Name && d.Kind == w.Kind) && Hid.StillFresh(contact[w.Kind + w.Name], DateTime.UtcNow)));
            windows = fresh;
            windowsAt = DateTime.UtcNow;
        }
        var list = new List<Device>(windows);
        list.AddRange(Hid.ReadAll(force));
        return list.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    // ---- Bluetooth and everything else Windows itself knows a battery level for ----

    [StructLayout(LayoutKind.Sequential)]
    struct PropKey
    {
        public Guid Fmt; public uint Pid;
        public PropKey(string g, uint p) { Fmt = new Guid(g); Pid = p; }
    }
    static PropKey BatteryKey = new PropKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static PropKey ConnectedKey = new PropKey("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static PropKey ContainerKey = new PropKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
    static PropKey ClassKey = new PropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);
    static PropKey DescKey = new PropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
    static PropKey FriendlyKey = new PropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);

    class Node { public uint Id; public string Path, Class; }

    static List<Device> FromWindows()
    {
        var result = new List<Device>();
        int n;
        const int present = 0x100;
        if (CM_Get_Device_ID_List_SizeW(out n, null, present) != 0) return result;
        var buf = new char[n];
        if (CM_Get_Device_ID_ListW(null, buf, n, present) != 0) return result;

        var groups = new Dictionary<string, List<Node>>();
        foreach (var path in Split(buf))
        {
            uint id;
            if (CM_Locate_DevNodeW(out id, path, 0) != 0) continue;
            var c = Get(id, ContainerKey);
            var key = c == null ? path : BitConverter.ToString(c);
            if (!groups.ContainsKey(key)) groups[key] = new List<Node>();
            groups[key].Add(new Node { Id = id, Path = path, Class = Str(id, ClassKey) });
        }

        foreach (var g in groups.Values)
        {
            foreach (var node in g)
            {
                var level = Get(node.Id, BatteryKey);
                if (level == null || level.Length != 1 || level[0] > 100) continue;
                var main = g.FirstOrDefault(x => x.Path.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase)
                    || x.Path.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)) ?? node;
                // A paired but switched-off device keeps its last level.
                var conn = Get(main.Id, ConnectedKey);
                if (conn != null && conn[0] == 0) continue;
                var kind = g.Any(x => x.Class == "MEDIA" || x.Class == "AudioEndpoint") ? Kind.Headset
                    : g.Any(x => x.Class == "Mouse") ? Kind.Mouse
                    : g.Any(x => x.Class == "Keyboard") ? Kind.Keyboard : Kind.Other;
                // shortcut: Windows exposes no charging flag for Bluetooth, only the percentage.
                result.Add(new Device { Name = Str(main.Id, FriendlyKey) ?? Str(main.Id, DescKey) ?? "Bluetooth", Kind = kind, Percent = level[0] });
                break;
            }
        }
        return result;
    }

    static string[] Split(char[] b)
    {
        return new string(b).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }
    static byte[] Get(uint node, PropKey key)
    {
        uint type; int size = 0;
        CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
        if (size == 0) return null;
        var b = new byte[size];
        return CM_Get_DevNode_PropertyW(node, ref key, out type, b, ref size, 0) == 0 ? b : null;
    }
    static string Str(uint node, PropKey key)
    {
        var b = Get(node, key);
        return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int n, string f, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string f, [Out] char[] b, int n, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint node, string id, int f);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint node, ref PropKey k, out uint type, [Out] byte[] b, ref int size, int f);
}
