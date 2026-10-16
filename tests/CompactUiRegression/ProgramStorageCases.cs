using KLaniBot.Transport.CompactUi;

// Storage naming is checked against path injection and filesystems that fold case or Unicode normalization.
internal static class ProgramStorageCases
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "k-lani-program-storage-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            RootSeparation(directory);
            IdentityNames(directory);
            ExistingLinks(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RootSeparation(string directory)
    {
        string library = Path.Combine(directory, "library");
        string learning = Path.Combine(directory, "private");
        Require(CompactProgramStorage.TryRoots(library, learning, out var valid), "separate roots");
        Require(!Directory.Exists(library) && !Directory.Exists(learning), "root validation creates no state");
        Require(!CompactProgramStorage.TryRoots(library, library, out var same)
            && same.Error == CompactStorageError.OverlappingRoots, "equal roots");
        Require(!CompactProgramStorage.TryRoots(library, Path.Combine(library, "private"), out _), "private root within exports");
        Require(!CompactProgramStorage.TryRoots(Path.Combine(learning, "exports"), learning, out _), "exports within private root");
        Require(!CompactProgramStorage.TryRoots("relative", learning, out _), "relative library root");
        Require(!CompactProgramStorage.TryRoots(library, "relative", out _), "relative private root");
        Require(!CompactProgramStorage.TryRoots(Path.GetPathRoot(directory)!, null, out _), "whole filesystem root");
        Require(CompactProgramStorage.TryRoots(library, null, out var withoutLearning)
            && withoutLearning.Value!.Learning == null, "reading without private learning root");
        Require(!CompactProgramStorage.TryDirectories(valid.Value!, new("app", "unknown"), out var unknown)
            && unknown.Error == CompactStorageError.UnknownApplication, "unknown build has no persistent directory");
        string file = Path.Combine(directory, "existing-file");
        File.WriteAllText(file, "synthetic");
        Require(!CompactProgramStorage.TryRoots(file, null, out _), "file cannot become a compact library root");
    }

    private static void IdentityNames(string directory)
    {
        CompactProgramStorage.TryRoots(Path.Combine(directory, "library"), Path.Combine(directory, "private"), out var parsed);
        CompactStorageRoots roots = parsed.Value!;
        string[] keys = ["/", "..", "CON", "app", "App", "é", "e\u0301", "ﬀ", "ff", "😀", "\uD800", "\uFFFD"];
        string[] builds = ["../1", "1/2", "..", "A", "a", "é", "e\u0301"];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            foreach (string build in builds)
            {
                Require(CompactProgramStorage.TryDirectories(roots, new(key, build), out var resolved), "raw identity naming");
                CompactProgramDirectories paths = resolved.Value!;
                Require(paths.Library.StartsWith(roots.Library + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "export containment");
                Require(paths.Learning!.StartsWith(roots.Learning! + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    "private containment");
                Require(names.Add(paths.Library.Normalize()), "case and normalization collision resistance");
                CompactProgramStorage.TryDirectories(roots, new(key, build), out var repeated);
                Require(paths == repeated.Value, "same variant/build reuses one active directory");
                Require(Path.GetFileName(paths.Library).StartsWith("build-", StringComparison.Ordinal), "revision is not a directory");
                Require(!Directory.Exists(paths.Library), "name resolution creates no files");
            }
        }
    }

    private static void ExistingLinks(string directory)
    {
        string target = Path.Combine(directory, "link-target");
        string link = Path.Combine(directory, "linked-root");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        Require(!CompactProgramStorage.TryRoots(link, null, out _), "linked root refusal");
        string library = Path.Combine(directory, "library");
        Directory.CreateDirectory(library);
        Directory.CreateSymbolicLink(Path.Combine(library, "programs"), target);
        Require(CompactProgramStorage.TryRoots(library, null, out var roots), "root itself remains valid");
        Require(!CompactProgramStorage.TryDirectories(roots.Value!, new("app", "1.0"), out var unsafePath)
            && unsafePath.Error == CompactStorageError.UnsafePath, "linked identity directory refusal");
        Require(Directory.GetFileSystemEntries(target).Length == 0, "link target remains untouched");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Program storage regression failed: " + name);
        }
    }
}
