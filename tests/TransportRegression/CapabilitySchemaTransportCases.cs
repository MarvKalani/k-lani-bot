using System.Text.Json.Nodes;

// A genuinely isolated HTTPS node proves selected schema discovery honors actual worker scope and preserves the omitted-query reply.
internal static class CapabilitySchemaTransportCases
{
    public static async Task<int> RunAsync(Func<JsonObject[], Task<JsonObject[]>> call)
    {
        var requests = new[] { Request(null), Request(new JsonArray("get_machine_capabilities", "type_text")),
            Request(new JsonArray("get_machine_capabilities", "get_machine_capabilities")), Request(new JsonArray("invented")) };
        JsonObject[] replies = await call(requests);
        Require(replies[0]["success"]?.ToString() == "true" && replies[0]["capabilities"]?["actionSchemas"] == null,
            "Omitted selection changed the compact capability reply.");
        var schemas = replies[1]["capabilities"]?["actionSchemas"] as JsonArray;
        Require(replies[1]["success"]?.ToString() == "true" && schemas?.Count == 1
            && schemas[0]?["name"]?.ToString() == "get_machine_capabilities", "Unauthorized UI schema was exposed.");
        JsonObject contract = JsonNode.Parse(File.ReadAllText("contracts/tools.json"))!["tools"]!.AsArray().OfType<JsonObject>()
            .Single(tool => tool["name"]!.ToString() == "get_machine_capabilities");
        Require(JsonNode.DeepEquals(schemas![0]!["inputSchema"], contract["inputSchema"]), "Node returned a different actual schema.");
        Require(replies[2]["success"]?.ToString() == "false" && replies[2]["code"]?.ToString() == "invalid_request",
            "Duplicate schema names were not refused.");
        Require(replies[3]["success"]?.ToString() == "false" && replies[3]["code"]?.ToString() == "invalid_request",
            "Unknown schema names were not refused.");

        return 5;
    }

    private static JsonObject Request(JsonArray? names)
    {
        var arguments = new JsonObject();
        if (names != null)
        {
            arguments["action_schema_names"] = names;
        }

        return new JsonObject { ["action"] = "get_machine_capabilities", ["args"] = arguments };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
