using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared legacy/current document fixtures compare real loader acceptance and preserved wire content, never hidden application answers.
internal static class FunctionDocumentParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(System.IO.File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        var application = new CompactProgramIdentity("library-fixture", "1.0");
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            CompactFunctionDocumentCodec.TryRead(fixture["document"], application, out var loaded);
            JsonObject reply = loaded.Value == null ? new()
            {
                ["error"] = loaded.Detail
                ?? CompactFunctionNames.Error(loaded.Error!.Value)
            } : new()
            {
                ["revision"] = loaded.Value.Revision,
                ["source_version"] = loaded.Value.Version,
                ["functions"] = loaded.Value.Source["functions"]!.DeepClone(),
                ["candidates"] = loaded.Value.Source["candidates"]?.DeepClone() ?? new JsonArray()
            };
            output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = reply });
        }

        return output;
    }
}
