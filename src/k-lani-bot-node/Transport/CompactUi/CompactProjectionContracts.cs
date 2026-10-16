namespace KLaniBot.Transport.CompactUi;

// The native observation route supplies current scope/lifetime metadata and independent paging options to projection. The tree was
// read for exactly one observed window, which owns every row it projects.
public sealed record CompactProjectionOptions(string Node, string Instance, string? Root = null, bool IncludeOffscreen = true,
    string? Contains = null, int MaximumCharacters = 2000, CompactViewOptions? Page = null,
    CompactNativeWindow? ObservedWindow = null);

// A published reference still needs a verified owning window and an unambiguous identity before fresh resolution.
public enum CompactReferenceError
{
    Unknown,
    Ambiguous,
    UnverifiedWindow
}

// Target lookup returns typed failure evidence without granting authority from a reference string alone.
public sealed record CompactTargetAttempt(CompactSemanticTarget? Value, CompactReferenceError? Error);

// Projection reports either one paged view or a precise tree/page failure at the protocol boundary.
public sealed record CompactProjectionAttempt(CompactViewPage? Value, CompactTreeError? TreeError,
    CompactViewError? PageError, string? Detail = null);
