using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Private checkpoint decoding preserves complete values while refusing foreign identity, malformed operations and unknown job formats.
public static class CompactWorkflowJobCodec
{
    private static readonly string[] Fields = ["format", "job_ref", "authority", "request_fingerprint", "definition", "parameters",
        "revision", "status", "completed_stages", "outputs", "pending", "applications", "created_at", "updated_at", "decision", "error",
        "stage_evidence", "output_digests", "final_outputs", "function_hashes", "function_hash_kind"];

    public static bool TryRead(JsonNode? node, CompactWorkflowJobReference reference, CompactContractDigest authority,
        ICompactWorkflowFunctions functions, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        attempt = new(null, CompactWorkflowJobError.ForeignOrInvalidJob);
        if (node is not JsonObject source || source.Any(entry => !Fields.Contains(entry.Key, StringComparer.Ordinal))
            || !Text(source, "format", out string format) || format != CompactWorkflowJobWire.Format
            || !Text(source, "job_ref", out string key) || key != reference.Digest.Hex
            || !Text(source, "authority", out string owner) || owner != authority.Hex
            || !Text(source, "request_fingerprint", out string fingerprint) || !CompactContractHash.TryDigest(fingerprint, out var request)
            || !CompactFunctionDocumentCodec.TryInteger(source["revision"], out long revision)
            || !Text(source, "status", out string status) || !TryStatus(status, out var state)
            || !Text(source, "created_at", out string created) || !Text(source, "updated_at", out string updated)
            || !CompactWorkflowDefinitionCodec.TryRead(source["definition"], functions, out var definition))
        {
            return false;
        }

        return TryValues(source, reference, authority, request!, revision, state, created, updated, definition.Value!, out attempt);
    }

    private static bool TryValues(JsonObject source, CompactWorkflowJobReference reference, CompactContractDigest authority,
        CompactContractDigest request, long revision, CompactWorkflowStatus status, string created, string updated,
        CompactWorkflowDefinition definition, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        attempt = new(null, CompactWorkflowJobError.InvalidCheckpoint);
        if (!CompactWorkflowParameters.TryRead(definition.Inputs, source["parameters"], true, out var parameters)
            || !TryCompleted(source["completed_stages"], definition, out var completed)
            || !TryOutputs(source["outputs"], out var outputs) || !TryInstances(source["applications"], out var instances)
            || !CompactWorkflowPendingCodec.TryRead(source["pending"], definition, out var pending)
            || !TryDecision(source, definition, out var decision) || !TryEvidence(source["stage_evidence"], out var evidence)
            || !TryDigests(source["output_digests"], out var digests))
        {
            return false;
        }
        IReadOnlyDictionary<string, CompactWorkflowValue>? final = null;
        if (source.ContainsKey("final_outputs") && !TryOutputValues(source["final_outputs"], out final))
        {
            return false;
        }
        var adapter = new JsonObject();
        foreach (string key in new[] { "function_hashes", "function_hash_kind" })
        {
            if (source.ContainsKey(key))
            {
                adapter[key] = source[key]?.DeepClone();
            }
        }
        var job = new CompactWorkflowJob(reference, authority, request, definition, parameters.Value!, revision, status, completed!,
            outputs!, pending.Value, instances!, created, updated, decision, Text(source, "error", out string error) ? error : null,
            evidence, digests, final, adapter.Count > 0 ? adapter : null);
        attempt = new(job);

        return true;
    }

    private static bool TryCompleted(JsonNode? node, CompactWorkflowDefinition definition, out IReadOnlyList<string>? stages)
    {
        stages = null;
        if (node is not JsonArray array || array.Count > definition.Stages.Count)
        {
            return false;
        }
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? value in array)
        {
            if (value is not JsonValue text || !text.TryGetValue(out string? id) || id == null
                || !definition.Stages.Any(stage => stage.Id == id) || !identifiers.Add(id))
            {
                return false;
            }
        }
        stages = array.Select(value => value!.GetValue<string>()).ToArray();

