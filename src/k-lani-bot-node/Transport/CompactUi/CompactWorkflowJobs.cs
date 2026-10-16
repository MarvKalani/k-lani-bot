using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Private jobs retain a finite lifecycle distinct from the status of any single native operation.
public enum CompactWorkflowStatus
{
    Running,
    AwaitingInput,
    Blocked,
    Unconfirmed,
    Failed,
    Completed
}

// A pending stage distinguishes a durable preflight from possible dispatch and fully verified effects.
public enum CompactWorkflowPendingPhase
{
    Prepared,
    Dispatching,
    Verified
}

// Native operation intent is checkpointed before dispatch; a missing reply never becomes an implicit retry.
public enum CompactWorkflowOperationPhase
{
    Intent,
    Dispatching,
    Responded
}

// Existing node ledger outcomes retain their typed success and refusal code without assuming dispatched means verified.
public sealed record CompactWorkflowOperationResult(bool Success, ActionOutcome Outcome, ActionErrorCode? Code = null,
    JsonObject? Source = null);

// One operation keeps its original UUIDv7 and exact private request payload for reconciliation through the node ledger.
public sealed record CompactWorkflowOperation(Guid RequestId, Tool Tool, int? StepIndex, JsonObject Arguments,
    CompactWorkflowOperationPhase Phase, CompactWorkflowOperationResult? Response = null);

// Stage evidence binds the current function body and complete before-values to the set of mutation steps expected during execution.
public sealed record CompactWorkflowStageEvidence(JsonObject Source, int? ExpectedSteps = null,
    IReadOnlyList<int>? MutationSteps = null);

// A pending stage retains intent, observed baseline and complete values privately until its effects are reconciled or confirmed.
public sealed record CompactWorkflowPending(string StageId, CompactWorkflowPendingPhase Phase, CompactWorkflowStageEvidence Before,
    IReadOnlyDictionary<string, CompactWorkflowValue> Parameters, IReadOnlyList<CompactWorkflowOperation> Operations,
    int? StepIndex = null, IReadOnlyDictionary<string, CompactWorkflowValue>? Outputs = null);

// Workflow jobs bind application aliases to actual process/window lifetimes rather than saved element IDs or coordinates.
public sealed record CompactWorkflowInstance(int ProcessId, string Started, long Handle);

// A runtime decision records the missing declared input and bounded owner prompt, without inventing approval.
public sealed record CompactWorkflowPendingDecision(string Input, string Prompt);

// Opaque job references are authority/request digests, never filesystem paths supplied by the model.
public sealed record CompactWorkflowJobReference(CompactContractDigest Digest);

// Immutable job snapshots retain confirmed stages and original operation IDs through an atomic private checkpoint.
public sealed record CompactWorkflowJob(CompactWorkflowJobReference Reference, CompactContractDigest Authority,
    CompactContractDigest RequestFingerprint, CompactWorkflowDefinition Definition,
    IReadOnlyDictionary<string, CompactWorkflowValue> Parameters, long Revision, CompactWorkflowStatus Status,
    IReadOnlyList<string> CompletedStages, IReadOnlyDictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>> Outputs,
    CompactWorkflowPending? Pending, IReadOnlyDictionary<string, CompactWorkflowInstance> Applications,
    string CreatedAt, string UpdatedAt, CompactWorkflowPendingDecision? Decision = null, string? Error = null,
    IReadOnlyDictionary<string, CompactWorkflowStageEvidence>? StageEvidence = null,
    IReadOnlyDictionary<string, CompactContractDigest>? OutputDigests = null,
    IReadOnlyDictionary<string, CompactWorkflowValue>? FinalOutputs = null,
    JsonObject? AdapterState = null);

// Job failures identify private-store, authority or revision problems without authorizing native input.
public enum CompactWorkflowJobError
{
    InvalidConfiguration,
    InvalidRequestId,
    RequestConflict,
    Full,
    TooLarge,
    UnknownJob,
    ForeignOrInvalidJob,
    ForeignJob,
    InvalidLock,
    Locked,
    StaleRevision,
    InvalidCheckpoint,
    StorageUnavailable,
    UnknownOperation,
    InvalidDefinition,
    InvalidParameters,
    ImmutableInput
}

// Private job attempts preserve the failed layer and optional OS/protocol diagnostic.
public sealed record CompactWorkflowJobAttempt<T>(T? Value, CompactWorkflowJobError? Error = null, string? Detail = null);

// A private file read carries its byte digest alongside typed state so external edits cannot be hidden behind the same revision.
public sealed record CompactWorkflowJobSnapshot(CompactWorkflowJob Job, string Digest);
