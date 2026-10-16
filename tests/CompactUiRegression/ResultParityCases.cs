using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// The parity executable returns exact semantic pages/errors for the same corpus used by the independent JS reference runner.
internal static class ResultParityCases
{
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static JsonArray Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? item in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = item!.AsObject();
            var store = new CompactResultStore();
            var owner = new CompactResultAuthority(fixture["owner"]!.GetValue<string>(), Guid.Empty);
            var outputs = fixture["outputs"]!.AsObject().ToDictionary(field => field.Key, field => field.Value!.GetValue<string>());
            if (!store.TryPut(owner, outputs, fixture["debug"]!.GetValue<string>(), out var stored))
            {
                throw new InvalidOperationException("Parity evidence could not be stored.");
            }
            var pages = new JsonArray();
            foreach (JsonNode? query in fixture["queries"]!.AsArray())
            {
                pages.Add(ReadPage(store, owner, stored.Value!, query!.AsObject()));
            }
            replies.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["pages"] = pages });
        }

        return replies;
    }

    private static JsonNode ReadPage(CompactResultStore store, CompactResultAuthority owner,
        CompactEvidenceReference reference, JsonObject query)
    {
        var arguments = query["arguments"]!.DeepClone().AsObject();
        arguments["evidence_ref"] ??= reference.Value.ToString();
        if (!CompactResultQueryCodec.TryParse(arguments, out var parsed))
        {
            return Error(parsed.Error!.Value);
        }
        var requesting = owner with
        {
            Node = query["request_owner"]?.GetValue<string>() ?? owner.Node
        };
        if (!store.TryRead(requesting, parsed.Value!, out var page))
        {
            return Error(page.Error!.Value);
        }

        return JsonSerializer.SerializeToNode(page.Value, WireOptions)!;
    }

    private static JsonObject Error(CompactResultError error)
    {
        return new JsonObject { ["error"] = CompactResultErrors.ToWireMessage(error) };
    }
}
