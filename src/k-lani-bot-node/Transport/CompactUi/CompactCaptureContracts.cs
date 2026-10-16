namespace KLaniBot.Transport.CompactUi;

// Captured functions retain only a finite supported action family, never arbitrary scripts or coordinate input.
public enum CompactRecipeAction
{
    Click,
    Write,
    Select,
    Read,
    Key
}

// A demonstration records the route actually used; recording a route is distinct from compatibility approval for reuse.
public enum CompactRecipeRoute
{
    Semantic,
    NativeText,
    NativeChoice,
    NativeRead,
    FocusKeyboard
}

// Output contracts address the observed caption or full native value rather than a screenshot or stored training answer.
public enum CompactOutputField
{
    Name,
    Value
}

// Reuse evaluates an explicit output postcondition rather than equating dispatched input with successful completion.
public enum CompactOutputCheckKind
{
    Changed,
    EqualsParameter,
    Exists,
    NotEmpty
}

// Cartridge topics isolate a selected branch beneath one of the product's four top-level categories.
public enum CompactTopicCategory
{
    Administration,
    Use,
    Info,
    Programming
}

// A topic branch remains application data; its category is typed before discovery or capture uses it.
public sealed record CompactCartridgeTopic(CompactTopicCategory Category, string Branch);

// Live demonstration inputs are volatile and never copied into the persistent recipe step.
public sealed record CompactDemonstrationStep(CompactRecipeAction Action, CompactSemanticTarget? Target,
    CompactRecipeRoute Route, string? Text = null, string? Reference = null);

// Only the gateway's current successful demonstration can supply capture evidence and before/after observations.
// The window handle names the window the demonstration acted in, so learning proves that window and no other of the process.
public sealed record CompactDemonstration(Guid Evidence, int ProcessId, IReadOnlyList<CompactDemonstrationStep> Steps,
    IReadOnlyList<CompactTreeNode> Before, IReadOnlyList<CompactTreeNode> After, long? WindowHandle = null);

// Capture references must resolve within the gateway's published application scope, not arbitrary native IDs.
public sealed record CompactCaptureOutput(string Name, string Reference, CompactOutputField Field, string? NamePrefix = null);

// A validated capture request names every write parameter and the observed goal outputs explicitly.
// The optional description tells a later model what the function does before it trusts it.
public sealed record CompactCaptureRequest(string Name, CompactCartridgeTopic Topic, IReadOnlyList<string> Parameters,
    IReadOnlyList<CompactCaptureOutput> Outputs, string? Description = null);

// A saved step contains semantic identity and a parameter name, excluding the live text, PID, snapshot and window handle.
// A false flag lets preflight accept a target that was disabled before the first action; dispatch still requires it enabled.
public sealed record CompactRecipeStep(CompactRecipeAction Action, CompactStoredSelector Selector, CompactRecipeRoute Route,
    string? Parameter = null, bool EnabledBeforeFirstAction = true);

// One typed output condition can refer to a declared parameter, but cannot contain a training value or expression.
public sealed record CompactRecipeOutput(string Name, CompactStoredSelector Selector, CompactOutputField Field,
    CompactOutputCheckKind Check, string? Parameter = null);

// Capture distinguishes a verified mutation demonstration from a freshly observed nonempty read source.
public enum CompactCaptureValidation
{
    DemonstratedMutation,
    ObservedRead
}

// A compiled draft still needs trusted compatibility evidence and an atomic library installation before it is callable.
// Its description is wording for the listing only; the contract hash leaves it out, so rewording never forces revalidation.
public sealed record CompactFunctionDraft(string Name, CompactCartridgeTopic Topic, IReadOnlyList<CompactRecipeStep> Steps,
    IReadOnlyList<CompactRecipeOutput> Outputs, CompactCaptureValidation Validation,
    System.Text.Json.Nodes.JsonObject? DeclaredInputSchema = null, string? Description = null);

// Optional reads preserve absence separately from a present field whose current value is null.
public sealed record CompactOptionalOutput(bool Exists, string? Value);

// Capture failures cannot authorize input, storage, a retry or a new compatibility receipt.
public enum CompactCaptureError
{
    EvidenceMissingOrUnknown,
    InvalidNameOrTopic,
    InvalidDescription,
    UnknownArgument,
    InvalidParameters,
    UnsupportedSequence,
    InvalidOutputs,
    InvalidOutput,
    InvalidPrefix,
    OwningWindowMissing,
    UnknownReference,
    ForeignReference,
    InvalidSelector,
    OutputAbsent,
    OutputAmbiguous,
    TargetTypeChanged,
    WindowAmbiguous,
    PrefixMismatch,
    OutputUnchanged,
    WrittenValueMismatch,
    InvalidRead,
    EmptyRead,
    InvalidDefinition,
    PostconditionFailed,
    OutputBoundExceeded,
    GoalOutputRequired,
    NavigationHeadingAmbiguous,
    CapturedScopeChanged
}

// Typed capture attempts return ordinary validation failures without throwing or promoting incomplete evidence.
public sealed record CompactCaptureAttempt<T>(T? Value, CompactCaptureError? Error = null, string? Detail = null);
