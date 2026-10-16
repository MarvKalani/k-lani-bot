using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// A running job persists step/intent/dispatch/response checkpoints before a backend may send the original UUIDv7 request.
public sealed class CompactWorkflowCheckpoint(CompactWorkflowStore store, CompactWorkflowJobSnapshot initial)
{
    private CompactWorkflowJobSnapshot _current = initial;
    public CompactWorkflowJobSnapshot Current => _current;

    public bool TryUpdate(CompactWorkflowJob job, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        if (!store.TryWrite(_current, job, out var saved))
        {
            attempt = new(null, saved.Error, saved.Detail);
            return false;
        }
        _current = saved.Value!;
        attempt = new(_current.Job);

        return true;
    }

    public bool TryStep(int index, out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        if (index < 0 || _current.Job.Pending == null)
        {
            attempt = new(null, CompactWorkflowJobError.InvalidCheckpoint);
            return false;
        }

        return TryUpdate(_current.Job with
        {
            Pending = _current.Job.Pending with
            {
                StepIndex = index
            }
        }, out attempt);
    }

    public bool TryBefore(Tool tool, JsonObject arguments, out CompactWorkflowJobAttempt<JsonObject> attempt)
    {
        CompactWorkflowPending? pending = _current.Job.Pending;
        if (pending == null || pending.Phase == CompactWorkflowPendingPhase.Verified)
        {
            attempt = new(null, CompactWorkflowJobError.InvalidCheckpoint);
            return false;
        }
        Guid identifier = Guid.CreateVersion7();
        JsonObject request = arguments.DeepClone().AsObject();
        request["request_id"] = identifier.ToString();
        var operation = new CompactWorkflowOperation(identifier, tool, pending.StepIndex, request, CompactWorkflowOperationPhase.Intent);
        var operations = pending.Operations.Append(operation).ToArray();
        if (!TryPending(operations, pending.Phase, out var intent))
        {
            attempt = new(null, intent.Error, intent.Detail);
            return false;
        }
        operations[^1] = operation with
        {
            Phase = CompactWorkflowOperationPhase.Dispatching
        };
        if (!TryPending(operations, CompactWorkflowPendingPhase.Dispatching, out var dispatch))
        {
            attempt = new(null, dispatch.Error, dispatch.Detail);
            return false;
        }
        attempt = new(request.DeepClone().AsObject());

        return true;
    }

    private bool TryPending(IReadOnlyList<CompactWorkflowOperation> operations, CompactWorkflowPendingPhase phase,
        out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        var pending = _current.Job.Pending! with
        {
            Operations = operations,
            Phase = phase
        };

        return TryUpdate(_current.Job with
        {
            Pending = pending
        }, out attempt);
    }

    public bool TryAfter(Guid identifier, CompactWorkflowOperationResult response,
        out CompactWorkflowJobAttempt<CompactWorkflowJob> attempt)
    {
        CompactWorkflowPending? pending = _current.Job.Pending;
        int index = pending?.Operations.ToList().FindIndex(operation => operation.RequestId == identifier) ?? -1;
        if (pending == null || index < 0)
        {
            attempt = new(null, CompactWorkflowJobError.UnknownOperation);
            return false;
        }
        CompactWorkflowOperation[] operations = pending.Operations.ToArray();
        operations[index] = operations[index] with
        {
            Phase = CompactWorkflowOperationPhase.Responded,
            Response = response
        };

        return TryUpdate(_current.Job with
        {
            Pending = pending with
            {
                Operations = operations
            }
        }, out attempt);
    }
}
