using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Typed runtime values return as plain protocol values without paths, arbitrary objects or executable binding expressions.
public static class CompactWorkflowWire
{
    public static JsonNode? Value(CompactWorkflowValue value)
    {
        return value.Kind switch
        {
            CompactWorkflowValueKind.String => JsonValue.Create(value.Text),
            CompactWorkflowValueKind.Enum => JsonValue.Create(value.Text),
            CompactWorkflowValueKind.Number => JsonValue.Create(value.Number),
            CompactWorkflowValueKind.Boolean => JsonValue.Create(value.Boolean),
            CompactWorkflowValueKind.Document => new JsonObject
            {
                ["ref"] = value.Document!.Reference,
                ["sha256"] = value.Document.Digest.Hex,
                ["origin"] = value.Document.Origin
            },
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    public static JsonObject Values(IReadOnlyDictionary<string, CompactWorkflowValue> values)
    {
        return new JsonObject(values.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key, Value(entry.Value))));
    }
}
