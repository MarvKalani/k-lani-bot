using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Use case: CompactWindowState turns native labels into declaration identifiers and state paths with these rules.
// They match the JS desktop reference, so one window yields the same declaration and shape in both gateways.
internal static class CompactWindowStateNames
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string[] EllipsisSuffixes = { "...", "…" };
    private static readonly Regex NumberPattern = new(@"\A(?:-?[0-9]+(\.[0-9]+)?)\z", MatchOptions);

    // Trailing ellipses are dropped, because a menu label names the same member with or without them.
    public static string SourceText(string name)
    {
        string text = name.Trim();
        foreach (string suffix in EllipsisSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - suffix.Length).TrimEnd();
            }
        }

        return text;
    }

    // Null means the text yields no identifier: it has no letters or digits, or the identifier would start with a digit.
    public static string? Identifier(string text)
    {
        List<string> words = SplitWords(text);
        if (words.Count == 0)
        {
            return null;
        }

        string identifier = JoinWords(words);
        return StartsWithDigit(identifier) ? null : identifier;
    }

    // Quotes escape only the backslash and the double quote, as the JS reference does.
    public static string Quote(string text)
    {
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    // Keys that are not identifiers are written quoted, so the declaration stays valid for any native label.
    public static string RenderKey(CompactMemberKey key)
    {
        return key.Quoted ? Quote(key.Name) : key.Name;
    }

    // Plain names are joined with a dot and quoted names are bracketed, so every nested state path can be read by its key.
    public static string JoinPath(IReadOnlyList<CompactMemberKey> path, CompactMemberKey key)
    {
        StringBuilder joined = new();
        foreach (CompactMemberKey step in path.Append(key))
        {
            AppendStep(joined, step);
        }

        return joined.ToString();
    }

    // Only plain decimal literals become numbers, so values such as "n/a" or "1e5" stay text.
    public static bool TryNumber(string? value, out JsonValue? number)
    {
        number = null;
        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();
        if (!NumberPattern.IsMatch(trimmed))
        {
            return false;
        }

        double parsed = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
        number = JsonValue.Create(parsed);
        return true;
    }

    private static void AppendStep(StringBuilder joined, CompactMemberKey step)
    {
        if (step.Quoted)
        {
            joined.Append('[').Append(Quote(step.Name)).Append(']');
            return;
        }

        if (joined.Length > 0)
        {
            joined.Append('.');
        }

        joined.Append(step.Name);
    }

    private static List<string> SplitWords(string text)
    {
        List<string> words = new();
        StringBuilder current = new();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    // The first word is lower case and every later word starts upper case with the rest lower case, as in the JS reference.
    private static string JoinWords(List<string> words)
    {
        StringBuilder identifier = new(LowerText(words[0]));
        for (int index = 1; index < words.Count; index++)
        {
            identifier.Append(CapitalizedWord(words[index]));
        }

        return identifier.ToString();
    }

    private static string CapitalizedWord(string word)
    {
        Rune first = word.EnumerateRunes().First();
        string rest = word.Substring(first.Utf16SequenceLength);
        return Rune.ToUpperInvariant(first).ToString() + LowerText(rest);
    }

    private static string LowerText(string text)
    {
        StringBuilder lowered = new();
        foreach (Rune rune in text.EnumerateRunes())
        {
            lowered.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        return lowered.ToString();
    }

    private static bool StartsWithDigit(string identifier)
    {
        Rune first = identifier.EnumerateRunes().First();
        return Rune.GetUnicodeCategory(first) == UnicodeCategory.DecimalDigitNumber;
    }
}
