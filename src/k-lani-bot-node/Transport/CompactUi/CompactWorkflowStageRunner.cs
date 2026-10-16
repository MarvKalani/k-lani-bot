namespace KLaniBot.Transport.CompactUi;

// Stage execution checkpoints preparation before input and confirms complete outputs only after a separate fresh observation.
internal sealed class CompactWorkflowStageRunner(ICompactWorkflowAdapter adapter)
{
    // Throws CompactWorkflowPersistenceException when a required private checkpoint fails.
    public async Task<bool> ExecuteAsync(CompactWorkflowExecutionState state, CompactWorkflowStage stage, CancellationToken cancellation)
    {
        if (!CompactWorkflowBindings.TryResolve(stage.Bindings, state.Job.Parameters, state.Job.Outputs, out var bound))
        {
            state.Mark(CompactWorkflowStatus.Failed, CompactWorkflowNames.Error(bound.Error!.Value, bound.Detail));
            return false;
        }
        CompactWorkflowAdapterAttempt<CompactWorkflowPreparation> prepared;
        try
        {
            prepared = await adapter.PrepareAsync(stage, bound.Value!, state.Job, cancellation);
        }
        catch (Exception exception)
        {
            state.Mark(CompactWorkflowStatus.Blocked, exception.Message);
            return false;
        }
        if (prepared.Error != null || prepared.Value == null)
        {
            state.Mark(prepared.Blocked ? CompactWorkflowStatus.Blocked : CompactWorkflowStatus.Failed,
                prepared.Error ?? "Stage preparation is unavailable.");
            return false;
        }
        state.Save(prepared.Value.Job with
        {
            Pending = new(stage.Id, CompactWorkflowPendingPhase.Prepared, prepared.Value.Evidence, bound.Value!, [])
        });

        return await DispatchAsync(state, stage, cancellation);
    }

    // Throws CompactWorkflowPersistenceException when an intent, response or verification checkpoint fails.
    private async Task<bool> DispatchAsync(CompactWorkflowExecutionState state, CompactWorkflowStage stage, CancellationToken cancellation)
    {
        try
        {
            var pending = state.Job.Pending!;
            var result = await adapter.ExecuteAsync(stage, pending.Parameters, pending.Before, state.Checkpoint, cancellation);
            state.Job = state.Checkpoint.Current.Job;
            if (result.Error != null || result.Value == null)
            {
                MarkUncertain(state, result.Error ?? "Stage execution is unavailable.");
                return false;
            }
            var verification = await adapter.VerifyAsync(stage, pending.Parameters, pending.Before, result.Value.Outputs,
                state.Job, cancellation);
            if (!result.Value.Verified || verification.Error != null || verification.Value != true)
            {
                state.Mark(CompactWorkflowStatus.Unconfirmed, "Stage effect did not satisfy its fresh postconditions.");
                return false;
            }
            state.Save(state.Job with
            {
                Pending = state.Job.Pending! with
                {
                    Phase = CompactWorkflowPendingPhase.Verified,
                    Outputs = result.Value.Outputs
                }
            });
            state.Complete(stage.Id, result.Value.Outputs);
            return true;
        }
        catch (CompactWorkflowPersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.Job = state.Checkpoint.Current.Job;
            MarkUncertain(state, exception.Message);
            return false;
        }
    }

    // Throws CompactWorkflowPersistenceException when uncertain progress cannot be committed.
    private static void MarkUncertain(CompactWorkflowExecutionState state, string error)
    {
        state.Mark(CompactWorkflowRuntimePolicy.PermissionBlocked(state.Job.Pending!.Operations)
            ? CompactWorkflowStatus.Blocked : CompactWorkflowStatus.Unconfirmed, error);
    }
}
