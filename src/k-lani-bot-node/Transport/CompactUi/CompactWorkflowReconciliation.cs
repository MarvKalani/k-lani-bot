using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Resume queries original operation IDs and current application state; it never dispatches the missing suffix of an uncertain stage.
internal sealed class CompactWorkflowReconciliation(ICompactWorkflowAdapter adapter)
{
    // Throws CompactWorkflowPersistenceException when reconciled progress cannot be committed.
    public async Task<bool> ReconcileAsync(CompactWorkflowExecutionState state, CancellationToken cancellation)
    {
        CompactWorkflowPending pending = state.Job.Pending!;
        if (pending.Phase == CompactWorkflowPendingPhase.Prepared && pending.Operations.Count == 0)
        {
            state.Save(state.Job with
            {
                Pending = null
            });
            return true;
        }
        try
        {
            if (CompactWorkflowRuntimePolicy.PrefixOnly(pending))
            {
                state.Mark(CompactWorkflowStatus.Unconfirmed,
                    "Only a prefix of the stage was attempted; its remaining effects cannot be inferred.");
                return false;
            }
            if (!await LedgerAsync(state, cancellation))
            {
                return false;
            }
            return await ObserveAsync(state, cancellation);
        }
        catch (CompactWorkflowPersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.Mark(CompactWorkflowStatus.Unconfirmed, exception.Message);
            return false;
        }
    }

    // Throws CompactWorkflowPersistenceException when a ledger refusal cannot be checkpointed.
    private async Task<bool> LedgerAsync(CompactWorkflowExecutionState state, CancellationToken cancellation)
    {
        foreach (CompactWorkflowOperation operation in state.Job.Pending!.Operations)
        {
            var result = await adapter.OperationStatusAsync(operation.RequestId, state.Job, cancellation);
            if (CompactWorkflowRuntimePolicy.Refused(result))
            {
                var operations = state.Job.Pending.Operations.Select(entry => entry.RequestId == operation.RequestId
                    ? entry with
                    {
                        Response = result
                    } : entry).ToArray();
                state.Job = state.Job with
                {
                    Pending = state.Job.Pending with
                    {
                        Operations = operations
                    }
                };
                state.Mark(CompactWorkflowRuntimePolicy.PermissionBlocked(operations)
                    ? CompactWorkflowStatus.Blocked : CompactWorkflowStatus.Unconfirmed, "Native scope or consent refused the operation.");
                return false;
            }
            if (!result.Success || !result.Outcome.ReportsSuccess())
            {
                state.Mark(CompactWorkflowStatus.Unconfirmed, "Native ledger cannot establish the previous effect.");
                return false;
            }
        }

        return true;
    }

    // Throws CompactWorkflowPersistenceException when fresh effects cannot be checkpointed.
    private async Task<bool> ObserveAsync(CompactWorkflowExecutionState state, CancellationToken cancellation)
    {
        CompactWorkflowPending pending = state.Job.Pending!;
        CompactWorkflowStage stage = state.Job.Definition.Stages.Single(stage => stage.Id == pending.StageId);
        var result = await adapter.ReconcileAsync(stage, pending.Parameters, pending.Before, state.Job, cancellation);
        if (result.Value != null && result.Error == null)
        {
            var verification = await adapter.VerifyAsync(stage, pending.Parameters, pending.Before, result.Value, state.Job, cancellation);
            if (verification.Error == null && verification.Value == true)
            {
                state.Complete(stage.Id, result.Value);
                return true;
            }
        }
        state.Mark(CompactWorkflowStatus.Unconfirmed, "Current application state does not establish the checkpointed effect.");

        return false;
    }
}
