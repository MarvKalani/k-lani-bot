using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Bindings allow only declared inputs or earlier stage outputs; forward references, cycles and literals never become executable values.
public static class CompactWorkflowBindings
{
    public static bool TryRead(JsonNode? node, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior,
        out CompactWorkflowAttempt<CompactWorkflowBinding> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject binding || !CompactDiscoveryDocumentCodec.Only(binding, "input", "stage", "output"))
        {
            return false;
        }
        if (binding.ContainsKey("input"))
        {
            if (binding.Count != 1 || !CompactDiscoveryDocumentCodec.Text(binding, "input", out string name)
                || !CompactDiscoveryCodec.ValidName(name) || !inputs.ContainsKey(name))
            {
                attempt = new(null, CompactWorkflowError.UndeclaredInput);
                return false;
            }
            attempt = new(new(CompactWorkflowBindingKind.Input, Input: name));
            return true;
        }
        if (binding.Count != 2 || !CompactDiscoveryDocumentCodec.Text(binding, "stage", out string stage)
            || !CompactDiscoveryCodec.ValidName(stage) || !CompactDiscoveryDocumentCodec.Text(binding, "output", out string output)
            || !CompactDiscoveryCodec.ValidName(output) || !prior.TryGetValue(stage, out var outputs) || !outputs.ContainsKey(output))
        {
            attempt = new(null, CompactWorkflowError.UnknownPriorOutput);
            return false;
        }
        attempt = new(new(CompactWorkflowBindingKind.ConfirmedOutput, Stage: stage, Output: output));

        return true;
    }

    public static bool TryResolve(IReadOnlyDictionary<string, CompactWorkflowBinding> bindings,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>> confirmedOutputs,
        out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>> attempt)
    {
        var values = new Dictionary<string, CompactWorkflowValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, CompactWorkflowBinding> binding in bindings)
        {
            if (!TryValue(binding.Value, parameters, confirmedOutputs, out var value))
            {
                attempt = new(null, binding.Value.Kind == CompactWorkflowBindingKind.Input ? CompactWorkflowError.MissingParameter
                    : CompactWorkflowError.UnconfirmedOutput, binding.Value.Input);
                return false;
            }
            values.Add(binding.Key, value!);
        }
        attempt = new(values);

        return true;
    }

    private static bool TryValue(CompactWorkflowBinding binding, IReadOnlyDictionary<string, CompactWorkflowValue> parameters,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>> confirmed,
        out CompactWorkflowValue? value)
    {
        value = null;
        switch (binding.Kind)
        {
            case CompactWorkflowBindingKind.Input:
                return binding.Input != null && parameters.TryGetValue(binding.Input, out value);
            case CompactWorkflowBindingKind.ConfirmedOutput:
                return binding.Stage != null && binding.Output != null && confirmed.TryGetValue(binding.Stage, out var outputs)
                    && outputs.TryGetValue(binding.Output, out value);
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    public static CompactWorkflowValueKind Type(CompactWorkflowBinding binding, IReadOnlyDictionary<string, CompactWorkflowInput> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValueKind>> prior)
    {
        return binding.Kind switch
        {
            CompactWorkflowBindingKind.Input => inputs[binding.Input!].Kind,
            CompactWorkflowBindingKind.ConfirmedOutput => prior[binding.Stage!][binding.Output!],
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }
}
