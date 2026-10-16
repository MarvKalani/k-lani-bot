using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Resume restores only recorded window lifetimes and original function body hashes; absent historical bindings never become
// invented evidence.
public static class CompactFunctionCheckpoint
{
    // Throws CompactNodeException for changed function bodies or missing/inconsistent private checkpoint evidence.
    public static CompactFunctionPreflight Restore(CompactLoadedFunction function, CompactProgramIdentity application,
        CompactNativeWindow instance, CompactWorkflowStageEvidence evidence)
    {
        string kind = evidence.Source["function_hash_kind"]?.ToString() ?? "full-entry/1";
        if (Hash(function, kind) != evidence.Source["function_hash"]?.ToString()
            || evidence.Source["window_bindings"] is not JsonObject windows || evidence.Source["outputs"] is not JsonObject outputs)
        {
            throw new CompactNodeException("Checkpointed function changed or native window evidence is missing.");
        }
        var bindings = new Dictionary<CompactWindowKind, CompactNativeWindow>();
        foreach (var entry in windows)
        {
            if (!CompactWindowKindWords.TryParse(entry.Key, out var windowKind))
            {
                throw new CompactNodeException("Checkpointed native window kind is invalid.");
            }
            bindings.Add(windowKind, ReadWindow(entry.Value, instance, windowKind));
        }
        var before = new Dictionary<string, CompactOptionalOutput>(StringComparer.Ordinal);
        foreach (var output in function.Draft.Outputs)
        {
            if (outputs[output.Name] is not JsonObject value || value["exists"] is not JsonValue exists
                || !exists.TryGetValue<bool>(out bool present)
                || value["value"] != null && (value["value"] is not JsonValue text || !text.TryGetValue<string>(out _)))
            {
                throw new CompactNodeException("Checkpointed output baseline is invalid.");
            }
            before.Add(output.Name, new(present, value["value"]?.GetValue<string>()));
        }

        return new(function, application, instance, bindings, before);
    }

    // Throws CompactNodeException for unsupported function-hash editions.
    public static string Hash(CompactLoadedFunction function, string kind)
    {
        if (kind == "function-scope/1")
        {
            return CompactContractHash.Function(function.Draft).Hex;
        }
        if (kind == "full-entry/1")
        {
            return CompactFormFields.Fingerprint(function.Document);
        }

        throw new CompactNodeException("Unknown workflow function checkpoint format.");
    }

    // Throws CompactNodeException for incomplete, foreign or malformed private window bindings. The caption is not evidence, so
    // a restored window carries no title.
    private static CompactNativeWindow ReadWindow(JsonNode? node, CompactNativeWindow instance, CompactWindowKind kind)
    {
        if (node is not JsonObject window || window["pid"] is not JsonValue identifier || !identifier.TryGetValue<int>(out int process)
            || process != instance.ProcessId || window["started"]?.ToString() != instance.Started
            || window["handle"] is not JsonValue handle
            || !handle.TryGetValue<long>(out long value) || value == 0
            || window["build"]?.ToString() != instance.Build || window["process_name"]?.ToString() != instance.ProcessName)
        {
            throw new CompactNodeException("Checkpointed native window identity is invalid or foreign.");
        }

        return new(process, instance.ProcessName, instance.Build, "", value, instance.Started, kind);
    }
}
