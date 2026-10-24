using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Fleet;

// Which limits a node's last vitals crossed. A model learns about vitals only through these, so it spends no tokens on a
// healthy node; the operator sees every value in the controller tray.
[Flags]
public enum NodeAlerts
{
    None = 0,
    MemoryLow = 1,
    DiskLow = 2,
    CpuHigh = 4
}

// Use case: the operator decides how often the master asks the nodes, how often and how long it keeps their vitals, and
// from when a node's state matters to an AI client: too little free memory, a nearly full system volume (below a share of
// its size or below an absolute size, so small and large disks both warn in time) or a processor that stayed busy since the
// previous poll. The master reads vitals.json in its state directory whenever it reports or polls, so a changed file applies
// without a restart; names the file leaves out keep their default.
public sealed record NodeVitalsSettings
{
    public const string FileName = "vitals.json";
    private const double Gigabyte = 1024.0 * 1024 * 1024;

    public double MemoryFreePercent { get; init; } = 10;
    public double DiskFreePercent { get; init; } = 5;
    public double DiskFreeGigabytes { get; init; } = 5;
    public double CpuLoadPercent { get; init; } = 90;
    // How often the master asks each node; every request costs the node one measurement.
    public int PollSeconds { get; init; } = 30;
    // How often a node's vitals are written to its history; never more often than the master asks.
    public int HistoryIntervalSeconds { get; init; } = 600;
    // How long the history is kept; 0 keeps none.
    public int HistoryDays { get; init; } = 7;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollSeconds);
    public TimeSpan HistoryInterval => TimeSpan.FromSeconds(Math.Max(PollSeconds, HistoryIntervalSeconds));

    public NodeAlerts Alerts(NodeVitals vitals)
    {
        NodeAlerts alerts = NodeAlerts.None;
        if (vitals is { MemoryAvailableBytes: { } available, MemoryTotalBytes: > 0 and var total }
            && 100.0 * available / total < MemoryFreePercent)
        {
            alerts |= NodeAlerts.MemoryLow;
        }
        if (vitals is { DiskFreeBytes: { } free, DiskTotalBytes: > 0 and var size }
            && (100.0 * free / size < DiskFreePercent || free / Gigabyte < DiskFreeGigabytes))
        {
            alerts |= NodeAlerts.DiskLow;
        }
        if (vitals.CpuLoadPercent is { } load && load > CpuLoadPercent)
        {
            alerts |= NodeAlerts.CpuHigh;
        }
        return alerts;
    }

    // One short English line per alert with the value that crossed the limit, for a model.
    public static IReadOnlyList<string> Describe(NodeAlerts alerts, NodeVitals vitals)
    {
        var lines = new List<string>();
        if (alerts.HasFlag(NodeAlerts.MemoryLow))
        {
            lines.Add("memory_low: " + Size(vitals.MemoryAvailableBytes) + " of " + Size(vitals.MemoryTotalBytes) + " GB free");
        }
        if (alerts.HasFlag(NodeAlerts.DiskLow))
        {
            string free = Size(vitals.DiskFreeBytes) + " of " + Size(vitals.DiskTotalBytes);
            lines.Add("disk_low: " + vitals.DiskVolume + " " + free + " GB free");
        }
        if (alerts.HasFlag(NodeAlerts.CpuHigh))
        {
            lines.Add("cpu_high: " + Math.Round(vitals.CpuLoadPercent ?? 0).ToString(CultureInfo.InvariantCulture) + " % load");
        }
        return lines;
    }

    // Throws ArgumentOutOfRangeException for None or a combination; only single alerts have a wire name.
    public static string WireName(NodeAlerts alert)
    {
        return alert switch
        {
            NodeAlerts.MemoryLow => "memory_low",
            NodeAlerts.DiskLow => "disk_low",
            NodeAlerts.CpuHigh => "cpu_high",
            _ => throw new ArgumentOutOfRangeException(nameof(alert))
        };
    }

    public static JsonArray ToJson(NodeAlerts alerts)
    {
        var names = new JsonArray();
        NodeAlerts[] each = [NodeAlerts.MemoryLow, NodeAlerts.DiskLow, NodeAlerts.CpuHigh];
        foreach (NodeAlerts alert in each.Where(alert => alerts.HasFlag(alert)))
        {
            names.Add(WireName(alert));
        }
        return names;
    }

    public static NodeAlerts FromJson(JsonNode? names)
    {
        NodeAlerts alerts = NodeAlerts.None;
        foreach (JsonNode? name in names as JsonArray ?? [])
        {
            alerts |= name?.ToString() switch
            {
                "memory_low" => NodeAlerts.MemoryLow,
                "disk_low" => NodeAlerts.DiskLow,
                "cpu_high" => NodeAlerts.CpuHigh,
                _ => NodeAlerts.None
            };
        }
        return alerts;
    }

    // The defaults without a file, and the defaults with a console note for a file that cannot be used, so a typo never
    // silences every alert or stops the history.
    public static NodeVitalsSettings Read(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, FileName);
        if (!File.Exists(path))
        {
            return new NodeVitalsSettings();
        }
        string? content = null;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        NodeVitalsSettings? limits = content == null ? null : TryParse(content);
        if (limits == null)
        {
            Console.Error.WriteLine(FileName + " is unreadable or invalid; the default vitals settings apply.");
        }
        return limits ?? new NodeVitalsSettings();
    }

    // Null for anything but an object of known names with numbers in range.
    public static NodeVitalsSettings? TryParse(string content)
    {
        JsonObject? document;
        try
        {
            document = JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        string[] known = ["memory_free_percent", "disk_free_percent", "disk_free_gb", "cpu_load_percent", "poll_seconds",
            "history_interval_seconds", "history_days"];
        if (document == null || document.Any(entry => !known.Contains(entry.Key)))
        {
            return null;
        }
        var defaults = new NodeVitalsSettings();
        double?[] values =
        [
            Value(document, "memory_free_percent", 0, 100, defaults.MemoryFreePercent),
            Value(document, "disk_free_percent", 0, 100, defaults.DiskFreePercent),
            Value(document, "disk_free_gb", 0, 1 << 20, defaults.DiskFreeGigabytes),
            Value(document, "cpu_load_percent", 0, 100, defaults.CpuLoadPercent),
            Value(document, "poll_seconds", 10, 3600, defaults.PollSeconds),
            Value(document, "history_interval_seconds", 10, 86400, defaults.HistoryIntervalSeconds),
            Value(document, "history_days", 0, 366, defaults.HistoryDays)
        ];
        return values.Any(value => value == null) ? null : FromValues(values);
    }

    private static NodeVitalsSettings FromValues(double?[] values)
    {
        return new NodeVitalsSettings
        {
            MemoryFreePercent = values[0]!.Value,
            DiskFreePercent = values[1]!.Value,
            DiskFreeGigabytes = values[2]!.Value,
            CpuLoadPercent = values[3]!.Value,
            PollSeconds = (int)values[4]!.Value,
            HistoryIntervalSeconds = (int)values[5]!.Value,
            HistoryDays = (int)values[6]!.Value
        };
    }

    // The default when the name is missing, null when the value is no number in range.
    private static double? Value(JsonObject document, string name, double minimum, double maximum, double fallback)
    {
        if (!document.ContainsKey(name))
        {
            return fallback;
        }
        return document[name] is JsonValue value && value.TryGetValue(out double number) && number >= minimum && number <= maximum
            ? number
            : null;
    }

    private static string Size(long? bytes)
    {
        double value = (bytes ?? 0) / Gigabyte;
        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
    }
}
