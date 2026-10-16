using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared workflow contract fixtures compare typed validation, hashes and confirmed-output bindings without application or model input.
internal static class WorkflowDefinitionParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = RunCase(fixture) });
        }

        return output;
    }

    private static JsonObject RunCase(JsonObject fixture)
    {
        if (!CompactWorkflowDefinitionCodec.TryRead(fixture["definition"], new Functions(fixture["functions"]!.AsObject()),
                out var definition))
        {
            return Error(definition.Error!.Value, definition.Detail);
        }
        if (!fixture.ContainsKey("parameters"))
        {
            return new()
            {
                ["hash"] = definition.Value!.Hash.Hex,
                ["definition"] = definition.Value.Source.DeepClone()
            };
        }
        if (!CompactWorkflowParameters.TryRead(definition.Value!.Inputs, fixture["parameters"],
            fixture["allow_missing"]?.GetValue<bool>() ?? false, out var parameters))
        {
            return Error(parameters.Error!.Value, parameters.Detail);
        }
        var reply = new JsonObject { ["hash"] = definition.Value.Hash.Hex, ["parameters_valid"] = true };
        if (fixture["resolve_stage"] is JsonValue stageName)
        {
            var prior = Confirmed(fixture["confirmed"]!.AsObject());
            var stage = definition.Value.Stages.Single(stage => stage.Id == stageName.GetValue<string>());
            if (!CompactWorkflowBindings.TryResolve(stage.Bindings, parameters.Value!, prior, out var bound))
            {
                return Error(bound.Error!.Value, bound.Detail);
            }
            reply["bindings"] = CompactWorkflowWire.Values(bound.Value!);
        }

        return reply;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>> Confirmed(JsonObject outputs)
    {
        var stages = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> stage in outputs)
        {
            var fields = new Dictionary<string, CompactWorkflowValue>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonNode?> field in stage.Value!.AsObject())
            {
                fields.Add(field.Key, new(CompactWorkflowValueKind.String, Text: field.Value!.GetValue<string>()));
            }
            stages.Add(stage.Key, fields);
        }

        return stages;
    }

    private static JsonObject Error(CompactWorkflowError error, string? detail)
    {
        return new()
        {
            ["error"] = CompactWorkflowNames.Error(error, detail)
        };
    }

    // A synthetic host catalog supplies only the selected reference's verified contract to definition validation.
    private sealed class Functions(JsonObject entries) : ICompactWorkflowFunctions
    {
        public bool TryResolve(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
            out CompactWorkflowFunctionContract? function)
        {
            function = null;
            if (entries[reference.Application + "/" + reference.Name] is not JsonObject entry)
            {
                return false;
            }
            JsonObject parameters = entry["inputSchema"]!["properties"]!["parameters"]!.AsObject();
            var inputs = parameters["properties"]!.AsObject().ToDictionary(property => property.Key,
                property => Kind(property.Value!["type"]!.GetValue<string>()), StringComparer.Ordinal);
            var required = parameters["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            var outputs = entry["outputs"]!.AsArray().ToDictionary(node => node!["name"]!.GetValue<string>(),
                node => Kind(node!["type"]?.GetValue<string>() ?? "string"), StringComparer.Ordinal);
            function = new(entry["validation"]?.GetValue<string>()?.StartsWith("demonstrated_once", StringComparison.Ordinal) == true,
                entry["compatibility"] is JsonObject, inputs, required, outputs);

            return true;
        }

        private static CompactWorkflowValueKind Kind(string type)
        {
            CompactWorkflowNames.TryKind(type, out var kind);

            return kind;
        }
    }
}
