namespace KLaniBot.Guard;

// Use case: the operator names in policy.json which folder a node may back up, where its encrypted archives and recovery
// copies go and which vault key encrypts them. Agents call the backup tools but can choose none of these, and the workspace
// file tools cannot reach archives or restored plaintext.
public sealed class BackupPolicy
{
    private const int MaximumAgeLimitHours = 720;

    public string Source { get; set; } = "";
    public string Archives { get; set; } = "";
    public string Restores { get; set; } = "";
    public Guid KeyId
    {
        get; set;
    }
    public int KeyVersion
    {
        get; set;
    }
    // A receipt older than this cannot authorize a mutation; verification does not make an old capture newer.
    public int MaximumAgeHours { get; set; } = 24;

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan MaximumAge => TimeSpan.FromHours(MaximumAgeHours);

    // Throws InvalidDataException naming the first setting that would let an agent reach or redirect backup data.
    public void Validate(string workspaceRoot, string stateRoot, string policyFile)
    {
        if (!Path.IsPathFullyQualified(Source) || !Path.IsPathFullyQualified(Archives) || !Path.IsPathFullyQualified(Restores))
            throw new InvalidDataException("Backup Source, Archives and Restores must be absolute paths.");
        Source = Path.GetFullPath(Source);
        Archives = Path.GetFullPath(Archives);
        Restores = Path.GetFullPath(Restores);
        if (!Directory.Exists(Source) || !Directory.Exists(Archives) || !Directory.Exists(Restores))
            throw new InvalidDataException("Backup Source, Archives and Restores must exist.");
        if (KeyId == Guid.Empty || KeyVersion < 1)
            throw new InvalidDataException("Backup KeyId and KeyVersion must name a provisioned vault key.");
        if (MaximumAgeHours is < 1 or > MaximumAgeLimitHours)
            throw new InvalidDataException("Backup MaximumAgeHours must be between 1 and 720.");
        RequireSeparateRoots(workspaceRoot, stateRoot, policyFile);
    }

    // The other policy is the file as re-read while the node runs; its paths are not yet normalized.
    public bool SameRootsAndKey(BackupPolicy? other)
    {
        return other != null
            && Same(Normalized(other.Source), Source)
            && Same(Normalized(other.Archives), Archives)
            && Same(Normalized(other.Restores), Restores)
            && other.KeyId == KeyId
            && other.KeyVersion == KeyVersion;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string first, string second)
    {
        return string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), PathComparison);
    }

    private static bool Overlap(string first, string second)
    {
        return Contains(first, second) || Contains(second, first);
    }

    private static bool Contains(string parent, string child)
    {
        string prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;

        return Same(parent, child) || child.StartsWith(prefix, PathComparison);
    }

    private static string Normalized(string path)
    {
        return Path.IsPathFullyQualified(path) && !path.Contains('\0') ? Path.GetFullPath(path) : path;
    }

    // Throws InvalidDataException for archive or recovery roots that overlap the workspace, the node state or each other.
    private void RequireSeparateRoots(string workspaceRoot, string stateRoot, string policyFile)
    {
        string[] protectedRoots = [Archives, Restores];
        foreach (string root in protectedRoots)
        {
            if (Overlap(root, workspaceRoot) || Overlap(root, stateRoot))
                throw new InvalidDataException("Backup Archives and Restores must be outside the agent workspace and the state.");
            if (Overlap(root, policyFile))
                throw new InvalidDataException("Backup Archives and Restores must not contain the policy file.");
        }
        if (Overlap(Source, Archives) || Overlap(Source, Restores) || Overlap(Archives, Restores))
            throw new InvalidDataException("Backup Source, Archives and Restores must not overlap.");
    }
}
