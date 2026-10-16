namespace KLaniBot.Transport.CompactUi;

// Private discovery binds one application variant and build; revisions never create another application identity.
public sealed record CompactProgramIdentity(string Key, string Build);

// Discovery stores semantic selectors only, excluding process IDs, handles, coordinates and current field values. The owning window
// is named by its kind, so a caption change never changes what a stored selector means.
public sealed record CompactStoredSelector(CompactRole Role, string? Name, string? AutoId, CompactWindowKind WindowKind,
    IReadOnlyList<CompactContainerSelector>? ContainerPath = null, string? NamePrefix = null)
{
    public CompactSemanticTarget Target(int processId)
    {
        return new(processId, Name ?? "", Role, AutoId ?? "", WindowKind, ContainerPath, NamePrefix: NamePrefix);
    }
}

// Scanning suggests a finite operation family, without approving any action for later execution.
public enum CompactSuggestedAction
{
    Read,
    SetText,
    Select,
    Toggle,
    Invoke,
    SetValue
}

// A stored field is observed inventory material, not an executable function or a business rule.
public sealed record CompactObservedField(string Key, string Name, bool NameTruncated, CompactFormFieldKind Kind,
    CompactRole Role, bool Enabled, bool Offscreen, bool TargetReady, CompactStoredSelector Selector);

// Discovery compatibility retains reported legacy metadata; it never substitutes for a fresh function-level assessment.
public sealed record CompactScanCompatibility(string NodeVersion, string Platform, string ApplicationVersion);

// A scan request has already crossed the MCP argument boundary; current contents remain response-only. The window is the
// native handle the caller named, and absent when the scan follows the remembered or only window.
public sealed record CompactScanOptions(int ProcessId, string Form, long? Window, string? Root, bool Contents,
    int MaximumCharacters, CompactResultOptions Result);

// Live content is deliberately separate from the persisted structural fields.
public sealed record CompactScanContent(string Key, string Name, string? Value, bool Truncated);

// A live scan preserves explicit clipping and text coverage without claiming unvisited controls were checked.
public sealed record CompactScanContents(IReadOnlyList<CompactScanContent> Fields, IReadOnlyList<string> Texts, bool TextTruncated);

// Changes describe successive observations; they do not establish which input caused the change.
public sealed record CompactScanChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed);

// A structural observation contains no live values; its digest includes only persisted descriptors.
public sealed record CompactObservedState(IReadOnlyList<CompactObservedField> Fields, CompactScanCompatibility Compatibility);

// A transition has an explicitly unknown cause, even when observations occurred immediately after an input.
public sealed record CompactObservedTransition(string From, string To);

// Discovery preserves bounded states and their history instead of replacing earlier completed observations.
public sealed record CompactObservedForm(string Latest, IReadOnlyDictionary<string, CompactObservedState> States,
    IReadOnlyList<CompactObservedTransition> Transitions);

// Scanners return a typed live observation before any private structural state is persisted.
public sealed record CompactScanObservation(string Form, CompactNativeWindow Window, string State,
    IReadOnlyList<CompactObservedField> Fields,
    CompactScanContents? Contents);

// A recorded scan returns state counts and changes separately from live application contents.
public sealed record CompactScanResult(CompactScanObservation Observation, int ObservedStates, CompactScanChanges Changes);

// Expected discovery failures remain distinct from native input failures; discovery never dispatches input.
public enum CompactDiscoveryError
{
    InvalidArguments,
    InvalidLookup,
    WindowAbsentOrAmbiguous,
    RootAbsentOrAmbiguous,
    ScopeAbsent,
    ControlBoundExceeded,
    StateBoundExceeded,
    UnknownState,
    InvalidDocument,
    DocumentBoundExceeded,
    InvalidPath,
    StorageUnavailable,
    StorageChanged
}

// Discovery APIs report typed errors and optional boundary diagnostics rather than throwing on ordinary input or storage failure.
public sealed record CompactDiscoveryAttempt<T>(T? Value, CompactDiscoveryError? Error = null, string? Detail = null);
