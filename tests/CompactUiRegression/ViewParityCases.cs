using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Both runtimes process the same ordered observation corpus; only opaque context/view/cursor identifiers are normalized.
internal static class ViewParityCases
{
    public static JsonArray Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var store = new CompactViewStore();
        var actual = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var responses = new JsonArray();
        foreach (JsonNode? node in corpus["steps"]!.AsArray())
        {
            JsonObject step = node!.AsObject();
            responses.Add(RunStep(store, actual, step));
        }

        return responses;
    }

    private static JsonObject RunStep(CompactViewStore store, Dictionary<string, JsonObject> actual, JsonObject step)
    {
        string name = step["name"]!.GetValue<string>();
        CompactViewContext context = store.Context(new(step["context"]!.GetValue<string>()));
        JsonObject arguments = step["options"]?.DeepClone().AsObject() ?? new();
        BindReference(step, arguments, actual, "since", "since_view", "view_ref");
        BindReference(step, arguments, actual, "cursor", "cursor", "next_cursor");
        if (!CompactViewCodec.TryOptions(arguments, out CompactViewOptions? options))
        {
            return Error(name, CompactViewError.InvalidPageArguments);
        }
        IReadOnlyList<CompactViewRow> records = Records(step);
        if (!store.TryPage(context, records, step["stamp"]!.GetValue<string>(), options!, out CompactViewAttempt attempt))
        {
            return Error(name, attempt.Error!.Value);
        }
        JsonObject reply = CompactViewCodec.Serialize(attempt.Value!);
        actual[name] = reply.DeepClone().AsObject();
        reply["view_ref"] = "$view";
        reply["context_ref"] = "$context";
        if (reply["next_cursor"] != null)
        {
            reply["next_cursor"] = "$cursor";
        }

        return new()
        {
            ["name"] = name,
            ["reply"] = reply
        };
    }

    private static void BindReference(JsonObject step, JsonObject arguments, Dictionary<string, JsonObject> actual,
        string field, string option, string property)
    {
        if (step[field] is JsonValue alias)
        {
            arguments[option] = actual[alias.GetValue<string>()][property]?.DeepClone();
        }
    }

    private static IReadOnlyList<CompactViewRow> Records(JsonObject step)
    {
        if (step["records"] is JsonArray records)
        {
            return records.Select(record => new CompactViewRow(record!["ref"]!.GetValue<string>(),
                record.DeepClone().AsObject())).ToArray();
        }
        int count = step["count"]!.GetValue<int>();

        return Enumerable.Range(0, count).Select(index => new CompactViewRow("e" + index,
            new JsonObject { ["ref"] = "e" + index, ["name"] = "Field " + index })).ToArray();
    }

    private static JsonObject Error(string name, CompactViewError error)
    {
        return new()
        {
            ["name"] = name,
            ["error"] = CompactViewNames.Error(error)
        };
    }
}
