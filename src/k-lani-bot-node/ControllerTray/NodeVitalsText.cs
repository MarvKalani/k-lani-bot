using System.Globalization;
using KLaniBot.Fleet;
using KLaniBot.Localization;

namespace KLaniBot.ControllerTray;

// Use case: the controller tray shows under each node what the person checks first on a remote computer: which system it
// runs, how long since it started, how busy it is, how much memory and disk is free and when it last answered. Values the
// node could not measure are left out. Numbers are rounded so that the menu, which is redrawn only when its text changes,
// does not redraw on every poll: contact under a minute is "just now", larger sizes lose their decimals. A load line whose
// values crossed one of the master's limits is marked.
internal static class NodeVitalsText
{
    private const double Gigabyte = 1024.0 * 1024 * 1024;

    // The indented lines under a node; an offline node shows only when it was last heard from, never stale load.
    public static IReadOnlyList<string> Lines(ControllerNodeRow node)
    {
        var lines = new List<string>();
        if (node.Connected && node.Vitals is { } vitals)
        {
            AddIfAny(lines, SystemLine(vitals));
            string load = LoadLine(vitals);
            AddIfAny(lines, node.Alerts != NodeAlerts.None && load.Length > 0 ? "⚠ " + load : load);
        }
        lines.Add(ContactLine(node.SecondsSinceContact));
        return lines;
    }

    private static void AddIfAny(List<string> lines, string line)
    {
        if (line.Length > 0)
        {
            lines.Add(line);
        }
    }

    private static string SystemLine(NodeVitals vitals)
    {
        if (vitals.UptimeSeconds is not { } uptime)
        {
            return vitals.System;
        }
        return UiTexts.Format(UiText.ControllerNodeSystemUptime, vitals.System, Duration(uptime));
    }

    private static string LoadLine(NodeVitals vitals)
    {
        var parts = new List<string>();
        if (vitals.CpuLoadPercent is { } load)
        {
            parts.Add(UiTexts.Format(UiText.ControllerNodeCpu, Math.Round(load).ToString(CultureInfo.InvariantCulture)));
        }
        if (vitals is { MemoryAvailableBytes: { } available, MemoryTotalBytes: { } total })
        {
            parts.Add(UiTexts.Format(UiText.ControllerNodeMemory, Gigabytes(available), Gigabytes(total)));
        }
        if (vitals is { DiskFreeBytes: { } free, DiskTotalBytes: { } size })
        {
            parts.Add(UiTexts.Format(UiText.ControllerNodeDisk, vitals.DiskVolume, Gigabytes(free), Gigabytes(size)));
        }
        return string.Join(" · ", parts);
    }

    private static string ContactLine(long? seconds)
    {
        return seconds switch
        {
            null => UiTexts.Get(UiText.ControllerNodeNoContact),
            < 60 => UiTexts.Get(UiText.ControllerNodeContactNow),
            _ => UiTexts.Format(UiText.ControllerNodeContactAgo, Duration(seconds.Value))
        };
    }

    // Two units at most: "12 s", "5 min", "4 h 5 min", "3 d 4 h".
    private static string Duration(long seconds)
    {
        long minutes = seconds / 60;
        long hours = minutes / 60;
        long days = hours / 24;
        if (days > 0)
        {
            return UiTexts.Format(UiText.DurationDaysHours, days, hours % 24);
        }
        if (hours > 0)
        {
            return UiTexts.Format(UiText.DurationHoursMinutes, hours, minutes % 60);
        }
        return minutes > 0 ? UiTexts.Format(UiText.DurationMinutes, minutes) : UiTexts.Format(UiText.DurationSeconds, seconds);
    }

    // One decimal below 10 GB, where it still tells something apart, whole gigabytes above. The German comma is written
    // directly, because a node without ICU data has no German culture to ask.
    public static string Gigabytes(long bytes)
    {
        double value = bytes / Gigabyte;
        string text = value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        return UiTexts.Language == UiLanguage.German ? text.Replace('.', ',') : text;
    }
}
