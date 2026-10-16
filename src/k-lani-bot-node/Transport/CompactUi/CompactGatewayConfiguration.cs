using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Trusted local configuration binds existing private acceptance evidence to its authorized issuer, never to MCP request fields.
public sealed record CompactAcceptanceSettings(string EvidenceRoot, CompactAcceptanceAuthority Authority);

// Optional native profile configuration enables separately configured private services; absent settings grant no acceptance access.
public sealed record CompactGatewayConfiguration(CompactAcceptanceSettings? Compatibility = null, CompactWorkflowSettings? Workflow = null);

// Private job state is bound to trusted host authority and kept outside exported application definitions.
public sealed record CompactWorkflowSettings(string StateRoot, CompactAcceptanceAuthority Authority);

// Startup reads an owner-only bounded file outside exports and rejects unsupported fields or versions without creating private state.
public static class CompactGatewayConfigurationCodec
{
    public const int Version = 1;
    public const int MaximumBytes = 64 * 1024;
    private const int MaximumAuthorityLength = 256;

    // Throws ArgumentException for unsafe, missing or invalid private configuration; messages never echo private input.
    public static CompactGatewayConfiguration Read(string? file, CompactStorageRoots roots)
    {
        if (file == null)
        {
            return new();
        }
        if (!Path.IsPathFullyQualified(file) || !CompactProgramStorage.TryRoots(roots.Library, Path.GetDirectoryName(file), out _)
            || !CompactAcceptanceFiles.TryRead(file, MaximumBytes, out var read))
        {
            throw new ArgumentException("--ui-config requires an existing private absolute regular file outside cartridge exports.");
        }
        if (!TryRead(read.Value!, roots, out var configuration))
        {
            throw new ArgumentException("Invalid or unsupported native UI configuration; use version 1 and bounded authorized settings.");
        }

        return configuration!;
    }

    private static bool TryRead(byte[] source, CompactStorageRoots roots, out CompactGatewayConfiguration? configuration)
    {
        configuration = null;
        try
        {
            if (JsonNode.Parse(source) is not JsonObject document
                || !CompactDiscoveryDocumentCodec.Only(document, "version", "compatibility", "workflow")
                || !CompactFunctionDocumentCodec.TryInteger(document["version"], out long version) || version != Version)
            {
                return false;
            }
            CompactAcceptanceSettings? compatibility = null;
            if (document.ContainsKey("compatibility") && !TryCompatibility(document["compatibility"], roots, out compatibility))
            {
                return false;
            }
            CompactWorkflowSettings? workflow = null;
            if (document.ContainsKey("workflow") && !TryWorkflow(document["workflow"], roots, out workflow))
            {
                return false;
            }
            configuration = new(compatibility, workflow);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCompatibility(JsonNode? node, CompactStorageRoots roots, out CompactAcceptanceSettings? settings)
    {
        settings = null;
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "evidenceRoot", "authority")
            || !CompactDiscoveryDocumentCodec.Text(document, "evidenceRoot", out string directory)
            || !CompactProgramStorage.TryRoots(roots.Library, directory, out var paths) || document["authority"] is not JsonObject authority
            || !CompactDiscoveryDocumentCodec.Only(authority, "node", "principal", "connection")
            || !TryAuthority(authority, "node", out string target) || !TryAuthority(authority, "principal", out string principal)
            || !TryAuthority(authority, "connection", out string connection))
        {
            return false;
        }
        settings = new(paths.Value!.Learning!, new(target, principal, connection));

        return true;
    }

    private static bool TryWorkflow(JsonNode? node, CompactStorageRoots roots, out CompactWorkflowSettings? settings)
    {
        settings = null;
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "stateRoot", "authority"))
        {
            return false;
        }
        var normalized = document.DeepClone().AsObject();
        normalized["evidenceRoot"] = normalized["stateRoot"]?.DeepClone();
        normalized.Remove("stateRoot");
        if (!TryCompatibility(normalized, roots, out var configured))
        {
            return false;
        }
        settings = new(configured!.EvidenceRoot, configured.Authority);

        return true;
    }

    private static bool TryAuthority(JsonObject authority, string name, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(authority, name, out value)
            && !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumAuthorityLength;
    }
}
