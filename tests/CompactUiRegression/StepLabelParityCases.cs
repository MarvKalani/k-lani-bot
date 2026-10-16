using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Both gateways must name a stored step the same way; the JS reference reads the same corpus and writes the same labels.
internal static class StepLabelParityCases
{
    public static JsonArray Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var results = new JsonArray();
        foreach (var testCase in corpus["cases"]!.AsArray().OfType<JsonObject>())
        {
            var labels = new JsonArray(testCase["steps"]!.AsArray().OfType<JsonObject>().Select(Step)
                .Select(step => (JsonNode?)JsonValue.Create(CompactStepLabel.Of(step))).ToArray());
            results.Add(new JsonObject { ["name"] = testCase["name"]!.ToString(), ["labels"] = labels });
        }

        return results;
    }

    // A corpus entry carries only the stored selector and action, never a parameter value, as a saved recipe does.
    private static CompactRecipeStep Step(JsonObject entry)
    {
        var selector = entry["selector"]!.AsObject();
        var containers = (selector["containerPath"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(Container).ToArray();
        var stored = new CompactStoredSelector(CompactRole.Parse(selector["role"]!.ToString()), selector["name"]?.ToString(),
            selector["autoId"]?.ToString(), CompactWindowKind.Main, containers);

        return new(Enum.Parse<CompactRecipeAction>(entry["action"]!.ToString(), ignoreCase: true), stored, CompactRecipeRoute.Semantic);
    }

    private static CompactContainerSelector Container(JsonObject container)
    {
        return new(CompactRole.Parse(container["role"]!.ToString()), container["autoId"]?.ToString(), container["name"]?.ToString());
    }
}
