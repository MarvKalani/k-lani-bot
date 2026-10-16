using System.IO.Compression;
using System.Text.Json.Nodes;

namespace RepositoryGuard;

// Use case: a commit, a push or the CI history scan stops when one file or the published history grows beyond the reviewed
// limits in .release/repository-size.json. A sudden jump usually means a build output, log or dump was committed; after a
// review the limit is raised by a commit. Each distinct file version counts once, compressed with zlib, which is close to
// what the Git host stores and gives the same number on every machine.
internal sealed class RepositorySizePolicy
{
    private const string PolicyFile = "repository-size.json";
    private readonly Dictionary<string, long> _compressedVersions = new(StringComparer.Ordinal);

    private RepositorySizePolicy(long maximumHistoryCompressedBytes, long maximumFileBytes)
    {
        MaximumHistoryCompressedBytes = maximumHistoryCompressedBytes;
        MaximumFileBytes = maximumFileBytes;
    }

    public long MaximumHistoryCompressedBytes
    {
        get;
    }
    public long MaximumFileBytes
    {
        get;
    }
    public long HistoryCompressedBytes => _compressedVersions.Values.Sum();

    // Throws InvalidDataException for an unversioned policy or a limit that is not positive; IOException when it is unreadable.
    public static RepositorySizePolicy Read(string policyRoot)
    {
        var policy = JsonNode.Parse(File.ReadAllText(Path.Combine(policyRoot, ".release", PolicyFile)))!;
        if (policy["version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Reviewed repository size policy required.");
        long history = policy["maximumHistoryCompressedBytes"]?.GetValue<long>() ?? 0;
        long file = policy["maximumFileBytes"]?.GetValue<long>() ?? 0;

        if (history <= 0 || file <= 0)
            throw new InvalidDataException("Repository size limits must be positive.");
        return new RepositorySizePolicy(history, file);
    }

    public void AddContent(string objectId, byte[] content)
    {
        if (!_compressedVersions.ContainsKey(objectId))
            _compressedVersions[objectId] = Compressed(content);
    }

    // A version too large to be read counts with its full size, which only overstates the compressed total.
    public void AddUnread(string objectId, long bytes)
    {
        _compressedVersions.TryAdd(objectId, bytes);
    }

    public bool FileTooLarge(long bytes)
    {
        return bytes > MaximumFileBytes;
    }

    public bool HistoryTooLarge()
    {
        return HistoryCompressedBytes > MaximumHistoryCompressedBytes;
    }

    private static long Compressed(byte[] content)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(content);
        }

        return output.Length;
    }
}
