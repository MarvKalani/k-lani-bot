using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared synthetic workflow faults compare progress, complete values, original-ID ledger queries and durable phase boundaries.
internal static class WorkflowEngineParityCases
{
    public static async Task<JsonArray> RunAsync(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var results = new JsonArray();
        foreach (JsonObject fixture in corpus["cases"]!.AsArray().Cast<JsonObject>())
        {
            results.Add(await FixtureAsync(fixture));
        }

        return results;
    }

    private static async Task<JsonObject> FixtureAsync(JsonObject fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-engine-parity-" + Guid.NewGuid());
        try
        {
            return await StepsAsync(root, fixture);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<JsonObject> StepsAsync(string root, JsonObject fixture)
    {
        var adapter = new WorkflowEngineSyntheticAdapter(Fault(fixture["mode"]!.GetValue<string>()));
        CompactWorkflowDefinition definition = Definition(fixture["decision"]?.GetValue<bool>() == true);
        CompactWorkflowStore store = WorkflowJobStoreCases.Open(root);
        CompactWorkflowEngine engine = Engine(store, adapter, definition);
        CompactWorkflowProgress? progress = null;
        JsonObject parameters = Parameters(fixture);
        var steps = new JsonArray();
        foreach (string action in fixture["actions"]!.AsArray().Select(node => node!.GetValue<string>()))
        {
            if (action == "restart")
            {
                store = WorkflowJobStoreCases.Open(root);
                engine = Engine(store, adapter, definition);
                continue;
            }
            if (action.StartsWith("inject-", StringComparison.Ordinal))
            {
                progress = Inject(store, engine, adapter, progress!, action);
                continue;
            }
            var attempt = await ActAsync(engine, progress, fixture, parameters, action);
            if (attempt.Error != null)
            {
                steps.Add(new JsonObject
                {
                    ["error"] = CompactWorkflowJobNames.Error(attempt.Error.Value, attempt.Detail),
                    ["dispatches"] = adapter.Dispatches,
                    ["queries"] = adapter.Queries
                });
                continue;
            }
            progress = attempt.Value!;
            WorkflowEngineSyntheticAdapter.Require(store.TryRead(progress.Reference, out var loaded), "read final checkpoint");
            steps.Add(Snapshot(progress, loaded.Value!.Job, adapter));
        }

        return new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["steps"] = steps };
    }

    private static JsonObject Parameters(JsonObject fixture)
    {
        JsonObject parameters = fixture["parameters"]!.DeepClone().AsObject();
        if (fixture["repeat_text"] is JsonValue repeat)
        {
            parameters["text"] = string.Concat(Enumerable.Repeat(parameters["text"]!.GetValue<string>(), repeat.GetValue<int>()));
        }

        return parameters;
    }

    private static CompactWorkflowEngine Engine(CompactWorkflowStore store, WorkflowEngineSyntheticAdapter adapter,
        CompactWorkflowDefinition definition)
    {
        return new(store, adapter, new WorkflowJobStoreCases.Functions(),
            new Dictionary<string, CompactWorkflowDefinition> { [definition.Key] = definition });
    }

    private static Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>> ActAsync(CompactWorkflowEngine engine,
        CompactWorkflowProgress? progress, JsonObject fixture, JsonObject parameters, string action)
    {
        if (action == "run")
        {
            return engine.RunAsync("fixture", 1, "request", parameters);
        }
        if (action == "status")
        {
            engine.TryStatus(progress!.Reference, out var status);
            return Task.FromResult(status);
        }

        return engine.ResumeAsync(progress!.Reference, progress.Revision - (fixture["stale"]?.GetValue<bool>() == true ? 1 : 0),
            fixture["answers"]?.DeepClone().AsObject());
    }

    private static CompactWorkflowProgress Inject(CompactWorkflowStore store, CompactWorkflowEngine engine,
        WorkflowEngineSyntheticAdapter adapter, CompactWorkflowProgress progress, string action)
    {
        WorkflowEngineSyntheticAdapter.Require(store.TryRead(progress.Reference, out var loaded), "read injected checkpoint");
        var job = loaded.Value!.Job;
        CompactWorkflowPending pending;
        if (action == "inject-prepared")
        {
            pending = new("open", CompactWorkflowPendingPhase.Prepared, WorkflowEngineSyntheticAdapter.Evidence(1), job.Parameters, []);
            adapter.Fault = WorkflowEngineFault.Success;
        }
        else
        {
            pending = job.Pending! with
            {
                Phase = CompactWorkflowPendingPhase.Verified,
                Outputs = new Dictionary<string, CompactWorkflowValue>
                {
                    ["value"] = new(CompactWorkflowValueKind.String, Text: adapter.Value)
                }
            };
        }
        WorkflowEngineSyntheticAdapter.Require(store.TryWrite(loaded.Value, job with
        {
            Pending = pending
        }, out _), "injected recovery state");
        WorkflowEngineSyntheticAdapter.Require(engine.TryStatus(progress.Reference, out var status), "injected recovery status");

        return status.Value!;
    }

    public static CompactWorkflowDefinition Definition(bool decision)
    {
        var source = WorkflowJobStoreCases.Definition().Source.DeepClone().AsObject();
        if (decision)
        {
            source["inputs"]!["approve"] = new JsonObject { ["type"] = "boolean" };
            source["stages"]![0]!["decision"] = new JsonObject { ["input"] = "approve", ["prompt"] = "Approve the synthetic stage." };
        }
        WorkflowEngineSyntheticAdapter.Require(CompactWorkflowDefinitionCodec.TryRead(source, new WorkflowJobStoreCases.Functions(),
            out var definition), "shared engine definition");

        return definition.Value!;
    }

    private static JsonObject Snapshot(CompactWorkflowProgress progress, CompactWorkflowJob job, WorkflowEngineSyntheticAdapter adapter)
    {
        JsonObject wire = CompactWorkflowJobWire.Job(job);
        JsonObject summary = Progress(progress);
        JsonNode? pending = null;
        if (job.Pending != null)
        {
            pending = new JsonObject
            {
                ["phase"] = CompactWorkflowJobNames.Pending(job.Pending.Phase),
                ["operations"] = new JsonArray(job.Pending.Operations.Select(operation => (JsonNode?)new JsonObject
                {
                    ["step_index"] = operation.StepIndex,
                    ["phase"] = CompactWorkflowJobNames.Operation(operation.Phase),
                    ["response"] = operation.Response == null ? null : CompactWorkflowJobWire.Response(operation.Response)
                }).ToArray())
            };
        }

        return new JsonObject
        {
            ["progress"] = summary,
            ["dispatches"] = adapter.Dispatches,
            ["queries"] = adapter.Queries,
            ["fingerprint"] = job.RequestFingerprint.Hex,
            ["outputs"] = wire["outputs"]!.DeepClone(),
            ["output_digests"] = wire["output_digests"]?.DeepClone() ?? new JsonObject(),
            ["final_outputs"] = wire["final_outputs"]?.DeepClone(),
            ["pending"] = pending
        };
    }

    private static JsonObject Progress(CompactWorkflowProgress progress)
    {
        var result = new JsonObject
        {
            ["success"] = true,
            ["status"] = CompactWorkflowJobNames.Status(progress.Status),
            ["revision"] = progress.Revision,
            ["completed_stages"] = new JsonArray(progress.CompletedStages.Select(stage =>
                (JsonNode?)JsonValue.Create(stage)).ToArray()),
            ["next_stage"] = progress.NextStage
        };
        if (progress.Decision != null)
        {
            result["decision"] = new JsonObject { ["input"] = progress.Decision.Input, ["prompt"] = progress.Decision.Prompt };
        }
        if (progress.Error != null)
        {
            result["error"] = progress.Error;
        }

        return result;
    }

    private static WorkflowEngineFault Fault(string mode)
    {
        return mode switch
        {
            "wrong-tail" => WorkflowEngineFault.WrongTail,
            "success" => WorkflowEngineFault.Success,
            "lost" => WorkflowEngineFault.Lost,
            "unknown" => WorkflowEngineFault.Unknown,
            "prefix" => WorkflowEngineFault.Prefix,
            "denied" => WorkflowEngineFault.Denied,
            "append" => WorkflowEngineFault.Append,
            "empty" => WorkflowEngineFault.Empty,
            "missing-target" => WorkflowEngineFault.MissingTarget,
            "changed-instance" => WorkflowEngineFault.ChangedInstance,
            "changed-function" => WorkflowEngineFault.ChangedFunction,
            "prepared" => WorkflowEngineFault.Prepared,
            "changed-value" => WorkflowEngineFault.ChangedValue,
            "mixed-denied" => WorkflowEngineFault.MixedDenied,
            _ => throw new InvalidOperationException("Unknown synthetic engine fault.")
        };
    }
}
