using KLaniBot.Cartridges;

namespace KLaniBot.Platforms.MacOS;

// The macOS cartridge keeps the bundle an update replaced, so a failed update can be undone by hand. Only the most
// recent one is useful for that; every update added another copy of about 50 MB and none was ever removed.
public static class PreviousBundles
{
    public const string DirectoryName = "previous-bundles";

    // Every kept copy except the one the current update just made.
    public static IReadOnlyList<string> Superseded(IEnumerable<string> keptCopies, string newest)
    {
        return keptCopies
            .Where(copy => !string.Equals(Path.GetFileName(copy), Path.GetFileName(newest), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static void RemoveSuperseded(string directory, string newest)
    {
        foreach (string copy in Superseded(Directory.EnumerateDirectories(directory), newest))
        {
            CartridgeManifest.RejectLinks(copy);
            Directory.Delete(copy, true);
        }
    }
}
