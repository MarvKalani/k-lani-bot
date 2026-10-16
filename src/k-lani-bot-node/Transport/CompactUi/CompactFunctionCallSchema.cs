using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Reusable functions preserve their approved parameter schema while accepting the existing optional result projection at the MCP boundary.
public static class CompactFunctionCallSchema
{
    public static JsonObject Create(CompactFunctionDraft function)
    {
        var schema = CompactCaptureWire.InputSchema(function);
        var properties = schema["properties"]!.AsObject();
        properties["result_mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("summary", "values", "debug") };
        properties["fields"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = CompactResultOptionsCodec.MaximumFields,
            ["items"] = new JsonObject { ["type"] = "string" }
        };

        return schema;
    }

    // A call may name the window whose kind the function binds; the advertised input schema leaves it out.
    public static JsonObject Invocation(CompactFunctionDraft function)
    {
        var schema = Create(function);
        schema["properties"]!.AsObject()["window"] = new JsonObject { ["type"] = "integer" };

        return schema;
    }
}
