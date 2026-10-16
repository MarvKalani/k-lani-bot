using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// The native formatter runs the shared projection corpus; only opaque runtime evidence references are normalized.
internal static class ResultFormatParityCases
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static JsonArray Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? fixture in corpus["cases"]!.AsArray())
        {
            replies.Add(FormatFixture(fixture!.AsObject()));
        }

        return replies;
    }

    private static JsonObject FormatFixture(JsonObject fixture)
    {
        JsonObject result = PrepareResult(fixture);
        var names = result["outputs"] is JsonObject outputs ? outputs.Select(field => field.Key).ToHashSet(StringComparer.Ordinal) : [];
        if (!CompactResultOptionsCodec.TryParse(fixture["options"]!.AsObject(), names, out var options, out string? error))
        {
            return new()
            {
                ["name"] = fixture["name"]!.DeepClone(),
                ["error"] = error
            };
        }
        var store = new CompactResultStore();
        var formatter = new CompactResultFormatter(store);
        var authority = new CompactResultAuthority("node", Guid.Empty);
        JsonObject reply = formatter.Format(authority, result, options!, Checks(fixture), Scan(fixture));
        var response = new JsonObject { ["name"] = fixture["name"]!.DeepClone() };
        if (reply["evidence_ref"] is JsonValue reference)
        {
            var query = new CompactResultPageQuery(new(Guid.Parse(reference.GetValue<string>())),
                CompactResultSection.Parse("debug"), 0, 8000);
            if (!store.TryRead(authority, query, out var debug))
            {
                throw new InvalidOperationException("Debug result not retrievable.");
            }
            response["debug_page"] = JsonSerializer.SerializeToNode(debug.Value, WireOptions);
        }
        NormalizeReferences(reply);
        response["reply"] = reply;

        return response;
    }

    private static JsonObject PrepareResult(JsonObject fixture)
    {
        JsonObject result = fixture["result"]!.DeepClone().AsObject();
        if (result["outputs"] is JsonObject outputs)
        {
            foreach (string name in outputs.Select(field => field.Key).ToArray())
            {
                string text = outputs[name]!.GetValue<string>();
                int count = fixture["repeat_outputs"]?.GetValue<int>() ?? 1;
                outputs[name] = string.Concat(Enumerable.Repeat(text, count));
            }
        }

        return result;
    }

    private static Dictionary<string, CompactOutputCheck> Checks(JsonObject fixture)
    {
        var checks = new Dictionary<string, CompactOutputCheck>();
        if (fixture["checks"] is JsonObject values)
        {
            foreach (KeyValuePair<string, JsonNode?> value in values)
            {
                checks[value.Key] = new(value.Value?["verified"]?.GetValue<bool>(), value.Value?["matches_input"]?.GetValue<bool>());
            }
        }

        return checks;
    }

    private static CompactScanCounts? Scan(JsonObject fixture)
    {
        if (fixture["scan"] is not JsonObject scan)
        {
            return null;
        }

        return new(scan["field_count"]!.GetValue<int>(), scan["content_count"]!.GetValue<int>());
    }

    private static void NormalizeReferences(JsonNode? node)
    {
        if (node is JsonObject record)
        {
            foreach (string name in record.Select(field => field.Key).ToArray())
            {
                if (name == "evidence_ref")
                {
                    record[name] = "$evidence";
                }
                else
                {
                    NormalizeReferences(record[name]);
                }
            }
        }
        else if (node is JsonArray values)
        {
            foreach (JsonNode? value in values)
            {
                NormalizeReferences(value);
            }
        }
    }
}
