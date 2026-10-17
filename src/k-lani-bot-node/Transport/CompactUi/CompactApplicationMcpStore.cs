using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport.CompactUi;

// Use case: the gateway keeps the stored app MCP functions of one program version as one active document under the cartridge
// library, beside the UI cartridges and outside their programs/ tree. A revision counter in the file and an exclusive lock keep
// two writers from losing a function; directory names are digests, the program id and version stay inside the document.
public sealed class CompactApplicationMcpStore(string libraryRoot)
{
    public const string FileName = "app-mcp-functions.json";
    public const int VersionLimit = 16;

    // Null when no document exists for this version; false for a damaged or unreadable document.
    public bool TryRead(string program, string version, out CompactApplicationMcpAttempt<CompactApplicationMcpDocument?> attempt)
    {
        attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "The program id or version cannot be stored.");
        if (!CompactApplicationMcpFunctionCodec.ValidProgram(program) || !CompactApplicationMcpFunctionCodec.KnownVersion(version))
        {
            return false;
        }

        return TryReadFile(DocumentPath(program, version), program, version, out attempt);
    }

    // Every readable document of the program, newest version text first; damaged ones are left out.
    public IReadOnlyList<CompactApplicationMcpDocument> ReadAll(string program)
    {
        string directory = ProgramDirectory(program);
        if (!CompactApplicationMcpFunctionCodec.ValidProgram(program) || !Directory.Exists(directory))
        {
            return [];
        }
        var documents = new List<CompactApplicationMcpDocument>();
        foreach (string versionDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).Take(VersionLimit))
        {
            if (TryReadAny(Path.Combine(versionDirectory, FileName), program, out var document))
            {
                documents.Add(document!);
            }
        }

        return documents.OrderByDescending(document => document.Version, StringComparer.Ordinal).ToArray();
    }

    // Adds the function or replaces the one of the same name, as the next revision of the version's document.
    public bool TrySave(string program, string version, CompactApplicationMcpFunction function,
        out CompactApplicationMcpAttempt<CompactApplicationMcpDocument> attempt)
    {
        attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "The program id or version cannot be stored.");
        if (!CompactApplicationMcpFunctionCodec.ValidProgram(program) || !CompactApplicationMcpFunctionCodec.KnownVersion(version))
        {
            return false;
        }
        string file = DocumentPath(program, version);
        var gate = new CompactRevisionLock(file + ".lock");
        if (!TryCreateDirectory(Path.GetDirectoryName(file)!) || !gate.TryAcquire(out _))
        {
            attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "The stored functions are locked or not writable.");
            return false;
        }
        bool saved = TryReplace(file, program, version, function, out attempt);
        bool released = gate.TryRelease(out _);

        return saved && released;
    }

    private bool TryReplace(string file, string program, string version, CompactApplicationMcpFunction function,
        out CompactApplicationMcpAttempt<CompactApplicationMcpDocument> attempt)
    {
        if (!TryReadFile(file, program, version, out var current))
        {
            attempt = new(null, current.Failure, current.Detail);
            return false;
        }
        var functions = (current.Value?.Functions ?? []).Where(known => known.Name != function.Name).Append(function).ToArray();
        if (functions.Length > CompactApplicationMcpFunctionCodec.MaximumFunctions)
        {
            attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "A program version keeps at most 64 functions.");
            return false;
        }
        var next = new CompactApplicationMcpDocument(program, version, (current.Value?.Revision ?? 0) + 1, functions);
        byte[] source = CompactRevisionFiles.Encode(CompactApplicationMcpFunctionCodec.WriteDocument(next));
        string temporary = file + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        if (!CompactRevisionFiles.TryWriteNew(temporary, source, out var written) || !TryMove(temporary, file))
        {
            attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, written.Detail ?? "The document cannot be replaced.");
            return false;
        }
        attempt = new(next);

        return CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(file)!, out _);
    }

    private static bool TryReadFile(string file, string program, string version,
        out CompactApplicationMcpAttempt<CompactApplicationMcpDocument?> attempt)
    {
        attempt = new(null, CompactApplicationMcpFailure.StorageUnavailable, "The stored app MCP document cannot be read.");
        if (!CompactRevisionFiles.TryRead(file, out var source))
        {
            return false;
        }
        if (source.Value == null)
        {
            attempt = new(null);
            return true;
        }
        if (!TryParse(source.Value, out JsonNode? node) || !CompactApplicationMcpFunctionCodec.TryDocument(node, program, version,
                out var document))
        {
            return false;
        }
        attempt = new(document.Value);

        return true;
    }

    // Reads a document whose version is not known in advance; its directory must be the digest of the version it names.
    private bool TryReadAny(string file, string program, out CompactApplicationMcpDocument? document)
    {
        document = null;
        if (!CompactRevisionFiles.TryRead(file, out var source) || source.Value == null || !TryParse(source.Value, out JsonNode? node)
            || node?["version"] is not JsonValue value || !value.TryGetValue(out string? version)
            || !CompactApplicationMcpFunctionCodec.KnownVersion(version) || DocumentPath(program, version) != file)
        {
            return false;
        }
        if (!CompactApplicationMcpFunctionCodec.TryDocument(node, program, version, out var parsed))
        {
            return false;
        }
        document = parsed.Value;

        return true;
    }

    private static bool TryParse(byte[] source, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(source);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            CartridgeManifest.RejectLinks(directory);
            Common.PrivateFile.CreateDirectory(directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryMove(string temporary, string file)
    {
        try
        {
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            return false;
        }
    }

    private string ProgramDirectory(string program)
    {
        return Path.Combine(libraryRoot, "app-mcps", "program-" + Digest(program));
    }

    private string DocumentPath(string program, string version)
    {
        return Path.Combine(ProgramDirectory(program), "version-" + Digest(version), FileName);
    }

    private static string Digest(string value)
    {
        return CompactFormFields.Fingerprint(JsonValue.Create(value)!);
    }
}
