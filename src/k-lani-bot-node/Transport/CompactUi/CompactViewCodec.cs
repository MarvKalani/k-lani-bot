using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// MCP discovery/observation adapters translate paging options and typed replies without exposing host context keys.
public static class CompactViewCodec
{
    public static bool TryOptions(JsonObject arguments, out CompactViewOptions? options)
    {
        options = null;
        if (arguments == null || !TryLimit(arguments, out int? limit)
            || !TryReference(arguments, "since_view", out string? since)
            || !TryReference(arguments, "cursor", out string? cursor))
        {
            return false;
        }
        bool full = IsTruthy(arguments["full"]);
        if (cursor != null && (since != null || full))
        {
            return false;
        }
        CompactViewReference? baseline = since != null ? new(ParseOpaque(since)) : null;
        CompactViewCursor? continuation = cursor != null ? new(ParseOpaque(cursor)) : null;
        options = new(limit, baseline, continuation, full);

        return true;
    }

    private static bool TryLimit(JsonObject arguments, out int? limit)
    {
        limit = null;
        if (!arguments.ContainsKey("limit"))
        {
            return true;
        }
        if (arguments["limit"] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number) || Math.Truncate(number) != number
            || number < 1 || number > CompactViewStore.MaximumPageSize)
        {
            return false;
        }
        limit = (int)number;

        return true;
    }

    private static bool TryReference(JsonObject arguments, string name, out string? text)
    {
        text = null;
        if (!arguments.ContainsKey(name))
        {
            return true;
        }

        return arguments[name] is JsonValue value && value.TryGetValue(out text) && text != null;
    }

    private static Guid ParseOpaque(string value)
    {
        return Guid.TryParse(value, out Guid reference) ? reference : Guid.Empty;
    }

    public static JsonObject Serialize(CompactViewPage page)
    {
        var result = new JsonObject();
        if (page.Kind == CompactViewKind.Full)
        {
            result["elements"] = Rows(page.Elements);
        }
        else
        {
            result["changed"] = Rows(page.Changed);
            result["removed"] = new JsonArray(page.Removed.Select(reference => (JsonNode?)JsonValue.Create(reference)).ToArray());
            result["visibility"] = new JsonArray(page.Visibility.Select(change => (JsonNode?)new JsonObject
            {
                ["ref"] = change.Reference,
                ["offscreen"] = change.Offscreen
            }).ToArray());
        }
        result["view_ref"] = page.ViewReference.Value.ToString();
        result["context_ref"] = page.ContextReference.ToString();
        result["kind"] = CompactViewNames.Kind(page.Kind);
        result["next_cursor"] = page.NextCursor?.Value.ToString();
        result["full"] = page.Kind == CompactViewKind.Full;
        result["total"] = page.Total;
        result["truncated"] = page.Truncated;
        if (page.BaselineReason != null)
        {
            result["baseline_reason"] = CompactViewNames.Reason(page.BaselineReason.Value);
        }
        if (page.OffscreenOmitted > 0)
        {
            result["offscreen_omitted"] = page.OffscreenOmitted;
            result["hint"] = CompactViewNames.OffscreenHint;
        }

        return result;
    }

    // A request lists offscreen controls only when it asks for them, or when a contains filter must be able to find them.
    public static bool ShowsOffscreen(bool requested, string? contains)
    {
        return requested || !string.IsNullOrEmpty(contains);
    }

    // The references of the rows a page showed: its full elements and its changed rows, which the caller may still address.
    public static IEnumerable<string> ShownReferences(JsonObject page)
    {
        return new[] { "elements", "changed" }.Where(key => page[key] is JsonArray)
            .SelectMany(key => page[key]!.AsArray().OfType<JsonObject>()).Select(row => row["ref"]!.ToString());
    }

    private static JsonArray Rows(IReadOnlyList<CompactViewRow> records)
    {
        return new(records.Select(row => (JsonNode?)row.Attributes.DeepClone()).ToArray());
    }

    private static bool IsTruthy(JsonNode? node)
    {
        if (node == null)
        {
            return false;
        }
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool flag))
            {
                return flag;
            }
            if (value.TryGetValue(out string? text))
            {
                return !string.IsNullOrEmpty(text);
            }
            if (value.TryGetValue(out double number))
            {
                return number != 0;
            }
        }

        return true;
    }
}
