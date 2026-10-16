using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Native workflow routing uses the existing engine, original operation ledger and private authority-bound checkpoints.
public sealed class CompactGatewayWorkflows(ICompactNodeClient client, CompactGatewayState state, CompactStorageRoots roots,
    CompactWorkflowSettings settings, Func<string> protocol)
{
    private readonly CompactWorkflowCaptureSession _capture = new();
    private readonly CompactGatewayWorkflowFunctions _functions = new(new(roots));

    // Throws ArgumentException for invalid requests; CompactNodeException for absent trusted scope or unavailable private state.
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        var command = CompactWorkflowCommandCodec.Read(name, arguments);
        var definitions = new CompactGatewayWorkflowDefinitions(roots, _functions);
        switch (command.Kind)
        {
            case CompactWorkflowCommandKind.Begin:
                return _capture.Begin(arguments);
            case CompactWorkflowCommandKind.Finish:
                var finished = definitions.Install(_capture.Finish(arguments));
                _capture.Clear();
                return Captured(finished);
            case CompactWorkflowCommandKind.Capture:
                return Captured(definitions.Install(arguments["definition"]!.AsObject()));
            case CompactWorkflowCommandKind.Run:
            case CompactWorkflowCommandKind.Resume:
            case CompactWorkflowCommandKind.Status:
                return await ExecuteAsync(command, definitions, cancellation);
            default:
                throw new UnreachableException();
        }
    }

    // Throws CompactNodeException for wrong current node, storage refusal or failed revision/parameter/checkpoint reconciliation.
    private async Task<JsonObject> ExecuteAsync(CompactWorkflowCommand command, CompactGatewayWorkflowDefinitions definitions,
        CancellationToken cancellation)
    {
        if (command.Kind != CompactWorkflowCommandKind.Status && state.Node != settings.Authority.Node)
        {
            throw new CompactNodeException("Read ui_info on the configured workflow authority before run/resume.");
        }
        if (!CompactWorkflowStore.TryOpen(settings.StateRoot, roots.Library, settings.Authority, _functions, out var opened))
        {
            throw new CompactNodeException(CompactWorkflowJobNames.Error(opened.Error!.Value, opened.Detail));
        }
        var store = opened.Value!;
        var node = new CompactNodeAccess(client, settings.Authority.Node);
        var wire = CompactCompatibilityWire.Wire(protocol());
        var executor = new CompactFunctionExecutor(node, wire, CompactGatewayPrograms.Implementation());
        var adapter = new CompactNativeWorkflowAdapter(node, executor, _functions);
        var engine = new CompactWorkflowEngine(store, adapter, _functions,
            command.Kind == CompactWorkflowCommandKind.Status ? new Dictionary<string, CompactWorkflowDefinition>() : definitions.Read());
        var result = await ProgressAsync(command, engine, cancellation);
        if (result.Error != null)
        {
            throw new CompactNodeException(CompactWorkflowJobNames.Error(result.Error.Value, result.Detail));
        }

        return Report(result.Value!, store);
    }

    private static Task<CompactWorkflowJobAttempt<CompactWorkflowProgress>> ProgressAsync(CompactWorkflowCommand command,
        CompactWorkflowEngine engine, CancellationToken cancellation)
    {
        var arguments = command.Arguments;
        switch (command.Kind)
        {
            case CompactWorkflowCommandKind.Run:
                CompactFunctionDocumentCodec.TryInteger(arguments["revision"], out long revision);
                return engine.RunAsync(arguments["workflow_key"]!.ToString(), revision, arguments["client_request_id"]!.ToString(),
                    arguments["parameters"]!.AsObject(), cancellation);
            case CompactWorkflowCommandKind.Resume:
                CompactFunctionDocumentCodec.TryInteger(arguments["expected_revision"], out long expected);
                return engine.ResumeAsync(command.Job!, expected, arguments["answers"] as JsonObject, cancellation);
            case CompactWorkflowCommandKind.Status:
                engine.TryStatus(command.Job!, out var status);
                return Task.FromResult(status);
            case CompactWorkflowCommandKind.Capture:
            case CompactWorkflowCommandKind.Begin:
            case CompactWorkflowCommandKind.Finish:
                throw new UnreachableException();
            default:
                throw new UnreachableException();
        }
    }

    // Throws CompactNodeException when a complete private job/evidence snapshot cannot be retained for its concise progress reply.
    private JsonObject Report(CompactWorkflowProgress progress, CompactWorkflowStore store)
    {
        if (!store.TryRead(progress.Reference, out var snapshot))
        {
            throw new CompactNodeException(CompactWorkflowJobNames.Error(snapshot.Error!.Value, snapshot.Detail));
        }
        var job = CompactWorkflowJobWire.Job(snapshot.Value!.Job);
        var details = new JsonObject();
        foreach (string field in new[] { "completed_stages", "outputs", "pending", "error" })
        {
            details[field] = job[field]?.DeepClone();
        }
        if (!state.Results.TryPut(state.Authority, new Dictionary<string, string>(), CompactJsonText.Serialize(details), out var evidence))
        {
            throw new CompactNodeException(CompactResultErrors.ToWireMessage(evidence.Error!.Value));
        }
        var reply = CompactWorkflowProgressWire.Reply(progress);
        reply["evidence_ref"] = evidence.Value!.Value.ToString();

        return reply;
    }

    public JsonObject Browse()
    {
        var definitions = new CompactGatewayWorkflowDefinitions(roots, _functions).Read();

        return new JsonObject
        {
            ["workflows"] = new JsonArray(definitions.Values.Select(definition => (JsonNode?)new JsonObject
            {
                ["key"] = definition.Key,
                ["revision"] = definition.Revision,
                ["stages"] = definition.Stages.Count
            }).ToArray())
        };
    }

    public bool TryRecord(CompactLoadedFunction function, CompactProgramIdentity application, out string? error)
    {
        return _capture.TryRecord(function, application, out error);
    }

    public void Clear()
    {
        _capture.Clear();
    }

    private static JsonObject Captured(CompactWorkflowDefinition definition)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["workflow_key"] = definition.Key,
            ["revision"] = definition.Revision,
            ["stages"] = definition.Stages.Count,
            ["validation"] = "verified function effects; fresh checks per stage",
            ["stored_values"] = false
        };
    }
}
