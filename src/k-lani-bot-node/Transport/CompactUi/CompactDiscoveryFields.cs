using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Native role observations produce bounded structural inventory; selectors are not verified recipes merely because they resolve.
public static class CompactDiscoveryFields
{
    public const int MaximumNameCharacters = 256;

    // The ordering preserves provider roles that match more than one broad operation family.
    private sealed record RoleRule(Regex Pattern, CompactFormFieldKind Kind);

    private static readonly RoleRule[] Rules =
    [
        Rule(@"\bEdit\b|entry", CompactFormFieldKind.Text),
        Rule("combo|list box", CompactFormFieldKind.Choice),
        Rule("check|radio|toggle button", CompactFormFieldKind.Toggle),
        Rule(@"\btab\b", CompactFormFieldKind.Navigation),
        Rule("button|menu item|link", CompactFormFieldKind.Action),
        Rule("spin|slider", CompactFormFieldKind.Number)
    ];

    private static RoleRule Rule(string pattern, CompactFormFieldKind kind)
    {
        return new(new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), kind);
    }

    public static bool TryKind(CompactRole role, out CompactFormFieldKind kind)
    {
        kind = CompactFormFieldKind.Text;
        if (CompactRoleVocabulary.TryEntry(role.Name, out CompactRoleVocabularyEntry? entry))
        {
            kind = entry.FieldKind ?? CompactFormFieldKind.Text;

            return entry.FieldKind.HasValue;
        }
        RoleRule? matched = Rules.FirstOrDefault(rule => rule.Pattern.IsMatch(role.Name)
            && (rule.Kind != CompactFormFieldKind.Navigation || !role.Name.Contains("tab list", StringComparison.OrdinalIgnoreCase)));
        if (matched == null)
        {
            return false;
        }
        kind = matched.Kind;

        return true;
    }

    public static CompactObservedField Create(IReadOnlyList<CompactTreeNode> nodes, CompactTreeNode node, CompactWindowKind window)
    {
        var selector = new CompactStoredSelector(node.Role, node.Name, node.AutoId, window);
        bool ready = (node.Name.Length > 0 || node.AutoId.Length > 0)
            && CompactTargetResolver.TryResolve(nodes, selector.Target(0), true, out _);
        TryKind(node.Role, out CompactFormFieldKind kind);

        return new(Key(selector), Clip(node.Name, MaximumNameCharacters), node.Name.Length > MaximumNameCharacters,
            kind, node.Role, node.Enabled, node.Offscreen, ready, selector);
    }

    public static string Key(CompactStoredSelector selector)
    {
        JsonObject identity = string.IsNullOrEmpty(selector.AutoId) ? CompactDiscoveryWire.Selector(selector) : new()
        {
            ["role"] = selector.Role.Name,
            ["autoId"] = selector.AutoId,
            ["window"] = CompactWindowKindWords.Text(selector.WindowKind)
        };

        return CompactFormFields.Fingerprint(identity)[..16];
    }

    public static string State(IReadOnlyList<CompactObservedField> fields)
    {
        return CompactFormFields.Fingerprint(new JsonArray(fields.Select(field => (JsonNode?)
            CompactDiscoveryWire.Field(field, true)).ToArray()))[..16];
    }

    public static IReadOnlyList<CompactSuggestedAction> Actions(CompactFormFieldKind kind)
    {
        return kind switch
        {
            CompactFormFieldKind.Text => [CompactSuggestedAction.Read, CompactSuggestedAction.SetText],
            CompactFormFieldKind.Choice => [CompactSuggestedAction.Read, CompactSuggestedAction.Select],
            CompactFormFieldKind.Toggle => [CompactSuggestedAction.Read, CompactSuggestedAction.Toggle],
            CompactFormFieldKind.Navigation => [CompactSuggestedAction.Invoke],
            CompactFormFieldKind.Action => [CompactSuggestedAction.Invoke],
            CompactFormFieldKind.Number => [CompactSuggestedAction.Read, CompactSuggestedAction.SetValue],
            _ => throw new UnreachableException()
        };
    }

    public static string Clip(string text, int maximum)
    {
        return text[..Math.Min(text.Length, maximum)];
    }
}
