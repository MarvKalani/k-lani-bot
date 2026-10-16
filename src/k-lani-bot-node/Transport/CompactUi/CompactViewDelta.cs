using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Projection callers compare complete immutable records; offscreen-only changes remain a separate visibility delta.
internal static class CompactViewDelta
{
    // A lean page never listed an offscreen control; one that appears is reported whole, so its reference becomes usable.
    public static IReadOnlyList<CompactViewItem> Compare(IReadOnlyList<CompactViewRow> before, IReadOnlyList<CompactViewRow> after,
        bool revealAsChanged)
    {
        var previous = new Dictionary<string, CompactViewRow>(StringComparer.Ordinal);
        foreach (CompactViewRow row in before)
        {
            previous[row.Reference] = row;
        }
        var current = after.Select(row => row.Reference).ToHashSet(StringComparer.Ordinal);
        var items = new List<CompactViewItem>();
        foreach (CompactViewRow row in after)
        {
            previous.TryGetValue(row.Reference, out CompactViewRow? earlier);
            bool shownWhole = revealAsChanged && earlier != null && Offscreen(earlier) && !Offscreen(row);
            if (earlier == null || WithoutVisibility(earlier) != WithoutVisibility(row) || shownWhole)
            {
                items.Add(new(CompactViewItemKind.Changed, row, row.Reference));
            }
            if (earlier != null && Offscreen(earlier) != Offscreen(row) && !shownWhole)
            {
                items.Add(new(CompactViewItemKind.Visibility, null, row.Reference, Offscreen(row)));
            }
        }
        foreach (string reference in previous.Keys)
        {
            if (!current.Contains(reference))
            {
                items.Add(new(CompactViewItemKind.Removed, null, reference));
            }
        }

        return items;
    }

    private static string WithoutVisibility(CompactViewRow row)
    {
        JsonObject attributes = row.Attributes.DeepClone().AsObject();
        attributes.Remove("offscreen");

        return CompactJsonText.Serialize(attributes);
    }

    internal static bool Offscreen(CompactViewRow row)
    {
        return row.Attributes["offscreen"] is JsonValue value && value.TryGetValue(out bool offscreen) && offscreen;
    }
}
