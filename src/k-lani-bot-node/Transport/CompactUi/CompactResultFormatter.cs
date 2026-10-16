using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// A verified function supplies bounded output checks separately from the actual complete output value.
public sealed record CompactOutputCheck(bool? Verified = null, bool? MatchesInput = null);

// A form scanner reports counts without copying its complete fields/content into a routine result.
public sealed record CompactScanCounts(int FieldCount, int ContentCount);

// The compact gateway preserves exact evidence before projecting optional summary, values or bounded debug replies.
public sealed class CompactResultFormatter(CompactResultStore store)
{
    public const int MaximumDebugBytes = 8 * 1024;
    private const int MaximumSummaryErrorCharacters = 2000;
    private const int MaximumDebugErrorCharacters = 512;
    private const int MaximumInlineChangeEntries = 20;
    private static readonly HashSet<string> Omitted = ["outputs", "ui", "fields", "contents", "images"];
    private static readonly HashSet<string> DebugFields = ["success", "verified", "function", "completed_steps", "steps", "failed_step",
        "failed_step_may_have_changed_ui", "phase", "input_sent", "retry", "unconfirmed_effects", "form", "state",
        "observed_states", "unknown", "action_status", "offscreen_meaning", "coverage"];

    public JsonObject Format(CompactResultAuthority authority, JsonObject result, CompactResultOptions options,
        IReadOnlyDictionary<string, CompactOutputCheck>? checks = null, CompactScanCounts? scan = null)
    {
        if (options.Mode == CompactResultMode.Legacy)
        {
            return result;
        }
        Dictionary<string, string> outputs = ReadOutputs(result);
        if (!store.TryPut(authority, outputs, CompactJsonText.Serialize(result), out var evidence))
        {
            return DeliveryFailure(result, evidence.Error!.Value);
        }
        string reference = evidence.Value!.Value.ToString();
        return options.Mode switch
        {
            CompactResultMode.Legacy => result,
            CompactResultMode.Summary => Summary(result, outputs, checks, scan, reference),
            CompactResultMode.Values => Values(result, outputs, options.Fields, checks, scan, reference),
            CompactResultMode.Debug => Debug(result, outputs.Keys, scan, reference),
            _ => throw new UnreachableException()
        };
    }

    // Throws InvalidOperationException if an internal producer supplies non-text outputs instead of its typed output contract.
    private static Dictionary<string, string> ReadOutputs(JsonObject result)
    {
        if (result["outputs"] is not JsonObject outputs)
        {
            return new(StringComparer.Ordinal);
        }
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> output in outputs)
        {
            if (output.Value is not JsonValue value || !value.TryGetValue(out string? content) || content == null)
            {
                throw new InvalidOperationException("Compact outputs require complete text values.");
            }
            text.Add(output.Key, content);
        }

