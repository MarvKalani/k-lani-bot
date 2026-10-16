using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// The same native-tree corpus verifies projection content, compaction, stable references and exact semantic failures in JS/C#.
internal static class ProjectionParityCases
{
    public static JsonArray Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var projection = new CompactUiProjection();
        var actual = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var replies = new JsonArray();
        foreach (JsonNode? step in corpus["steps"]!.AsArray())
        {
            replies.Add(Observe(projection, actual, step!.AsObject()));
        }

        return replies;
    }

    private static JsonObject Observe(CompactUiProjection projection, Dictionary<string, JsonObject> actual, JsonObject step)
    {
        string name = step["name"]!.GetValue<string>();
        string tree = step["tree"]?.GetValue<string>() ?? BuildTree(step["nodes"]!.AsArray());
        if (!CompactTreeReader.TryParse(tree, out var parsed))
        {
            return Error(name, CompactTreeNames.Error(parsed.Error!.Value));
        }
        JsonObject options = step["options"]!.DeepClone().AsObject();
        if (step["since"] is JsonValue alias)
        {
            options["since_view"] = actual[alias.GetValue<string>()]["view_ref"]!.DeepClone();
        }
        CompactViewCodec.TryOptions(options, out var page);
        var settings = new CompactProjectionOptions(options["node"]?.GetValue<string>() ?? "local-session",
            options["instance"]?.GetValue<string>() ?? "projection-fixture", options["root"]?.GetValue<string>(),
            options["offscreen"]?.GetValue<bool>() ?? true, options["contains"]?.GetValue<string>(),
            options["max_chars"]?.GetValue<int>() ?? 2000, page, ObservedWindow(step["pid"]!.GetValue<int>(), options));
        if (!projection.TryObserve(step["pid"]!.GetValue<int>(), parsed.Value!, settings, out var observed))
        {
            return Error(name, observed.TreeError != null ? CompactTreeNames.Error(observed.TreeError.Value, observed.Detail)
                : CompactViewNames.Error(observed.PageError!.Value));
        }
        JsonObject reply = CompactViewCodec.Serialize(observed.Value!);
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

    // The fixture names the one window the tree was read for by its handle, lifetime and kind; a caption is never part of identity.
    private static CompactNativeWindow? ObservedWindow(int processId, JsonObject options)
    {
        if (options["window"] is not JsonObject window)
        {
            return null;
        }
        CompactWindowKindWords.TryParse(window["Kind"]?.GetValue<string>(), out CompactWindowKind kind);

        return new(processId, "fixture", "1.0", "", window["WindowHandle"]!.GetValue<long>(),
            window["ProcessStartedAtUtc"]?.GetValue<string>(), kind);
    }

    private static string BuildTree(JsonArray nodes)
    {
        return string.Join('\n', nodes.Select(node => Row(node!.AsObject())));
    }

    private static string Row(JsonObject node)
    {
        int depth = node["depth"]?.GetValue<int>() ?? 0;
        int identifier = node["id"]!.GetValue<int>();
        string role = node["role"]!.GetValue<string>();
        string name = CompactJsonText.Serialize(node["name"]);
        string autoId = CompactJsonText.Serialize(node["autoId"] ?? JsonValue.Create(""));
        string value = CompactJsonText.Serialize(node["value"] ?? JsonValue.Create(""));
        string enabled = node["enabled"]?.GetValue<bool>() == false ? "False" : "True";
        string offscreen = node["offscreen"]?.GetValue<bool>() == true ? "True" : "False";

        return new string(' ', depth) + $"[{identifier}] {role} {name} (autoId: {autoId}, val: {value}, "
            + $"enabled: {enabled}, offscreen: {offscreen})";
    }

    private static JsonObject Error(string name, string error)
    {
        return new()
        {
            ["name"] = name,
            ["error"] = error
        };
    }
}
