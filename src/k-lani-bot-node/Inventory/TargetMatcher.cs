using System.Text.RegularExpressions;

namespace KLaniBot.Inventory;

// How an installed program was recognised as one of the catalogue's targets.
public enum MatchBasis
{
    Identifier,
    Name
}

// An installed program recognised as a target, and on which hint the match rests.
public sealed record TargetMatch(ApplicationTarget Target, ApplicationEvidence Evidence, MatchBasis Basis);

// Use case: decide which installed entry is "Notepad++" or "OrcaSlicer". Installed software is named
// loosely ("Notepad++ (64-bit x64)", "OrcaSlicer 2.4.2", "libreoffice-calc"), and a wrong match would
// tell an administrator that a cartridge applies to a program it was never tried on, so the rules are strict:
// - a technical identifier (package, desktop entry, bundle, executable) must equal a hint exactly;
// - a name must equal a known name, optionally followed only by version and architecture words.
// "Claude" therefore matches "Claude 0.9.1 (x64)" but not "Claude Code".
public static class TargetMatcher
{
    private static readonly Regex NonNameCharacters = new(@"[^a-z0-9+#]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VersionWord = new(@"^v?\d[\w.\-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> ArchitectureWords = new(StringComparer.Ordinal)
    {
        "x64", "x86", "64", "32", "bit", "amd64", "arm64", "aarch64", "win64", "win32", "i386", "x86_64", "msi", "installer"
    };

    private static readonly Dictionary<string, string> NormalizedNames = new(StringComparer.Ordinal);

    public static bool TryMatch(ApplicationTarget target, string osFamily, ApplicationEvidence evidence, out MatchBasis basis)
    {
        TargetDetection detection = target.For(osFamily);
        basis = MatchBasis.Identifier;
        if (MatchesIdentifier(detection, evidence))
        {
            return true;
        }
        basis = MatchBasis.Name;
        return MatchesName(target, detection, evidence.Name);
    }

    public static TargetMatch? Find(ApplicationTarget target, string osFamily, IEnumerable<ApplicationEvidence> installed)
    {
        var candidates = new List<TargetMatch>();
        foreach (var evidence in installed)
        {
            if (TryMatch(target, osFamily, evidence, out MatchBasis basis))
            {
                candidates.Add(new TargetMatch(target, evidence, basis));
            }
        }
        // A listed program with a known version is more useful than the same program without one; an identifier is firmer than a name.
        return candidates
            .OrderBy(match => match.Basis)
            .ThenByDescending(match => ApplicationVersion.Parse(match.Evidence.Version).IsKnown)
            .FirstOrDefault();
    }

    public static bool NameMatches(string known, string candidate)
    {
        string wanted = Normalize(known);
        string given = Normalize(candidate);
        if (wanted.Length == 0 || given.Length == 0)
        {
            return false;
        }
        if (given == wanted)
        {
            return true;
        }
        if (!given.StartsWith(wanted + " ", StringComparison.Ordinal))
        {
            return false;
        }
        return given[(wanted.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(IsVersionOrArchitecture);
    }

    // The same few hundred names are normalized thousands of times in a scan; the cache keeps that cheap.
    public static string Normalize(string name)
    {
        lock (NormalizedNames)
        {
            if (NormalizedNames.TryGetValue(name, out string? cached))
            {
                return cached;
            }
            if (NormalizedNames.Count > 100_000)
            {
                NormalizedNames.Clear();
            }
            string normalized = NonNameCharacters.Replace(name.ToLowerInvariant(), " ").Trim();
            NormalizedNames[name] = normalized;
            return normalized;
        }
    }

    private static bool IsVersionOrArchitecture(string word)
    {
        return VersionWord.IsMatch(word) || ArchitectureWords.Contains(word);
    }

    private static bool MatchesIdentifier(TargetDetection detection, ApplicationEvidence evidence)
    {
        var hints = detection.Executables
            .Concat(detection.Packages)
            .Concat(detection.DesktopIds)
            .Concat(detection.AppNames)
            .Concat(detection.BundleIds);
        return hints.Any(hint => evidence.Identifiers.Contains(hint, StringComparer.OrdinalIgnoreCase));
    }

    private static bool MatchesName(ApplicationTarget target, TargetDetection detection, string installedName)
    {
        return target.KnownNames().Concat(detection.DisplayNames).Any(known => NameMatches(known, installedName));
    }
}
