using System.Text.Json.Nodes;

namespace KLaniBot.Fleet;

// One node's operating system and vital signs as the node measured them when the master last asked. A value the node could
// not measure is null, never an estimate. The disk is the volume the system runs from, named so the reader knows which one.
public sealed record NodeVitals(
    string System,
    long? MemoryAvailableBytes,
    long? MemoryTotalBytes,
    string DiskVolume,
    long? DiskFreeBytes,
    long? DiskTotalBytes,
    double? CpuLoadPercent,
    long? UptimeSeconds)
{
    private const int MaximumTextLength = 80;
    // Far above any real machine; a larger number is a broken or hostile node, not a measurement.
    private const long MaximumBytes = 1L << 60;
    private const long MaximumUptimeSeconds = 100L * 365 * 24 * 3600;

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["system"] = System,
            ["memory_available_bytes"] = MemoryAvailableBytes,
            ["memory_total_bytes"] = MemoryTotalBytes,
            ["disk_volume"] = DiskVolume,
            ["disk_free_bytes"] = DiskFreeBytes,
            ["disk_total_bytes"] = DiskTotalBytes,
            ["cpu_load_percent"] = CpuLoadPercent,
            ["uptime_seconds"] = UptimeSeconds
        };
    }

    // The node is not trusted with what the tray draws and a model reads: text is shortened and loses control characters,
    // and a number outside its range becomes unknown. Returns null when the value is no vitals object.
    public static NodeVitals? TryRead(JsonNode? value)
    {
        if (value is not JsonObject vitals)
        {
            return null;
        }

        return new NodeVitals(
            Text(vitals["system"]),
            Number(vitals["memory_available_bytes"], MaximumBytes),
            Number(vitals["memory_total_bytes"], MaximumBytes),
            Text(vitals["disk_volume"]),
            Number(vitals["disk_free_bytes"], MaximumBytes),
            Number(vitals["disk_total_bytes"], MaximumBytes),
            Percent(vitals["cpu_load_percent"]),
            Number(vitals["uptime_seconds"], MaximumUptimeSeconds));
    }

    private static string Text(JsonNode? value)
    {
        string text = value is JsonValue scalar && scalar.TryGetValue(out string? read) ? read ?? "" : "";
        string printable = new(text.Where(character => !char.IsControl(character)).ToArray());
        return printable.Length > MaximumTextLength ? printable[..MaximumTextLength] : printable;
    }

    private static long? Number(JsonNode? value, long maximum)
    {
        if (value is JsonValue scalar && scalar.TryGetValue(out long number) && number >= 0 && number <= maximum)
        {
            return number;
        }
        return null;
    }

    private static double? Percent(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue(out double percent) && percent is >= 0 and <= 100)
        {
            return percent;
        }
        return null;
    }
}
