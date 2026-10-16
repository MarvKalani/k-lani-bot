using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The compact gateway parses its finite form operations before refreshing or sending any native input.
public enum CompactFormCommandKind
{
    Read,
    Options,
    Set,
    Validate
}

// A validated form command carries typed references and exactly one value family, with strings only from the MCP boundary.
public sealed record CompactFormCommand(CompactFormCommandKind Kind, int? ProcessId, string? Scope,
    IReadOnlyList<string>? Fields, CompactFormReference? Form, string? Context, CompactFormFieldReference? Field,
    CompactFormPageOptions Page, CompactFormValue? Value);

// Form argument validation preserves the JS contract's exact refusals before the node may perform an opening or write.
public static class CompactFormCodec
{
    private const int MaximumReferenceLength = 256;
    private const int MaximumTextLength = 16000;
    private const int MaximumRequestedFields = 20;

    public static bool TryRead(string function, JsonNode? arguments, out CompactFormAttempt<CompactFormCommand> attempt)
    {
        if (!TryKind(function, out CompactFormCommandKind kind) || arguments is not JsonObject document
            || document.Any(entry => !Allowed(kind).Contains(entry.Key, StringComparer.Ordinal))
            || Required(kind).Any(name => !HasRequiredType(document[name], name)))
        {
            attempt = Invalid("Invalid form arguments.");
            return false;
        }
        foreach (string name in Allowed(kind).Where(document.ContainsKey))
        {
            if (!ValidProperty(name, document[name]))
            {
                attempt = Invalid("Invalid form argument: " + name);
                return false;
            }
        }
        if (kind == CompactFormCommandKind.Set && !HasOneValue(document))
        {
            attempt = Invalid("Select exactly one typed form value.");
            return false;
        }
        attempt = new(Command(kind, document), null);

        return true;
    }

    private static CompactFormAttempt<CompactFormCommand> Invalid(string message)
    {
        return new(null, CompactFormError.InvalidArguments, message);
    }

    private static bool TryKind(string function, out CompactFormCommandKind kind)
    {
        kind = CompactFormCommandKind.Read;
        if (function == "form.read")
        {
            return true;
        }
        if (function == "form.options")
        {
            kind = CompactFormCommandKind.Options;
            return true;
        }
        if (function == "form.set")
        {
            kind = CompactFormCommandKind.Set;
            return true;
        }
        if (function == "form.validate")
        {
            kind = CompactFormCommandKind.Validate;
            return true;
        }

        return false;
    }

    private static string[] Allowed(CompactFormCommandKind kind)
    {
        return kind switch
        {
            CompactFormCommandKind.Read => ["pid", "scope", "fields", "cursor", "limit"],
            CompactFormCommandKind.Options => ["form_ref", "context_rev", "field_ref", "cursor", "limit"],
            CompactFormCommandKind.Set => ["form_ref", "context_rev", "field_ref", "value", "option_id", "options_rev"],
            CompactFormCommandKind.Validate => ["form_ref", "context_rev"],
            _ => throw new UnreachableException()
        };
    }

    private static string[] Required(CompactFormCommandKind kind)
    {
        return kind switch
        {
            CompactFormCommandKind.Read => ["pid", "scope"],
            CompactFormCommandKind.Options => ["form_ref", "field_ref", "context_rev"],
            CompactFormCommandKind.Set => ["form_ref", "field_ref", "context_rev"],
            CompactFormCommandKind.Validate => ["form_ref", "context_rev"],
            _ => throw new UnreachableException()
        };
    }

    private static bool HasRequiredType(JsonNode? node, string name)
    {
        return node is JsonValue value && (name == "pid" ? value.GetValueKind() == JsonValueKind.Number
            : value.TryGetValue<string>(out _));
    }

    private static bool ValidProperty(string name, JsonNode? node)
    {
        if (name is "pid" or "limit")
        {
            return node is JsonValue number && number.GetValueKind() == JsonValueKind.Number
                && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) && Math.Truncate(value) == value && value >= 1
                && value <= (name == "pid" ? int.MaxValue : CompactFormPager.MaximumPageSize);
        }
        if (name == "fields")
        {
            return node is JsonArray fields && fields.Count <= MaximumRequestedFields
                && fields.All(field => field is JsonValue text && text.TryGetValue<string>(out _))
                && fields.Select(field => field!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == fields.Count;
        }

        return node is JsonValue literal && literal.TryGetValue(out string? content) && content != null
            && content.Length <= (name == "value" ? MaximumTextLength : MaximumReferenceLength);
    }

    private static bool HasOneValue(JsonObject arguments)
    {
        bool text = arguments.ContainsKey("value");
        bool option = arguments.ContainsKey("option_id");

        return text != option && (!option || arguments["options_rev"] is JsonValue)
            && (!text || !arguments.ContainsKey("options_rev"));
    }

    private static CompactFormCommand Command(CompactFormCommandKind kind, JsonObject arguments)
    {
        string? form = Text(arguments, "form_ref");
        string? field = Text(arguments, "field_ref");
        string? cursor = Text(arguments, "cursor");
        string[]? requested = arguments["fields"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        CompactFormValue? value = kind != CompactFormCommandKind.Set ? null : arguments.ContainsKey("value")
            ? new(CompactFormValueKind.Text, Text: Text(arguments, "value"))
            : new(CompactFormValueKind.Option, OptionId: Text(arguments, "option_id"), OptionsRevision: Text(arguments, "options_rev"));
        int? processId = Number(arguments, "pid");
        var page = new CompactFormPageOptions(cursor == null ? null : new(ParseOpaque(cursor)), Number(arguments, "limit"));

        return new(kind, processId, Text(arguments, "scope"), requested, form == null ? null : new(ParseOpaque(form)),
            Text(arguments, "context_rev"), field == null ? null : new(ParseOpaque(field)), page, value);
    }

    private static string? Text(JsonObject arguments, string name)
    {
        return arguments[name]?.GetValue<string>();
    }

    private static int? Number(JsonObject arguments, string name)
    {
        return arguments[name] is JsonValue value ? (int)double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) : null;
    }

    private static Guid ParseOpaque(string value)
    {
        return Guid.TryParseExact(value, "D", out Guid parsed) && parsed.ToString() == value ? parsed : Guid.Empty;
    }
}
