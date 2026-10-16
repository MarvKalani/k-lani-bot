using System.Text.Json.Nodes;
using KLaniBot.Cartridges;

namespace KLaniBot.Transport.CompactUi;

// Workflow definitions share an application's active directory; private locking never turns revisions into extra catalog entries.
public sealed class CompactGatewayWorkflowDefinitions(CompactStorageRoots roots, ICompactWorkflowFunctions functions)
{
    public const int MaximumDefinitions = 100;
    public const int MaximumBytes = 1024 * 1024;
    public const string Format = "k-lani-bot-workflows/1";

    // Throws CompactNodeException for unsafe, invalid, duplicate or unverified stored definitions.
    public IReadOnlyDictionary<string, CompactWorkflowDefinition> Read()
    {
        var definitions = new Dictionary<string, CompactWorkflowDefinition>(StringComparer.Ordinal);
        foreach (var application in new GatewayProgramCatalog(roots.Library).Read())
        {
            if (application.Generated == null)
            {
                continue;
            }
            string file = Path.Combine(Path.GetDirectoryName(application.Generated.File)!, "workflows.json");
            var document = ReadFile(file, application.Generated.Application);
            foreach (JsonNode? source in document["workflows"]!.AsArray())
            {
                var definition = Decode(source);
                if (definition.Applications.Values.First().Identity != application.Generated.Application)
                {
                    throw new CompactNodeException("Workflow primary application does not own this definition store.");
                }
                if (!definitions.TryAdd(definition.Key, definition) || definitions.Count > MaximumDefinitions)
                {
                    throw new CompactNodeException("Duplicate workflow key or bounded definition catalog exceeded.");
                }
            }
        }

        return definitions;
    }

    // Throws CompactNodeException for invalid definitions, unavailable learning or failed atomic bounded storage.
    public CompactWorkflowDefinition Install(JsonObject source)
    {
        if (roots.Learning == null)
        {
            throw new CompactNodeException("A private learning root is required to save workflow definitions.");
        }
        var definition = Decode(source);
        var ownership = new CompactRevisionLock(Path.Combine(roots.Learning, "workflow-definitions.lock"));
        if (!ownership.TryAcquire(out var acquired))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(acquired.Error!.Value, acquired.Detail));
        }
        try
        {
            InstallLocked(definition);
        }
        finally
        {
            if (!ownership.TryRelease(out var released))
            {
                throw new CompactNodeException(CompactFunctionNames.Error(released.Error!.Value, released.Detail));
            }
        }

        return definition;
    }

    // Throws CompactNodeException for duplicate names, missing active application or unsafe/external storage changes.
    private void InstallLocked(CompactWorkflowDefinition definition)
    {
        var current = Read();
        if (current.ContainsKey(definition.Key) || current.Count >= MaximumDefinitions)
        {
            throw new CompactNodeException("Workflow already exists or bounded definition catalog exceeded.");
        }
        var primary = definition.Applications.Values.First().Identity;
        var program = new GatewayProgramCatalog(roots.Library).Generated(primary);
        if (program == null)
        {
            throw new CompactNodeException("Workflow primary application requires one active generated extension.");
        }
        string file = Path.Combine(Path.GetDirectoryName(program.File)!, "workflows.json");
        var document = ReadFile(file, program.Application);
        string before = CompactJsonText.Serialize(document);
        document["workflows"]!.AsArray().Add(definition.Source.DeepClone());
        byte[] content = CompactRevisionFiles.Encode(document);
        if (content.Length > MaximumBytes)
        {
            throw new CompactNodeException("Workflow definition store exceeds its byte bound.");
        }

        Write(file, content, before, program.Application);
    }

    // Throws CompactNodeException for invalid/unsafe stores, without repairing or overwriting unknown formats.
    private JsonObject ReadFile(string file, CompactProgramIdentity application)
    {
        if (!CompactRevisionFiles.TryRead(file, out var read))
        {
            throw new CompactNodeException(CompactFunctionNames.Error(read.Error!.Value, read.Detail));
        }
        if (read.Value == null)
        {
            return new JsonObject
            {
                ["format"] = Format,
                ["application"] = new JsonObject
                {
                    ["key"] = application.Key,
                    ["version"] = application.Build
                },
                ["workflows"] = new JsonArray()
            };
        }
        try
        {
            return DecodeDocument(read.Value, application);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CompactNodeException("Workflow definition JSON is invalid.");
        }
    }

    // Throws CompactNodeException for unsupported stored format, ownership or bounded contents.
    private static JsonObject DecodeDocument(byte[] source, CompactProgramIdentity application)
    {
        if (source.Length > MaximumBytes || JsonNode.Parse(source) is not JsonObject document
            || !CompactDiscoveryDocumentCodec.Only(document, "format", "application", "workflows")
            || document["format"]?.ToString() != Format || document["application"] is not JsonObject identity
            || !CompactDiscoveryDocumentCodec.Only(identity, "key", "version") || identity["key"]?.ToString() != application.Key
            || identity["version"]?.ToString() != application.Build || document["workflows"] is not JsonArray definitions
            || definitions.Count > MaximumDefinitions)
        {
            throw new CompactNodeException("Workflow cartridge identity or definition store is invalid.");
        }

        return document;
    }

    // Throws CompactNodeException when a model supplies unknown functions, unverified effects or executable/literal bindings.
    private CompactWorkflowDefinition Decode(JsonNode? source)
    {
        if (!CompactWorkflowDefinitionCodec.TryRead(source, functions, out var parsed))
        {
            throw new CompactNodeException(CompactWorkflowNames.Error(parsed.Error!.Value, parsed.Detail));
        }

        return parsed.Value!;
    }

    // Throws CompactNodeException for external modifications or failed atomic replacement; no partially written active file is published.
    private void Write(string file, byte[] content, string before, CompactProgramIdentity application)
    {
        string temporary = file + "." + Guid.NewGuid() + ".tmp";
        try
        {
            if (!CompactRevisionFiles.TryWriteNew(temporary, content, out var written))
            {
                throw new CompactNodeException(CompactFunctionNames.Error(written.Error!.Value, written.Detail));
            }
            CartridgeManifest.RejectLinks(file);
            if (CompactJsonText.Serialize(ReadFile(file, application)) != before)
            {
                throw new CompactNodeException("Workflow definition store changed before replacement.");
            }
            File.Move(temporary, file, true);
            if (!CompactRevisionFiles.TrySyncDirectory(Path.GetDirectoryName(file)!, out var synced))
            {
                throw new CompactNodeException(CompactFunctionNames.Error(synced.Error!.Value, synced.Detail));
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
