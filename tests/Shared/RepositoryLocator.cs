namespace KLaniBot;

// Tests that read files of the repository (sources, documents, the Apple bundle) start from the build
// output, so they walk up until they find the repository root.
internal static class RepositoryLocator
{
    public static string Root()
    {
        for (string? directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            bool isRoot = File.Exists(Path.Combine(directory, "global.json"))
                && Directory.Exists(Path.Combine(directory, "src", "k-lani-bot-node"));
            if (isRoot)
            {
                return directory;
            }
        }
        throw new InvalidOperationException("The repository root could not be found from " + AppContext.BaseDirectory);
    }
}
