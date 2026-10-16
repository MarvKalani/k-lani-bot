using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Stages resolve one verified host function and enforce typed bindings plus fixed fresh-target/output conditions before registration.
public static class CompactWorkflowStages
{
    public static bool TryRead(JsonNode? node, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, CompactWorkflowApplication> applications,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior, ICompactWorkflowFunctions functions,
        out CompactWorkflowAttempt<CompactWorkflowStage> attempt, out IReadOnlyDictionary<string, CompactWorkflowValueKind>? outputs)
    {
        outputs = null;
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject stage || !CompactDiscoveryDocumentCodec.Only(stage, "id", "function_ref", "bindings", "preconditions",
                "postconditions", "decision") || stage["function_ref"] is not JsonObject reference
            || !CompactDiscoveryDocumentCodec.Only(reference, "application", "name"))
        {
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(stage, "id", out string id) || !CompactDiscoveryCodec.ValidName(id) || prior.ContainsKey(id)
            || !CompactDiscoveryDocumentCodec.Text(reference, "application", out string alias) || !CompactDiscoveryCodec.ValidName(alias)
            || !applications.TryGetValue(alias, out var application)
            || !CompactDiscoveryDocumentCodec.Text(reference, "name", out string name))
        {
            attempt = new(null, CompactWorkflowError.InvalidStage);
            return false;
        }
        var selected = new CompactWorkflowFunctionReference(alias, name);
        if (!functions.TryResolve(selected, application, out var function) || function == null || !function.Demonstrated
            || !function.CompatibilityPresent || function.Outputs.Count == 0)
        {
            attempt = new(null, CompactWorkflowError.UnknownFunction);
            return false;
        }

        return TryContracts(stage, id, selected, function, inputs, prior, out attempt, out outputs);
    }

    private static bool TryContracts(JsonObject stage, string id, CompactWorkflowFunctionReference reference,
        CompactWorkflowFunctionContract function, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior,
        out CompactWorkflowAttempt<CompactWorkflowStage> attempt, out IReadOnlyDictionary<string, CompactWorkflowValueKind>? outputs)
    {
        outputs = null;
        if (!CompactDiscoveryDocumentCodec.Text(stage, "preconditions", out string before) || before != "fresh_unique_enabled_targets"
            || !CompactDiscoveryDocumentCodec.Text(stage, "postconditions", out string after) || after != "recorded_function_outputs")
        {
            attempt = new(null, CompactWorkflowError.InvalidConditions);
            return false;
        }
        if (!TryBindings(stage["bindings"], function, inputs, prior, out var bindings))
        {
            attempt = new(null, bindings.Error, bindings.Detail);
            return false;
        }
        if (!TryDecision(stage, inputs, out var decision))
        {
            attempt = new(null, decision.Error);
            return false;
        }
        attempt = new(new(id, reference, bindings.Value!, decision.Value));
        outputs = function.Outputs;

        return true;
    }

    private static bool TryBindings(JsonNode? node, CompactWorkflowFunctionContract function,
        IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior,
        out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowBinding>> attempt)
    {
        attempt = new(null, CompactWorkflowError.InvalidStageBindings);
        if (node is not JsonObject bindings || bindings.Any(entry => !function.Parameters.ContainsKey(entry.Key))
            || function.Required.Any(name => !bindings.ContainsKey(name)))
        {
            return false;
        }
        var parsed = new Dictionary<string, CompactWorkflowBinding>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in bindings)
        {
            if (!CompactWorkflowBindings.TryRead(entry.Value, inputs, prior, out var binding))
            {
                attempt = new(null, binding.Error, binding.Detail);
                return false;
            }
            CompactWorkflowValueKind source = CompactWorkflowBindings.Type(binding.Value!, inputs, prior);
            CompactWorkflowValueKind target = function.Parameters[entry.Key];
            if (source != target && !(source == CompactWorkflowValueKind.Enum && target == CompactWorkflowValueKind.String))
            {
                attempt = new(null, CompactWorkflowError.BindingTypeMismatch);
                return false;
            }
            parsed.Add(entry.Key, binding.Value!);
        }
        attempt = new(parsed);

        return true;
    }

    private static bool TryDecision(JsonObject stage, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        out CompactWorkflowAttempt<CompactWorkflowDecision> attempt)
    {
        attempt = new(null);
        if (!stage.ContainsKey("decision"))
        {
            return true;
        }
        if (stage["decision"] is not JsonObject decision || !CompactDiscoveryDocumentCodec.Only(decision, "input", "prompt"))
        {
            attempt = new(null, CompactWorkflowError.UnsupportedFields);
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(decision, "input", out string input) || !CompactDiscoveryCodec.ValidName(input)
            || !inputs.TryGetValue(input, out var declared) || declared.Kind != CompactWorkflowValueKind.Boolean
            || !CompactDiscoveryDocumentCodec.Text(decision, "prompt", out string prompt) || prompt.Length is < 1 or > 512)
        {
            attempt = new(null, CompactWorkflowError.InvalidDecision);
            return false;
        }
        attempt = new(new(input, prompt));

        return true;
    }
}
