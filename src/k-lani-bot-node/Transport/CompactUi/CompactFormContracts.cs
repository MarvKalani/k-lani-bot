using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Form callers preserve an absent native constraint instead of converting unknown into false.
public enum CompactKnownBoolean
{
    Unknown,
    False,
    True
}

// Native field roles suggest a bounded operation family without proving its implementation or business effect.
public enum CompactFormFieldKind
{
    Text,
    Choice,
    Toggle,
    Navigation,
    Action,
    Number
}

// The form gateway distinguishes text input from one explicitly observed choice identity.
public enum CompactFormValueKind
{
    Text,
    Option
}

// Public form references are gateway-session identifiers, never host paths or native widget IDs.
public sealed record CompactFormReference(Guid Value);

// A field reference survives an observed state refresh only within its original semantic scope.
public sealed record CompactFormFieldReference(Guid Value);

// Native refreshes supply current metadata and window/process lifetime evidence for a selected form scope; the tree is the scope's
// one native window.
public sealed record CompactFormSnapshot(int ProcessId, string SnapshotId, IReadOnlyList<CompactTreeNode> Nodes,
    IReadOnlyList<CompactNativeWindow> Windows, IReadOnlyDictionary<int, UiFieldMetadata> Metadata);

// A volatile form field retains complete values and real choice metadata; no field is an executable recipe by itself.
public sealed record CompactFormField(CompactFormFieldReference Reference, string Key, string Name, CompactRole Role,
    CompactFormFieldKind Kind, string? Value, CompactKnownBoolean Visible, bool Enabled, CompactKnownBoolean ReadOnly,
    CompactKnownBoolean Required, UiFieldMetadata Metadata, int NativeId, CompactSemanticTarget Target,
    IReadOnlyList<UiChoiceOption> Options, string OptionsRevision, bool MetadataPresent);

// A form mutation binds the current snapshot, type and declared context to the existing node dispatcher.
public sealed record CompactFormMutation(Tool Tool, int NativeId, string SnapshotId, CompactFormValueKind Kind,
    string Expected, string? Text = null, string? OptionId = null, string? OptionsRevision = null, string? Context = null,
    bool OpenOnly = false);

// The backend keeps Node-Dispatcher/Guard/OperationLedger enforcement; form code never instantiates a desktop visibility host.
public interface ICompactFormBackend
{
    Task<CompactFormSnapshot> RefreshAsync(int processId, long windowHandle, CancellationToken cancellation);
    Task<CompactFormDispatch> DispatchAsync(CompactFormMutation mutation, CancellationToken cancellation);
}

// A dispatched native input still needs a fresh complete postcondition before the form gateway reports verified.
public sealed record CompactFormDispatch(bool Success, string? Error = null);

// Expected form failures carry the failed layer without authorizing retries or changed-context input.
public enum CompactFormError
{
    InvalidArguments,
    UnknownOrExpiredForm,
    StaleContext,
    UnknownOrStaleField,
    FieldBoundExceeded,
    AmbiguousField,
    RequestedFieldUnknownOrAmbiguous,
    UnsupportedField,
    OptionsUnsupported,
    LiteralChoice,
    StaleOptions,
    OptionUnknownOrDisabled,
    StaleCursor,
    ScopeChanged,
    ObservationUnavailable,
    Unconfirmed
}

// Form orchestration returns named failure/value records rather than bool-plus-exception mutation paths.
public sealed record CompactFormAttempt<T>(T? Value, CompactFormError? Error, string? Detail = null);

// The selected literal or option must be typed at the protocol boundary before a native dispatch is prepared.
public sealed record CompactFormValue(CompactFormValueKind Kind, string? Text = null,
    string? OptionId = null, string? OptionsRevision = null);

// Current required-value/choice validation never promotes unknown constraints to a ready form.
public sealed record CompactFormValidation(bool Ready, IReadOnlyList<CompactFormFieldReference> MissingRequired,
    IReadOnlyList<CompactFormFieldReference> InvalidValues, IReadOnlyList<CompactFormFieldReference> UnknownConstraints);

// Form and option pages share one-use cursor bounds, with binding to the current context and selected field names.
public sealed record CompactFormCursor(Guid Value);

// A form page preserves whether the caller supplied an explicit limit when continuing an existing page.
public sealed record CompactFormPageOptions(CompactFormCursor? Cursor = null, int? Limit = null);

// Public pages contain only the selected records and an opaque continuation, never hidden current fields.
public sealed record CompactFormPage<T>(IReadOnlyList<T> Items, bool Complete, CompactFormCursor? NextCursor);

// Opening a native dropdown may expose real options but must preserve the current selection before any page is returned.
public sealed record CompactFormOptionsResult(CompactFormState Form, CompactFormField Field,
    CompactFormPage<UiChoiceOption> Page, bool NativeComplete, bool Opened, bool SelectionPreserved, string? Error = null);

// A set result preserves fresh revisions and declared dependency evidence without inferring causality from timing.
public sealed record CompactFormSetResult(bool Success, bool Verified, string ContextRevision, string StructureRevision,
    CompactFormFieldReference Field, IReadOnlyList<CompactFormFieldReference> InvalidatedFields,
    UiFieldMetadataSource MetadataSource, bool DependenciesKnown, string CheckedContext, string? Error = null);
