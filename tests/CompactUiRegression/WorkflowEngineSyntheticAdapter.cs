using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Named synthetic faults exercise native engine boundaries without claiming a real application or model benchmark.
internal enum WorkflowEngineFault
{
    Success,
    Lost,
    Unknown,
    Prefix,
    Denied,
    Append,
    Empty,
    MissingTarget,
    ChangedInstance,
    ChangedFunction,
    Prepared,
    ChangedValue,
    MixedDenied,
    WrongTail
}

// The regression adapter records dispatches and original IDs independently of the workflow engine's private state.
internal sealed class WorkflowEngineSyntheticAdapter(WorkflowEngineFault fault) : ICompactWorkflowAdapter
{
    private readonly Dictionary<Guid, CompactWorkflowOperationResult> _ledger = new();
    private bool _dialog;
    public WorkflowEngineFault Fault { get; set; } = fault;
    public string Value { get; private set; } = "old";
    public int Dispatches
    {
        get; private set;
    }
    public int Queries
    {
        get; private set;
    }

    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowJob>> RefreshInfoAsync(CompactWorkflowJob job,
            CancellationToken cancellation)
    {
        return Task.FromResult(Fault == WorkflowEngineFault.ChangedInstance && job.Pending != null
            ? new CompactWorkflowAdapterAttempt<CompactWorkflowJob>(null, "Application instance changed.") : new(job));
    }

    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>> PrepareAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowJob job, CancellationToken cancellation)
    {
        if (stage.Id == "write" && (!_dialog || Fault == WorkflowEngineFault.MissingTarget))
        {
            return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>(null, "Later target absent."));
        }
        if (Fault == WorkflowEngineFault.Prepared)
        {
            return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>(null, "Target absent before dispatch."));
        }
        int steps = stage.Id == "write" && Fault is WorkflowEngineFault.Prefix or WorkflowEngineFault.MixedDenied ? 2 : 1;

        return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowPreparation>(new(job, Evidence(steps))));
    }

    public Task<CompactWorkflowAdapterAttempt<CompactWorkflowExecution>> ExecuteAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowCheckpoint recorder, CancellationToken cancellation)
    {
        int count = stage.Id == "write" && Fault == WorkflowEngineFault.MixedDenied ? 2 : 1;
        for (int index = 0; index < count; index++)
        {
            Require(recorder.TryStep(index, out _), "durable step");
            var arguments = new JsonObject { ["text"] = parameters["text"].Text };
            Require(recorder.TryBefore(Tool.TypeText, arguments, out var request), "durable intent");
            Guid identifier = Guid.Parse(request.Value!["request_id"]!.GetValue<string>());
            bool denied = stage.Id == "write"
                && (Fault == WorkflowEngineFault.Denied || Fault == WorkflowEngineFault.MixedDenied && index == 1);
            var response = denied ? new CompactWorkflowOperationResult(false, ActionOutcome.NotDispatched, ActionErrorCode.Denied)
                : new(true, ActionOutcome.Completed);
            _ledger.Add(identifier, response);
            if (denied)
            {
                Require(recorder.TryAfter(identifier, response, out _), "refusal checkpoint");
                return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowExecution>(null, "Native scope refused input."));
            }
            Dispatches++;
            Apply(stage, parameters["text"].Text!);
            if (stage.Id == "write" && Fault is WorkflowEngineFault.Lost or WorkflowEngineFault.Unknown or WorkflowEngineFault.Prefix
                or WorkflowEngineFault.ChangedInstance or WorkflowEngineFault.ChangedFunction or WorkflowEngineFault.ChangedValue)
            {
                return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowExecution>(null, "Lost native response."));
            }
            Require(recorder.TryAfter(identifier, response, out _), "response checkpoint");
        }

        return Task.FromResult(new CompactWorkflowAdapterAttempt<CompactWorkflowExecution>(new(true, Outputs(stage))));
    }

    private void Apply(CompactWorkflowStage stage, string text)
    {
        if (stage.Id == "open")
        {
            _dialog = true;
            return;
        }

        Value = Fault == WorkflowEngineFault.Append ? "old" + text : Fault == WorkflowEngineFault.Empty ? ""
            : Fault == WorkflowEngineFault.WrongTail ? text[..^1] + "y" : text;
    }

    public Task<CompactWorkflowAdapterAttempt<bool>> VerifyAsync(CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        IReadOnlyDictionary<string, CompactWorkflowValue> outputs, CompactWorkflowJob job, CancellationToken cancellation)
    {
        bool verified = stage.Id == "open" ? _dialog && outputs["value"].Text == "opened"
            : Value == parameters["text"].Text && outputs["value"].Text == Value;

        return Task.FromResult(new CompactWorkflowAdapterAttempt<bool>(verified));
    }

    public Task<CompactWorkflowOperationResult> OperationStatusAsync(Guid originalRequestId, CompactWorkflowJob job,
        CancellationToken cancellation)
    {
        Queries++;
        Require(_ledger.ContainsKey(originalRequestId), "resume queries the original ID");

        return Task.FromResult(Fault == WorkflowEngineFault.Unknown ? new(false, ActionOutcome.Unknown) : _ledger[originalRequestId]);
    }

    public Task<CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>> ReconcileAsync(
            CompactWorkflowStage stage,
        IReadOnlyDictionary<string, CompactWorkflowValue> parameters, CompactWorkflowStageEvidence before,
        CompactWorkflowJob job, CancellationToken cancellation)
    {
        if (Fault == WorkflowEngineFault.ChangedFunction)
        {
            return Task.FromException<CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>>(
                new InvalidOperationException("Function scope changed."));
        }
        if (Fault == WorkflowEngineFault.ChangedValue)
        {
            Value = "different";
        }

        return Task.FromResult(new CompactWorkflowAdapterAttempt<IReadOnlyDictionary<string, CompactWorkflowValue>>(Outputs(stage)));
    }

    private IReadOnlyDictionary<string, CompactWorkflowValue> Outputs(CompactWorkflowStage stage)
    {
        return new Dictionary<string, CompactWorkflowValue>
        {
            ["value"] = new(CompactWorkflowValueKind.String,
            Text: stage.Id == "open" ? "opened" : Value)
        };
    }

    public static CompactWorkflowStageEvidence Evidence(int steps)
    {
        int[] mutations = Enumerable.Range(0, steps).ToArray();
        return new(new JsonObject
        {
            ["expected_steps"] = steps,
            ["mutation_steps"] = new JsonArray(mutations.Select(index => (JsonNode?)JsonValue.Create(index)).ToArray())
        }, steps, mutations);
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Workflow engine regression failed: " + message);
        }
    }
}
