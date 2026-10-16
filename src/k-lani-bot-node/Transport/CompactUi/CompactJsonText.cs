using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Private result details use literal Unicode and compact JSON string escaping like the JS reference, before codepoint paging.
public static class CompactJsonText
{
    public static string Serialize(JsonNode? value)
    {
        if (value == null)
        {
            return "null";
        }
        if (value is JsonObject record)
        {
            return "{" + string.Join(',', record.Select(field => Quote(field.Key) + ":" + Serialize(field.Value))) + "}";
        }
        if (value is JsonArray values)
        {
            return "[" + string.Join(',', values.Select(Serialize)) + "]";
        }
        if (value.GetValueKind() == JsonValueKind.String)
        {
            return Quote(value.GetValue<string>());
        }
        if (value.GetValueKind() == JsonValueKind.Number)
        {
            return Number(value.ToJsonString());
        }

        return value.ToJsonString();
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            string? escape = character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => null
            };
            if (escape != null)
            {
                result.Append(escape);
            }
            else if (character < ' ' || char.IsSurrogate(character) && !HasPair(text, index))
            {
                result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(character);
            }
        }
        result.Append('"');

        return result.ToString();
    }

    private static bool HasPair(string text, int index)
    {
        return char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            || char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]);
    }

    private static string Number(string token)
    {
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
        if (!double.IsFinite(value))
        {
            return "null";
        }
        if (value == 0)
        {
            return "0";
        }
        string text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        string[] exponentParts = text.Split('e');
        int exponent = exponentParts.Length == 2 ? int.Parse(exponentParts[1], CultureInfo.InvariantCulture) : 0;
        int point = exponentParts[0].IndexOf('.');
        string digits = exponentParts[0].Replace(".", "", StringComparison.Ordinal);
        int position = (point < 0 ? digits.Length : point) + exponent;
        string sign = value < 0 ? "-" : "";
        if (Math.Abs(value) >= 0.000001 && Math.Abs(value) < 1e21)
        {
            return sign + FixedNumber(digits, position);
        }
        string fraction = digits.Length > 1 ? "." + digits[1..] : "";
        int scientificExponent = position - 1;

        return sign + digits[0] + fraction + "e" + (scientificExponent >= 0 ? "+" : "")
            + scientificExponent.ToString(CultureInfo.InvariantCulture);
    }

    private static string FixedNumber(string digits, int position)
    {
        if (position <= 0)
        {
            return "0." + new string('0', -position) + digits;
        }
        if (position >= digits.Length)
        {
            return digits + new string('0', position - digits.Length);
        }

        return digits[..position] + "." + digits[position..];
    }
}
