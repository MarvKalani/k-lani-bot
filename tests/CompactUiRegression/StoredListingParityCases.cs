using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Both gateways must bound, order, name and word a stored-function listing the same way; the JS reference reads the same corpus.
internal static class StoredListingParityCases
{
    public static JsonNode Run(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var summaries = new JsonArray();
        foreach (var testCase in corpus["cases"]!.AsArray().OfType<JsonObject>())
        {
            var functions = testCase["functions"]!.AsArray().OfType<JsonObject>().Select(Listed).ToArray();
            var summary = CompactFunctionSummary.Summarize(functions);
            summaries.Add(new JsonObject
            {
                ["name"] = testCase["name"]!.ToString(),
                ["functions"] = new JsonArray(summary.Functions.Select(function => (JsonNode?)function.ToJson()).ToArray()),
                ["more_functions"] = summary.MoreFunctions
            });
        }

        return new JsonObject
        {
            ["summaries"] = summaries,
            ["texts"] = new JsonObject
            {
                ["usage"] = CompactStoredFunctionListing.UsageHint,
                ["select"] = CompactStoredFunctionListing.SelectHint,
                ["pid"] = CompactGatewayState.UnlistedPidRefusal,
                ["instructions"] = CompactGatewayTools.Instructions,
                ["stored_functions"] = CompactStoredFunctionListing.StoredFunctionsSentence,
                ["no_stored_functions"] = CompactStoredFunctionListing.NoStoredFunctionsSentence
            }
        };
    }

    // A corpus entry names a function and, when it has them, its description, parameter names and step labels in their order.
    private static CompactListedFunction Listed(JsonObject entry)
    {
        string[] parameters = Texts(entry["parameters"]);
        string[] steps = Texts(entry["steps"]);

        return new CompactListedFunction(entry["name"]!.ToString(), entry["description"]?.ToString(), parameters, steps);
    }

    private static string[] Texts(JsonNode? node)
    {
        return (node as JsonArray)?.Select(text => text!.ToString()).ToArray() ?? [];
    }
}
