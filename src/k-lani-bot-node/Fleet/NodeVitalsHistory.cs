using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Startup;

namespace KLaniBot.Fleet;

// Use case: the operator looks back at how a node was doing, to see when its load shot up or its disk ran full. The master
// writes one sample per history interval to a private JSON-lines file per node in its state directory (vitals-history/),
// drops samples older than the kept period and answers the vitals-history command with the peaks of a period. Only the
// operator reads it; AI clients get alerts, never history.
internal sealed class NodeVitalsHistory(string stateDirectory)
{
    public const string DirectoryName = "vitals-history";
    // Rewriting a file to drop old samples costs more than an append, so it happens at most this often per node.
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:sszzz";
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pruned = new(StringComparer.Ordinal);

    public string FileOf(string node)
    {
        return Path.Combine(stateDirectory, DirectoryName, node + ".jsonl");
    }

    // Polls arrive a little early or late, so a sample is due shortly before a full interval has passed.
    public void Record(string node, NodeVitals vitals, NodeAlerts alerts, NodeVitalsSettings settings, DateTimeOffset now)
    {
        if (settings.HistoryDays == 0 || !NodeName.IsValid(node))
        {
            return;
        }
        lock (_gate)
        {
            if (_recorded.TryGetValue(node, out DateTimeOffset last) && now - last < settings.HistoryInterval - TimeSpan.FromSeconds(2))
            {
                return;
            }
            _recorded[node] = now;
            Append(node, vitals, alerts, now);
            if (!_pruned.TryGetValue(node, out DateTimeOffset pruned) || now - pruned >= PruneInterval)
            {
                _pruned[node] = now;
                Prune(node, now - TimeSpan.FromDays(settings.HistoryDays));
            }
        }
    }

    // The highest load, the least free memory and disk of the period, each with its time, and how often each limit was crossed.
    public JsonObject Summary(string node, TimeSpan period, DateTimeOffset now)
    {
        List<Sample> samples;
        lock (_gate)
        {
            samples = NodeName.IsValid(node) ? Read(node).Where(sample => sample.At >= now - period).ToList() : [];
        }
        var summary = new JsonObject
        {
            ["success"] = true,
            ["node"] = node,
            ["hours"] = Math.Round(period.TotalHours, 2),
            ["samples"] = samples.Count,
            ["from"] = samples.Count > 0 ? Time(samples[0].At) : null,
            ["to"] = samples.Count > 0 ? Time(samples[^1].At) : null,
            ["file"] = FileOf(node)
        };
        summary["cpu_load_percent"] = Peak(samples, sample => sample.Vitals.CpuLoadPercent, highest: true);
        summary["memory_available_bytes"] = Peak(samples, sample => sample.Vitals.MemoryAvailableBytes, highest: false);
        summary["disk_free_bytes"] = Peak(samples, sample => sample.Vitals.DiskFreeBytes, highest: false);
        summary["alerts"] = AlertCounts(samples);
        return summary;
    }

    // One sample as a line of the history file.
    private sealed record Sample(DateTimeOffset At, NodeVitals Vitals, NodeAlerts Alerts);

    private void Append(string node, NodeVitals vitals, NodeAlerts alerts, DateTimeOffset now)
    {
        PrivateFile.CreateDirectory(Path.Combine(stateDirectory, DirectoryName));
        var line = new JsonObject { ["at"] = Time(now), ["vitals"] = vitals.ToJson(), ["alerts"] = NodeVitalsSettings.ToJson(alerts) };
        using FileStream stream = PrivateFile.OpenWrite(FileOf(node), FileMode.Append);
        stream.Write(Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"));
    }

    private void Prune(string node, DateTimeOffset oldest)
    {
        List<Sample> kept = Read(node).Where(sample => sample.At >= oldest).ToList();
        var content = new StringBuilder();
        foreach (Sample sample in kept)
        {
            var line = new JsonObject
            {
                ["at"] = Time(sample.At),
                ["vitals"] = sample.Vitals.ToJson(),
                ["alerts"] = NodeVitalsSettings.ToJson(sample.Alerts)
            };
            content.Append(line.ToJsonString()).Append('\n');
        }
        PrivateFile.WriteAllBytesAtomically(FileOf(node), Encoding.UTF8.GetBytes(content.ToString()));
    }

    // A line that cannot be read (a write cut by a crash) is skipped, not fatal.
    private List<Sample> Read(string node)
    {
        string file = FileOf(node);
        if (!File.Exists(file))
        {
            return [];
        }
        var samples = new List<Sample>();
        foreach (string line in File.ReadLines(file))
        {
            if (TryParse(line) is { } sample)
            {
                samples.Add(sample);
            }
        }
        return samples;
    }

    private static Sample? TryParse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject entry
                || !DateTimeOffset.TryParseExact(entry["at"]?.ToString(), TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTimeOffset at)
                || NodeVitals.TryRead(entry["vitals"]) is not { } vitals)
            {
                return null;
            }
            return new Sample(at, vitals, NodeVitalsSettings.FromJson(entry["alerts"]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? Peak<T>(List<Sample> samples, Func<Sample, T?> value, bool highest) where T : struct, IComparable<T>
    {
        Sample? best = null;
        T bestValue = default;
        foreach (Sample sample in samples)
        {
            if (value(sample) is not { } current)
            {
                continue;
            }
            int order = current.CompareTo(bestValue);
            if (best == null || (highest ? order > 0 : order < 0))
            {
                best = sample;
                bestValue = current;
            }
        }
        string key = highest ? "max" : "min";
        return best == null ? null : new JsonObject { [key] = JsonValue.Create(bestValue), ["at"] = Time(best.At) };
    }

    private static JsonObject AlertCounts(List<Sample> samples)
    {
        var counts = new JsonObject();
        foreach (NodeAlerts alert in new[] { NodeAlerts.MemoryLow, NodeAlerts.DiskLow, NodeAlerts.CpuHigh })
        {
            counts[NodeVitalsSettings.WireName(alert)] = samples.Count(sample => sample.Alerts.HasFlag(alert));
        }
        return counts;
    }

    private static string Time(DateTimeOffset at)
    {
        return at.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
    }
}
