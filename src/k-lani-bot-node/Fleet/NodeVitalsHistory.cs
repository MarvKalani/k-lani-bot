using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Startup;

namespace KLaniBot.Fleet;

// One sample as a line of a node's history file: the worst values of the polls in its interval, stamped with its end.
internal sealed record VitalsSample(DateTimeOffset At, NodeVitals Vitals, NodeAlerts Alerts);

// Use case: the operator looks back at how a node was doing, to see when its load shot up or its disk ran full. The master
// writes one sample per history interval to a private JSON-lines file per node in its state directory (vitals-history/),
// drops samples older than the kept period and answers the vitals-history command with the peaks of a period. A sample
// holds the worst values of the polls in its interval (highest load, least free memory and disk, every alert raised), so a
// spike between two written samples is not lost. Only the operator reads it; AI clients get alerts, never history.
internal sealed class NodeVitalsHistory(string stateDirectory)
{
    public const string DirectoryName = "vitals-history";
    // Rewriting a file to drop old samples costs more than an append, so it happens at most this often per node.
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:sszzz";
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pruned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VitalsSample> _pending = new(StringComparer.Ordinal);
    private bool _failureReported;

    public string FileOf(string node)
    {
        return Path.Combine(stateDirectory, DirectoryName, node + ".jsonl");
    }

    // Polls arrive a little early or late, so a sample is due shortly before a full interval has passed. A history that
    // cannot be written (disk full, no permission) is reported once on the console and never stops the polls.
    public void Record(string node, NodeVitals vitals, NodeAlerts alerts, NodeVitalsSettings settings, DateTimeOffset now)
    {
        if (settings.HistoryDays == 0 || !NodeName.IsValid(node))
        {
            return;
        }
        lock (_gate)
        {
            VitalsSample merged = _pending.TryGetValue(node, out VitalsSample? pending)
                ? Worst(pending, vitals, alerts, now)
                : new VitalsSample(now, vitals, alerts);
            if (_recorded.TryGetValue(node, out DateTimeOffset last) && now - last < settings.HistoryInterval - TimeSpan.FromSeconds(2))
            {
                _pending[node] = merged;
                return;
            }
            _pending.Remove(node);
            _recorded[node] = now;
            Write(node, merged, now - TimeSpan.FromDays(settings.HistoryDays));
        }
    }

    private void Write(string node, VitalsSample sample, DateTimeOffset oldest)
    {
        try
        {
            Append(node, sample);
            if (!_pruned.TryGetValue(node, out DateTimeOffset pruned) || sample.At - pruned >= PruneInterval)
            {
                _pruned[node] = sample.At;
                Prune(node, oldest);
            }
            _failureReported = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_failureReported)
            {
                Console.Error.WriteLine("The vitals history cannot be written: " + exception.GetType().Name + ".");
                _failureReported = true;
            }
        }
    }

    private static VitalsSample Worst(VitalsSample pending, NodeVitals vitals, NodeAlerts alerts, DateTimeOffset now)
    {
        NodeVitals worst = vitals with
        {
            CpuLoadPercent = Highest(pending.Vitals.CpuLoadPercent, vitals.CpuLoadPercent),
            MemoryAvailableBytes = Lowest(pending.Vitals.MemoryAvailableBytes, vitals.MemoryAvailableBytes),
            DiskFreeBytes = Lowest(pending.Vitals.DiskFreeBytes, vitals.DiskFreeBytes)
        };
        return new VitalsSample(now, worst, pending.Alerts | alerts);
    }

    private static double? Highest(double? first, double? second)
    {
        return first is { } a && second is { } b ? Math.Max(a, b) : first ?? second;
    }

    private static long? Lowest(long? first, long? second)
    {
        return first is { } a && second is { } b ? Math.Min(a, b) : first ?? second;
    }

    // The samples of the period, oldest first, for the operator's report.
    public IReadOnlyList<VitalsSample> Samples(string node, TimeSpan period, DateTimeOffset now)
    {
        lock (_gate)
        {
            return NodeName.IsValid(node) ? Read(node).Where(sample => sample.At >= now - period).ToList() : [];
        }
    }

    // The highest load, the least free memory and disk of the period, each with its time, and how often each limit was crossed.
    public JsonObject Summary(string node, TimeSpan period, DateTimeOffset now)
    {
        IReadOnlyList<VitalsSample> samples = Samples(node, period, now);
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

    private void Append(string node, VitalsSample sample)
    {
        PrivateFile.CreateDirectory(Path.Combine(stateDirectory, DirectoryName));
        using FileStream stream = PrivateFile.OpenWrite(FileOf(node), FileMode.Append);
        stream.Write(Encoding.UTF8.GetBytes(Line(sample) + "\n"));
    }

    private void Prune(string node, DateTimeOffset oldest)
    {
        var content = new StringBuilder();
        foreach (VitalsSample sample in Read(node).Where(sample => sample.At >= oldest))
        {
            content.Append(Line(sample)).Append('\n');
        }
        PrivateFile.WriteAllBytesAtomically(FileOf(node), Encoding.UTF8.GetBytes(content.ToString()));
    }

    private static string Line(VitalsSample sample)
    {
        var line = new JsonObject
        {
            ["at"] = Time(sample.At),
            ["vitals"] = sample.Vitals.ToJson(),
            ["alerts"] = NodeVitalsSettings.ToJson(sample.Alerts)
        };
        return line.ToJsonString();
    }

    // A line that cannot be read (a write cut by a crash) is skipped, not fatal.
    private List<VitalsSample> Read(string node)
    {
        string file = FileOf(node);
        if (!File.Exists(file))
        {
            return [];
        }
        var samples = new List<VitalsSample>();
        foreach (string line in File.ReadLines(file))
        {
            if (TryParse(line) is { } sample)
            {
                samples.Add(sample);
            }
        }
        return samples;
    }

    private static VitalsSample? TryParse(string line)
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
            return new VitalsSample(at, vitals, NodeVitalsSettings.FromJson(entry["alerts"]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? Peak<T>(IReadOnlyList<VitalsSample> samples, Func<VitalsSample, T?> value, bool highest)
        where T : struct, IComparable<T>
    {
        VitalsSample? best = null;
        T bestValue = default;
        foreach (VitalsSample sample in samples)
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

    private static JsonObject AlertCounts(IReadOnlyList<VitalsSample> samples)
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
