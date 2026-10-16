using System.Text.RegularExpressions;

namespace KLaniBot.Inventory;

// Use case: an administrator already knows what is on the machine and says it ("Word, Notepad++ 8.6, Claude
// Desktop") instead of letting the scanner read it. The machine is not touched. A trailing version
// ("Notepad++ 8.6") is taken as the installed version.
public static class DeclaredApplications
{
    private static readonly Regex TrailingVersion = new(
        @"^(?<name>.+?)\s+v?(?<version>\d+(?:\.\d+)*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // Throws ArgumentException for no or more than 200 programs, or a name that is too long or contains control characters.
    public static IReadOnlyList<ApplicationEvidence> Parse(string list)
    {
        string[] items = list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0 || items.Length > 200)
        {
            throw new ArgumentException("Name between 1 and 200 programs.");
        }
        var evidence = new List<ApplicationEvidence>();
        foreach (string item in items)
        {
            if (item.Length > 128 || item.Any(char.IsControl))
            {
                throw new ArgumentException("A program name is too long or contains control characters.");
            }
            Match versioned = TrailingVersion.Match(item);
            string name = versioned.Success ? versioned.Groups["name"].Value : item;
            string version = versioned.Success ? versioned.Groups["version"].Value : "";
            evidence.Add(new ApplicationEvidence(name, version, "", "declared", []));
        }
        return evidence;
    }
}
