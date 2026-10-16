using System.Text.Json.Nodes;

namespace KLaniBot.Actions;

// Remote compatibility clients request a bounded selected schema set from the actual node, filtered by its current permitted tools.
public static class SelectedActionSchemas
{
    public const int MaximumRequestedActions = 64;

    public static bool TrySelect(JsonNode? names, IEnumerable<JsonObject> available, out JsonArray? schemas)
    {
        schemas = null;
        if (names is not JsonArray requested || requested.Count > MaximumRequestedActions)
        {
            return false;
        }
        var selected = new HashSet<Tool>();
        foreach (JsonNode? node in requested)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var name)
                || !ToolNames.TryParse(name, out var action) || !selected.Add(action))
            {
                return false;
            }
        }
        schemas = new JsonArray(available.Where(tool => selected.Contains(ToolNames.OfContractEntry(tool)))
            .Select(tool => (JsonNode?)Descriptor(tool)).ToArray());

        return true;
    }

    private static JsonObject Descriptor(JsonObject tool)
    {
        var descriptor = new JsonObject { ["name"] = tool["name"]!.DeepClone(), ["inputSchema"] = tool["inputSchema"]!.DeepClone() };
        if (tool["outputSchema"] != null)
        {
            descriptor["outputSchema"] = tool["outputSchema"]!.DeepClone();
        }

        return descriptor;
    }
}
