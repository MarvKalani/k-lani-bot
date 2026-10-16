using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compiled draft serialization contains parameter schemas and semantic checks.
// Trusted compatibility is supplied separately at installation.
public static class CompactCaptureWire
{
    // A document without a description stays in its earlier shape, so stores written before descriptions existed read the same.
    public static JsonObject Draft(CompactFunctionDraft draft)
    {
        var document = new JsonObject
        {
            ["name"] = draft.Name,
            ["topic"] = CompactCaptureNames.Topic(draft.Topic),
            ["steps"] = new JsonArray(draft.Steps.Select(step => (JsonNode?)Step(step)).ToArray()),
            ["outputs"] = new JsonArray(draft.Outputs.Select(output => (JsonNode?)Output(output)).ToArray()),
            ["validation"] = Validation(draft.Validation),
            ["inputSchema"] = InputSchema(draft)
        };
        if (draft.Description != null)
        {
            document["description"] = draft.Description;
        }

        return document;
    }

    public static JsonObject Step(CompactRecipeStep step)
    {
        var record = new JsonObject
        {
            ["action"] = CompactCaptureNames.Action(step.Action),
            ["selector"] = CompactDiscoveryWire.Selector(step.Selector),
            ["route"] = CompactCaptureNames.Route(step.Route)
        };
        if (step.Parameter != null)
        {
            record["parameter"] = step.Parameter;
        }
        if (!step.EnabledBeforeFirstAction)
        {
            record["enabledBeforeFirstAction"] = false;
        }

        return record;
    }

    public static JsonObject Output(CompactRecipeOutput output)
    {
        var check = new JsonObject { ["kind"] = CompactCaptureNames.Check(output.Check) };
        if (output.Parameter != null)
        {
            check["parameter"] = output.Parameter;
        }

        return new()
        {
            ["name"] = output.Name,
            ["selector"] = CompactDiscoveryWire.Selector(output.Selector),
            ["field"] = CompactCaptureNames.Field(output.Field),
            ["check"] = check
        };
    }

    public static JsonObject InputSchema(CompactFunctionDraft draft)
    {
        if (draft.DeclaredInputSchema != null)
        {
            return draft.DeclaredInputSchema.DeepClone().AsObject();
        }
        var properties = new JsonObject();
        var required = new JsonArray();
        IEnumerable<string> names = draft.Steps.Where(step => step.Parameter != null)
            .Select(step => step.Parameter!).Distinct(StringComparer.Ordinal);
        foreach (string name in names)
        {
            properties[name] = new JsonObject { ["type"] = "string", ["maxLength"] = CompactCaptureCodec.MaximumTextCharacters };
            required.Add(name);
        }

        return new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pid"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false
                }
            },
            // A function that declares no parameters is called with its pid alone; declared inputs keep the parameters object required.
            ["required"] = required.Count > 0 ? new JsonArray("pid", "parameters") : new JsonArray("pid"),
            ["additionalProperties"] = false
        };
    }

    public static string Validation(CompactCaptureValidation validation)
    {
        return validation switch
        {
            CompactCaptureValidation.DemonstratedMutation => "demonstrated_once; every execution verifies outputs",
            CompactCaptureValidation.ObservedRead => "demonstrated_once; every execution verifies the current source",
            _ => throw new UnreachableException()
        };
    }
}
