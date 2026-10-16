using KLaniBot.Cartridges;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compact session setup validates export and private-learning roots before it selects or opens any application state.
public sealed record CompactStorageRoots(string Library, string? Learning);

// One variant/build maps to one active directory; original identity values stay in manifests rather than filesystem components.
public sealed record CompactProgramDirectories(string Library, string? Learning);

// Storage setup distinguishes a bad root, overlapping private/public data and an application identity that cannot be persisted.
public enum CompactStorageError
{
    InvalidRoot,
    OverlappingRoots,
    UnknownApplication,
    UnsafePath
}

// Storage callers inspect typed failure evidence without creating a node, credentials or application directories implicitly.
public sealed record CompactStorageAttempt<T>(T? Value, CompactStorageError? Error = null, string? Detail = null);

// Application storage uses raw-identity digests so path separators, reserved names and normalization/case aliases never become paths.
public static class CompactProgramStorage
{
    public static bool TryRoots(string library, string? learning, out CompactStorageAttempt<CompactStorageRoots> attempt)
    {
        attempt = new(null, CompactStorageError.InvalidRoot);
        try
        {
            if (!TryRoot(library, out string? exports) || learning != null && !TryRoot(learning, out _))
            {
                return false;
            }
            string? state = learning == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(learning));
            if (state != null && (Contains(exports!, state) || Contains(state, exports!)))
            {
                attempt = new(null, CompactStorageError.OverlappingRoots);
                return false;
            }
            attempt = new(new(exports!, state));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactStorageError.UnsafePath, exception.Message);
            return false;
        }
    }

    public static bool TryDirectories(CompactStorageRoots roots, CompactProgramIdentity application,
        out CompactStorageAttempt<CompactProgramDirectories> attempt)
    {
        attempt = new(null, CompactStorageError.UnknownApplication);
        if (roots == null || application == null || string.IsNullOrWhiteSpace(application.Key)
            || string.IsNullOrWhiteSpace(application.Build) || application.Build.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!TryRoots(roots.Library, roots.Learning, out var checkedRoots))
        {
            attempt = new(null, checkedRoots.Error, checkedRoots.Detail);
            return false;
        }
        string variant = "application-" + IdentityDigest(application.Key);
        string build = "build-" + IdentityDigest(application.Build);
        string directory = Path.Combine(checkedRoots.Value!.Library, "programs", variant, build);
        string? state = checkedRoots.Value.Learning == null ? null
            : Path.Combine(checkedRoots.Value.Learning, "programs", variant, build);

        return TrySafeDirectories(directory, state, out attempt);
    }

    private static bool TrySafeDirectories(string directory, string? state,
        out CompactStorageAttempt<CompactProgramDirectories> attempt)
    {
        try
        {
            CartridgeManifest.RejectLinks(directory);
            if (state != null)
            {
                CartridgeManifest.RejectLinks(state);
            }
            attempt = new(new(directory, state));

            return true;
        }
        catch (Exception exception)
        {
            attempt = new(null, CompactStorageError.UnsafePath, exception.Message);
            return false;
        }
    }

    private static string IdentityDigest(string value)
    {
        return CompactFormFields.Fingerprint(JsonValue.Create(value)!);
    }

    private static bool TryRoot(string root, out string? normalized)
    {
        normalized = null;
        try
        {
            if (root == null || !Path.IsPathFullyQualified(root))
            {
                return false;
            }
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full == Path.GetPathRoot(full) || File.Exists(full))
            {
                return false;
            }
            CartridgeManifest.RejectLinks(full);
            normalized = full;

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Contains(string parent, string candidate)
    {
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return candidate.Equals(parent, comparison)
            || candidate.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }
}
