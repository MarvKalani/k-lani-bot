using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Staged workflows use actual native capabilities, selected application libraries and the original node ledger without replaying
// uncertain input.
public sealed class CompactNativeWorkflowAdapter(CompactNodeAccess node, CompactFunctionExecutor executor,
    ICompactWorkflowFunctionSource functions) : ICompactWorkflowAdapter
{
    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowJob>> RefreshInfoAsync(CompactWorkflowJob job, CancellationToken cancellation)
    {
        return GuardAsync(() => RefreshAsync(job, cancellation));
    }

    // Throws CompactNodeException when actual authority, application build or process/window lifetime is unknown or changed.
    private async Task<CompactWorkflowJob> RefreshAsync(CompactWorkflowJob job, CancellationToken cancellation)
    {
        await node.CapabilitiesAsync(null, cancellation);
        var windows = await node.WindowsAsync(cancellation);
        var instances = new Dictionary<string, CompactWorkflowInstance>(StringComparer.Ordinal);
        foreach (var application in job.Definition.Applications)
        {
            var window = CompactWorkflowApplicationBinding.Select(windows, application.Value,
                functions.ProcessName(application.Value.Identity), job.Applications.GetValueOrDefault(application.Key));
            instances.Add(application.Key, new(window.ProcessId, window.Started!, window.Handle));
        }

        return job with
        {
            Applications = instances
        };
    }

    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>> PrepareAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowJob job, CancellationToken cancellation)
    {
        return GuardAsync(() => PrepareStageAsync(stage, parameters, job, cancellation));
    }

    // Throws CompactNodeException when source/target/function evidence changed; ArgumentException for mismatching native inputs.
    private async Task<CompactWorkflowPreparation> PrepareStageAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowJob job, CancellationToken cancellation)
    {
        var refreshed = await RefreshAsync(job, cancellation);
        await RequireSourcesAsync(stage, refreshed, cancellation);
        var application = refreshed.Definition.Applications[stage.Function.Application];
        var function = Load(stage, application);
        var instance = await InstanceAsync(stage, refreshed, cancellation);
        var metadata = Metadata(refreshed, stage, function);
        CompactRecipeValues.RequireParameters(function.Draft, instance.ProcessId, NativeParameters(parameters));
        var prepared = await executor.PreflightAsync(function, application.Identity, instance, null, cancellation);

        return new(refreshed with
        {
            AdapterState = metadata
        }, CompactFunctionEvidence.Create(prepared));
    }

    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowExecution>> ExecuteAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowCheckpoint recorder, CancellationToken cancellation)
    {
        return GuardAsync(async () =>
        {
            var job = recorder.Current.Job;
            var prepared = await RestoreAsync(stage, before, job, cancellation);
            var result = await executor.ExecuteAsync(prepared, NativeParameters(parameters), recorder, Decision(stage, job), cancellation);
            return new CompactWorkflowExecution(true, CompactWorkflowApplicationBinding.Values(result.Outputs));
        });
    }

    public Task<CompactWorkflowAdapterAttempt<bool>> VerifyAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        IReadOnlyDictionary<string, CompactWorkflowValue> outputs, CompactWorkflowJob job, CancellationToken cancellation)
    {
        return GuardAsync(async () =>
        {
            var current = await CurrentOutputsAsync(stage, parameters, before, job, cancellation);
            return CompactFormFields.Fingerprint(CompactWorkflowWire.Values(current))
                == CompactFormFields.Fingerprint(CompactWorkflowWire.Values(outputs));
        });
    }

    public Task<CompactWorkflowOperationResult> OperationStatusAsync(Guid originalRequestId, CompactWorkflowJob job,
        CancellationToken cancellation)
    {
        return node.OperationStatusAsync(originalRequestId, cancellation);
    }

    public Task<CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>> ReconcileAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowJob job, CancellationToken cancellation)
    {
        return GuardAsync(() => CurrentOutputsAsync(stage, parameters, before, job, cancellation));
    }

    // Throws CompactNodeException when actual outputs, final replacements or recorded native scope no longer hold.
    private async Task<IReadOnlyDictionary<string, CompactWorkflowValue>> CurrentOutputsAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowJob job, CancellationToken cancellation)
    {
        var refreshed = await RefreshAsync(job, cancellation);
        var prepared = await RestoreAsync(stage, before, refreshed, cancellation);
        int verifiedBefore = job.Pending?.StageId == stage.Id ? job.Pending.StepIndex ?? 0 : 0;
        var outputs = await executor.CurrentOutputsAsync(prepared, NativeParameters(parameters), verifiedBefore, cancellation);

        return CompactWorkflowApplicationBinding.Values(outputs);
    }

    // Throws CompactNodeException for a changed read-only binding source, before any target input is sent.
    private async Task RequireSourcesAsync(CompactWorkflowStage stage, CompactWorkflowJob job,
        CancellationToken cancellation)
    {
        var sources = stage.Bindings.Values.Where(binding => binding.Kind == CompactWorkflowBindingKind.ConfirmedOutput)
            .Select(binding => binding.Stage!).Distinct(StringComparer.Ordinal);
        foreach (string id in sources)
        {
            var source = job.Definition.Stages.Single(candidate => candidate.Id == id);
            var function = Load(source, job.Definition.Applications[source.Function.Application]);
            if (function.Draft.Steps.Any(step => step.Action != CompactRecipeAction.Read))
            {
                continue;
            }
            if (!CompactWorkflowBindings.TryResolve(source.Bindings, job.Parameters, job.Outputs, out var parameters)
                || job.StageEvidence?.GetValueOrDefault(id) is not CompactWorkflowStageEvidence before
                || job.OutputDigests?.GetValueOrDefault(id) is not CompactContractDigest digest)
            {
                throw new CompactNodeException("Confirmed source evidence is unavailable.");
            }
            var current = await CurrentOutputsAsync(source, parameters.Value!, before, job, cancellation);
            if (CompactFormFields.Fingerprint(CompactWorkflowWire.Values(current)) != digest.Hex)
            {
                throw new CompactNodeException("Workflow document source digest changed before target input.");
            }
        }
    }

    // Throws CompactNodeException for missing or changed native window/body evidence.
    private async Task<CompactFunctionPreflight> RestoreAsync(CompactWorkflowStage stage, CompactWorkflowStageEvidence before,
        CompactWorkflowJob job, CancellationToken cancellation)
    {
        var application = job.Definition.Applications[stage.Function.Application];
        var function = Load(stage, application);
        var instance = await InstanceAsync(stage, job, cancellation);

        return CompactFunctionCheckpoint.Restore(function, application.Identity, instance, before);
    }

    // Throws CompactNodeException when the current application no longer matches its recorded native lifetime.
    private async Task<CompactNativeWindow> InstanceAsync(CompactWorkflowStage stage, CompactWorkflowJob job,
        CancellationToken cancellation)
    {
        var application = job.Definition.Applications[stage.Function.Application];
        var windows = await node.WindowsAsync(cancellation);

        return CompactWorkflowApplicationBinding.Select(windows, application, functions.ProcessName(application.Identity),
            job.Applications.GetValueOrDefault(stage.Function.Application));
    }

    // Throws CompactNodeException for a missing, unverified or invalid selected function.
    private CompactLoadedFunction Load(CompactWorkflowStage stage, CompactWorkflowApplication application)
    {
        if (!functions.TryLoad(stage.Function, application, out var loaded))
        {
            throw new CompactNodeException("Selected workflow function is unavailable or invalid.");
        }

        return loaded.Value!;
    }

    // Throws CompactNodeException when the private job has already bound a different function body to this stage.
    private static JsonObject Metadata(CompactWorkflowJob job, CompactWorkflowStage stage, CompactLoadedFunction function)
    {
        var metadata = job.AdapterState?.DeepClone().AsObject() ?? new JsonObject();
        var hashes = metadata["function_hashes"] as JsonObject ?? new JsonObject();
        string kind = metadata["function_hash_kind"]?.ToString() ?? (hashes.Count == 0 ? "function-scope/1" : "full-entry/1");
        string hash = CompactFunctionCheckpoint.Hash(function, kind);
        if (hashes[stage.Id] != null && hashes[stage.Id]!.ToString() != hash)
        {
            throw new CompactNodeException("Workflow function revision changed.");
        }
        hashes[stage.Id] = hash;
        if (!metadata.ContainsKey("function_hashes"))
        {
            metadata["function_hashes"] = hashes;
        }
        metadata["function_hash_kind"] = kind;

        return metadata;
    }

    // Throws CompactNodeException when a typed non-text parameter is passed to a text-only native function.
    private static IReadOnlyDictionary<string, string> NativeParameters(IReadOnlyDictionary<string, CompactWorkflowValue> parameters)
    {
        return CompactWorkflowApplicationBinding.Strings(parameters);
    }

    // Throws CompactNodeException when a declared stage decision was not explicitly confirmed.
    private static JsonObject? Decision(CompactWorkflowStage stage, CompactWorkflowJob job)
    {
        if (stage.Decision == null)
        {
            return null;
        }
        if (!job.Parameters.TryGetValue(stage.Decision.Input, out var value) || value.Boolean != true)
        {
            throw new CompactNodeException("Required workflow decision was not approved.");
        }

        return new JsonObject
        {
            ["severity"] = "light",
            ["occasion"] = stage.Decision.Prompt,
            ["choice"] = "Confirm",
            ["reason"] = "Explicit typed workflow decision supplied by the authorized client.",
            ["expected_result"] = "Verified recorded stage postconditions"
        };
    }

    private static async Task<CompactWorkflowAdapterAttempt<T>> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new(await action());
        }
        catch (Exception exception)
        {
            return new(default, exception.Message);
        }
    }
}
