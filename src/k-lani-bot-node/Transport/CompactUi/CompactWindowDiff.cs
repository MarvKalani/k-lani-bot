using System.Linq;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_apply reports what one step changed in a window. The diff compares the complete states of two observations of the
// same window, so a step names its changed values, enabled flags and members instead of repeating the whole window.
public static class CompactWindowDiff
{
    private const int DiffLimit = 20;

    // Two observations without a window have nothing to compare, so the reason is typed instead of thrown.
    public static CompactWindowDiffAttempt Between(
        IReadOnlyList<CompactTreeNode> before,
        IReadOnlyList<CompactTreeNode> after,
        CompactWindowStateRequest request)
    {
        CompactWindowStateBuilder? beforeBuilder = CompactWindowState.CollectBuilder(before);
        CompactWindowStateBuilder? afterBuilder = CompactWindowState.CollectBuilder(after);
        if (beforeBuilder is null || afterBuilder is null)
        {
            return new CompactWindowDiffAttempt(null, CompactWindowStateError.NoWindow);
        }

        JsonObject diff = new();
        JsonObject values = ChangedValues(beforeBuilder.State, afterBuilder.State, request.MaxChars);
        if (values.Count > 0)
        {
            diff["values"] = values;
        }

        JsonObject enabled = ChangedEnabled(beforeBuilder.Bindings, afterBuilder.Bindings);
        if (enabled.Count > 0)
        {
            diff["enabled"] = enabled;
        }

        AddCapped(diff, "added", PathsMissingFrom(afterBuilder.Bindings, beforeBuilder.Bindings));
        AddCapped(diff, "removed", PathsMissingFrom(beforeBuilder.Bindings, afterBuilder.Bindings));
        AddTitle(diff, before, after);
        AddShape(diff, beforeBuilder, afterBuilder, request);
        return new CompactWindowDiffAttempt(diff, null);
    }

    // A path changed when its value differs or when only one side has it; a missing side reads as null.
    private static JsonObject ChangedValues(JsonObject before, JsonObject after, int maxChars)
    {
        JsonObject values = new();
        foreach (string path in StatePaths(before, after))
        {
            JsonNode? beforeValue = before[path];
            JsonNode? afterValue = after[path];
            bool presenceChanged = before.ContainsKey(path) != after.ContainsKey(path);
            if (presenceChanged || !JsonNode.DeepEquals(beforeValue, afterValue))
            {
                values[path] = new JsonArray(SideOf(beforeValue, maxChars), SideOf(afterValue, maxChars));
            }
        }

        return values;
    }

    // The before paths keep their order, and the paths that only the after state has follow them.
    private static List<string> StatePaths(JsonObject before, JsonObject after)
    {
        List<string> paths = before.Select(pair => pair.Key).ToList();
        paths.AddRange(after.Select(pair => pair.Key).Where(path => !before.ContainsKey(path)));
        return paths;
    }

    // Text is clipped like the state view, a collection reports its row count and a fingerprint, and other values are copied.
    private static JsonNode? SideOf(JsonNode? value, int maxChars)
    {
        if (value is JsonArray rows)
        {
            return RowsSide(rows);
        }

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            return CompactWindowStateParts.ClipText(text, maxChars);
        }

        return value?.DeepClone();
    }

    private static JsonObject RowsSide(JsonArray rows)
    {
        List<string> rowTexts = new();
        foreach (JsonNode? row in rows)
        {
            rowTexts.Add(row!.GetValue<string>());
        }

        JsonObject side = new();
        side["rows"] = JsonValue.Create(rows.Count);
        side["sha256"] = JsonValue.Create(CompactWindowStateParts.Fingerprint(string.Join("\n", rowTexts)));
        return side;
    }

    private static JsonObject ChangedEnabled(
        IReadOnlyDictionary<string, CompactMemberBinding> before,
        IReadOnlyDictionary<string, CompactMemberBinding> after)
    {
        JsonObject enabled = new();
        foreach (KeyValuePair<string, CompactMemberBinding> pair in after)
        {
            if (before.TryGetValue(pair.Key, out CompactMemberBinding? previous) && previous.Enabled != pair.Value.Enabled)
            {
                enabled[pair.Key] = JsonValue.Create(pair.Value.Enabled);
            }
        }

        return enabled;
    }

    // Each list names the first members in declaration order and counts the rest in a *_more entry.
    private static void AddCapped(JsonObject diff, string key, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        JsonArray shown = new();
        foreach (string path in paths.Take(DiffLimit))
        {
            shown.Add(JsonValue.Create(path));
        }

        diff[key] = shown;
        if (paths.Count > DiffLimit)
        {
            diff[key + "_more"] = JsonValue.Create(paths.Count - DiffLimit);
        }
    }

    private static List<string> PathsMissingFrom(
        IReadOnlyDictionary<string, CompactMemberBinding> paths,
        IReadOnlyDictionary<string, CompactMemberBinding> other)
    {
        return paths.Keys.Where(path => !other.ContainsKey(path)).ToList();
    }

    private static void AddTitle(JsonObject diff, IReadOnlyList<CompactTreeNode> before, IReadOnlyList<CompactTreeNode> after)
    {
        string? beforeTitle = CompactWindowState.WindowTitle(before);
        string? afterTitle = CompactWindowState.WindowTitle(after);
        if (beforeTitle == afterTitle)
        {
            return;
        }

        diff["title"] = new JsonArray(JsonValue.Create(beforeTitle), JsonValue.Create(afterTitle));
    }

    private static void AddShape(
        JsonObject diff,
        CompactWindowStateBuilder before,
        CompactWindowStateBuilder after,
        CompactWindowStateRequest request)
    {
        string beforeShape = CompactWindowState.ShapeOf(before, request);
        string afterShape = CompactWindowState.ShapeOf(after, request);
        if (beforeShape == afterShape)
        {
            return;
        }

        diff["shape"] = new JsonObject
        {
            ["before"] = JsonValue.Create(beforeShape),
            ["after"] = JsonValue.Create(afterShape)
        };
    }
}

// Use case: ui_apply asks Between for the diff of one step; a window missing in either observation is a typed reason, not an exception.
public sealed record CompactWindowDiffAttempt(JsonObject? Value, CompactWindowStateError? Error);
