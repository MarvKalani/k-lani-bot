using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Runtime input validation converts each present value to its declared type and preserves missing optional answers for a later decision.
public static class CompactWorkflowParameters
{
    public static bool TryRead(IReadOnlyDictionary<string, CompactWorkflowInput> inputs, JsonNode? node, bool allowMissing,
        out CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject parameters || parameters.Any(entry => !inputs.ContainsKey(entry.Key)))
        {
            return false;
        }
        var values = new Dictionary<string, CompactWorkflowValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, CompactWorkflowInput> input in inputs)
        {
            if (!parameters.ContainsKey(input.Key))
            {
                if (!allowMissing && input.Value.Required)
                {
                    attempt = new(null, CompactWorkflowError.MissingParameter, input.Key);
                    return false;
                }
                continue;
            }
            if (!CompactWorkflowInputs.TryValue(parameters[input.Key], input.Value, out var value))
            {
                attempt = new(null, value.Error);
                return false;
            }
            values.Add(input.Key, value.Value!);
        }
        attempt = new(values);

        return true;
    }
}
