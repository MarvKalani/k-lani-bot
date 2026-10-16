using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport.CompactUi;

// Catalog discovery distinguishes uncallable future/corrupt documents from valid generated extensions and never repairs them implicitly.
public enum CompactGeneratedCatalogStatus
{
    Available,
    Invalid,
    UnsupportedVersion,
    Ambiguous
}

// Generated metadata retains its original application identity/path; revision is data within one active extension, not
// another catalog item.
public sealed record CompactGeneratedCatalogEntry(CompactProgramIdentity Application, string File, long Revision,
    int FunctionCount, CompactGeneratedCatalogStatus Status, string? ProcessName = null);

// Host discovery walks bounded directories without following links and reads only active generated documents, including legacy layouts.
public sealed class CompactGeneratedCatalog(string root)
{
    public const int MaximumDepth = 8;
    public const int MaximumDirectories = 10000;

    // Throws CompactNodeException for unsafe/oversized catalog state; no functions, traces or current values are returned to the model.
    public IReadOnlyList<CompactGeneratedCatalogEntry> Read()
    {
        var entries = new List<CompactGeneratedCatalogEntry>();
        var pending = new Queue<CompactCatalogDirectory>();
        pending.Enqueue(new(Path.GetFullPath(root), 0));
        int visited = 0;
        while (pending.Count != 0)
        {
            var directory = pending.Dequeue();
            CartridgeManifest.RejectLinks(directory.Path);
            if (++visited > MaximumDirectories)
            {
                throw new CompactNodeException("Generated catalog exceeds its directory bound.");
            }
            string file = Path.Combine(directory.Path, "generated-ui.json");
            if (Directory.EnumerateFileSystemEntries(directory.Path, "generated-ui.json").Any())
            {
                CompactGeneratedCatalogEntry? entry = ReadFile(file);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            if (directory.Depth < MaximumDepth)
            {
                foreach (string child in Directory.EnumerateDirectories(directory.Path).Order(StringComparer.Ordinal))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        if (visited + pending.Count >= MaximumDirectories)
                        {
                            throw new CompactNodeException("Generated catalog exceeds its directory bound.");
                        }
                        pending.Enqueue(new(child, directory.Depth + 1));
                    }
                }
            }
        }

        return entries;
    }

    // Throws CompactNodeException for unsafe/oversized files; malformed/future documents remain recorded and untouched
    // when identity is readable.
    private static CompactGeneratedCatalogEntry? ReadFile(string file)
    {
        if (!CompactRevisionFiles.TryRead(file, out var source))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(source.Error!.Value, source.Detail));
        }
        if (source.Value == null)
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(source.Value) is not JsonObject document || document["application"] is not JsonObject identity
                || !Text(identity, "key", out string key) || !Text(identity, "version", out string build))
            {
                throw new CompactNodeException("Generated catalog document has no readable application identity.");
            }
            var application = new CompactProgramIdentity(key, build);
            bool supported = document["version"] is JsonValue version && version.TryGetValue<int>(out int number) && number is 1 or 2;
            if (!supported)
            {
                return new(application, file, 0, 0, CompactGeneratedCatalogStatus.UnsupportedVersion);
            }
            if (!CompactFunctionDocumentCodec.TryRead(document, application, out var parsed))
            {
                return new(application, file, 0, 0, CompactGeneratedCatalogStatus.Invalid);
            }
            Text(identity, "process_name", out string process);
            return new(application, file, parsed.Value!.Revision, parsed.Value.Functions.Count,
                CompactGeneratedCatalogStatus.Available, string.IsNullOrEmpty(process) ? null : process);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CompactNodeException("Generated catalog document is malformed; it was not replaced.");
        }
    }

    private static bool Text(JsonObject source, string name, out string value)
    {
        value = "";

        return source[name] is JsonValue text && text.TryGetValue<string>(out var read) && !string.IsNullOrEmpty(read)
            && (value = read).Length != 0;
    }
}

// Catalog traversal records path/depth explicitly instead of tuples or unbounded recursive enumeration.
internal sealed record CompactCatalogDirectory(string Path, int Depth);
