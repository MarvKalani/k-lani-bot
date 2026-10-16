using System.Reflection;
using System.Text.Json.Serialization;

namespace KLaniBot.Common;

// Use case: an enum whose values are words in a file or a protocol (policy.json, contracts/tools.json) carries each word
// as JsonStringEnumMemberName. Code that reads or writes a single word uses this table, so it agrees with the JSON
// converter and the word is written down once.
public static class WireNames<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> NameByValue = Enum.GetValues<TEnum>().ToDictionary(value => value, ReadName);

    private static readonly Dictionary<string, TEnum> ValueByName = NameByValue.ToDictionary(entry => entry.Value, entry => entry.Key);

    public static IReadOnlyCollection<string> All => ValueByName.Keys;

    // Throws KeyNotFoundException for a number that is not a member of the enum.
    public static string Of(TEnum value)
    {
        return NameByValue[value];
    }

    public static bool TryParse(string? name, out TEnum value)
    {
        if (name == null)
        {
            value = default;
            return false;
        }

        return ValueByName.TryGetValue(name, out value);
    }

    private static string ReadName(TEnum value)
    {
        string memberName = value.ToString();
        FieldInfo? member = typeof(TEnum).GetField(memberName);
        JsonStringEnumMemberNameAttribute? wireName = member?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>();
        return wireName?.Name ?? memberName;
    }
}