        return true;
    }

    private static bool TryOutputs(JsonNode? node,
        out IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>? outputs)
    {
        outputs = null;
        if (node is not JsonObject source)
        {
            return false;
        }
        var parsed = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> stage in source)
        {
            if (!TryOutputValues(stage.Value, out var fields))
            {
                return false;
            }
            parsed.Add(stage.Key, fields!);
        }
        outputs = parsed;

        return true;
    }

    public static bool TryOutputValues(JsonNode? node, out IReadOnlyDictionary<string, CompactWorkflowValue>? values)
    {
        values = null;
        if (node is not JsonObject fields)
        {
            return false;
        }
        var parsed = new Dictionary<string, CompactWorkflowValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> field in fields)
        {
            if (!TryRuntimeValue(field.Value, out var value))
            {
                return false;
            }
            parsed.Add(field.Key, value!);
        }
        values = parsed;

        return true;
    }

    private static bool TryRuntimeValue(JsonNode? node, out CompactWorkflowValue? value)
    {
        value = null;
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue(out string? text) && text != null)
            {
                value = new(CompactWorkflowValueKind.String, Text: text);
                return true;
            }
            if (scalar.TryGetValue(out bool flag))
            {
                value = new(CompactWorkflowValueKind.Boolean, Boolean: flag);
                return true;
            }
            if (CompactWorkflowInputs.TryNumber(scalar, out double number))
            {
                value = new(CompactWorkflowValueKind.Number, Number: number);
                return true;
            }
        }
        if (CompactWorkflowInputs.TryValue(node, new(CompactWorkflowValueKind.Document), out var document))
        {
            value = document.Value;
            return true;
        }

        return false;
    }

    private static bool TryInstances(JsonNode? node, out IReadOnlyDictionary<string, CompactWorkflowInstance>? instances)
    {
        instances = null;
        if (node is not JsonObject source)
        {
            return false;
        }
        var parsed = new Dictionary<string, CompactWorkflowInstance>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            if (entry.Value is not JsonObject value || value["pid"] is not JsonValue pid || !pid.TryGetValue<int>(out int processId)
                || processId < 1 || value["handle"] is not JsonValue handle || !handle.TryGetValue<long>(out long window)
                || !Text(value, "started", out string started) || started.Length == 0)
            {
                return false;
            }
            parsed.Add(entry.Key, new(processId, started, window));
        }
        instances = parsed;

        return true;
    }

    private static bool TryDecision(JsonObject source, CompactWorkflowDefinition definition, out CompactWorkflowPendingDecision? decision)
    {
        decision = null;
        if (!source.ContainsKey("decision"))
        {
            return true;
        }
        if (source["decision"] is not JsonObject record || !Text(record, "input", out string input)
            || !definition.Inputs.ContainsKey(input) || !Text(record, "prompt", out string prompt) || prompt.Length > 512)
        {
            return false;
        }
        decision = new(input, prompt);

        return true;
    }

    private static bool TryEvidence(JsonNode? node, out IReadOnlyDictionary<string, CompactWorkflowStageEvidence>? evidence)
    {
        evidence = null;
        if (node == null)
        {
            return true;
        }
        if (node is not JsonObject source)
        {
            return false;
        }
        var parsed = new Dictionary<string, CompactWorkflowStageEvidence>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            if (!CompactWorkflowPendingCodec.TryEvidence(entry.Value, out var stage))
            {
                return false;
            }
            parsed.Add(entry.Key, stage!);
        }
        evidence = parsed;

        return true;
    }

    private static bool TryDigests(JsonNode? node, out IReadOnlyDictionary<string, CompactContractDigest>? digests)
    {
        digests = null;
        if (node == null)
        {
            return true;
        }
        if (node is not JsonObject source)
        {
            return false;
        }
        var parsed = new Dictionary<string, CompactContractDigest>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in source)
        {
            if (entry.Value is not JsonValue value || !value.TryGetValue(out string? text)
                || !CompactContractHash.TryDigest(text, out var digest))
            {
                return false;
            }
            parsed.Add(entry.Key, digest!);
        }
        digests = parsed;

        return true;
    }

    private static bool TryStatus(string value, out CompactWorkflowStatus status)
    {
        foreach (CompactWorkflowStatus candidate in Enum.GetValues<CompactWorkflowStatus>())
        {
            if (CompactWorkflowJobNames.Status(candidate) == value)
            {
                status = candidate;
                return true;
            }
        }
        status = CompactWorkflowStatus.Unconfirmed;

        return false;
    }

    private static bool Text(JsonObject source, string field, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(source, field, out value);
    }
}
