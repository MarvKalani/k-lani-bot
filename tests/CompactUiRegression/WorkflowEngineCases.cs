using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Native engine regressions pin expected fault outcomes independently of JS parity and verify serialization and write-before-send failures.
internal static class WorkflowEngineCases
{
    public static async Task RunAsync()
    {
        JsonArray results = await WorkflowEngineParityCases.RunAsync("tests/CompactUiRegression/fixtures/workflow-engine.json");
        Expected(results, "success", "completed", 2);
        Expected(results, "missing input", "completed", 2);
        Expected(results, "false decision", "completed", 2);
        Expected(results, "lost reply recovered", "completed", 2);
        Expected(results, "unknown ledger", "unconfirmed", 2);
        Expected(results, "partial stage", "unconfirmed", 2);
        Expected(results, "permission refused", "blocked", 1);
        Expected(results, "appended text", "unconfirmed", 2);
        Expected(results, "empty field", "unconfirmed", 2);
        Expected(results, "long identical prefix wrong tail", "unconfirmed", 2);
        Expected(results, "explicit clear", "completed", 2);
        Expected(results, "long full value", "completed", 2);
        Expected(results, "later target absent", "blocked", 1);
        Expected(results, "changed app instance", "unconfirmed", 2);
        Expected(results, "changed function", "unconfirmed", 2);
        Expected(results, "safe predispatch recovery", "completed", 2);
        Expected(results, "verified recovery", "completed", 2);
        Expected(results, "ledger verified is insufficient", "unconfirmed", 2);
        Expected(results, "mixed permission effects", "unconfirmed", 2);
        string root = Path.Combine(Path.GetTempPath(), "k-lani-engine-native-" + Guid.NewGuid());
        try
        {
            await ConcurrentResumeAsync(Path.Combine(root, "concurrent"));
            await CheckpointRefusalAsync(Path.Combine(root, "checkpoint"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void Expected(JsonArray results, string name, string status, int dispatches)
    {
        JsonObject result = results.Cast<JsonObject>().Single(row => row["name"]!.GetValue<string>() == name);
        JsonObject last = result["steps"]!.AsArray().Last()!.AsObject();
        Require(last["progress"]!["status"]!.GetValue<string>() == status, name + " expected status");
        Require(last["dispatches"]!.GetValue<int>() == dispatches, name + " no implicit replay or later input");
        if (status == "unconfirmed")
        {
            Require(last["progress"]!["completed_stages"]!.AsArray().Count == 1, name + " preserves only the confirmed prefix");
            Require(last["outputs"]!["write"] == null && last["final_outputs"] == null, name + " never exposes unconfirmed outputs");
        }
    }

    private static async Task ConcurrentResumeAsync(string root)
    {
        CompactWorkflowStore store = WorkflowJobStoreCases.Open(root);
        var adapter = new WorkflowEngineSyntheticAdapter(WorkflowEngineFault.Success);
        CompactWorkflowEngine engine = Engine(store, adapter);
        var created = await engine.RunAsync("fixture", 1, "concurrent", new JsonObject());
        var initial = created.Value!;
        var answers = new JsonObject { ["text"] = "new" };
        var first = engine.ResumeAsync(initial.Reference, initial.Revision, answers);
        var second = engine.ResumeAsync(initial.Reference, initial.Revision, answers);
        await Task.WhenAll(first, second);
        Require(first.Result.Value?.Status == CompactWorkflowStatus.Completed
            && second.Result.Error == CompactWorkflowJobError.StaleRevision, "serialized resume rejects stale queued caller");
        Require(adapter.Dispatches == 2, "concurrent resume sends each stage once");
        Require(store.TryRead(initial.Reference, out var read), "completed state survives queue");
        CompactWorkflowJob job = read.Value!.Job;
        Require(job.FinalOutputs!["result"].Text == "new" && job.OutputDigests!.Count == 2 && job.StageEvidence!.Count == 2,
            "confirmed outputs, digests and evidence persisted together");
        long revision = job.Revision;
        var refused = await engine.ResumeAsync(initial.Reference, revision, new JsonObject { ["text"] = "new" });
        Require(refused.Error == CompactWorkflowJobError.ImmutableInput, "even an identical consumed input is immutable");
        Require(store.TryRead(initial.Reference, out var unchanged) && unchanged.Value!.Job.Revision == revision,
            "invalid resume has no state write");
        var unknown = await engine.RunAsync("fixture", 2, "unknown", new JsonObject());
        Require(unknown.Error == CompactWorkflowJobError.InvalidDefinition, "unknown revision cannot dispatch");
    }

    private static async Task CheckpointRefusalAsync(string root)
    {
        CompactWorkflowStore store = WorkflowJobStoreCases.Open(root);
        var adapter = new CheckpointFaultAdapter(store);
        CompactWorkflowEngine engine = Engine(store, adapter);
        var result = await engine.RunAsync("fixture", 1, "checkpoint", new JsonObject { ["text"] = "new" });
        Require(result.Error == CompactWorkflowJobError.StaleRevision && adapter.Dispatches == 0,
            "failed prepared checkpoint prevents every native send");
    }

    private static CompactWorkflowEngine Engine(CompactWorkflowStore store, ICompactWorkflowAdapter adapter)
    {
        CompactWorkflowDefinition definition = WorkflowEngineParityCases.Definition(false);

        return new(store, adapter, new WorkflowJobStoreCases.Functions(),
            new Dictionary<string, CompactWorkflowDefinition> { [definition.Key] = definition });
    }

    private static void Require(bool condition, string message)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, message);
    }

    // A real same-revision file edit forces CAS refusal after preflight but before dispatch; other adapter methods are never reached.
    private sealed class CheckpointFaultAdapter(CompactWorkflowStore store) : ICompactWorkflowAdapter
    {
        public int Dispatches
        {
            get; private set;
        }

        public Task<CompactWorkflowAdapterAttempt<CompactWorkflowJob>> RefreshInfoAsync(CompactWorkflowJob job,
            CancellationToken cancellation)
        {
            return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowJob>(job));
        }

        public Task<CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>> PrepareAsync(CompactWorkflowStage stage,
            IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowJob job, CancellationToken cancellation)
        {
            File.AppendAllText(store.Filename(job.Reference), " ");

            var evidence = WorkflowEngineSyntheticAdapter.Evidence(1);

            return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>(new(job, evidence)));
        }

        public Task<CompactWorkflowAdapterAttempt<CompactWorkflowExecution>> ExecuteAsync(CompactWorkflowStage stage,
            IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
            CompactWorkflowCheckpoint recorder, CancellationToken cancellation)
        {
            Dispatches++;
            throw new InvalidOperationException("CAS failure must stop before Execute.");
        }

        public Task<CompactWorkflowAdapterAttempt<bool>> VerifyAsync(CompactWorkflowStage stage,
            IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
            IReadOnlyDictionary<string, CompactWorkflowValue> outputs, CompactWorkflowJob job, CancellationToken cancellation)
        {
            throw new InvalidOperationException("CAS failure must stop before Verify.");
        }

        public Task<CompactWorkflowOperationResult> OperationStatusAsync(Guid originalRequestId, CompactWorkflowJob job,
            CancellationToken cancellation)
        {
            throw new InvalidOperationException("CAS failure must stop before ledger query.");
        }

        public Task<CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>> ReconcileAsync(
            CompactWorkflowStage stage,
            IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
            CompactWorkflowJob job, CancellationToken cancellation)
        {
            throw new InvalidOperationException("CAS failure must stop before Reconcile.");
        }
    }
}
