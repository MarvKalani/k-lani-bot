using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Function preflight binds the approved body to current window lifetimes and complete observed before-values, never coordinates.
public sealed record CompactFunctionPreflight(CompactLoadedFunction Function, CompactProgramIdentity Application,
    CompactNativeWindow Instance, IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> Windows,
    IReadOnlyDictionary<string, CompactOptionalOutput> Outputs);

// Fresh execution observations read each bound window on its own, so every tree belongs to exactly one native window.
public sealed record CompactFunctionObservation(IReadOnlyDictionary<CompactWindowKind, CompactNativeObservation> Trees,
    IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> Windows)
{
    // An unbound kind has no tree, so its outputs read as absent instead of reading another window.
    public IReadOnlyList<CompactTreeNode> NodesOf(CompactWindowKind kind)
    {
        return Trees.TryGetValue(kind, out var tree) ? tree.Nodes : [];
    }
}

// Native function results preserve complete confirmed outputs and never equate a successful dispatch with a verified task.
public sealed record CompactFunctionExecutionResult(IReadOnlyDictionary<string, string> Outputs);

// A stored function run keeps its verified outputs together with the window change that its reply reports. When the change cannot be
// read after a verified run, the reply keeps the outcome and names the unread change instead of reporting the run as failed.
internal sealed record CompactStoredRun(CompactFunctionExecutionResult Result, JsonObject? Changes, string? ChangesError = null);

// Private function evidence persists the original body hash, before-values and window lifetimes required for safe resume.
public static class CompactFunctionEvidence
{
    public static CompactWorkflowStageEvidence Create(CompactFunctionPreflight prepared)
    {
        var outputs = new JsonObject(prepared.Outputs.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key,
            new JsonObject { ["exists"] = entry.Value.Exists, ["value"] = entry.Value.Value })));
        var windows = new JsonObject(prepared.Windows.Select(entry => new KeyValuePair<string, JsonNode?>(
            CompactWindowKindWords.Text(entry.Key),
            new JsonObject
            {
                ["pid"] = entry.Value.ProcessId,
                ["started"] = entry.Value.Started,
                ["handle"] = entry.Value.Handle,
                ["build"] = entry.Value.Build,
                ["process_name"] = entry.Value.ProcessName
            })));
        int[] mutations = prepared.Function.Draft.Steps.Select((step, index) => new CompactIndexedStep(index, step))
            .Where(item => item.Step.Action != CompactRecipeAction.Read).Select(item => item.Index).ToArray();
        var counts = new JsonObject(prepared.Function.Draft.Steps.Select((step, index) =>
            new KeyValuePair<string, JsonNode?>(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValue.Create(CompactRecipeWriter.ExpectedOperations(step)))));
        var source = new JsonObject
        {
            ["outputs"] = outputs,
            ["function_hash"] = CompactContractHash.Function(prepared.Function.Draft).Hex,
            ["function_hash_kind"] = "function-scope/1",
            ["expected_steps"] = prepared.Function.Draft.Steps.Count,
            ["mutation_steps"] = new JsonArray(mutations.Select(index => (JsonNode?)JsonValue.Create(index)).ToArray()),
            ["window_bindings"] = windows,
            ["expected_native_operations"] = counts
        };

        return new(source, prepared.Function.Draft.Steps.Count, mutations);
    }
}

// Indexing a finite recipe step retains its original position without tuples or assigning an operation ID before durable intent.
internal sealed record CompactIndexedStep(int Index, CompactRecipeStep Step);
