using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Job protocols preserve exact finite status/error names and keep unknown native effects unconfirmed.
public static class CompactWorkflowJobNames
{
    public static string Error(CompactWorkflowJobError error, string? detail = null)
    {
        return error switch
        {
            CompactWorkflowJobError.InvalidConfiguration => "Configure private workflow state and authority.",
            CompactWorkflowJobError.InvalidRequestId => "Use a bounded client_request_id.",
            CompactWorkflowJobError.RequestConflict => "client_request_id reused with different content.",
            CompactWorkflowJobError.Full => "workflow_store_full",
            CompactWorkflowJobError.TooLarge => "workflow_job_too_large",
            CompactWorkflowJobError.UnknownJob => "unknown_job",
            CompactWorkflowJobError.ForeignOrInvalidJob => "foreign_or_invalid_job",
            CompactWorkflowJobError.ForeignJob => "foreign_job",
            CompactWorkflowJobError.InvalidLock => "workflow_lock_invalid",
            CompactWorkflowJobError.Locked => "workflow_job_locked",
            CompactWorkflowJobError.StaleRevision => "stale_job_revision",
            CompactWorkflowJobError.InvalidCheckpoint => "Invalid workflow checkpoint.",
            CompactWorkflowJobError.StorageUnavailable => detail ?? "Private workflow checkpoint storage is unavailable.",
            CompactWorkflowJobError.UnknownOperation => "Unknown checkpointed native operation.",
            CompactWorkflowJobError.InvalidDefinition => "Unknown workflow revision.",
            CompactWorkflowJobError.InvalidParameters => detail ?? "Invalid workflow parameters.",
            CompactWorkflowJobError.ImmutableInput => "Confirmed workflow inputs cannot be replaced on resume.",
            _ => throw new UnreachableException()
        };
    }

    public static string Status(CompactWorkflowStatus status)
    {
        return status switch
        {
            CompactWorkflowStatus.Running => "running",
            CompactWorkflowStatus.AwaitingInput => "awaiting_input",
            CompactWorkflowStatus.Blocked => "blocked",
            CompactWorkflowStatus.Unconfirmed => "unconfirmed",
            CompactWorkflowStatus.Failed => "failed",
            CompactWorkflowStatus.Completed => "completed",
            _ => throw new UnreachableException()
        };
    }

    public static string Pending(CompactWorkflowPendingPhase phase)
    {
        return phase switch
        {
            CompactWorkflowPendingPhase.Prepared => "prepared",
            CompactWorkflowPendingPhase.Dispatching => "dispatching",
            CompactWorkflowPendingPhase.Verified => "verified",
            _ => throw new UnreachableException()
        };
    }

    public static string Operation(CompactWorkflowOperationPhase phase)
    {
        return phase switch
        {
            CompactWorkflowOperationPhase.Intent => "intent",
            CompactWorkflowOperationPhase.Dispatching => "dispatching",
            CompactWorkflowOperationPhase.Responded => "responded",
            _ => throw new UnreachableException()
        };
    }
}
