using System.Text;
using System.Text.Json.Nodes;

namespace KLaniBot.Actions.ApplicationMcp;

// Use case: what a program's MCP server returns is data from a third party and may be large: FilmCraft's command list has
// hundreds of entries and a rendered frame is a megabyte of image data. The agent gets bounded text pages, images only when
// it asks for them, and only the content of the answer, never other fields the program adds.
internal static class ApplicationMcpAnswers
{
    public const int DefaultCharacters = 4000;
    private const int DescriptionCharacters = 240;
    private const int SchemaCharacters = 16000;
    private const int MaximumImages = 2;
    private const int MaximumImageCharacters = 3_000_000;

    public static JsonObject Listing(RunningProgram program)
    {
        var tools = new JsonArray();
        foreach (JsonObject tool in program.Tools.OfType<JsonObject>())
        {
            string name = tool["name"]!.ToString();
            tools.Add(new JsonObject
            {
                ["name"] = name,
                ["read"] = program.Settings.IsReadTool(name),
                ["description"] = Shorten(tool["description"]?.ToString() ?? "", DescriptionCharacters)
            });
        }
        return new JsonObject
        {
            ["success"] = true,
            ["program"] = program.Settings.Id,
            ["version"] = program.Version,
            ["tools"] = tools,
            ["next"] = "Load one tool's schema with app_mcp_read {program, schema}; read tools run through app_mcp_read, all others "
                + "through app_mcp_call."
        };
    }

    // Throws ArgumentException when the program lists no such tool.
    public static JsonObject Schema(RunningProgram program, string toolName)
    {
        JsonObject tool = program.FindTool(toolName) ?? throw new ArgumentException("The program lists no tool '" + toolName + "'.");
        string schema = (tool["inputSchema"] ?? new JsonObject()).ToJsonString();
        bool complete = schema.Length <= SchemaCharacters;

        return new JsonObject
        {
            ["success"] = true,
            ["program"] = program.Settings.Id,
            ["tool"] = toolName,
            ["read"] = program.Settings.IsReadTool(toolName),
            ["inputSchema"] = complete ? JsonNode.Parse(schema) : Shorten(schema, SchemaCharacters),
            ["complete"] = complete
        };
    }

    public static JsonObject ToolResult(string programId, string toolName, JsonObject result, AnswerWindow window)
    {
        var text = new StringBuilder();
        var images = new JsonArray();
        int omittedImages = 0;
        foreach (JsonObject part in (result["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? type = part["type"]?.ToString();
            if (type == "text")
                text.Append(text.Length == 0 ? "" : "\n").Append(part["text"]?.ToString());
            else if (type == "image" && window.Images && images.Count < MaximumImages
                && (part["data"]?.ToString().Length ?? 0) <= MaximumImageCharacters)
                images.Add(new JsonObject { ["mimeType"] = part["mimeType"]?.DeepClone(), ["data"] = part["data"]?.DeepClone() });
            else if (type == "image")
                omittedImages++;
        }
        return Page(programId, toolName, result["isError"]?.GetValue<bool>() != true, text.ToString(), window, images, omittedImages);
    }

    public static JsonObject Failed(ApplicationMcpException failure)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["code"] = Code(failure.Failure),
            ["error"] = failure.Message
        };
    }

    private static JsonObject Page(string programId, string toolName, bool succeeded, string text, AnswerWindow window,
        JsonArray images, int omittedImages)
    {
        int start = Math.Min(window.Offset, text.Length);
        int length = Math.Min(window.Characters, text.Length - start);
        var answer = new JsonObject
        {
            ["success"] = succeeded,
            ["program"] = programId,
            ["tool"] = toolName,
            ["text"] = text.Substring(start, length),
            ["total_chars"] = text.Length
        };
        if (start + length < text.Length)
            answer["next_offset"] = start + length;
        if (images.Count > 0)
            answer["images"] = images;
        if (omittedImages > 0)
            answer["images_omitted"] = omittedImages;
        return answer;
    }

    private static string Code(ApplicationMcpFailure failure)
    {
        return failure switch
        {
            ApplicationMcpFailure.StartFailed => "program_start_failed",
            ApplicationMcpFailure.Stopped => "program_stopped",
            ApplicationMcpFailure.TimedOut => "program_timed_out",
            ApplicationMcpFailure.ProtocolError => "program_protocol_error",
            ApplicationMcpFailure.ToolError => "program_refused",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static string Shorten(string text, int length)
    {
        return text.Length <= length ? text : text[..length] + " …";
    }
}

// Which part of a long answer the agent wants: a character window of the text and whether images are included.
internal sealed record AnswerWindow(int Offset, int Characters, bool Images);
