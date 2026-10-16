using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Native observation readers validate actual node payloads without turning absent identity/value/choice evidence into defaults.
public static class CompactNodeObservation
{
    // Throws CompactNodeException for malformed identity observations; unknown version/lifetime remains explicitly unknown.
    public static IReadOnlyList<CompactNativeWindow> Windows(JsonObject response)
    {
        if (response["windows"] is not JsonArray windows)
        {
            throw new CompactNodeException("Native window observation is unavailable.");
        }
        var result = new List<CompactNativeWindow>();
        foreach (JsonNode? node in windows)
        {
            if (node is not JsonObject window || window["ProcessId"] is not JsonValue identifier
                || !identifier.TryGetValue<int>(out int process) || process < 1 || window["WindowHandle"] is not JsonValue handle
                || !handle.TryGetValue<long>(out long nativeHandle) || nativeHandle == 0 || !TryKind(window["Kind"], out var kind))
            {
                throw new CompactNodeException("Native window identity is invalid.");
            }
            result.Add(new(process, Text(window, "ProcessName") ?? "unknown", Text(window, "AppVersion") ?? "unknown",
                Text(window, "Title") ?? "", nativeHandle, Text(window, "ProcessStartedAtUtc"), kind));
        }

        return result;
    }

    // A guessed kind could send input to a main window that a dialog blocks, so only the node's two words are accepted.
    private static bool TryKind(JsonNode? node, out CompactWindowKind kind)
    {
        kind = CompactWindowKind.Main;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var word))
        {
            return false;
        }
        if (word == "dialog")
        {
            kind = CompactWindowKind.Dialog;
            return true;
        }

        return word == "main";
    }

    // Throws CompactNodeException for incomplete native trees, missing snapshots or malformed metadata.
    public static CompactNativeObservation Tree(JsonObject response)
    {
        string? snapshot = Text(response, "snapshot_id");
        string? tree = Text(response, "tree");
        if (response["truncated"]?.ToString() == "true" || string.IsNullOrEmpty(snapshot)
            || tree == null || !CompactTreeReader.TryParse(tree, out var parsed) || parsed.Value!.Count > CompactNodeAccess.MaximumNodes)
        {
            throw new CompactNodeException("The native tree or snapshot is incomplete; completion is unverified.");
        }
        var metadata = Metadata(response["form_metadata"]);
        IReadOnlyList<CompactTreeNode> nodes = parsed.Value.Select(node =>
        {
            var choices = metadata.GetValueOrDefault(node.Id)?.Choices;
            var selected = choices?.Options.FirstOrDefault(option => option.Id == choices.SelectedId);
            return selected == null ? node : node with
            {
                Value = selected.Label
            };
        }).ToArray();

        return new(snapshot, nodes, metadata);
    }

    // Throws CompactNodeException for invalid reported field metadata; an absent metadata block remains absent.
    private static IReadOnlyDictionary<int, UiFieldMetadata> Metadata(JsonNode? source)
    {
        var result = new Dictionary<int, UiFieldMetadata>();
        if (source == null)
        {
            return result;
        }
        try
        {
            if (source is not JsonObject fields)
            {
                throw new CompactNodeException("Native field metadata is malformed.");
            }
            foreach (var field in fields)
            {
                if (!int.TryParse(field.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int identifier)
                    || field.Value?.Deserialize<UiFieldMetadata>() is not UiFieldMetadata metadata || metadata.Choices?.Options == null)
                {
                    throw new CompactNodeException("Native field metadata is malformed.");
                }
                result.Add(identifier, metadata);
            }
        }
        catch (JsonException)
        {
            throw new CompactNodeException("Native field metadata is malformed.");
        }

        return result;
    }

    private static string? Text(JsonObject source, string name)
    {
        return source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
