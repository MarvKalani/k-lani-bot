using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Audit;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Visibility;

namespace KLaniBot.Transport;

// Use case: a controller (an AI client through the MCP gateway) talks to this node over the network,
// but only the actuator may touch the desktop. The supervisor stands between them. It authenticates
// the request, records it in its own journal, and passes it to the one actuator, one request at a time.
// Its journal and TLS keys stay outside the actuator's namespace, so a compromised or confused
// actuator cannot rewrite what the supervisor recorded.
//
// The conversation with the actuator is a strict lockstep of one line out and one line back, without
// frame numbers. If one round is left half finished, the next round would read the previous answer as
// its own. For that reason a round is never abandoned because a client went away; it ends only by
// completing or by hitting its own deadline, and a deadline ends the actuator for good.
public sealed class WorkerSupervisor : IDisposable
{
    private const int MaximumQueuedRequests = 16;
    private static readonly TimeSpan DefaultControlFrameDeadline = TimeSpan.FromSeconds(5);

    private readonly NodePolicy _policy;
    private readonly string _policyPath;
    private readonly AuditJournal _journal;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly IActuatorChannel _channel;
    private readonly TimeSpan _actionTimeout;
    private readonly TimeSpan _controlFrameDeadline;
    private int _queued;
    private bool _stopped;
    private bool _workerReady = true;

    public WorkerSupervisor(NodeConfiguration configuration, NodePolicy policy)
        : this(
            policy,
            configuration.PolicyPath,
            configuration.ActionTimeout,
            DefaultControlFrameDeadline,
            ProcessActuatorChannel.Start(configuration.WorkerCommand, policy))
    {
        _workerReady = false;
    }

    // Also the entry point for tests and for other ways of reaching an actuator.
    public WorkerSupervisor(
        NodePolicy policy,
        string policyPath,
        TimeSpan actionTimeout,
        TimeSpan controlFrameDeadline,
        IActuatorChannel channel)
    {
        _policy = policy;
        _policyPath = policyPath;
        _actionTimeout = actionTimeout;
        _controlFrameDeadline = controlFrameDeadline;
        _channel = channel;
        string journalRoot = Path.Combine(policy.StateRoot, "supervisor");
        PrivateFile.CreateDirectory(journalRoot);
        _journal = new AuditJournal(journalRoot, policy.OperationWindow);
    }

    public int ExitCode => _channel.ExitCode;
    public bool WorkerAvailable => _workerReady && !_stopped && !_channel.HasExited;

    public void MarkUnavailable()
    {
        _workerReady = false;
        Stop();
    }

    public Task WaitForExitAsync(CancellationToken cancellation)
    {
        return _channel.WaitForExitAsync(cancellation);
    }

