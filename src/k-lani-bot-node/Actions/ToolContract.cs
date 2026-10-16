using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Actions;

// Use case: an AI client learns the node's tools from contracts/tools.json (served as the MCP tool list), and the
// node must accept exactly what that file promises. The file is embedded in the executable, so the contract a
// client sees and the one the node enforces cannot come from different versions. Every tool's scope, feature
// and "mutates" flag are read from here by the dispatcher, the guard and the visibility indicator.
//
// Validate implements only the JSON Schema keywords tools.json uses. Any other keyword is ignored, so a new
// keyword in the contract limits nothing until it is implemented here.
public static class ToolContract
{
    private const int MaximumStringCharacters = 1_048_576;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly JsonArray Tools = JsonNode.Parse(Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("KLaniBot.tools.json")!)!["tools"]!.AsArray();

    // Looked up for almost every request and several times within one, so the lookup is a dictionary
    // access and not a scan of the whole contract.
    private static readonly Dictionary<string, JsonObject> ToolsByName = Tools.OfType<JsonObject>().ToDictionary(
        tool => tool["name"]!.ToString());

    public static JsonObject Get(Tool tool)
    {
        return ToolsByName[ToolNames.Of(tool)];
    }

    // Throws ArgumentException for a name that is not in the contract.
    public static JsonObject Get(string name)
    {
        if (ToolsByName.TryGetValue(name, out JsonObject? tool))
        {
            return tool;
        }
        throw new ArgumentException($"Unknown tool: {name}");
    }

    // Throws ArgumentException naming the path of the first value the schema refuses.
    public static void Validate(JsonObject schema, JsonNode? value, string path = "arguments")
    {
        string type = schema["type"]?.ToString() ?? "object";
        if (!HasType(type, value))
        {
            throw new ArgumentException($"{path} must be {type}.");
        }
        if (schema["enum"] is JsonArray allowed && !allowed.Any(v => JsonNode.DeepEquals(v, value)))
        {
            throw new ArgumentException($"Invalid value for {path}.");
        }

        switch (type)
        {
            case "object":
                ValidateObject(schema, (JsonObject)value!, path);
                break;
            case "integer":
                ValidateInteger(schema, value, path);
                break;
            case "string":
                ValidateString(schema, value!.GetValue<string>(), path);
                break;
            case "array":
                ValidateArray(schema, (JsonArray)value!, path);
                break;
        }
    }

    private static bool HasType(string type, JsonNode? value)
    {
        return type switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue s && s.TryGetValue<string>(out _),
            "integer" => TryInteger(value, out _),
            "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
            _ => false
        };
    }

    // Throws ArgumentException for a missing required property, an unknown one where none are allowed, or an invalid value.
    private static void ValidateObject(JsonObject schema, JsonObject value, string path)
    {
        if (schema["required"] is JsonArray required)
        {
            foreach (var key in required)
            {
                if (!value.ContainsKey(key!.ToString()))
                {
                    throw new ArgumentException($"Missing {path}.{key}");
                }
            }
        }

        var props = schema["properties"] as JsonObject;
        foreach (var (key, item) in value)
        {
            if (props?[key] is JsonObject spec)
            {
                Validate(spec, item, path + "." + key);
            }
            else if (schema["additionalProperties"]?.ToString() == "false")
            {
                throw new ArgumentException($"Unknown {path}.{key}");
            }
        }
    }

    // Throws ArgumentException for a number outside minimum and maximum.
    private static void ValidateInteger(JsonObject schema, JsonNode? value, string path)
    {
        TryInteger(value, out long n);

        if (schema["minimum"] is { } min && n < min.GetValue<long>() || schema["maximum"] is { } max && n > max.GetValue<long>())
        {
            throw new ArgumentException($"{path} is outside the supported range.");
        }
    }

    // Throws ArgumentException for a string that is too long, outside its length limits or not matching its pattern.
    private static void ValidateString(JsonObject schema, string text, string path)
    {
        // An upper bound before counting runes and running patterns, so a huge string cannot stall validation.
        if (text.Length > MaximumStringCharacters)
        {
            throw new ArgumentException($"{path} is too long.");
        }

        int length = text.EnumerateRunes().Count();
        if (schema["minLength"] is { } minLength && length < minLength.GetValue<int>()
            || schema["maxLength"] is { } maxLength && length > maxLength.GetValue<int>())
        {
            throw new ArgumentException($"Invalid length for {path}.");
        }
        if (schema["pattern"] is { } pattern
            && !Regex.IsMatch(text, pattern.ToString(), RegexOptions.CultureInvariant, PatternTimeout))
        {
            throw new ArgumentException($"Invalid {path}.");
        }
    }

    // Throws ArgumentException for an array outside its size limits or with an invalid item.
    private static void ValidateArray(JsonObject schema, JsonArray value, string path)
    {
        if (schema["minItems"] is { } min && value.Count < min.GetValue<int>()
            || schema["maxItems"] is { } max && value.Count > max.GetValue<int>())
        {
            throw new ArgumentException($"Invalid size for {path}.");
        }

        if (schema["items"] is JsonObject items)
        {
            foreach (var item in value)
            {
                Validate(items, item, path + "[]");
            }
        }
    }

    private static bool TryInteger(JsonNode? value, out long integer)
    {
        integer = 0;
        if (value is not JsonValue number)
            return false;
        if (number.TryGetValue<long>(out integer))
            return true;
        if (number.TryGetValue<int>(out int i))
        {
            integer = i;
            return true;
        }
        if (number.TryGetValue<uint>(out uint u))
        {
            integer = u;
            return true;
        }
        if (number.TryGetValue<short>(out short s))
        {
            integer = s;
            return true;
        }
        if (number.TryGetValue<byte>(out byte b))
        {
            integer = b;
            return true;
        }
        return false;
    }
}
