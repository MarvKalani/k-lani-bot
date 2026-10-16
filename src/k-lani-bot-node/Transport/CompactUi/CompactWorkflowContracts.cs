using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Workflow parameters cross a finite type boundary before a stage can bind them to a verified function.
public enum CompactWorkflowValueKind
{
    String,
    Number,
    Boolean,
    Enum,
    Document
}

// Input contracts contain bounds and finite choices, never literal runtime answers or executable expressions.
public sealed record CompactWorkflowInput(CompactWorkflowValueKind Kind, bool Required = true, int MaximumLength = 16000,
    double? Minimum = null, double? Maximum = null, IReadOnlyList<string>? Choices = null);

// Document parameters are opaque observed references with digest/provenance, not host filesystem paths.
public sealed record CompactWorkflowDocument(string Reference, CompactContractDigest Digest, string Origin);

// Runtime values retain their declared type and complete content until stage-local verification has succeeded.
public sealed record CompactWorkflowValue(CompactWorkflowValueKind Kind, string? Text = null, double? Number = null,
    bool? Boolean = null, CompactWorkflowDocument? Document = null);

// Binding definitions reference user inputs or earlier confirmed stage outputs, without embedding training literals.
public enum CompactWorkflowBindingKind
{
    Input,
    ConfirmedOutput
}

// One binding identifies exactly one input or a prior stage/output pair.
public sealed record CompactWorkflowBinding(CompactWorkflowBindingKind Kind, string? Input = null,
    string? Stage = null, string? Output = null);

// Applications are selected by a declared variant/build and observed process/window kind before a stage executes.
public sealed record CompactWorkflowApplication(CompactProgramIdentity Identity, string? ProcessName, CompactWindowKind? WindowKind);

// A stage loads only its selected function in one declared application branch.
public sealed record CompactWorkflowFunctionReference(string Application, string Name);

// A resolver supplies verified function metadata and typed parameter/output contracts independently of model assertions.
public sealed record CompactWorkflowFunctionContract(bool Demonstrated, bool CompatibilityPresent,
    IReadOnlyDictionary<string, CompactWorkflowValueKind> Parameters, IReadOnlyList<string> Required,
    IReadOnlyDictionary<string, CompactWorkflowValueKind> Outputs);

// Definition validation resolves selected host functions without loading their entire catalog into the model context.
public interface ICompactWorkflowFunctions
{
    bool TryResolve(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
        out CompactWorkflowFunctionContract? function);
}

// Decisions require an explicit boolean user input, with a bounded prompt and no model-confidence substitute.
public sealed record CompactWorkflowDecision(string Input, string Prompt);

// Every stage requires fresh unique enabled targets and the selected function's recorded postconditions.
public sealed record CompactWorkflowStage(string Id, CompactWorkflowFunctionReference Function,
    IReadOnlyDictionary<string, CompactWorkflowBinding> Bindings, CompactWorkflowDecision? Decision);

// A validated definition preserves the original ordered wire document for its exact revision hash.
public sealed record CompactWorkflowDefinition(string Key, long Revision, IReadOnlyDictionary<string, CompactWorkflowInput> Inputs,
    IReadOnlyDictionary<string, CompactWorkflowApplication> Applications, IReadOnlyList<CompactWorkflowStage> Stages,
    IReadOnlyDictionary<string, CompactWorkflowBinding> Outputs, JsonObject Source, CompactContractDigest Hash);

// Workflow contract failures remain finite and occur before native input, checkpoint creation or model execution.
public enum CompactWorkflowError
{
    UnsupportedFields,
    InvalidIdentity,
    InvalidBounds,
    InvalidInputName,
    InvalidValueType,
    InvalidEnum,
    InvalidLength,
    InvalidNumericBound,
    InvalidNumericRange,
    InvalidApplication,
    InvalidStage,
    UnknownFunction,
    InvalidConditions,
    InvalidStageBindings,
    BindingTypeMismatch,
    InvalidDecision,
    InvalidOutputName,
    UndeclaredInput,
    UnknownPriorOutput,
    MissingParameter,
    ParameterTypeMismatch,
    InvalidDocument,
    UnconfirmedOutput
}

// Named attempts preserve the exact failed contract layer and the optional input name in a missing-parameter refusal.
public sealed record CompactWorkflowAttempt<T>(T? Value, CompactWorkflowError? Error = null, string? Detail = null);