        return text;
    }

    private static JsonObject BaseResult(JsonObject result)
    {
        var summary = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> field in result)
        {
            if (!Omitted.Contains(field.Key))
            {
                summary[field.Key] = field.Value?.DeepClone();
            }
        }

        return summary;
    }

    private static JsonObject DeliveryFailure(JsonObject result, CompactResultError error)
    {
        JsonObject failure = BaseResult(result);
        failure["success"] = false;
        failure["error"] = CompactResultErrors.ToWireMessage(error);
        failure["observation_required"] = IsTruthy(result["ui"]);
        failure["retry"] = "Do not replay completed input. Request a narrower result scope.";

        return failure;
    }

    private static JsonObject Summary(JsonObject result, Dictionary<string, string> outputs,
        IReadOnlyDictionary<string, CompactOutputCheck>? checks, CompactScanCounts? scan, string reference)
    {
        JsonObject summary = BaseResult(result);
        ClipError(summary, MaximumSummaryErrorCharacters);
        KeepSmallChangeList(result, summary);
        if (scan != null)
        {
            summary["field_count"] = scan.FieldCount;
            summary["content_count"] = scan.ContentCount;
        }
        if (result["outputs"] != null)
        {
            var descriptors = new JsonObject();
            foreach (KeyValuePair<string, string> output in outputs)
            {
                descriptors[output.Key] = OutputDescriptor(output.Key, output.Value, checks);
            }
            summary["outputs"] = descriptors;
        }
        summary["evidence_ref"] = reference;

        return summary;
    }

    // A small, known change list answers the question the model would otherwise re-read the window for; only a large one is replaced
    // by counts and an observation request.
    private static void KeepSmallChangeList(JsonObject result, JsonObject summary)
    {
        if (!IsTruthy(result["ui"]))
        {
            return;
        }
        JsonObject? changes = result["ui"] as JsonObject;
        int changed = CountEntries(changes, "changed");
        int removed = CountEntries(changes, "removed");
        int visibility = CountEntries(changes, "visibility");
        if (changed + removed + visibility <= MaximumInlineChangeEntries)
        {
            summary["ui"] = result["ui"]!.DeepClone();

            return;
        }
        summary["ui_counts"] = new JsonObject { ["changed"] = changed, ["removed"] = removed, ["visibility"] = visibility };
        summary["observation_required"] = true;
        summary["observation_reason"] = "changes_too_large";
    }

    private static int CountEntries(JsonObject? changes, string name)
    {
        return changes?[name] is JsonArray entries ? entries.Count : 0;
    }

    private static JsonObject OutputDescriptor(string name, string text, IReadOnlyDictionary<string, CompactOutputCheck>? checks)
    {
        var description = new JsonObject { ["name"] = name, ["utf8_length"] = Encoding.UTF8.GetByteCount(text) };
        if (checks?.TryGetValue(name, out CompactOutputCheck? check) == true)
        {
            if (check.Verified != null)
            {
                description["verified"] = check.Verified.Value;
            }
            if (check.MatchesInput != null)
            {
                description["matches_input"] = check.MatchesInput.Value;
            }
        }

        return description;
    }

    private static JsonObject Values(JsonObject result, Dictionary<string, string> outputs, IReadOnlyList<string>? fields,
        IReadOnlyDictionary<string, CompactOutputCheck>? checks, CompactScanCounts? scan, string reference)
    {
        JsonObject summary = Summary(result, outputs, checks, scan, reference);
        var values = new JsonObject();
        foreach (string name in fields ?? outputs.Keys.ToArray())
        {
            if (!outputs.TryGetValue(name, out string? text))
            {
                continue;
            }
            JsonObject value = OutputDescriptor(name, text, checks);
            bool truncated = CompactUnicode.Count(text) > CompactResultStore.DefaultPageSize;
            value["text"] = CompactUnicode.Slice(text, 0, CompactResultStore.DefaultPageSize);
            value["truncated"] = truncated;
            if (truncated)
            {
                value["evidence_ref"] = reference;
                value["section"] = "outputs/" + name;
            }
            values[name] = value;
        }
        summary["outputs"] = values;

        return summary;
    }

    private static JsonObject Debug(JsonObject result, IEnumerable<string> outputNames, CompactScanCounts? scan, string reference)
    {
        var inline = result.DeepClone().AsObject();
        inline["evidence_ref"] = reference;
        if (Encoding.UTF8.GetByteCount(CompactJsonText.Serialize(inline)) <= MaximumDebugBytes)
        {
            return inline;
        }
        return DebugIndex(result, outputNames, scan, reference);
    }

    private static JsonObject DebugIndex(JsonObject result, IEnumerable<string> outputNames, CompactScanCounts? scan, string reference)
    {
        var index = new JsonObject();
        foreach (string name in DebugFields)
        {
            if (result.ContainsKey(name))
            {
                index[name] = result[name]?.DeepClone();
            }
        }
        index["error"] = result["error"]?.DeepClone();
        if (index["error"] == null)
        {
            index.Remove("error");
        }
        ClipError(index, MaximumDebugErrorCharacters);
        if (scan != null)
        {
            index["field_count"] = scan.FieldCount;
        }
        AddChangeCounts(result, index);
        index["evidence_ref"] = reference;
        index["truncated"] = true;
        if (IsTruthy(result["ui"]))
        {
            index["observation_required"] = true;
        }
        var sections = new JsonArray("debug");
        foreach (string name in outputNames.Take(CompactResultOptionsCodec.MaximumFields))
        {
            sections.Add("outputs/" + name);
        }
        index["sections"] = sections;
        index["total_sections"] = outputNames.Count() + 1;

        return index;
    }

    private static void AddChangeCounts(JsonObject result, JsonObject index)
    {
        if (result["change"] is not JsonObject change)
        {
            return;
        }
        var counts = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> item in change)
        {
            if (item.Value is JsonArray values)
            {
                counts[item.Key] = values.Count;
            }
        }
        index["change_counts"] = counts;
    }

    private static void ClipError(JsonObject result, int maximumCharacters)
    {
        if (result["error"] is JsonValue error && error.TryGetValue(out string? text) && text != null)
        {
            result["error"] = CompactUnicode.Slice(text, 0, maximumCharacters);
        }
    }

    private static bool IsTruthy(JsonNode? value)
    {
        if (value == null)
        {
            return false;
        }
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue(out bool flag))
            {
                return flag;
            }
            if (scalar.TryGetValue(out string? text))
            {
                return !string.IsNullOrEmpty(text);
            }
            if (scalar.TryGetValue(out double number))
            {
                return number != 0;
            }
        }

        return true;
    }
}
