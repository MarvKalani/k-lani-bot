using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Use case: a stored app MCP function fills its argument templates on every run, "{{name}}" with the caller's parameter values
// and {"$step": n, "pointer": "/items/0"} with a value of an earlier step's answer, such as the item id FilmCraft's import
// returned. Saving checks a template with the same rules that binding applies, so a stored template always binds or names why not.
public static class CompactApplicationMcpTemplate
{
    public const string StepKey = "$step";
    public const string PointerKey = "pointer";
    public const int MaximumTextCharacters = 4000;
    private static readonly Regex Placeholder = new(@"\{\{([a-z][a-z0-9._-]{0,79})\}\}", RegexOptions.CultureInvariant);
    private static readonly Regex WholePlaceholder = new(@"\A\{\{([a-z][a-z0-9._-]{0,79})\}\}\z", RegexOptions.CultureInvariant);

    // True when every placeholder names a declared parameter and every reference names a step numbered below stepNumber.
    public static bool IsValid(JsonNode? template, IReadOnlySet<string> parameters, int stepNumber)
    {
        switch (template)
        {
            case JsonObject reference when reference.ContainsKey(StepKey):
                return TryReference(reference, out int step, out _) && step >= 1 && step < stepNumber;
            case JsonObject entries:
                return entries.All(entry => IsValid(entry.Value, parameters, stepNumber));
            case JsonArray items:
                return items.All(item => IsValid(item, parameters, stepNumber));
            case JsonValue value when value.TryGetValue(out string? text):
                return IsValidText(text, parameters);
            default:
                return true;
        }
    }

