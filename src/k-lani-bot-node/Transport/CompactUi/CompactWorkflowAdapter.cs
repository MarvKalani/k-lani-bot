namespace KLaniBot.Transport.CompactUi;

// The engine uses typed adapter failures to distinguish a refused preflight from an invalid function before any mutation.
public sealed record CompactWorkflowAdapterAttempt<T>(T? Value, string? Error = null, bool Blocked = true);

// A dispatched stage supplies complete outputs and its own verification result; the engine independently requests a fresh verification.
public sealed record CompactWorkflowExecution(bool Verified, IReadOnlyDictionary<string, CompactWorkflowValue> Outputs);

// Preparation returns fresh private instance/function metadata so the engine commits it together with intent before sending native input.
public sealed record CompactWorkflowPreparation(CompactWorkflowJob Job, CompactWorkflowStageEvidence Evidence);

// Native adapters bind fresh application instances, selected functions and ledger queries without executing scripts or replaying mutations.
public interface ICompactWorkflowAdapter
{
    Task<CompactWorkflowAdapterAttempt<CompactWorkflowJob>> RefreshInfoAsync(CompactWorkflowJob job, CancellationToken cancellation);
    Task<CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>> PrepareAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowJob job, CancellationToken cancellation);
    Task<CompactWorkflowAdapterAttempt<CompactWorkflowExecution>> ExecuteAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowCheckpoint recorder, CancellationToken cancellation);
    Task<CompactWorkflowAdapterAttempt<bool>> VerifyAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        IReadOnlyDictionary<string, CompactWorkflowValue> outputs, CompactWorkflowJob job, CancellationToken cancellation);
    Task<CompactWorkflowOperationResult> OperationStatusAsync(Guid originalRequestId, CompactWorkflowJob job,
        CancellationToken cancellation);
    Task<CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>> ReconcileAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowJob job, CancellationToken cancellation);
}

// Status replies expose progress only; full parameters, operation payloads and output evidence remain in the private job store.
public sealed record CompactWorkflowProgress(CompactWorkflowJobReference Reference, CompactWorkflowStatus Status, long Revision,
    IReadOnlyList<string> CompletedStages, string? NextStage, CompactWorkflowPendingDecision? Decision, string? Error);
