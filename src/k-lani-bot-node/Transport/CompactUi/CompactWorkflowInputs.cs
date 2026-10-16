using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Typed workflow inputs reject unbounded lengths, nonfinite numbers and ambiguous finite-choice contracts before binding.
public static class CompactWorkflowInputs
{
    public const int MaximumChoices = 100;
    public const int MaximumText = 16000;
    private static readonly Regex DocumentReference = new(@"\A[a-zA-Z0-9-]{16,80}\z", RegexOptions.CultureInvariant);

    public static bool TrySchema(JsonNode? node, out CompactWorkflowAttempt<CompactWorkflowInput> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject schema
            || !CompactDiscoveryDocumentCodec.Only(schema, "type", "required", "maxLength", "minimum", "maximum", "values"))
        {
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(schema, "type", out string type) || !CompactWorkflowNames.TryKind(type, out var kind)
            || !TryRequired(schema, out bool required))
        {
            attempt = new(null, CompactWorkflowError.InvalidValueType);
            return false;
        }
        if (!TryChoices(schema, kind, out var choices))
        {
            attempt = new(null, CompactWorkflowError.InvalidEnum);
            return false;
        }
        if (!TryLength(schema, out int length))
        {
            attempt = new(null, CompactWorkflowError.InvalidLength);
            return false;
        }
        if (!TryBound(schema, "minimum", out double? minimum) || !TryBound(schema, "maximum", out double? maximum))
        {
            attempt = new(null, CompactWorkflowError.InvalidNumericBound);
            return false;
        }
        if (minimum != null && maximum != null && minimum > maximum)
        {
            attempt = new(null, CompactWorkflowError.InvalidNumericRange);
            return false;
        }
        attempt = new(new(kind, required, length, minimum, maximum, choices));

        return true;
    }

    private static bool TryRequired(JsonObject schema, out bool required)
    {
        required = true;

        return !schema.ContainsKey("required") || schema["required"] is JsonValue flag && flag.TryGetValue(out required);
    }

    private static bool TryChoices(JsonObject schema, CompactWorkflowValueKind kind, out IReadOnlyList<string>? choices)
    {
        choices = null;
        if (kind != CompactWorkflowValueKind.Enum)
        {
            return true;
        }
        if (schema["values"] is not JsonArray array || array.Count is < 1 or > MaximumChoices
            || array.Any(value => value is not JsonValue text || !text.TryGetValue<string>(out _)))
        {
            return false;
        }
        string[] values = array.Select(value => value!.GetValue<string>()).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            return false;
        }
        choices = values;

        return true;
    }

    private static bool TryLength(JsonObject schema, out int length)
    {
        length = MaximumText;
        if (!schema.ContainsKey("maxLength"))
        {
            return true;
        }
        if (!TryNumber(schema["maxLength"], out double value) || Math.Truncate(value) != value || value < 1 || value > MaximumText)
        {
            return false;
        }
        length = (int)value;

        return true;
    }

    private static bool TryBound(JsonObject schema, string name, out double? bound)
    {
        bound = null;
        if (!schema.ContainsKey(name))
        {
            return true;
        }
        if (!TryNumber(schema[name], out double value))
        {
            return false;
        }
        bound = value;

        return true;
    }

    public static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;

        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    public static bool TryValue(JsonNode? node, CompactWorkflowInput schema, out CompactWorkflowAttempt<CompactWorkflowValue> attempt)
    {
        attempt = new(null, CompactWorkflowError.ParameterTypeMismatch);
        switch (schema.Kind)
        {
            case CompactWorkflowValueKind.String:
            case CompactWorkflowValueKind.Enum:
                return TryText(node, schema, out attempt);
            case CompactWorkflowValueKind.Number:
                if (TryNumber(node, out double number) && (schema.Minimum == null || number >= schema.Minimum)
                    && (schema.Maximum == null || number <= schema.Maximum))
                {
                    attempt = new(new(schema.Kind, Number: number));
                    return true;
                }
                return false;
            case CompactWorkflowValueKind.Boolean:
                if (node is JsonValue flag && flag.TryGetValue(out bool boolean))
                {
                    attempt = new(new(schema.Kind, Boolean: boolean));
                    return true;
                }
                return false;
            case CompactWorkflowValueKind.Document:
                return TryDocument(node, out attempt);
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static bool TryText(JsonNode? node, CompactWorkflowInput schema, out CompactWorkflowAttempt<CompactWorkflowValue> attempt)
    {
        attempt = new(null, CompactWorkflowError.ParameterTypeMismatch);
        if (node is not JsonValue value || !value.TryGetValue(out string? text) || text == null
            || schema.Kind == CompactWorkflowValueKind.String && text.Length > schema.MaximumLength
            || schema.Kind == CompactWorkflowValueKind.Enum && schema.Choices?.Contains(text, StringComparer.Ordinal) != true)
        {
            return false;
        }
        attempt = new(new(schema.Kind, Text: text));

        return true;
    }

    private static bool TryDocument(JsonNode? node, out CompactWorkflowAttempt<CompactWorkflowValue> attempt)
    {
        attempt = new(null, CompactWorkflowError.UnsupportedFields);
        if (node is not JsonObject record || !CompactDiscoveryDocumentCodec.Only(record, "ref", "sha256", "origin"))
        {
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(record, "ref", out string reference) || !DocumentReference.IsMatch(reference)
            || !CompactDiscoveryDocumentCodec.Text(record, "sha256", out string digest)
            || !CompactContractHash.TryDigest(digest, out var sha)
            || !CompactDiscoveryDocumentCodec.Text(record, "origin", out string origin) || !CompactDiscoveryCodec.ValidName(origin))
        {
            attempt = new(null, CompactWorkflowError.InvalidDocument);
            return false;
        }
        attempt = new(new(CompactWorkflowValueKind.Document, Document: new(reference, sha!, origin)));

        return true;
    }
}