    // The parameter names a template uses; a function must declare each of them.
    public static IReadOnlySet<string> Names(JsonNode? template)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in Texts(template))
        {
            foreach (Match match in Placeholder.Matches(text))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    // False when a reference names an answer that is absent, null or no JSON; answers[i] is step i + 1 read as JSON.
    public static bool TryBind(JsonNode? template, IReadOnlyDictionary<string, JsonNode> values, IReadOnlyList<JsonNode?> answers,
        out JsonNode? bound)
    {
        bound = null;
        switch (template)
        {
            case JsonObject reference when reference.ContainsKey(StepKey):
                return TryBindReference(reference, answers, out bound);
            case JsonObject entries:
                return TryBindObject(entries, values, answers, out bound);
            case JsonArray items:
                return TryBindArray(items, values, answers, out bound);
            case JsonValue value when value.TryGetValue(out string? text):
                return TryBindText(text, values, out bound);
            default:
                bound = template?.DeepClone();
                return true;
        }
    }

    // RFC 6901 JSON pointer; false for a malformed pointer or a value that is absent.
    public static bool TryResolve(JsonNode? root, string pointer, out JsonNode? value)
    {
        value = root;
        if (pointer.Length == 0)
        {
            return true;
        }
        if (pointer[0] != '/')
        {
            return false;
        }
        foreach (string token in pointer[1..].Split('/'))
        {
            if (!TryDescend(value, token.Replace("~1", "/").Replace("~0", "~"), out value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidPointer(string pointer)
    {
        return pointer.Length == 0 || pointer[0] == '/';
    }

    // Converts the caller's values by the declared types; false for a missing, unknown or mistyped value.
    public static bool TryValues(JsonObject? supplied, IReadOnlyList<CompactApplicationMcpParameter> parameters,
        out IReadOnlyDictionary<string, JsonNode> values)
    {
        var converted = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        values = converted;
        JsonObject given = supplied ?? new JsonObject();
        if (given.Any(entry => parameters.All(parameter => parameter.Name != entry.Key)))
        {
            return false;
        }
        foreach (CompactApplicationMcpParameter parameter in parameters)
        {
            if (given[parameter.Name] is not JsonValue value || !TryConvert(value, parameter.Type, out JsonNode? typed))
            {
                return false;
            }
            converted.Add(parameter.Name, typed!);
        }

        return true;
    }

    // The value is read back from its JSON text first, so a value built in code converts like one parsed from a request.
    private static bool TryConvert(JsonValue given, CompactApplicationMcpParameterType type, out JsonNode? typed)
    {
        typed = null;
        var value = (JsonValue)JsonNode.Parse(given.ToJsonString())!;

        switch (type)
        {
            case CompactApplicationMcpParameterType.String:
                if (!value.TryGetValue(out string? text) || text.Length > MaximumTextCharacters || text.Any(char.IsControl))
                {
                    return false;
                }
                typed = JsonValue.Create(text);
                return true;
            case CompactApplicationMcpParameterType.Integer:
                return TryInteger(value, out typed);
            case CompactApplicationMcpParameterType.Number:
                return TryNumber(value, out typed);
            case CompactApplicationMcpParameterType.Boolean:
                if (!value.TryGetValue(out bool flag))
                {
                    return false;
                }
                typed = JsonValue.Create(flag);
                return true;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static bool TryInteger(JsonValue value, out JsonNode? typed)
    {
        typed = null;
        if (!value.TryGetValue(out double number) || Math.Floor(number) != number || Math.Abs(number) > long.MaxValue)
        {
            return false;
        }
        typed = JsonValue.Create((long)number);

        return true;
    }

    private static bool TryNumber(JsonValue value, out JsonNode? typed)
    {
        typed = null;
        if (!value.TryGetValue(out double number) || !double.IsFinite(number))
        {
            return false;
        }
        typed = JsonValue.Create(number);

        return true;
    }

    // A literal "{{" or "}}" outside a placeholder is refused, so a template never means two things.
    private static bool IsValidText(string text, IReadOnlySet<string> parameters)
    {
        foreach (Match match in Placeholder.Matches(text))
        {
            if (!parameters.Contains(match.Groups[1].Value))
            {
                return false;
            }
        }
        string rest = Placeholder.Replace(text, "");

        return !rest.Contains("{{", StringComparison.Ordinal) && !rest.Contains("}}", StringComparison.Ordinal);
    }

    private static bool TryReference(JsonObject reference, out int step, out string pointer)
    {
        step = 0;
        pointer = "";
        bool shaped = CompactDiscoveryDocumentCodec.Only(reference, StepKey, PointerKey) && reference[StepKey] is JsonValue number
            && number.TryGetValue(out step) && CompactDiscoveryDocumentCodec.Text(reference, PointerKey, out pointer);

        return shaped && ValidPointer(pointer);
    }

    private static bool TryBindReference(JsonObject reference, IReadOnlyList<JsonNode?> answers, out JsonNode? bound)
    {
        bound = null;
        if (!TryReference(reference, out int step, out string pointer) || step < 1 || step > answers.Count)
        {
            return false;
        }
        if (!TryResolve(answers[step - 1], pointer, out JsonNode? value) || value == null)
        {
            return false;
        }
        bound = value.DeepClone();

        return true;
    }

    private static bool TryBindObject(JsonObject entries, IReadOnlyDictionary<string, JsonNode> values, IReadOnlyList<JsonNode?> answers,
        out JsonNode? bound)
    {
        bound = null;
        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> entry in entries)
        {
            if (!TryBind(entry.Value, values, answers, out JsonNode? child))
            {
                return false;
            }
            result[entry.Key] = child;
        }
        bound = result;

        return true;
    }

    private static bool TryBindArray(JsonArray items, IReadOnlyDictionary<string, JsonNode> values, IReadOnlyList<JsonNode?> answers,
        out JsonNode? bound)
    {
        bound = null;
        var result = new JsonArray();
        foreach (JsonNode? item in items)
        {
            if (!TryBind(item, values, answers, out JsonNode? child))
            {
                return false;
            }
            result.Add(child);
        }
        bound = result;

        return true;
    }

    // A whole-string placeholder keeps the value's type; a placeholder inside longer text is replaced by the value's text.
    private static bool TryBindText(string text, IReadOnlyDictionary<string, JsonNode> values, out JsonNode? bound)
    {
        bound = null;
        Match whole = WholePlaceholder.Match(text);
        if (whole.Success)
        {
            if (!values.TryGetValue(whole.Groups[1].Value, out JsonNode? value))
            {
                return false;
            }
            bound = value.DeepClone();
            return true;
        }
        if (Placeholder.Matches(text).Any(match => !values.ContainsKey(match.Groups[1].Value)))
        {
            return false;
        }
        bound = JsonValue.Create(Placeholder.Replace(text, match => Text(values[match.Groups[1].Value])));

        return true;
    }

    private static string Text(JsonNode value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue(out string? text))
        {
            return text;
        }

        return value.ToJsonString();
    }

    private static bool TryDescend(JsonNode? node, string token, out JsonNode? child)
    {
        child = null;
        if (node is JsonObject entries)
        {
            return entries.TryGetPropertyValue(token, out child);
        }
        bool index = node is JsonArray && token.Length > 0 && token.All(char.IsAsciiDigit) && (token == "0" || token[0] != '0');
        if (!index || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int position))
        {
            return false;
        }
        JsonArray items = (JsonArray)node!;
        if (position >= items.Count)
        {
            return false;
        }
        child = items[position];

        return true;
    }

    private static IEnumerable<string> Texts(JsonNode? template)
    {
        switch (template)
        {
            case JsonObject entries:
                return entries.SelectMany(entry => Texts(entry.Value));
            case JsonArray items:
                return items.SelectMany(Texts);
            case JsonValue value when value.TryGetValue(out string? text):
                return [text];
            default:
                return [];
        }
    }
}
