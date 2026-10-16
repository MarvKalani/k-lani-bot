using System.Text.RegularExpressions;

namespace KLaniBot.Inventory;

// How an installed version relates to the version a cartridge was written and tested for.
public enum VersionRelation
{
    Unknown,
    Same,

    // Same major and minor, a different patch level: the user interface is normally identical.
    SameSeries,

    // Same major, a different minor: menus and dialogs may have moved.
    SameMajor,

    DifferentMajor
}

// Whether the installed version is older or newer than the one a cartridge was tested on.
public enum VersionDirection
{
    Unknown,
    Equal,
    InstalledIsOlder,
    InstalledIsNewer
}

// How two versions relate and in which direction they differ.
public sealed record VersionComparison(VersionRelation Relation, VersionDirection Direction);

// Use case: the scanner has to say whether the installed build of a program is the one a cartridge was
// tested on, or a neighbour of it ("is 2.4.1 close enough to try the 2.4.2 cartridge?"). Version texts
// differ wildly between sources (Windows file versions, Debian package versions, bundle versions), so
// only the leading dotted number is compared.
public sealed record ApplicationVersion(string Raw, int[] Components)
{
    private static readonly Regex DottedNumber = new(@"\d+(?:\.\d+)*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DebianEpoch = new(@"^\d+:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public bool IsKnown => Components.Length > 0;

    public static ApplicationVersion Parse(string? raw)
    {
        string text = (raw ?? "").Trim();
        // "1:7.5.1-0ubuntu2" is epoch 1, version 7.5.1, packaging revision 0ubuntu2: only 7.5.1 belongs to the program.
        string withoutEpoch = DebianEpoch.Replace(text, "");
        Match first = DottedNumber.Match(withoutEpoch);
        if (!first.Success)
        {
            return new ApplicationVersion(text, []);
        }
        var components = new List<int>();
        foreach (string part in first.Value.Split('.'))
        {
            // A component too large for an int (a build timestamp) cannot be compared; stop there.
            if (!int.TryParse(part, out int value))
            {
                break;
            }
            components.Add(value);
        }
        return new ApplicationVersion(text, components.ToArray());
    }

    public VersionComparison CompareTo(ApplicationVersion cartridgeVersion)
    {
        if (!IsKnown || !cartridgeVersion.IsKnown)
        {
            return new VersionComparison(VersionRelation.Unknown, VersionDirection.Unknown);
        }
        int length = Math.Max(Components.Length, cartridgeVersion.Components.Length);
        int[] installed = Pad(Components, length);
        int[] target = Pad(cartridgeVersion.Components, length);
        return new VersionComparison(RelationOf(installed, target), DirectionOf(installed, target));
    }

    public override string ToString()
    {
        return Raw;
    }

    private static int[] Pad(int[] components, int length)
    {
        var padded = new int[length];
        Array.Copy(components, padded, components.Length);
        return padded;
    }

    private static VersionRelation RelationOf(int[] installed, int[] target)
    {
        if (installed.SequenceEqual(target))
        {
            return VersionRelation.Same;
        }
        bool sameMajor = installed[0] == target[0];
        bool sameMinor = installed.Length < 2 || installed[1] == target[1];
        if (sameMajor && sameMinor)
        {
            return VersionRelation.SameSeries;
        }
        return sameMajor ? VersionRelation.SameMajor : VersionRelation.DifferentMajor;
    }

    private static VersionDirection DirectionOf(int[] installed, int[] target)
    {
        for (int index = 0; index < installed.Length; index++)
        {
            if (installed[index] != target[index])
            {
                return installed[index] < target[index] ? VersionDirection.InstalledIsOlder : VersionDirection.InstalledIsNewer;
            }
        }
        return VersionDirection.Equal;
    }
}
