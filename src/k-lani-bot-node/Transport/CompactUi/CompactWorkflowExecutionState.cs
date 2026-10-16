namespace KLaniBot.Transport.CompactUi;

// Engine steps carry the current immutable job while every mutation intent is persisted through the existing checkpoint recorder.
internal sealed class CompactWorkflowExecutionState(CompactWorkflowCheckpoint checkpoint)
{
    public CompactWorkflowCheckpoint Checkpoint { get; } = checkpoint;
    public CompactWorkflowJob Job { get; set; } = checkpoint.Current.Job;

    // Throws CompactWorkflowPersistenceException when an atomic checkpoint cannot be committed; callers must stop before native input.
    public void Save(CompactWorkflowJob job)
    {
        if (!Checkpoint.TryUpdate(job, out var saved))
        {
            throw new CompactWorkflowPersistenceException(saved.Error!.Value, saved.Detail);
        }

        Job = saved.Value!;
    }

    // Throws CompactWorkflowPersistenceException when private progress cannot be committed.
    public void Mark(CompactWorkflowStatus status, string? error = null)
    {
        Save(Job with
        {
            Status = status,
            Error = error ?? Job.Error
        });
    }

    // Throws CompactWorkflowPersistenceException when confirmed outputs cannot be committed atomically with stage completion.
    public void Complete(string stage, IReadOnlyDictionary<string, CompactWorkflowValue> values)
    {
        var outputs = Job.Outputs.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        outputs[stage] = values;
        var evidence = Job.StageEvidence?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal) ?? new();
        evidence[stage] = Job.Pending!.Before;
        var digests = Job.OutputDigests?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal) ?? new();
        digests[stage] = new(CompactFormFields.Fingerprint(CompactWorkflowWire.Values(values)));

        Save(Job with
        {
            Outputs = outputs,
            StageEvidence = evidence,
            OutputDigests = digests,
            CompletedStages = Job.CompletedStages.Append(stage).ToArray(),
            Pending = null,
            Status = CompactWorkflowStatus.Running,
            Error = null
        });
    }

    public CompactWorkflowProgress Progress()
    {
        return new(Job.Reference, Job.Status, Job.Revision, Job.CompletedStages,
            Job.Definition.Stages.FirstOrDefault(stage => !Job.CompletedStages.Contains(stage.Id))?.Id, Job.Decision, Job.Error);
    }
}

// Checkpoint failures cross async adapter boundaries as an explicit store error and never authorize a retry of native input.
internal sealed class CompactWorkflowPersistenceException(CompactWorkflowJobError error, string? detail) : Exception(detail)
{
    public CompactWorkflowJobError Error { get; } = error;
}
