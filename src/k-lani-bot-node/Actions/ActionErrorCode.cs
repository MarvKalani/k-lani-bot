using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Actions;

// Why an action was refused or failed. The code is for the AI client and the operator; the
// ActionOutcome next to it says what that means for the machine.
public enum ActionErrorCode
{
    // The principal or the local policy does not allow the action.
    Denied,

    // The request is malformed, refers to a stale target, or reuses an id with other content.
    InvalidRequest,

    // The platform or backend cannot do this.
    Unsupported,

    // The queue in front of the single desktop is full. Try again later.
    Busy,

    // The actuator has ended; an operator has to restart it.
    WorkerStopped,

    // The actuator failed during this request.
    WorkerUnavailable,

    // The connection between controller and node failed.
    TransportError,

    // The client cancelled before the request was sent.
    Cancelled,

    // The operation is admitted, but its result is not yet recorded.
    Pending,

    // No record of this request id.
    UnknownOperation,

    // The visible indicator could not be shown, so nothing was started.
    IndicatorUnavailable,

    // The person at the machine has not yet approved this session.
    ConsentRequired,

    // The person at the machine declined or revoked.
    ConsentDenied,

    // The person at the machine postponed the question.
    ConsentDeferred,

    // The request id was created before the retention window.
    RequestIdExpired,

    // The request id was created in the future; the clocks disagree.
    RequestIdFromFuture,

    // Too many operations inside the retention window.
    OperationLedgerFull,

    // A mutation did not carry a UUIDv7 request id.
    InvalidRequestId,

    // The screenshot is larger than the bounded transfer.
    ImageTooLarge,

    // The person at the machine paused the agent; the step was not dispatched.
    PausedByPerson,

    // No current backup covers this session's first change, and the administrator has not switched the requirement off.
    BackupRequired,

    // The window exists, but the accessibility bus publishes no tree for it; only the last-resort input route can operate it.
    NoAccessibilityTree
}

// Receipts, journal entries and failure answers carry the error code as a word; this table gives each code its word and reads it back.
public static class ActionErrorCodeNames
{
    public static string ToWireName(this ActionErrorCode code)
    {
        return code switch
        {
            ActionErrorCode.Denied => "denied",
            ActionErrorCode.InvalidRequest => "invalid_request",
            ActionErrorCode.Unsupported => "unsupported",
            ActionErrorCode.Busy => "busy",
            ActionErrorCode.WorkerStopped => "worker_stopped",
            ActionErrorCode.WorkerUnavailable => "worker_unavailable",
            ActionErrorCode.TransportError => "transport_error",
            ActionErrorCode.Cancelled => "cancelled",
            ActionErrorCode.Pending => "pending",
            ActionErrorCode.UnknownOperation => "unknown_operation",
            ActionErrorCode.IndicatorUnavailable => "indicator_unavailable",
            ActionErrorCode.ConsentRequired => "consent_required",
            ActionErrorCode.ConsentDenied => "consent_denied",
            ActionErrorCode.ConsentDeferred => "consent_deferred",
            ActionErrorCode.RequestIdExpired => "request_id_expired",
            ActionErrorCode.RequestIdFromFuture => "request_id_from_future",
            ActionErrorCode.OperationLedgerFull => "operation_ledger_full",
            ActionErrorCode.InvalidRequestId => "invalid_request_id",
            ActionErrorCode.ImageTooLarge => "image_too_large",
            ActionErrorCode.PausedByPerson => "paused_by_person",
            ActionErrorCode.BackupRequired => "backup_required",
            ActionErrorCode.NoAccessibilityTree => "no_accessibility_tree",
            _ => throw new UnreachableException()
        };
    }

    // A code word that this version does not know is simply not that code. Callers that wait for
    // one particular refusal (for example a pending consent) compare with this.
    public static bool Is(JsonObject result, ActionErrorCode code)
    {
        return result["code"]?.ToString() == code.ToWireName();
    }
}
