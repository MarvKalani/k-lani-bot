using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Localization;
using KLaniBot.Visibility;

namespace KLaniBot.Actions;

// The node describes its actual observation locally. Byte counts use UTF-8 JSON/text, excluding network framing,
// TLS and external model calls; display detail never changes what the controller requested or receives.
internal static class StepDiagnosticDescriber
{
    private const int MaximumDetailCharacters = 65536;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string[] SelectorFields =
    [
        "pid", "area", "entry_id", "store_id", "folder_id", "destination_folder_id", "expected_folder_id",
        "offset", "limit", "body_offset", "max_characters", "text_format", "operation", "field", "type_name",
        "id", "snapshot_id", "name", "auto_id", "role", "title"
    ];

    public static StepDiagnostics Describe(Tool tool, JsonObject arguments, JsonObject result, string operationId, long elapsed)
    {
        long argumentBytes = Encoding.UTF8.GetByteCount(arguments.ToJsonString());
        long resultBytes = Encoding.UTF8.GetByteCount(result.ToJsonString());
        string summary = Summary(tool, result);
        string debug = DebugDetails(tool, arguments, result, operationId, elapsed, argumentBytes, resultBytes);
        string verbose = ObservationContent(tool, result);

        return new(summary, Limit(debug), Limit(verbose), argumentBytes, resultBytes, elapsed, DateTimeOffset.Now);
    }

    private static string Summary(Tool tool, JsonObject result)
    {
        if (tool == Tool.InspectApplication && result["mail"] is JsonObject mail)
        {
            string body = Field(mail, "Body");
            string sender = Field(mail, "SenderAddress");
            if (sender.Length == 0)
            {
                sender = Field(mail, "Sender");
            }
            return UiTexts.Format(UiText.StepMailSummary, StepText.Clean(Field(mail, "Subject")), StepText.Clean(sender),
                FormatBytes(Encoding.UTF8.GetByteCount(body)), body.Length, Field(mail, "TextFormat"))
                + "\n" + UiTexts.Format(UiText.StepMailRange, Field(mail, "BodyOffset"), Field(mail, "BodyLength"),
                    UiTexts.Get(mail["Truncated"]?.GetValue<bool>() == true ? UiText.StepPartialContent : UiText.StepCompleteContent));
        }
        if (tool == Tool.InspectApplication && result["messages"] is JsonObject messages)
        {
            int count = (messages["Messages"] as JsonArray)?.Count ?? 0;
            return UiTexts.Format(UiText.StepMailPageSummary, count, Field(messages, "Total"), Field(messages, "NextOffset"));
        }

        return StepResultDescriber.Describe(tool, result);
    }

    private static string DebugDetails(Tool tool, JsonObject arguments, JsonObject result, string operationId,
        long elapsed, long argumentBytes, long resultBytes)
    {
        var selectors = new JsonObject();
        foreach (string name in SelectorFields)
        {
            if (arguments[name] is { } value)
            {
                selectors[name] = value.DeepClone();
            }
        }
        var metadata = new JsonObject
        {
            ["operation_id"] = operationId,
            ["action"] = ToolNames.Of(tool),
            ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            ["arguments"] = selectors,
            ["outcome"] = result["outcome"]?.DeepClone(),
            ["returned_fields"] = new JsonArray(result.Select(field => (JsonNode?)JsonValue.Create(field.Key)).ToArray())
        };
        if (result["mail"] is JsonObject mail)
        {
            metadata["mail"] = MailMetadata(mail);
        }

        return UiTexts.Format(UiText.StepPayloadSizes, FormatBytes(argumentBytes), FormatBytes(resultBytes), elapsed)
            + "\n" + UiTexts.Get(UiText.StepPayloadScope) + "\n" + metadata.ToJsonString(Indented);
    }

    private static JsonObject MailMetadata(JsonObject mail)
    {
        var metadata = mail.DeepClone().AsObject();
        metadata.Remove("Body");
        metadata["ReturnedBodyUtf8Bytes"] = Encoding.UTF8.GetByteCount(Field(mail, "Body"));

        return metadata;
    }

    private static string ObservationContent(Tool tool, JsonObject result)
    {
        if (tool != Tool.InspectApplication)
        {
            return ToolContract.Get(tool)["mutates"]?.GetValue<bool>() == false ? ReadOnlyContent(result) : "";
        }
        if (result["mail"] is JsonObject mail)
        {
            return Field(mail, "Body");
        }
        var payload = new JsonObject();
        foreach (string name in new[] { "info", "status", "api", "messages", "folders", "folder" })
        {
            if (result[name] is { } value)
            {
                payload[name] = value.DeepClone();
            }
        }

        return payload.Count == 0 ? "" : payload.ToJsonString(Indented);
    }

    private static string ReadOnlyContent(JsonObject result)
    {
        var payload = result.DeepClone().AsObject();
        foreach (string name in new[] { "receipt", "transport", "trace_file", "image" })
        {
            payload.Remove(name);
        }

        return payload.ToJsonString(Indented);
    }

    internal static string FormatBytes(long bytes)
    {
        const int BytesPerKilobyte = 1000;

        return UiTexts.Format(UiText.StepBytes, bytes / (double)BytesPerKilobyte, bytes);
    }

    private static string Limit(string content)
    {
        return content.Length <= MaximumDetailCharacters ? content
            : content[..MaximumDetailCharacters] + "\n" + UiTexts.Format(UiText.StepDetailsTruncated, MaximumDetailCharacters);
    }

    private static string Field(JsonObject value, string name)
    {
        return value[name]?.ToString() ?? "";
    }
}
