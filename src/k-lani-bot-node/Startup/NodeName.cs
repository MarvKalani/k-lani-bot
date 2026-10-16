using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KLaniBot.Startup;

// Use case: a node name identifies one machine in the controller's registry, at the master and on the indicator. The
// administrator chooses it; every setup path suggests the name of the computer it runs on, so the suggestion is
// the name people already know the machine by. A fixed default would make two machines collide at one master,
// which refuses a second node with the same name.
//
// The pattern is the one pairing, invitations and the registry accept, so a name that passes here is never
// refused later in the setup.
public static partial class NodeName
{
    public const int MaximumLength = 48;

    public static bool IsValid(string name)
    {
        return ValidName().IsMatch(name);
    }

    // Throws ArgumentException for a name pairing would not accept.
    public static string Require(string name)
    {
        if (!IsValid(name))
        {
            throw new ArgumentException(
                "Node name: 1–48 letters, digits, underscores or hyphens, starting with a letter or digit.");
        }
        return name;
    }

    // The suggestion for this computer, or null when its name yields nothing usable; the name is then required.
    public static string? SuggestForThisComputer()
    {
        return SuggestFrom(Environment.MachineName);
    }

    public static string? SuggestFrom(string computerName)
    {
        string hostPart = computerName.Split('.')[0];
        string withoutAccents = RemoveAccents(hostPart).ToLowerInvariant();
        string replaced = InvalidCharacters().Replace(withoutAccents, "-");
        string collapsed = RepeatedHyphens().Replace(replaced, "-").Trim('-', '_');
        if (collapsed.Length > MaximumLength)
        {
            collapsed = collapsed[..MaximumLength].TrimEnd('-', '_');
        }
        return IsValid(collapsed) ? collapsed : null;
    }

    // "Müller-PC" should become "muller-pc", not "m-ller-pc": the base letter is kept and the accent dropped.
    private static string RemoveAccents(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_-]{0,47}$")]
    private static partial Regex ValidName();

    [GeneratedRegex("[^a-z0-9_-]")]
    private static partial Regex InvalidCharacters();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedHyphens();
}
