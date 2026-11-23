using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

// Turns a jumpy voltage reading into a steady percentage. The voltage rises as soon as the charger is
// connected and sags under load, while the real charge can only move slowly and in one direction.
class Gauge
{
    readonly List<int> recent = new List<int>();
    double level = -1;
    DateTime at, lastReading;
    bool starting; // first run ever: follow the readings until there are enough of them

    public int Update(int measured, bool charging, DateTime now, double perMinute = 1)
    {
        // After a pause the old readings mean nothing; a single new one may be a sag, so three are needed first.
        if (level >= 0 && (now - at).TotalMinutes > 10 && (now - lastReading).TotalMinutes > 10) recent.Clear();
        lastReading = now;
        recent.Add(measured);
        if (recent.Count > 5) recent.RemoveAt(0);
        int median = recent.OrderBy(v => v).ElementAt(recent.Count / 2);
        if (level >= 0 && recent.Count < 3) return (int)Math.Round(level);
        if (level < 0 || starting && recent.Count < 5) { level = median; starting = true; }
        else
        {
            double minutes = Math.Max(0, (now - at).TotalMinutes);
            double step = minutes * perMinute;
            if (charging) level = Math.Max(level, Math.Min(median, level + step));
            else if (median < level) level = Math.Max(median, level - step);
            // Without a charger a level only looks higher after a rest, unless it was charged while we were not watching.
            else level = Math.Min(median, level + (minutes > 10 ? step : minutes * .1));
        }
        if (recent.Count == 5) starting = false;
        at = now;
        return (int)Math.Round(level);
    }

    // The last level survives a restart of the program or of the computer.
    const string Key = @"Software\HeadsetBar";
    public void Save(string name)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
            k.SetValue(name, level.ToString(CultureInfo.InvariantCulture) + "|" + at.Ticks);
    }
    public static Gauge Load(string name, DateTime now)
    {
        var g = new Gauge();
        using (var k = Registry.CurrentUser.OpenSubKey(Key))
        {
            var parts = ((k == null ? null : k.GetValue(name) as string) ?? "").Split('|');
            double level;
            long ticks;
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out level) && level >= 0 && level <= 100
                && long.TryParse(parts[1], out ticks) && ticks > 0 && ticks <= now.Ticks)
            {
                g.level = level;
                g.at = new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        return g;
    }
}
