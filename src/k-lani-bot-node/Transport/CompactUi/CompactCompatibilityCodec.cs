using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compatibility documents are read through an explicit bounded format loader, with unknown contracts rejected before input.
public static class CompactCompatibilityCodec
{
    private static readonly string[] Fields = ["format", "node_version", "node_wire_version", "platform", "application",
        "access_route", "action_schemas", "implementations", "selector_scope", "function_scope"];
    public const int MaximumActionSchemas = 20;

    public static bool TryRecorded(JsonNode? value, out CompactCompatibilityAttempt<CompactRecordedCompatibility> attempt)
    {
        attempt = new(null, CompactCompatibilityError.UnsupportedContract);
        if (value is not JsonObject document)
        {
            return false;
        }
        if (!document.ContainsKey("format"))
        {
            if (!CompactDiscoveryDocumentCodec.Only(document, "node_version", "platform", "application_version")
                || !Text(document, "node_version", out string node) || !Text(document, "platform", out string platform)
                || !Text(document, "application_version", out string build))
            {
                return false;
            }
            attempt = new(new(CompactCompatibilityFormat.Legacy, new(node, platform, build)));
            return true;
        }
        if (!TryContract(document, out var contract))
        {
            attempt = new(null, contract.Error);
            return false;
        }
        attempt = new(new(CompactCompatibilityFormat.Contracts2, Contracts: contract.Value));

        return true;
    }

    private static bool TryContract(JsonObject document, out CompactCompatibilityAttempt<CompactCompatibilityContract> attempt)
    {
        attempt = new(null, CompactCompatibilityError.UnsupportedContract);
        if (document.Any(entry => !Fields.Contains(entry.Key, StringComparer.Ordinal) && entry.Key != "acceptance_ref")
            || Fields.Any(field => !document.ContainsKey(field)) || !Text(document, "format", out string format)
            || format != CompactCompatibilityWire.Format || !Text(document, "node_version", out string node) || node.Length == 0
            || !Text(document, "node_wire_version", out string wire)
            || CompactCompatibilityWire.Wire(wire).Edition == CompactWireEdition.Other
            || !Text(document, "platform", out string platform) || !TryAccess(document, out var access))
        {
            return false;
        }
        if (!TryApplication(document["application"], out var application))
        {
            attempt = new(null, CompactCompatibilityError.InvalidApplication);
            return false;
        }
        if (!TryFingerprints(document, out var fingerprints))
        {
            attempt = new(null, CompactCompatibilityError.InvalidFingerprints);
            return false;
        }
        if (!TryAcceptance(document, out Guid? acceptance))
        {
            attempt = new(null, CompactCompatibilityError.InvalidAcceptance);
            return false;
        }
        attempt = new(new(node, CompactCompatibilityWire.Wire(wire), platform, application!, access, fingerprints!.Actions,
            fingerprints.Native, fingerprints.Gateway, fingerprints.Selector, fingerprints.Function, acceptance));

        return true;
    }

    // This local parse result binds all required modern fingerprint fields before constructing a compatibility contract.
    private sealed record Fingerprints(CompactActionSchemas Actions, CompactContractDigest Native, CompactContractDigest Gateway,
        CompactContractDigest Selector, CompactContractDigest Function);

    private static bool TryFingerprints(JsonObject document, out Fingerprints? parsed)
    {
        parsed = null;
        if (!TrySchemas(document["action_schemas"], out var actions)
            || actions!.Known.Count + actions.Extensions.Count is < 1 or > MaximumActionSchemas
            || document["implementations"] is not JsonObject implementations || implementations.Count != 2
            || !Digest(implementations, "native", out var native) || !Digest(implementations, "gateway", out var gateway)
            || !Digest(document, "selector_scope", out var selector) || !Digest(document, "function_scope", out var function))
        {
            return false;
        }
        parsed = new(actions, native!, gateway!, selector!, function!);

        return true;
    }

    public static bool TrySchemas(JsonNode? node, out CompactActionSchemas? schemas)
    {
        schemas = null;
        if (node is not JsonObject document)
        {
            return false;
        }
        var known = new Dictionary<Tool, CompactContractDigest>();
        var extensions = new Dictionary<string, CompactContractDigest>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in document)
        {
            if (entry.Value is not JsonValue value || !value.TryGetValue(out string? text)
                || !CompactContractHash.TryDigest(text, out var digest))
            {
                return false;
            }
            if (ToolNames.TryParse(entry.Key, out Tool tool))
            {
                known.Add(tool, digest!);
            }
            else
            {
                extensions.Add(entry.Key, digest!);
            }
        }
        schemas = new(known, extensions);

        return true;
    }

    private static bool TryApplication(JsonNode? node, out CompactProgramIdentity? application)
    {
        application = null;
        if (node is not JsonObject record || record.Count != 2 || !Text(record, "key", out string key)
            || !Text(record, "build", out string build) || build.Length == 0 || build == "unknown")
        {
            return false;
        }
        application = new(key, build);

        return true;
    }

    private static bool TryAccess(JsonObject document, out CompactCompatibilityAccess access)
    {
        access = CompactCompatibilityAccess.Accessibility;
        if (!Text(document, "access_route", out string value))
        {
            return false;
        }
        if (value == "accessibility_with_keyboard")
        {
            access = CompactCompatibilityAccess.AccessibilityWithKeyboard;
            return true;
        }

        return value == "accessibility";
    }

    private static bool TryAcceptance(JsonObject document, out Guid? acceptance)
    {
        acceptance = null;
        if (!document.ContainsKey("acceptance_ref"))
        {
            return true;
        }
        if (!Text(document, "acceptance_ref", out string value) || !Guid.TryParseExact(value, "D", out Guid parsed)
            || parsed.ToString() != value)
        {
            return false;
        }
        acceptance = parsed;

        return true;
    }

    private static bool Digest(JsonObject document, string name, out CompactContractDigest? digest)
    {
        digest = null;

        return Text(document, name, out string text) && CompactContractHash.TryDigest(text, out digest);
    }

    private static bool Text(JsonObject document, string name, out string text)
    {
        return CompactDiscoveryDocumentCodec.Text(document, name, out text);
    }
}
