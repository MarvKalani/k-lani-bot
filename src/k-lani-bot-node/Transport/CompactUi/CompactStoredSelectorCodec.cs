using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Generated selectors are semantic, window-owned and bounded; physical coordinates and runtime IDs are always rejected.
public static class CompactStoredSelectorCodec
{
    public static bool TryRead(JsonNode? node, bool output, out CompactFunctionAttempt<CompactStoredSelector> attempt)
    {
        attempt = new(null, CompactFunctionError.InvalidSelector);
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Text(document, "role", out string role)
            || !CompactDiscoveryDocumentCodec.Text(document, "window", out string word)
            || !CompactWindowKindWords.TryParse(word, out CompactWindowKind windowKind)
            || document.Any(entry => entry.Key is not "role" and not "name" and not "autoId" and not "window" and not "containerPath"
                && (!output || entry.Key != "namePrefix")))
        {
            return false;
        }
        string? name = Text(document, "name");
        string? prefix = output ? Text(document, "namePrefix") : null;
        if (name == null && prefix == null)
        {
            attempt = new(null, CompactFunctionError.InvalidSemanticSelector);
            return false;
        }
        if (!TryContainers(document, out var path))
        {
            attempt = new(null, CompactFunctionError.InvalidContainerPath);
            return false;
        }
        if (document.ContainsKey("autoId") && Text(document, "autoId") == null)
        {
            return false;
        }
        attempt = new(new(CompactRole.Parse(role), name, Text(document, "autoId"), windowKind, path, prefix));

        return true;
    }

    private static bool TryContainers(JsonObject document, out IReadOnlyList<CompactContainerSelector>? path)
    {
        path = null;
        if (!document.ContainsKey("containerPath"))
        {
            return true;
        }
        if (document["containerPath"] is not JsonArray array || array.Count > CompactCaptureCodec.MaximumSteps)
        {
            return false;
        }
        var containers = new List<CompactContainerSelector>();
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject part || !CompactDiscoveryDocumentCodec.Only(part, "role", "autoId", "name")
                || !CompactDiscoveryDocumentCodec.Text(part, "role", out string role)
                || Text(part, "autoId") == null && Text(part, "name") == null)
            {
                return false;
            }
            containers.Add(new(CompactRole.Parse(role), Text(part, "autoId"), Text(part, "name")));
        }
        path = containers;

        return true;
    }

    private static string? Text(JsonObject document, string field)
    {
        return CompactDiscoveryDocumentCodec.Text(document, field, out string value) ? value : null;
    }
}
