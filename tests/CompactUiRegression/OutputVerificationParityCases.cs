using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Synthetic output contracts verify full-value equality and failures independently from compact response clipping.
internal static class OutputVerificationParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            CompactRecipeOutput[] outputs = fixture["outputs"]!.AsArray().Select(output => Output(output!.AsObject())).ToArray();
            var parameters = fixture["parameters"]!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>());
            JsonArray after = fixture["after"]!.DeepClone().AsArray();
            if (fixture["repeat_value"] is JsonValue repeat)
            {
                foreach (JsonNode? row in after)
                {
                    string? value = row!["value"]?.GetValue<string>();
                    row["value"] = value == null ? null : string.Concat(Enumerable.Repeat(value, repeat.GetValue<int>()));
                }
            }
            CompactOutputVerifier.TryVerify(outputs, parameters, CaptureParityCases.Nodes(fixture["before"]!.AsArray()),
                CaptureParityCases.Nodes(after), fixture["legacy_bound"]?.GetValue<bool>() ?? true, out var verified);
            JsonObject reply = verified.Value == null ? new()
            {
                ["error"] = CompactCaptureNames.Error(verified.Error!.Value)
            }
                : new()
                {
                    ["outputs"] = new JsonObject(verified.Value.Select(entry =>
                    new KeyValuePair<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value))))
                };
            replies.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = reply });
        }

        return replies;
    }

    private static CompactRecipeOutput Output(JsonObject output)
    {
        CompactSemanticTarget target = CaptureParityCases.Target(output["selector"]!.AsObject());
        CompactCapturedSelectors.TryCreate(target, target.ProcessId, out var selector);
        CompactCaptureCodec.TryField(output["field"]!.GetValue<string>(), out var field);
        string check = output["check"]!["kind"]!.GetValue<string>();
        CompactOutputCheckKind kind = check switch
        {
            "changed" => CompactOutputCheckKind.Changed,
            "equals_parameter" => CompactOutputCheckKind.EqualsParameter,
            "exists" => CompactOutputCheckKind.Exists,
            "not_empty" => CompactOutputCheckKind.NotEmpty,
            _ => throw new InvalidOperationException("Unexpected synthetic output condition.")
        };

        return new(output["name"]!.GetValue<string>(), selector.Value!, field, kind, output["check"]!["parameter"]?.GetValue<string>());
    }
}
