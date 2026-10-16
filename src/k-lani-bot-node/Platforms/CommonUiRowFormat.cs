using System.Text;
using System.Text.Json;

namespace KLaniBot.Platforms;

// Writes the common UI tree rows that the compact gateway parses. The Windows backend calls it after it has read every
// element. A row with an unknown field refuses the whole tree, because a substitute value would look like an observation.
public static class CommonUiRowFormat
{
    // Returned in place of a tree that cannot be completed; the handler turns it into a failed response.
    public const string IncompleteTreeError = "error: The native UI tree is incomplete; completion is unverified.";

    private const int SpacesPerLevel = 2;

    // Returns false when the walk failed or any field is unknown; the text is then empty and must not be used.
    public static bool TryFormat(IReadOnlyList<CommonUiRowReading> rows, bool walkComplete, out string text)
    {
        text = "";
        if (!walkComplete || rows.Any(row => !IsKnown(row)))
        {
            return false;
        }

        text = FormatRows(rows);

        return true;
    }

    private static string FormatRows(IReadOnlyList<CommonUiRowReading> rows)
    {
        var builder = new StringBuilder();
        foreach (CommonUiRowReading row in rows)
        {
            builder.Append(FormatRow(row)).Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsKnown(CommonUiRowReading row)
    {
        bool valueKnown = row.Value.State != CommonValueState.Unreadable
            && (row.Value.State != CommonValueState.Read || row.Value.Text != null);

        return row.Role != null && row.Name != null && row.AutoId != null && row.Enabled != null && row.Offscreen != null
            && valueKnown;
    }

    private static string FormatRow(CommonUiRowReading row)
    {
        string indent = new(' ', row.Depth * SpacesPerLevel);
        string nameText = JsonSerializer.Serialize(row.Name);
        string autoIdText = JsonSerializer.Serialize(row.AutoId);
        string valueText = row.Value.State == CommonValueState.Read ? JsonSerializer.Serialize(row.Value.Text) : "null";
        string enabledText = row.Enabled == true ? "True" : "False";
        string offscreenText = row.Offscreen == true ? "True" : "False";

        return $"{indent}[{row.Id}] {row.Role} {nameText} (autoId: {autoIdText}, val: {valueText}, "
            + $"enabled: {enabledText}, offscreen: {offscreenText})";
    }
}

// One element as read for the common rows. A null field is unknown; a password value is Withheld rather than unknown.
public sealed record CommonUiRowReading(int Depth, int Id, string? Role, string? Name, string? AutoId,
    CommonValueReading Value, bool? Enabled, bool? Offscreen);

// The value text is present only when the state is Read; Absent means the element has no value pattern.
public sealed record CommonValueReading(CommonValueState State, string? Text);

// Withheld is a deliberate redaction of a password value and is written as null; Unreadable makes the tree unknown.
public enum CommonValueState
{
    Absent,
    Read,
    Withheld,
    Unreadable
}
