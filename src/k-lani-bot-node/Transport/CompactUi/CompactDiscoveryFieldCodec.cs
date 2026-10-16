using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Stored structural descriptors are validated independently; injected values, IDs or executable actions cannot enter the inventory.
public static class CompactDiscoveryFieldCodec
{
    public static bool TryRead(JsonNode? node, out CompactObservedField? field)
    {
        field = null;
        if (node is not JsonObject document
            || !CompactDiscoveryDocumentCodec.Only(document, "key", "name", "name_truncated", "type", "role", "enabled",
                "offscreen", "target_ready", "actions", "selector")
            || !CompactDiscoveryDocumentCodec.Text(document, "key", out string key)
            || !CompactDiscoveryDocumentCodec.Text(document, "name", out string name)
            || !CompactDiscoveryDocumentCodec.Text(document, "role", out string role)
            || !CompactDiscoveryDocumentCodec.Text(document, "type", out string type)
            || !CompactDiscoveryDocumentCodec.Boolean(document, "name_truncated", out bool nameTruncated)
            || !CompactDiscoveryDocumentCodec.Boolean(document, "enabled", out bool enabled)
            || !CompactDiscoveryDocumentCodec.Boolean(document, "offscreen", out bool offscreen)
            || !CompactDiscoveryDocumentCodec.Boolean(document, "target_ready", out bool ready)
            || !TrySelector(document["selector"], out CompactStoredSelector? selector)
            || !CompactDiscoveryFields.TryKind(CompactRole.Parse(role), out CompactFormFieldKind kind)
            || type != CompactFormWire.Kind(kind) || selector!.Role.Name != role
            || key != CompactDiscoveryFields.Key(selector) || ready && !enabled
            || name != CompactDiscoveryFields.Clip(selector.Name!, CompactDiscoveryFields.MaximumNameCharacters)
            || nameTruncated != (selector.Name!.Length > CompactDiscoveryFields.MaximumNameCharacters))
        {
            return false;
        }
        var descriptor = new CompactObservedField(key, name, nameTruncated, kind, selector.Role, enabled, offscreen, ready, selector);
        if (!JsonNode.DeepEquals(document["actions"], CompactDiscoveryWire.Field(descriptor, true)["actions"]))
        {
            return false;
        }
        field = descriptor;

        return true;
    }

    private static bool TrySelector(JsonNode? node, out CompactStoredSelector? selector)
    {
        selector = null;
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "role", "name", "autoId", "window")
            || !CompactDiscoveryDocumentCodec.Text(document, "role", out string role)
            || !CompactDiscoveryDocumentCodec.Text(document, "name", out string name)
            || !CompactDiscoveryDocumentCodec.Text(document, "autoId", out string autoId)
            || !CompactDiscoveryDocumentCodec.Text(document, "window", out string word)
            || !CompactWindowKindWords.TryParse(word, out CompactWindowKind windowKind))
        {
            return false;
        }
        selector = new(CompactRole.Parse(role), name, autoId, windowKind);

        return true;
    }
}
