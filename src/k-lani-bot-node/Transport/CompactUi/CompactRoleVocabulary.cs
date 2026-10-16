using System.Diagnostics.CodeAnalysis;

namespace KLaniBot.Transport.CompactUi;

// Native platform whose exact role names have their own vocabulary; Linux names are classified by the broad patterns.
public enum CompactRolePlatform
{
    Linux,
    Windows,
    MacOs
}

// One exact native role name with the family and form field kind that both runtimes report for it.
public sealed record CompactRoleVocabularyEntry(CompactRolePlatform Platform, string RoleName, CompactRoleFamily Family,
    CompactFormFieldKind? FieldKind);

// Tree parsing and form discovery call this before their broad name patterns, so a platform role listed here keeps its own
// classification while every other name keeps the pattern result it had before.
public static class CompactRoleVocabulary
{
    private static readonly CompactRoleVocabularyEntry[] LinuxEntries = [];
    private static readonly CompactRoleVocabularyEntry[] WindowsEntries =
    [
        new(CompactRolePlatform.Windows, "MenuItem", CompactRoleFamily.Interactive, CompactFormFieldKind.Action),
        new(CompactRolePlatform.Windows, "ListItem", CompactRoleFamily.Interactive, null),
        new(CompactRolePlatform.Windows, "TreeItem", CompactRoleFamily.Interactive, null)
    ];
    private static readonly CompactRoleVocabularyEntry[] MacOsEntries =
    [
        new(CompactRolePlatform.MacOs, "TextField", CompactRoleFamily.Editable, CompactFormFieldKind.Text),
        new(CompactRolePlatform.MacOs, "TextArea", CompactRoleFamily.Editable, CompactFormFieldKind.Text),
        new(CompactRolePlatform.MacOs, "PopUpButton", CompactRoleFamily.Interactive, CompactFormFieldKind.Choice),
        new(CompactRolePlatform.MacOs, "MenuItem", CompactRoleFamily.Interactive, CompactFormFieldKind.Action),
        new(CompactRolePlatform.MacOs, "SecureTextField", CompactRoleFamily.Other, null)
    ];
    private static readonly CompactRoleVocabularyEntry[] Entries = [.. LinuxEntries, .. WindowsEntries, .. MacOsEntries];

    // Platform vocabularies differ in capitalisation, so names are compared without regard to case.
    public static bool TryEntry(string roleName, [NotNullWhen(true)] out CompactRoleVocabularyEntry? entry)
    {
        entry = Entries.FirstOrDefault(candidate => string.Equals(candidate.RoleName, roleName, StringComparison.OrdinalIgnoreCase));

        return entry != null;
    }

    // Only an Edit or entry name, or a vocabulary text field such as TextArea, takes plain typed text.
    // A password never qualifies, even when its name matches.
    public static bool IsPlainTextEntry(string roleName)
    {
        if (roleName.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (TryEntry(roleName, out CompactRoleVocabularyEntry? entry))
        {
            return entry.Family == CompactRoleFamily.Editable && entry.FieldKind == CompactFormFieldKind.Text;
        }

        return CompactRole.Parse(roleName).Family == CompactRoleFamily.Editable;
    }
}
