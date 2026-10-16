using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Native role names remain exact protocol data; typed families guide compaction without assuming every backend role is known.
public enum CompactRoleFamily
{
    Window,
    Dialog,
    Document,
    DesktopRoot,
    Editable,
    Interactive,
    Other
}

// Tree adapters classify boundary role names while exact names remain part of the target/type identity.
public sealed record CompactRole(string Name, CompactRoleFamily Family)
{
    private static readonly Regex EditablePattern = new(@"\bEdit\b|entry",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InteractivePattern = new(
        @"button|\bEdit\b|entry|tab|menu item|list item|check|radio|combo|slider|spin|link",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static CompactRole Parse(string name)
    {
        if (CompactRoleVocabulary.TryEntry(name, out CompactRoleVocabularyEntry? entry))
        {
            return new(name, entry.Family);
        }
        string normalized = name.ToLowerInvariant();
        CompactRoleFamily family = normalized switch
        {
            "window" or "frame" => CompactRoleFamily.Window,
            "dialog" or "file chooser" => CompactRoleFamily.Dialog,
            "document web" => CompactRoleFamily.Document,
            "root" => CompactRoleFamily.DesktopRoot,
            _ => EditablePattern.IsMatch(name) ? CompactRoleFamily.Editable
                : InteractivePattern.IsMatch(name) ? CompactRoleFamily.Interactive : CompactRoleFamily.Other
        };

        return new(name, family);
    }
}

// The native tree reader converts one reported row without retaining screenshots, bounds or coordinate input.
public sealed record CompactTreeNode(int Depth, int Id, CompactRole Role, string Name, string AutoId,
    string? Value, bool Enabled, bool Offscreen);

// A semantic container path starts inside the owning window rather than at a native desktop ancestor.
public sealed record CompactContainerSelector(CompactRole Role, string? AutoId, string? Name);

// Target identities stay volatile and require a fresh native tree before any later input. A null window kind means the
// owning window could not be attributed to one observed native window, so the target cannot be acted on.
public sealed record CompactSemanticTarget(int ProcessId, string Name, CompactRole Role, string AutoId, CompactWindowKind? WindowKind,
    IReadOnlyList<CompactContainerSelector>? ContainerPath, long? WindowHandle = null, string? ProcessStarted = null,
    bool UnverifiedWindow = false, bool Ambiguous = false, string? NamePrefix = null);

// Projection or target resolution failures remain typed until the MCP boundary reports their established message.
public enum CompactTreeError
{
    UnsupportedFormat,
    RootAmbiguousOrAbsent,
    WindowIdentityChanged,
    TargetAbsentOrAmbiguous,
    TargetTypeChanged,
    TargetDisabled
}

// Native parsing/resolution returns named evidence rather than throwing for ordinary application state changes.
public sealed record CompactTreeAttempt<T>(T? Value, CompactTreeError? Error, string? Detail = null);