    // Throws IOException when the worker exits during start-up and InvalidDataException when it does not report a visible indicator.
    public async Task RequireVisibleWorkerAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        string? line = await _channel.ReadLineAsync(deadline.Token);
        var ready = JsonNode.Parse(line ?? throw new IOException("Visible worker exited during startup."))!.AsObject();
        bool visibleProtocol = ready["ready"]?.GetValue<bool>() == true && ready["visibility_protocol"]?.GetValue<int>() == 1;
        if (!visibleProtocol)
        {
            throw new InvalidDataException("Worker did not establish its permanent visible indicator.");
        }
        _workerReady = true;
    }

    public async Task<JsonObject> NotifySessionAsync(RequestContext context, bool connected, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            _policy.Authorize(context, Scope.Observe);
            if (_stopped)
            {
                return ActionResults.Failure(ActionErrorCode.WorkerStopped, ActionOutcome.NotDispatched, "Visible worker unavailable.");
            }
            var frame = new JsonObject
            {
                ["control"] = connected ? "heartbeat" : "close",
                ["principal_id"] = context.PrincipalId,
                ["session_id"] = context.SessionId
            };
            // Nothing has been written yet, so a client that is already gone costs nothing here.
            cancellation.ThrowIfCancellationRequested();
            return await NotifyActuatorAsync(frame);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ActionResults.Failure(ActionErrorCode.Denied, ActionOutcome.NotDispatched, exception.Message);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<JsonObject> NotifyActuatorAsync(JsonObject frame)
    {
        try
        {
            return await ExchangeControlFrameAsync(frame, "session notification");
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException
                or System.Text.Json.JsonException or InvalidDataException)
        {
            // The exchange has already ended the actuator and logged why. The caller only learns that the indicator is gone.
            return ActionResults.Failure(
                ActionErrorCode.IndicatorUnavailable,
                ActionOutcome.NotDispatched,
                "Visible worker unavailable.");
        }
    }

    public async Task<JsonObject> SetInventoryPermissionAsync(bool enabled, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            var context = new RequestContext("operator", Guid.CreateVersion7().ToString());
            _policy.Authorize(context, Scope.Observe);
            var frame = new JsonObject
            {
                ["control"] = "inventory_permission",
                ["principal_id"] = context.PrincipalId,
                ["session_id"] = context.SessionId,
                ["enabled"] = enabled
            };
            cancellation.ThrowIfCancellationRequested();
            JsonObject result = await ExchangeControlFrameAsync(frame, "inventory permission");
            if (result["success"]?.GetValue<bool>() == true)
            {
                InventoryPermission.Set(_policy, _policyPath, context.PrincipalId, enabled);
                RecordInventoryPermissionChange(context.PrincipalId, enabled);
            }
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    // The master's administrator switched the backup requirement; worker and supervisor policy change together.
    public async Task<JsonObject> SetBackupRequirementAsync(BackupRequirement requirement, string requestedAt,
        CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            var context = new RequestContext("operator", Guid.CreateVersion7().ToString());
            _policy.Authorize(context, Scope.Observe);
            var frame = new JsonObject
            {
                ["control"] = "backup_requirement",
                ["principal_id"] = context.PrincipalId,
                ["session_id"] = context.SessionId,
                ["requirement"] = JsonSerializer.SerializeToNode(requirement)
            };
            cancellation.ThrowIfCancellationRequested();
            JsonObject result = await ExchangeControlFrameAsync(frame, "backup requirement");
            if (result["success"]?.GetValue<bool>() == true)
            {
                BackupRequirementSetting.Set(_policy, _policyPath, requirement);
                RecordBackupRequirementChange(requirement, requestedAt);
            }
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    // Throws IOException when the worker rejects the details.
    public async Task SetSessionPresentationAsync(RequestContext context, SessionPresentation presentation, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            _policy.Authorize(context, Scope.Observe);
            var frame = new JsonObject
            {
                ["control"] = "session_details",
                ["principal_id"] = context.PrincipalId,
                ["session_id"] = context.SessionId,
                ["details"] = presentation.ToJson()
            };
            cancellation.ThrowIfCancellationRequested();
            JsonObject response = await ExchangeControlFrameAsync(frame, "session details");
            if (response["success"]?.GetValue<bool>() != true)
            {
                // The actuator is in step but refused its own indicator data. It cannot show who is operating, so it is not kept.
                Stop();
                throw new IOException("Worker rejected session details.");
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<JsonObject> ExecuteAsync(JsonObject request, RequestContext context, CancellationToken cancellation)
    {
        if (!WorkerAvailable && request["action"]?.ToString() == ToolNames.Of(Tool.GetMachineCapabilities))
        {
            try
            {
                ValidateFrame(request, context);
                return NodeManagementStatus.Capabilities(_policy);
            }
            catch (Exception failure) when (failure is ArgumentException or UnauthorizedAccessException)
            {
                return FailureResult(failure, false);
            }
        }
        if (Interlocked.Increment(ref _queued) > MaximumQueuedRequests)
        {
            Interlocked.Decrement(ref _queued);
            return ActionResults.Failure(ActionErrorCode.Busy, ActionOutcome.NotDispatched, "Desktop queue full.");
        }

        try
        {
            return await ExecuteQueuedAsync(request, context, cancellation);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    private async Task<JsonObject> ExecuteQueuedAsync(JsonObject request, RequestContext context, CancellationToken cancellation)
    {
        bool lockHeld = false;
        bool sentToActuator = false;
        try
        {
            await _serial.WaitAsync(cancellation);
            lockHeld = true;
            cancellation.ThrowIfCancellationRequested();
            ActionFrame frame = ValidateFrame(request, context);
            JsonObject? answer = AnswerWithoutActuator(frame);
            if (answer != null)
            {
                return answer;
            }
            if (frame.Mutation)
            {
                AdmitMutation(frame, context);
            }
            sentToActuator = true;
            return await DispatchToActuatorAsync(request, context, frame);
        }
        catch (Exception exception)
        {
            return FailureResult(exception, sentToActuator);
        }
        finally
        {
            if (lockHeld)
            {
                _serial.Release();
            }
        }
    }

    // Status lookups and replays are answered from the journal; a stopped actuator gets nothing.
    private JsonObject? AnswerWithoutActuator(ActionFrame frame)
    {
        if (frame.Tool == Tool.GetActionStatus)
        {
            return LookUpRecordedStatus(frame.Key);
        }
        JsonObject? replay = TryReplay(frame);
        if (replay != null)
        {
            return replay;
        }
        if (!WorkerAvailable)
        {
            // Checked before the admission is recorded: nothing was sent, so nothing may look as if it might have been.
            return ActionResults.Failure(
                ActionErrorCode.WorkerStopped,
                ActionOutcome.NotDispatched,
                "Desktop worker unavailable; management and recorded operation status remain accessible.");
        }
        return null;
    }

    private async Task<JsonObject> DispatchToActuatorAsync(JsonObject request, RequestContext context, ActionFrame frame)
    {
        var outgoing = (JsonObject)request.DeepClone();
        outgoing["principal_id"] = context.PrincipalId;
        outgoing["session_id"] = context.SessionId;

        JsonObject result = await ExchangeActionAsync(outgoing);
        if (frame.Tool == Tool.GetMachineCapabilities)
        {
            result["management"] = NodeManagementStatus.Read(_policy, WorkerAvailable);
        }
        if (frame.Mutation)
        {
            ActionOutcome outcome = ActionOutcomeNames.Read(result, ReportUnrecognizedOutcome);
            Record(frame.Key, frame.Digest, context, frame.Action, frame.OperationId, outcome);
            _journal.Operations.Remember(frame.Key, frame.Digest, Status(result));
        }
        return result;
    }

    // Every failure of one action becomes an answer. Before dispatch it is a refusal; after dispatch the outcome is unknown.
    private JsonObject FailureResult(Exception exception, bool sentToActuator)
    {
        if (exception is OperationCanceledException && !sentToActuator)
        {
            return ActionResults.Failure(ActionErrorCode.Cancelled, ActionOutcome.NotDispatched, "Cancelled before dispatch.");
        }
        if (exception is OperationWindowException windowException)
        {
            return ActionResults.Failure(windowException.Code, ActionOutcome.NotDispatched, windowException.Message);
        }
        if (exception is ArgumentException or UnauthorizedAccessException)
        {
            return ActionResults.Failure(ActionErrorCode.Denied, ActionOutcome.NotDispatched, exception.Message);
        }
        return HandleActuatorFailure(exception, sentToActuator);
    }

    // A controller request after its checks: action, tool, whether it mutates, its operation id, ledger key and content hash.
    private sealed record ActionFrame(string Action, Tool Tool, bool Mutation, string OperationId, string Key, string Digest);

    // Throws ArgumentException for fields besides action and args.
    private ActionFrame ValidateFrame(JsonObject request, RequestContext context)
    {
        bool wellFormed = request.Count == 2 && request["action"] != null && request["args"] is JsonObject;
        if (!wellFormed)
        {
            throw new ArgumentException("Only action and args are allowed in a controller request.");
        }
        string action = request["action"]!.ToString();
        var args = request["args"]!.AsObject();
        var tool = ToolContract.Get(action);
        ToolContract.Validate(tool["inputSchema"]!.AsObject(), args);
        _policy.Authorize(context, Scope.Observe);
        _policy.Authorize(context, ToolScope.Of(tool));
        string operationId = args["request_id"]?.ToString() ?? Guid.CreateVersion7().ToString();
        string digest = CanonicalJson.Hash(new JsonObject { ["action"] = action, ["args"] = args.DeepClone() });
        bool mutation = tool["mutates"]!.GetValue<bool>();
        Tool requestedTool = ToolNames.Parse(action);
        return new ActionFrame(action, requestedTool, mutation, operationId, context.PrincipalId + ":" + operationId, digest);
    }

    private JsonObject LookUpRecordedStatus(string key)
    {
        if (_journal.Operations.TryGetValue(key, out var recorded))
        {
            return Status(recorded.Result);
        }
        return ActionResults.Failure(
            ActionErrorCode.UnknownOperation,
            ActionOutcome.Unknown,
            "No recorded operation; inspect the independent result.");
    }

    // A known operation id gets its recorded answer back. Only an operation that provably never
    // reached the actuator may run again under the same id.
    // Throws ArgumentException when the id was used before with other content.
    private JsonObject? TryReplay(ActionFrame frame)
    {
        if (!_journal.Operations.TryGetValue(frame.Key, out var previous))
        {
            return null;
        }
        if (previous.Hash != frame.Digest)
        {
            throw new ArgumentException("Operation ID reused with different content.");
        }
        bool neverReachedActuator = ActionOutcomeNames.Read(previous.Result, ReportUnrecognizedOutcome) == ActionOutcome.NotDispatched;
        return neverReachedActuator ? null : Status(previous.Result);
    }

    // Throws ArgumentException when the id is no UUIDv7.
    private void AdmitMutation(ActionFrame frame, RequestContext context)
    {
        bool versionSeven = Guid.TryParse(frame.OperationId, out var identifier) && identifier.Version == 7;
        if (!versionSeven)
        {
            throw new ArgumentException("UUIDv7 required.");
        }
        _journal.Operations.RequireAdmissible(frame.OperationId);
        Record(frame.Key, frame.Digest, context, frame.Action, frame.OperationId, ActionOutcome.Admitted);
        var pending = ActionResults.Failure(
            ActionErrorCode.Pending,
            ActionOutcome.Unknown,
            "Operation admitted; outcome not yet confirmed.");
        _journal.Operations.Remember(frame.Key, frame.Digest, pending);
    }

    // The client's cancellation token is not used from here on: once a line is written, the answer must be read.
    // Throws IOException when the actuator exits before answering.
    private async Task<JsonObject> ExchangeActionAsync(JsonObject outgoing)
    {
        using var deadline = new CancellationTokenSource(_actionTimeout);
        await _channel.WriteLineAsync(outgoing.ToJsonString(), deadline.Token);
        string? line = await _channel.ReadLineAsync(deadline.Token);
        return JsonNode.Parse(line ?? throw new IOException("Actuator exited."))!.AsObject();
    }

    // One control round. The caller holds the serial lock and has already checked its client's cancellation,
    // so nothing here depends on the client any more: the round either completes or ends the actuator.
    // Throws IOException when the actuator exits; rethrows any other Exception after ending the actuator.
    private async Task<JsonObject> ExchangeControlFrameAsync(JsonObject frame, string description)
    {
        using var deadline = new CancellationTokenSource(_controlFrameDeadline);
        try
        {
            await _channel.WriteLineAsync(frame.ToJsonString(), deadline.Token);
            string? line = await _channel.ReadLineAsync(deadline.Token);
            return JsonNode.Parse(line ?? throw new IOException("Actuator exited."))!.AsObject();
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException
                or System.Text.Json.JsonException or InvalidDataException)
        {
            // A half finished round leaves an unread answer in the stream, so the stream can no longer be trusted.
            Console.Error.WriteLine("Actuator " + description + " round failed: " + exception.GetType().Name + "; ending the actuator.");
            Stop();
            throw;
        }
    }

    private JsonObject HandleActuatorFailure(Exception failure, bool sentToActuator)
    {
        string exitInformation = _channel.HasExited ? "; exit_code=" + _channel.ExitCode : "";
        Console.Error.WriteLine("Actuator failure: " + failure.GetType().Name + ": " + failure.Message + exitInformation);
        string? state = _channel.DescribeFailure();
        if (state != null)
        {
            Console.Error.WriteLine(state);
        }
        if (sentToActuator)
        {
            Stop();
        }
        ActionOutcome outcome = sentToActuator ? ActionOutcome.Unknown : ActionOutcome.NotDispatched;
        return ActionResults.Failure(ActionErrorCode.WorkerUnavailable, outcome, "Actuator unavailable. No automatic mutation retry.");
    }

    private void RecordInventoryPermissionChange(string principalId, bool enabled)
    {
        _journal.Append(new JsonObject
        {
            ["event"] = "inventory_permission_changed_by_master_admin",
            ["principal_id"] = principalId,
            ["enabled"] = enabled,
            ["request_id"] = Guid.CreateVersion7().ToString(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private void RecordBackupRequirementChange(BackupRequirement requirement, string requestedAt)
    {
        _journal.Append(new JsonObject
        {
            ["event"] = "backup_requirement_changed_by_master_admin",
            ["requirement"] = JsonSerializer.SerializeToNode(requirement),
            ["requested_at"] = requestedAt,
            ["request_id"] = Guid.CreateVersion7().ToString(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private void Record(string key, string digest, RequestContext context, string action, string operation, ActionOutcome outcome)
    {
        _journal.Append(new JsonObject
        {
            ["operation_key"] = key,
            ["request_hash"] = digest,
            ["operation_id"] = operation,
            ["principal_id"] = context.PrincipalId,
            ["session_id"] = context.SessionId,
            ["action"] = action,
            ["outcome"] = outcome.ToWireName(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private static JsonObject Status(JsonObject result)
    {
        return new JsonObject
        {
            ["success"] = result["success"]?.DeepClone(),
            ["outcome"] = result["outcome"]?.DeepClone(),
            ["receipt"] = result["receipt"]?.DeepClone(),
            ["replayed"] = true
        };
    }

    // An outcome word this version does not know is read as Unknown; the line keeps it from disappearing silently.
    private static void ReportUnrecognizedOutcome(string wireName)
    {
        Console.Error.WriteLine("Actuator reported an unrecognized outcome '" + wireName + "'; treating it as unknown.");
    }

    private void Stop()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _channel.Terminate();
    }

    public void Dispose()
    {
        Stop();
        _channel.Dispose();
        _serial.Dispose();
    }
}
