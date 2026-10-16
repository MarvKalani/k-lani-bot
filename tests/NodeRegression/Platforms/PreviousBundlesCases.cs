using KLaniBot;
using KLaniBot.Platforms.MacOS;

// The Mac cartridge removes the copies older than the one an update just made; the newest must always stay.
internal static class PreviousBundlesCases
{
    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Previous bundles: " + name);
            }
            passed++;
        }
        string directory = Path.Combine(temporaryRoot, PreviousBundles.DirectoryName);
        string[] names =
        [
            "k-lani-bot-node.app.previous-01a0ff57-bb4e-7685-ac52-f217f8837c10",
            "k-lani-bot-node.app.previous-01a0ffa0-73bd-772f-888d-903d4bca45e2",
            "k-lani-bot-node.app.previous-01a10000-0000-7000-8000-000000000000"
        ];
        foreach (string name in names)
        {
            Directory.CreateDirectory(Path.Combine(directory, name, "Contents"));
        }
        string newest = Path.Combine(directory, names[2]);
        IReadOnlyList<string> superseded = PreviousBundles.Superseded(Directory.EnumerateDirectories(directory), newest);
        Check(superseded.Count == 2 && !superseded.Any(copy => copy.EndsWith(names[2], StringComparison.Ordinal)), "only older copies go");
        PreviousBundles.RemoveSuperseded(directory, newest);
        string[] left = Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().ToArray();
        Check(left.SequenceEqual([names[2]]), "the copy the update just made stays");
        PreviousBundles.RemoveSuperseded(directory, newest);
        Check(Directory.Exists(newest), "running it again keeps the newest copy");
        return passed;
    }
}
