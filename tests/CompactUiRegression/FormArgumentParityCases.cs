using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared malformed MCP arguments must be rejected identically before native input, including value-family and page bounds.
internal static class FormArgumentParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? fixture in corpus["cases"]!.AsArray())
        {
            JsonObject row = fixture!.AsObject();
            JsonNode? arguments = row["arguments"]?.DeepClone();
            if (row["repeat_value"] is JsonValue repeat)
            {
                string text = arguments!["value"]!.GetValue<string>();
                arguments["value"] = string.Concat(Enumerable.Repeat(text, repeat.GetValue<int>()));
            }
            bool valid = CompactFormCodec.TryRead(row["function"]!.GetValue<string>(), arguments, out var parsed);
            var reply = new JsonObject { ["name"] = row["name"]!.DeepClone() };
            if (valid)
            {
                reply["accepted"] = true;
            }
            else
            {
                reply["error"] = parsed.Detail;
            }
            replies.Add(reply);
        }

        return replies;
    }
}
