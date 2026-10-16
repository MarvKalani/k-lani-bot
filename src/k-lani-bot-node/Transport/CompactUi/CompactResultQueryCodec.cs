using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The MCP boundary validates paging shape before converting opaque wire identifiers and integer codepoint offsets.
public static class CompactResultQueryCodec
{
    private static readonly HashSet<string> AllowedFields = ["evidence_ref", "section", "offset", "limit"];

    public static bool TryParse(JsonObject arguments, out CompactResultAttempt<CompactResultPageQuery> attempt)
    {
        attempt = new(null, CompactResultError.InvalidPageArguments);
        if (arguments == null || arguments.Any(field => !AllowedFields.Contains(field.Key))
            || !TryString(arguments["evidence_ref"], out string? reference)
            || !TryString(arguments["section"], out string? section)
            || !TryInteger(arguments, "offset", 0, out int offset)
            || !TryInteger(arguments, "limit", CompactResultStore.DefaultPageSize, out int limit)
            || offset < 0 || limit < 1 || limit > CompactResultStore.MaximumPageSize)
        {
            return false;
        }
        CompactEvidenceReference? identifier = Guid.TryParse(reference, out Guid value) ? new(value) : null;
        attempt = new(new(identifier, CompactResultSection.Parse(section!), offset, limit), null);

        return true;
    }

    private static bool TryString(JsonNode? value, out string? text)
    {
        text = null;

        return value is JsonValue scalar && scalar.TryGetValue(out text) && text != null;
    }

    private static bool TryInteger(JsonObject arguments, string name, int defaultValue, out int value)
    {
        value = defaultValue;
        if (!arguments.ContainsKey(name))
        {
            return true;
        }

        if (arguments[name] is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number) || Math.Truncate(number) != number)
        {
            return false;
        }
        value = number > int.MaxValue ? int.MaxValue : number < int.MinValue ? int.MinValue : (int)number;

        return true;
    }
}
