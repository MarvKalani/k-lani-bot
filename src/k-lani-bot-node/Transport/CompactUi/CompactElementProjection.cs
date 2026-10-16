using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// The compact host projects meaningful control/text records while retaining complete target identities separately.
public static class CompactElementProjection
{
    private const char ObjectReplacement = '\uFFFC';

    // Offscreen controls stay projected: a full page decides whether to list them, while deltas must still report their changes.
    public static bool IsProjected(IReadOnlyList<CompactTreeNode> all, CompactTreeNode node, string? contains)
    {
        string? value = UsefulValue(node.Value, node.Role);
        bool interactive = node.Role.Family is CompactRoleFamily.Editable or CompactRoleFamily.Interactive;
        if (!interactive && node.Name.Length == 0 && string.IsNullOrEmpty(value))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(contains) && !MatchesText(node.Name, value, contains))
        {
            return false;
        }

        return !all.Any(parent => CompactTreeScope.Contains(all, parent, node) && node.Name.Length > 0 && !interactive
            && (parent.Name.Length > 0 ? parent.Name : UsefulValue(parent.Value, parent.Role)) == node.Name);
    }

    // A name or a value matches; the two are compared separately so that a search cannot match across their boundary.
    private static bool MatchesText(string name, string? value, string text)
    {
        return name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || value != null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static string? UsefulValue(string? value, CompactRole role)
    {
        if (value == null)
        {
            return null;
        }
        if (role.Family == CompactRoleFamily.Editable)
        {
            return value;
        }
        string stripped = value.Replace(ObjectReplacement.ToString(), "", StringComparison.Ordinal);

        return value.Contains(ObjectReplacement) && string.IsNullOrWhiteSpace(stripped) ? null : stripped;
    }

    public static CompactViewRow Row(string reference, CompactTreeNode node, int maximumCharacters)
    {
        var fields = new JsonObject
        {
            ["ref"] = reference,
            ["role"] = node.Role.Name,
            ["name"] = Clip(node.Name, maximumCharacters)
        };
        if (node.Name.Length > maximumCharacters)
        {
            fields["name_truncated"] = true;
        }
        string? value = UsefulValue(node.Value, node.Role);
        if (value != null && (value != node.Name || node.Role.Family == CompactRoleFamily.Editable))
        {
            fields["value"] = Clip(value, maximumCharacters);
            if (value.Length > maximumCharacters)
            {
                fields["value_truncated"] = true;
            }
        }
        if (!node.Enabled)
        {
            fields["disabled"] = true;
        }
        if (node.Offscreen)
        {
            fields["offscreen"] = true;
        }

        return new(reference, fields);
    }

    private static string Clip(string text, int maximumCharacters)
    {
        return text[..Math.Min(text.Length, maximumCharacters)];
    }
}
