using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;
using KLaniBot.Actions;

// Private job-store regressions use real files with synthetic typed definitions, checking authority, capacity, serialization and CAS.
internal static class WorkflowJobStoreCases
{
    private static readonly CompactAcceptanceAuthority Authority = new("fixture-node", "owner", "fixture-client");

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-job-store-" + Guid.NewGuid());
        try
        {
            RestartAndAuthority(Path.Combine(root, "restart"));
            await SerializationAsync(Path.Combine(root, "serialized"));
            CapacityAndUnsafe(Path.Combine(root, "capacity"));
            LockOwnership(Path.Combine(root, "locks"));
            DurableOperationIntent(Path.Combine(root, "intent"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void RestartAndAuthority(string root)
    {
        CompactWorkflowStore store = Open(root);
        CompactWorkflowJob job = Create(store, "request-one");
        Require(store.TryCreate("request-one", job.RequestFingerprint, job.Definition, job.Parameters, out var same)
            && same.Value!.Reference == job.Reference, "deduplicated job creation");
        Require(!store.TryCreate("request-one", new(new string('b', 64)), job.Definition, job.Parameters, out var conflict)
            && conflict.Error == CompactWorkflowJobError.RequestConflict, "request content conflict");
        CompactWorkflowStore restarted = Open(root);
        Require(restarted.TryRead(job.Reference, out var loaded)
            && loaded.Value!.Job.Parameters["text"].Text == "PRIVATE_SYNTHETIC_RUNTIME_INPUT",
            "complete private input survives restart");
        Require(CompactWorkflowStore.TryOpen(Path.Combine(root, "jobs"), Path.Combine(root, "exports"),
            Authority with
            {
                Principal = "foreign"
            }, new Functions(), out var foreign), "foreign view opens without touching state");
        Require(!foreign.Value!.TryRead(job.Reference, out var refused)
            && refused.Error == CompactWorkflowJobError.ForeignOrInvalidJob, "authority read refusal");
        if (!OperatingSystem.IsWindows())
        {
            Require(File.GetUnixFileMode(store.Filename(job.Reference)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "private file mode");
        }
        string source = File.ReadAllText(store.Filename(job.Reference));
        File.AppendAllText(store.Filename(job.Reference), " ");
        Require(!restarted.TryWrite(loaded.Value!, job with
        {
            Status = CompactWorkflowStatus.Blocked
        }, out var stale)
            && stale.Error == CompactWorkflowJobError.StaleRevision, "byte digest detects same-revision external edit");
        Require(File.ReadAllText(store.Filename(job.Reference)) == source + " ", "failed CAS leaves source intact");
    }

    private static async Task SerializationAsync(string root)
    {
        CompactWorkflowStore store = Open(root);
        CompactWorkflowJob job = Create(store, "serialized");
        Task<CompactWorkflowJobAttempt<long>> first = store.SerializedAsync<long>(job.Reference, async snapshot =>
        {
            await Task.Yield();
            var outputs = new Dictionary<string, IReadOnlyDictionary<string, CompactWorkflowValue>>
            {
                ["open"] = new Dictionary<string, CompactWorkflowValue> { ["value"] = new(CompactWorkflowValueKind.String, Text: "opened") }
            };
            var next = snapshot.Job with
            {
                CompletedStages = ["open"],
                Outputs = outputs
            };
            store.TryWrite(snapshot, next, out var saved);
            return new(saved.Value!.Job.Revision);
        }, CancellationToken.None);
        Task<CompactWorkflowJobAttempt<long>> second = store.SerializedAsync<long>(job.Reference, snapshot =>
        {
            Require(snapshot.Job.CompletedStages.SequenceEqual(["open"]), "queue reads previous committed stage");
            return Task.FromResult(new CompactWorkflowJobAttempt<long>(snapshot.Job.Revision));
        }, CancellationToken.None);
        await Task.WhenAll(first, second);
        Require(first.Result.Value == 2 && second.Result.Value == 2, "serialized complete checkpoints");
        Require(store.TryRead(job.Reference, out var loaded) && loaded.Value!.Job.CompletedStages.Count == 1, "confirmed stage retained");
    }

    private static void CapacityAndUnsafe(string root)
    {
        CompactWorkflowStore store = Open(root);
        CompactWorkflowJob first = Create(store, "request-0");
        for (int index = 1; index < CompactWorkflowStore.MaximumJobs; index++)
        {
            Create(store, "request-" + index);
        }
        Require(!store.TryCreate("overflow", first.RequestFingerprint, first.Definition, first.Parameters, out var full)
            && full.Error == CompactWorkflowJobError.Full, "capacity does not evict active jobs");
        Require(Directory.GetFiles(store.Root, "*.json").Length == CompactWorkflowStore.MaximumJobs, "all jobs retained");
        string file = store.Filename(first.Reference);
        File.Delete(file);
        File.CreateSymbolicLink(file, Path.Combine(root, "missing"));
        Require(!store.TryRead(first.Reference, out var unsafeJob) && unsafeJob.Error == CompactWorkflowJobError.UnknownJob,
            "symlink refusal");
        var badReference = new CompactWorkflowJobReference(new("../outside"));
        Require(!store.TryRead(badReference, out var path) && path.Error == CompactWorkflowJobError.UnknownJob, "path cannot name job");
    }

    private static void LockOwnership(string root)
    {
        CompactWorkflowStore store = Open(root);
        CompactWorkflowJob job = Create(store, "locks");
        string file = store.Filename(job.Reference) + ".lock";
        var dead = new JsonObject { ["pid"] = int.MaxValue, ["process_start"] = "dead", ["authority"] = store.Authority.Hex };
        File.WriteAllText(file, dead.ToJsonString());
        using (var owner = new CompactWorkflowJobLock(file, store.Authority))
        {
            Require(owner.TryAcquire(out _), "OS-proven dead owner can be recovered");
        }
        Require(!File.Exists(file), "owned lock released");
        var foreign = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["process_start"] = "foreign",
            ["authority"] = new string('a', 64)
        };
        File.WriteAllText(file, foreign.ToJsonString());
        using var blocked = new CompactWorkflowJobLock(file, store.Authority);
        Require(!blocked.TryAcquire(out var refused) && refused.Error == CompactWorkflowJobError.InvalidLock,
            "foreign owner never removed");
        Require(File.Exists(file), "foreign lock remains");
    }

    public static CompactWorkflowStore Open(string root)
    {
        Require(CompactWorkflowStore.TryOpen(Path.Combine(root, "jobs"), Path.Combine(root, "exports"), Authority,
            new Functions(), out var store), "open private job store");

        return store.Value!;
    }

    private static void DurableOperationIntent(string root)
    {
        CompactWorkflowStore store = Open(root);
        CompactWorkflowJob job = Create(store, "intent");
        store.TryRead(job.Reference, out var initial);
        var recorder = new CompactWorkflowCheckpoint(store, initial.Value!);
        var before = new CompactWorkflowStageEvidence(new JsonObject
        {
            ["expected_steps"] = 1,
            ["mutation_steps"] = new JsonArray(0)
        }, 1, [0]);
        var pending = new CompactWorkflowPending("open", CompactWorkflowPendingPhase.Prepared, before, job.Parameters, []);
        Require(recorder.TryUpdate(job with
        {
            Pending = pending
        }, out _), "prepared checkpoint durable");
        Require(recorder.TryStep(0, out _), "step checkpoint durable");
        Require(recorder.TryBefore(Tool.TypeText, new JsonObject { ["text"] = "PRIVATE_SYNTHETIC_RUNTIME_INPUT" }, out var request),
            "original operation intent persisted before send");
        string identifier = request.Value!["request_id"]!.GetValue<string>();
        Require(Uuid7Time.TryReadMilliseconds(identifier, out _), "native ledger compatible UUIDv7");
        Require(store.TryRead(job.Reference, out var persisted)
            && persisted.Value!.Job.Pending!.Operations.Single().RequestId.ToString() == identifier,
            "restart observes original request ID");
        Require(persisted.Value!.Job.Pending!.Phase == CompactWorkflowPendingPhase.Dispatching, "possible dispatch remains explicit");
        Require(recorder.TryAfter(Guid.Parse(identifier), new(true, ActionOutcome.Completed), out _), "native response checkpoint");
        var restarted = Open(root);
        Require(restarted.TryRead(job.Reference, out var restored)
            && restored.Value!.Job.Pending!.Operations.Single().Response!.Outcome == ActionOutcome.Completed, "response survives restart");
        Require(!recorder.TryAfter(Guid.NewGuid(), new(true, ActionOutcome.Completed), out var unknown)
            && unknown.Error == CompactWorkflowJobError.UnknownOperation, "unknown response cannot alter a different operation");
    }

    public static CompactWorkflowJob Create(CompactWorkflowStore store, string request)
    {
        CompactWorkflowDefinition definition = Definition();
        var parameters = new Dictionary<string, CompactWorkflowValue>
        {
            ["text"] = new(CompactWorkflowValueKind.String, Text: "PRIVATE_SYNTHETIC_RUNTIME_INPUT")
        };
        Require(store.TryCreate(request, new(new string('a', 64)), definition, parameters, out var created), "create typed job");

        return created.Value!;
    }

    public static CompactWorkflowDefinition Definition()
    {
        JsonObject definition = JsonNode.Parse("""
            {"format":"k-lani-bot-workflow/1","key":"fixture","revision":1,"inputs":{"text":{"type":"string"}},
            "applications":{"editor":{"key":"fixture-editor","version":"1.0","window":"main"}},
            "stages":[{"id":"open","function_ref":{"application":"editor","name":"open"},"bindings":{"text":{"input":"text"}},
            "preconditions":"fresh_unique_enabled_targets","postconditions":"recorded_function_outputs"},
            {"id":"write","function_ref":{"application":"editor","name":"write"},"bindings":{"text":{"input":"text"}},
            "preconditions":"fresh_unique_enabled_targets","postconditions":"recorded_function_outputs"}],
            "outputs":{"result":{"stage":"write","output":"value"}}}
            """)!.AsObject();
        Require(CompactWorkflowDefinitionCodec.TryRead(definition, new Functions(), out var parsed), "typed fixture definition");

        return parsed.Value!;
    }

    // Verified synthetic contracts stay available during private checkpoint decoding without probing any real application.
    public sealed class Functions : ICompactWorkflowFunctions
    {
        public bool TryResolve(CompactWorkflowFunctionReference reference, CompactWorkflowApplication application,
            out CompactWorkflowFunctionContract? function)
        {
            function = new(true, true, new Dictionary<string, CompactWorkflowValueKind> { ["text"] = CompactWorkflowValueKind.String },
                ["text"], new Dictionary<string, CompactWorkflowValueKind> { ["value"] = CompactWorkflowValueKind.String });

            return true;
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Workflow job regression failed: " + name);
        }
    }
}
