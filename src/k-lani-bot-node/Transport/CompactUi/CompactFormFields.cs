using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Form refresh converts native rows and explicit metadata into immutable instance-bound fields, keeping values volatile.
public static class CompactFormFields
{
    private static readonly Regex TextRole = new("Edit|entry", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChoiceRole = new("combo|list box", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryKind(CompactRole role, out CompactFormFieldKind kind)
    {
        kind = CompactFormFieldKind.Text;
        if (CompactRoleVocabulary.TryEntry(role.Name, out CompactRoleVocabularyEntry? entry))
        {
            return TryVocabularyKind(entry, out kind);
        }
        if (TextRole.IsMatch(role.Name))
        {
            return true;
        }
        if (ChoiceRole.IsMatch(role.Name))
        {
            kind = CompactFormFieldKind.Choice;
            return true;
        }

        return false;
    }

    // A vocabulary entry decides alone, so an action or other non-form role is not matched by the broad patterns.
    private static bool TryVocabularyKind(CompactRoleVocabularyEntry entry, out CompactFormFieldKind kind)
    {
        kind = CompactFormFieldKind.Text;
        if (entry.FieldKind is CompactFormFieldKind.Text or CompactFormFieldKind.Choice)
        {
            kind = entry.FieldKind.Value;

            return true;
        }

        return false;
    }

    public static CompactFormField Create(int processId, CompactFormSnapshot snapshot, CompactNativeWindow window, CompactTreeNode node,
        IReadOnlyDictionary<string, CompactFormField> previous)
    {
        CompactSemanticTarget target = CompactSemanticIdentity.Target(processId, snapshot.Nodes, node, window);
        string key = Fingerprint(JsonNode.Parse(CompactSemanticIdentity.StableKey(target))!);
        bool present = snapshot.Metadata.TryGetValue(node.Id, out UiFieldMetadata? observed);
        UiFieldMetadata metadata = observed ?? UiFieldMetadata.Unsupported;
        UiChoiceMetadata choices = metadata.Choices;
        UiChoiceOption? selected = choices.Options.FirstOrDefault(option => option.Id == choices.SelectedId);
        TryKind(node.Role, out CompactFormFieldKind kind);

        return new(previous.GetValueOrDefault(key)?.Reference ?? new(Guid.NewGuid()), key, node.Name, node.Role, kind,
            kind == CompactFormFieldKind.Choice ? selected?.Label : node.Value,
            Known(metadata.Visible), metadata.Enabled ?? node.Enabled, Known(metadata.ReadOnly), Known(metadata.Required),
            metadata, node.Id, target, choices.Options, present ? choices.Revision : "unknown", present);
    }

    public static CompactKnownBoolean Known(bool? value)
    {
        return value == null ? CompactKnownBoolean.Unknown : value.Value ? CompactKnownBoolean.True : CompactKnownBoolean.False;
    }

    public static string Fingerprint(JsonNode value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CompactJsonText.Serialize(value))));
    }

    public static bool Matches(CompactFormField field, string name)
    {
        return name == field.Reference.Value.ToString() || name == field.Name || name == field.Metadata.Field;
    }
}
