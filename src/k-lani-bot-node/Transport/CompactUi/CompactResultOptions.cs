using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// A selected function converts optional wire projection settings before any input can be sent.
public sealed record CompactResultOptions(CompactResultMode Mode, IReadOnlyList<string>? Fields);

// Result projection validation distinguishes omitted settings from invalid explicit null or unknown values.
public static class CompactResultOptionsCodec
{
    public const int MaximumFields = 20;

    public static bool TryParse(JsonObject arguments, IReadOnlySet<string>? outputNames,
        out CompactResultOptions? options, out string? error)
    {
        options = null;
        error = "Invalid result_mode.";
        if (arguments == null || !TryMode(arguments, out CompactResultMode mode))
        {
            return false;
        }
        error = "Invalid result fields.";
        if (!TryFields(arguments, mode, outputNames, out IReadOnlyList<string>? fields))
        {
            return false;
        }
        options = new(mode, fields);
        error = null;

        return true;
    }

    private static bool TryMode(JsonObject arguments, out CompactResultMode mode)
    {
        mode = CompactResultMode.Legacy;
        if (!arguments.ContainsKey("result_mode"))
        {
            return true;
        }
        if (arguments["result_mode"] is not JsonValue value || !value.TryGetValue(out string? text))
        {
            return false;
        }
        switch (text)
        {
            case "summary":
                mode = CompactResultMode.Summary;
                return true;
            case "values":
                mode = CompactResultMode.Values;
                return true;
            case "debug":
                mode = CompactResultMode.Debug;
                return true;
            default:
                return false;
        }
    }

    private static bool TryFields(JsonObject arguments, CompactResultMode mode, IReadOnlySet<string>? outputNames,
        out IReadOnlyList<string>? fields)
    {
        fields = null;
        if (!arguments.ContainsKey("fields"))
        {
            return true;
        }
        if (mode != CompactResultMode.Values || outputNames == null || arguments["fields"] is not JsonArray names
            || names.Count > MaximumFields)
        {
            return false;
        }
        var selected = new List<string>();
        foreach (JsonNode? item in names)
        {
            if (item is not JsonValue value || !value.TryGetValue(out string? name) || name == null
                || !outputNames.Contains(name) || selected.Contains(name, StringComparer.Ordinal))
            {
                return false;
            }
            selected.Add(name);
        }
        fields = selected;

        return true;
    }
}
