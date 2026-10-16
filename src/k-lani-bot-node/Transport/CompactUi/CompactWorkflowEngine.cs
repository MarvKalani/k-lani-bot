using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compact workflows serialize run/resume per private job and keep confirmed progress separate from uncertain native effects.
public sealed class CompactWorkflowEngine(CompactWorkflowStore store, ICompactWorkflowAdapter adapter,
    ICompactWorkflowFunctions functions, IReadOnlyDictionary<string, CompactWorkflowDefinition> definitions)
{
    public Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>> RunAsync(string key, long revision, string requestId,
        JsonObject parameters, CancellationToken cancellation = default)
    {
        if (!definitions.TryGetValue(key, out var definition) || definition.Revision != revision
            || !CompactWorkflowDefinitionCodec.TryRead(definition.Source, functions, out var validated))
        {
            return Task.FromResult(new CompactWorkflowJobAttempt<CompactWorkflowProgress>(null, CompactWorkflowJobError.InvalidDefinition));
        }
        if (!CompactWorkflowParameters.TryRead(validated.Value!.Inputs, parameters, true, out var values))
        {
            return Task.FromResult(InvalidParameters(values));
        }
        var fingerprint = new CompactContractDigest(CompactFormFields.Fingerprint(new JsonObject
        {
            ["definition"] = validated.Value.Source.DeepClone(),
            ["parameters"] = parameters.DeepClone()
        }));
        if (!store.TryCreate(requestId, fingerprint, validated.Value, values.Value!, out var created))
        {
            return Task.FromResult(new CompactWorkflowJobAttempt<CompactWorkflowProgress>(null, created.Error, created.Detail));
        }

        return SerializedAsync(created.Value!.Reference, null, null, cancellation);
    }

    public bool TryStatus(CompactWorkflowJobReference reference, out CompactWorkflowJobAttempt<CompactWorkflowProgress> attempt)
    {
        if (!store.TryRead(reference, out var read))
        {
            attempt = new(null, read.Error, read.Detail);
            return false;
        }
        var state = new CompactWorkflowExecutionState(new(store, read.Value!));
        attempt = new(state.Progress());

        return true;
    }

    public Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>> ResumeAsync(CompactWorkflowJobReference reference,
        long expectedRevision, JsonObject? answers = null, CancellationToken cancellation = default)
    {
        return SerializedAsync(reference, expectedRevision, answers ?? new JsonObject(), cancellation);
    }

    private Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>> SerializedAsync(CompactWorkflowJobReference reference,
        long? expectedRevision, JsonObject? answers, CancellationToken cancellation)
    {
        return store.SerializedAsync<CompactWorkflowProgress>(reference, async snapshot =>
        {
            var state = new CompactWorkflowExecutionState(new(store, snapshot));
            try
            {
                if (expectedRevision != null)
                {
                    var resumed = await ResumeStateAsync(state, expectedRevision.Value, answers!, cancellation);
                    if (resumed != null)
                    {
                        return resumed;
                    }
                }
                await AdvanceAsync(state, cancellation);
                return new(state.Progress());
            }
            catch (CompactWorkflowPersistenceException exception)
            {
                return new(null, exception.Error, exception.Message);
            }
        }, cancellation);
    }

    // Throws CompactWorkflowPersistenceException when accepted answers or reconciled progress cannot be saved.
    private async Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>?> ResumeStateAsync(CompactWorkflowExecutionState state,
        long expectedRevision, JsonObject answers, CancellationToken cancellation)
    {
        if (state.Job.Revision != expectedRevision)
        {
            return new(null, CompactWorkflowJobError.StaleRevision);
        }
        if (!CompactWorkflowParameters.TryRead(state.Job.Definition.Inputs, answers, true, out var parsed))
        {
            return InvalidParameters(parsed);
        }
        if (!CompactWorkflowRuntimePolicy.TryAnswers(state.Job, parsed.Value!, out var parameters))
        {
            return new(null, CompactWorkflowJobError.ImmutableInput);
        }
        state.Save(state.Job with
        {
            Parameters = parameters!
        });
        if (!await RefreshAsync(state, state.Job.Pending == null ? CompactWorkflowStatus.Blocked : CompactWorkflowStatus.Unconfirmed,
            cancellation))
        {
            return new(state.Progress());
        }
        if (state.Job.Pending != null && !await new CompactWorkflowReconciliation(adapter).ReconcileAsync(state, cancellation))
        {
            return new(state.Progress());
        }

        return null;
    }

    // Throws CompactWorkflowPersistenceException when stage progress or final outputs cannot be saved.
    private async Task AdvanceAsync(CompactWorkflowExecutionState state, CancellationToken cancellation)
    {
        if (state.Job.Status == CompactWorkflowStatus.Completed)
        {
            return;
        }
        if (state.Job.Pending != null)
        {
            state.Mark(CompactWorkflowRuntimePolicy.PermissionBlocked(state.Job.Pending.Operations)
                ? CompactWorkflowStatus.Blocked : CompactWorkflowStatus.Unconfirmed,
                "Reconcile the previous native operation before continuing.");
            return;
        }
        state.Job = state.Job with
        {
            Status = CompactWorkflowStatus.Running
        };
        if (!await RefreshAsync(state, CompactWorkflowStatus.Blocked, cancellation))
        {
            return;
        }
        var runner = new CompactWorkflowStageRunner(adapter);
        foreach (var stage in state.Job.Definition.Stages.Where(stage => !state.Job.CompletedStages.Contains(stage.Id)))
        {
            string? missing = CompactWorkflowRuntimePolicy.MissingInput(stage, state.Job);
            if (missing != null)
            {
                AwaitInput(state, stage, missing);
                return;
            }
            state.Job = state.Job with
            {
                Decision = null
            };
            if (!await runner.ExecuteAsync(state, stage, cancellation))
            {
                return;
            }
        }
        Complete(state);
    }

    // Throws CompactWorkflowPersistenceException when a missing declared input cannot be checkpointed.
    private static void AwaitInput(CompactWorkflowExecutionState state, CompactWorkflowStage stage, string missing)
    {
        state.Job = state.Job with
        {
            Decision = new(missing, stage.Decision?.Prompt ?? "Supply the declared workflow input.")
        };

        state.Mark(CompactWorkflowStatus.AwaitingInput);
    }

    // Throws CompactWorkflowPersistenceException when final confirmed outputs cannot be saved.
    private static void Complete(CompactWorkflowExecutionState state)
    {
        if (!CompactWorkflowBindings.TryResolve(state.Job.Definition.Outputs, state.Job.Parameters, state.Job.Outputs, out var outputs))
        {
            state.Mark(CompactWorkflowStatus.Failed, CompactWorkflowNames.Error(outputs.Error!.Value, outputs.Detail));
            return;
        }
        state.Job = state.Job with
        {
            FinalOutputs = outputs.Value
        };
        state.Mark(CompactWorkflowStatus.Completed);
    }

    // Throws CompactWorkflowPersistenceException when a failed fresh application check cannot be saved.
    private async Task<bool> RefreshAsync(CompactWorkflowExecutionState state, CompactWorkflowStatus failure,
        CancellationToken cancellation)
    {
        try
        {
            var result = await adapter.RefreshInfoAsync(state.Job, cancellation);
            if (result.Value != null && result.Error == null)
            {
                state.Job = result.Value;
                return true;
            }
            state.Mark(failure, result.Error ?? "Current application info is unavailable.");
        }
        catch (CompactWorkflowPersistenceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            state.Mark(failure, exception.Message);
        }

        return false;
    }

    private static CompactWorkflowJobAttempt<CompactWorkflowProgress> InvalidParameters(
        CompactWorkflowAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>> parsed)
    {
        return new(null, CompactWorkflowJobError.InvalidParameters, CompactWorkflowNames.Error(parsed.Error!.Value, parsed.Detail));
    }
}
