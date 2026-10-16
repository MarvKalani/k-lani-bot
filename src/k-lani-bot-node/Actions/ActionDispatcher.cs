using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions.Handlers;
using KLaniBot.Advisory;
using KLaniBot.Audit;
using KLaniBot.Backup;
using KLaniBot.Common;
using KLaniBot.Credentials;
using KLaniBot.Guard;
using KLaniBot.Inventory;
using KLaniBot.Platforms;
using KLaniBot.Visibility;

namespace KLaniBot.Actions;

// Use case: every request of an AI client for this machine ends here. The dispatcher decides whether the
// request may run (the phases of Execute) and hands it to the handler group that knows the tool; the
// groups are registered by name, so a tool and its handler cannot drift apart. It also owns everything
// that proves what happened: the receipt, the journal entry and the trace.
public sealed class ActionDispatcher : IDisposable
{
    private const int MaximumMacroDepth = 8;

    // Tells the caller that its decision was not applied, because no modal dialog stood in the way of the target.
    private const string DecisionIgnoredMessage = "No dialog is open.";

    private readonly IUiService _ui;
    private readonly NodePolicy _policy;
    private readonly OperationLedger _operations;
    private readonly AuditJournal _journal;
    private readonly AccessVisibility? _visibility;
    private readonly ActionTrace _trace;
    private readonly NodeRiskClassifier? _classifier;
    private readonly DialogClassifier? _dialogClassifier;
    private readonly DesktopObservationState _desktop = new();
    private readonly HandlerServices _services;
    private readonly Dictionary<Tool, ActionHandler> _handlers = new();
    private readonly BackupGate _backupGate;

    // The caller of the request that is executing. Macro steps run with the same caller, so nesting does
    // not change it.
    private RequestContext _context = new("", "");
    private int _depth;

    public ActionDispatcher(
        IUiService ui,
        NodePolicy policy,
        AccessVisibility? visibility = null,
        NodeRiskClassifier? classifier = null,
        TimeProvider? clock = null,
        ISecretProvider? backupKeys = null)
    {
        _ui = ui;
        _policy = policy;
        _visibility = visibility;
        _journal = new AuditJournal(policy.StateRoot, policy.OperationWindow, clock);
        _trace = new ActionTrace(policy.StateRoot, policy.TraceRetention, Audit);
        _operations = _journal.Operations;
        _classifier = classifier ?? CreateConfiguredClassifier(policy);
        _dialogClassifier = _classifier == null ? null : new DialogClassifier(_classifier.Evaluator);
        _services = new HandlerServices
        {
            Ui = ui,
            Policy = policy,
            Visibility = visibility,
            Desktop = _desktop,
            Files = new FileSystemModule(policy),
            Terminal = new TerminalModule(policy.WorkspaceRoot),
            Inventory = new InventoryPages(),
            Macros = new MacroEngine(Path.Combine(policy.StateRoot, "macros.json")),
            Atlas = new UiStateAtlas(policy.StateRoot),
            ClassifierEnabled = _classifier != null,
            Backup = new NodeBackup(policy, backupKeys ?? NodeBackup.PlatformKeys(policy), clock),
            ApplicationMcps = new ApplicationMcp.ApplicationMcpHost(policy, clock),
            RunNestedAction = Execute
        };
        _backupGate = new BackupGate(policy, _services.Backup, clock);
        RegisterHandlers();
    }

    // The tools this dispatcher answers itself. The two fleet tools of the contract are answered by the gateway.
    internal IReadOnlyCollection<Tool> HandledActions => _handlers.Keys;

    // Throws InvalidOperationException when two handler groups register the same tool, a defect of this build.
    private void RegisterHandlers()
    {
        void Add(Tool action, ActionHandler handler)
        {
            if (!_handlers.TryAdd(action, handler))
            {
                throw new InvalidOperationException("Two handlers are registered for " + action + ".");
            }
        }
        new MetaActionHandlers(_services).Register(Add);
        new ApplicationObjectActionHandlers(_services).Register(Add);
        new FileActionHandlers(_services).Register(Add);
        new TerminalActionHandlers(_services).Register(Add);
        new WindowActionHandlers(_services).Register(Add);
        new InputActionHandlers(_services).Register(Add);
        new ObservationActionHandlers(_services).Register(Add);
        new ClipboardActionHandlers(_services).Register(Add);
        new ProcessActionHandlers(_services).Register(Add);
        new MacroActionHandlers(_services).Register(Add);
        new BackupActionHandlers(_services).Register(Add);
        new ApplicationMcpActionHandlers(_services).Register(Add);
    }

    // The advisory classifier is optional. It exists only when the operator configured a provider.
    private static NodeRiskClassifier? CreateConfiguredClassifier(NodePolicy policy)
    {
        if (policy.GuardClassifierConfigFile == null)
        {
            return null;
        }
        return new NodeRiskClassifier(AdvisoryProviders.Create(policy.GuardClassifierConfigFile));
    }

    // The order of the phases is the order in which a request may learn things and cause effects.
    // Nothing reaches the operating system before the request is authorized, approved by the person at
    // the machine, compared with what the node remembers of earlier requests, passed by the guard and
    // the advisor, and admitted to the journal. A phase refuses by throwing; Execute turns that into an
    // answer and a journal entry.
    public JsonObject Execute(JsonObject frame, RequestContext context)
    {
        _context = context;
        ActionRequest request = ActionRequest.Read(frame, context);
        var execution = new ActionExecution(request);
        try
        {
            RejectManagementFields(frame);
            AuthorizeRequest(request);
            RespectPace(request);
            var (activity, rejection) = RequireLocalConsent(request);
            if (rejection != null)
            {
                return rejection;
            }
            using (activity)
            {
                JsonObject? knownAnswer = ReplayIfKnown(request);
                if (knownAnswer != null)
                {
                    return knownAnswer;
                }
                CheckTargetSnapshot(request);
                ApplyUiGuard(request);
                RequireExpectedResult(request);
                RouteDialogDecision(execution);
                ConsultAdvisor(request);
                RequireBackup(request);
                AdmitMutation(execution);
                return ExecuteAdmitted(execution);
            }
        }
        catch (Exception exception)
        {
            return RejectRequest(execution, exception);
        }
    }

    // Administrative controls (heartbeat, close, inventory permission) travel on the same channel in
    // frames of their own. A tool call must not be able to smuggle one in as an extra field.
    // Throws ArgumentException for any field besides action, args, principal_id and session_id.
    private static void RejectManagementFields(JsonObject frame)
    {
        bool hasUnexpectedField = frame.Any(field => field.Key is not ("action" or "args" or "principal_id" or "session_id"));
        if (hasUnexpectedField)
        {
            throw new ArgumentException("Unexpected action frame field; management controls are not MCP actions.");
        }
    }

    private void AuthorizeRequest(ActionRequest request)
    {
        _policy.Authorize(request.Context, Scope.Observe);
        WindowInteractionPolicy.ValidateAction(WindowInteraction, request.Tool, request.Arguments);
        ToolContract.Validate(request.Specification["inputSchema"]!.AsObject(), request.Arguments);
        _policy.Authorize(request.Context, request.Scope);
    }

    // Asks the person at the machine, once per session, whether this client may act. A rejection is
    // returned to the client as it is, without a receipt: nothing was admitted that could be receipted.
    private ActivityStart RequireLocalConsent(ActionRequest request)
    {
        if (_visibility == null)
        {
            return ActivityStart.WithoutActivity;
        }
        return _visibility.Begin(request.Tool, request.Context);
    }

    // A client that asks again with the same id gets the recorded answer instead of a second effect.
    // Throws ArgumentException when the id was used before with other content.
    private JsonObject? ReplayIfKnown(ActionRequest request)
    {
        if (request.Tool == Tool.GetActionStatus)
        {
            return LookUpOperationStatus(request);
        }
        if (!_operations.TryGetValue(request.OperationKey, out var existing))
        {
            return null;
        }
        if (existing.Hash != request.Hash)
        {
            throw new ArgumentException("Operation identifier reused with different content.");
        }
        return Status(existing.Result);
    }

    private JsonObject LookUpOperationStatus(ActionRequest request)
    {
        string operationKey = request.Context.PrincipalId + ":" + request.Arguments["request_id"];
        if (_operations.TryGetValue(operationKey, out var recorded))
        {
            return Status(recorded.Result);
        }
        return ActionResults.Failure(
            ActionErrorCode.UnknownOperation,
            ActionOutcome.Unknown,
            "No recorded result. This does not prove that an action did not occur before a worker restart.");
    }

    // A target id is only valid together with the snapshot in which it was handed out. Ids are reused
    // by the platform backends, so an old id could otherwise point at a different element.
    // Throws ArgumentException when the target id belongs to an older snapshot.
    private void CheckTargetSnapshot(ActionRequest request)
    {
        bool refersToTarget = request.Arguments.ContainsKey("id");
        if (refersToTarget && request.Arguments["snapshot_id"]?.ToString() != _desktop.Snapshot)
        {
            throw new ArgumentException("Stale target snapshot.");
        }
    }

    private void ApplyUiGuard(ActionRequest request)
    {
        UiActionGuard.Authorize(_policy, _policy.Get(request.Context.PrincipalId), _ui, request.Tool, ArgumentsForGuard(request));
        // A postcondition is another UI read. It is checked against the guard now, before the mutation
        // happens, and again when it is evaluated.
        if (request.Arguments["postcondition"] is JsonObject proposedCondition)
        {
            UiActionGuard.Authorize(_policy, _policy.Get(request.Context.PrincipalId), _ui, Tool.VerifyElement, proposedCondition);
        }
    }

    // Every step the person could watch passes the pace gate before it reaches the desktop. Only while the agent is paused or
    // slowed down is the target read for the step list, because that is a backend call.
    // Throws PausedByPersonException when the step met a pause; nothing was dispatched.
    private void RespectPace(ActionRequest request)
    {
        if (_visibility == null || !AccessVisibility.NeedsConsent(request.Tool))
        {
            return;
        }

        bool watched = _visibility.CurrentPace != Pace.Normal;
        UiTargetIdentity? target = watched ? DialogEvidence.TargetOf(_ui, request.Arguments) : null;
        StepView step = StepDescriber.Describe(request.Tool, request.Arguments, target);
        IReadOnlyList<StepView>? announced = StepDescriber.Announced(request.Arguments);
        if (_visibility.AwaitPace(request.Context, step, announced, request.OperationId) == PaceDecision.PausedByPerson)
        {
            throw new PausedByPersonException();
        }
    }

    // Throws UnauthorizedAccessException when the policy requires a postcondition and the request names none.
    private void RequireExpectedResult(ActionRequest request)
    {
        bool required = _policy.RequirePostcondition && ProducesApplicationResult(request.Tool);
        if (required && request.Arguments["postcondition"] is not JsonObject)
        {
            throw new UnauthorizedAccessException("This node requires a postcondition naming the expected result of this action.");
        }
    }

    // A recorded decision is routed when the node finds a modal dialog for its target, and also when the guard rule of the target
    // requires a decision, which keeps the routing it has. The advisor sorts the dialog by what the node read, and the
    // administrator's DialogDeciders say whether the agent may answer it. Pause mode still stops it in the backend. Otherwise the
    // decision is recorded and reported as not applied, and the advisor is not asked.
    // Throws DialogRoutingException when the dialog's class is left to the person at the machine or blocked.
    private void RouteDialogDecision(ActionExecution execution)
    {
        ActionRequest request = execution.Request;
        if (!request.Mutates || request.Arguments["decision"] is not JsonObject)
        {
            return;
        }

        JsonObject evidence = DialogEvidence.Describe(_ui, request.Arguments, DialogTextEvidence.Excerpt);
        execution.TargetEvidence = evidence;
        bool noDialog = DetectModalDialogFor(request) == ModalDialogDetection.Absent;
        if (noDialog && !GuardRequiresDecision(request))
        {
            execution.DecisionIgnored = true;
            return;
        }

        DialogClass dialogClass = _dialogClassifier?.Classify(evidence) ?? DialogClass.Unclear;
        var routing = new DialogRouting(dialogClass, _policy.DialogDeciders.For(dialogClass), evidence);
        if (routing.Decider != DialogDecider.Agent)
        {
            throw new DialogRoutingException(routing);
        }
        execution.DialogRouting = routing;
    }

    // The rule of a control can demand a decision for every action on it, as for a modal dialog, even when no dialog is open. That
    // demand keeps the routing of the decision. Only the guarded profile has rules, and a target the guard cannot resolve keeps it too.
    private bool GuardRequiresDecision(ActionRequest request)
    {
        UiGuardOptions guardOptions = _policy.Get(request.Context.PrincipalId).UiGuard;
        JsonNode? elementId = request.Arguments["id"];
        if (guardOptions.Mode != UiGuardMode.Guarded || elementId == null)
        {
            return false;
        }
        UiTargetIdentity? target = _ui.GetTargetIdentity(elementId.GetValue<int>());
        return target == null || UiActionGuard.RuleRequiresDecision(guardOptions, target, request.Tool);
    }

    // Unknown when the request names no element, so such a request keeps the routing it always had.
    private ModalDialogDetection DetectModalDialogFor(ActionRequest request)
    {
        JsonNode? elementId = request.Arguments["id"];
        if (elementId == null)
        {
            return ModalDialogDetection.Unknown;
        }
        return _ui.DetectModalDialog(elementId.GetValue<int>());
    }

    // Inputs an application answers with work of its own, such as loading data or opening a page.
    private static bool ProducesApplicationResult(Tool action)
    {
        return action is Tool.ClickElement or Tool.SilentClick or Tool.TypeText or Tool.MouseClick or Tool.DragAndDrop
            or Tool.Hotkey or Tool.PasteClipboard;
    }

    // Under the guard, a chapter section is judged by the process of the chapter that was observed, not
    // by whatever process the client names.
    private JsonObject ArgumentsForGuard(ActionRequest request)
    {
        bool guarded = _policy.Get(request.Context.PrincipalId).UiGuard.Mode == UiGuardMode.Guarded;
        if (request.Tool != Tool.InspectSection || !guarded)
        {
            return request.Arguments;
        }
        var bound = (JsonObject)request.Arguments.DeepClone();
        bound["pid"] = _desktop.ChapterProcess;
        return bound;
    }

    // The advisor can take seconds, and it can be exchanged for another provider. It may only stop an
    // action, never allow one that the guard refused, and local approval is checked again after it.
    // Throws UnauthorizedAccessException when the advisor is configured but the principal states no task intent, or when it vetoes.
    private void ConsultAdvisor(ActionRequest request)
    {
        bool exempt = request.Tool is Tool.RunMacro or Tool.SaveMacro or Tool.EndSession;
        if (_classifier == null || !request.Mutates || exempt)
        {
            return;
        }
        PrincipalPolicy principal = _policy.Get(request.Context.PrincipalId);
        if (string.IsNullOrWhiteSpace(principal.UiGuard.Intent))
        {
            throw new UnauthorizedAccessException("Node classifier requires a locally configured task intent.");
        }
        UiTargetIdentity? target = request.Arguments["id"] is { } targetId ? _ui.GetTargetIdentity(targetId.GetValue<int>()) : null;
        _classifier.Check(request.Tool, request.Arguments, principal.UiGuard.Intent, target);
        _visibility?.RequireApproval(request.Context);
    }

    // After authorization, replay and the guards, so a retry still gets its recorded answer and a refused step stays undone.
    // Throws BackupRequiredException when no current backup covers the session, UnauthorizedAccessException for an unreadable policy.
    private void RequireBackup(ActionRequest request)
    {
        if (request.Mutates)
        {
            string? program = request.Tool == Tool.ApplicationMcpCall ? request.Arguments["program"]?.ToString() : null;
            _backupGate.Require(request.Context, request.Tool, program);
        }
    }

    // From here on the request is on record: a crash leaves "admitted" in the journal, which recovery
    // reads as "unknown", so the action is never silently repeated.
    // Throws ArgumentException when a mutation has no UUIDv7 request_id.
    private void AdmitMutation(ActionExecution execution)
    {
        ActionRequest request = execution.Request;
        if (request.Mutates)
        {
            string? requestId = request.Arguments["request_id"]?.ToString();
            bool versionSeven = Guid.TryParse(requestId, out var operationGuid) && operationGuid.Version == 7;
            if (!versionSeven)
            {
                throw new ArgumentException("Mutations require an RFC 9562 UUIDv7 request_id.");
            }
            _operations.RequireAdmissible(request.OperationId);
        }
        execution.Decision = AgentDecision.Read(request.Arguments);
        if (!request.Mutates)
        {
            return;
        }
        if (execution.Decision != null)
        {
            RecordPlannedDecision(execution, execution.Decision);
        }
        Audit(AdmissionEntry(request));
        execution.MutationAdmitted = true;
        _operations.Remember(
            request.OperationKey,
            request.Hash,
            ActionResults.Failure(ActionOutcome.Unknown, "Operation admitted; outcome not yet confirmed."));
    }

    private static JsonObject AdmissionEntry(ActionRequest request)
    {
        return new JsonObject
        {
            ["operation_key"] = request.OperationKey,
            ["request_hash"] = request.Hash,
            ["operation_id"] = request.OperationId,
            ["principal_id"] = request.Context.PrincipalId,
            ["session_id"] = request.Context.SessionId,
            ["action"] = request.Action,
            ["outcome"] = ActionOutcome.Admitted.ToWireName(),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    // The agent's reasons are written down before the action, so they cannot be adjusted to the result, together with what
    // the node itself saw of the target and its window. A decision the node found no dialog for names that instead of a class.
    private void RecordPlannedDecision(ActionExecution execution, AgentDecision decision)
    {
        ActionRequest request = execution.Request;
        var planned = new JsonObject
        {
            ["event"] = "agent_decision_planned",
            ["operation_id"] = request.OperationId,
            ["principal_id"] = request.Context.PrincipalId,
            ["session_id"] = request.Context.SessionId,
            ["action"] = request.Action,
            ["decision"] = decision.ToAudit()
        };
        if (execution.DecisionIgnored)
        {
            planned["decision_ignored"] = DecisionIgnoredMessage;
        }
        else
        {
            DialogRouting routing = execution.DialogRouting ?? throw new UnreachableException();
            planned["dialog_class"] = WireNames<DialogClass>.Of(routing.Class);
        }
        JsonObject evidence = execution.TargetEvidence ?? throw new UnreachableException();
        planned["evidence"] = DialogEvidence.ForJournal(evidence, _policy.DialogTextEvidence);
        planned["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        Audit(planned);
    }

    private bool RecordActionScreenshots => _visibility?.State.RecordActionScreenshots ?? _policy.Visibility.RecordActionScreenshots;

    private JsonObject ExecuteAdmitted(ActionExecution execution)
    {
        ActionRequest request = execution.Request;
        execution.RecordTrace = request.Tool != Tool.EndSession && RecordActionScreenshots;
        execution.RecordScreenshots = execution.RecordTrace && AccessVisibility.NeedsBorder(request.Tool);
        DialogWatch? watch = WatchForDialogs(request);
        JsonObject result = Dispatch(execution);
        AddIgnoredDecisionNotice(execution, result);
        ActionOutcome outcome = DecideOutcome(request, result, watch);
        result["outcome"] = outcome.ToWireName();
        ActionReceipt receipt = BuildReceipt(execution, result, outcome);
        result["receipt"] = receipt.ToJson();
        SaveTraceAndJournal(execution, result);
        if (request.Mutates)
        {
            _operations.Remember(request.OperationKey, request.Hash, (JsonObject)result.DeepClone());
        }
        if (_visibility != null)
        {
            StepDiagnostics diagnostics = StepDiagnosticDescriber.Describe(request.Tool, request.Arguments, result,
                request.OperationId, execution.ElapsedMilliseconds);
            _visibility.FinishStep(request.OperationId, StatusOfStep(outcome),
                StepResultDescriber.Describe(request.Tool, result), diagnostics);
        }
        return result;
    }

    private static StepStatus StatusOfStep(ActionOutcome outcome)
    {
        return outcome switch
        {
            ActionOutcome.Completed or ActionOutcome.Verified or ActionOutcome.Dispatched => StepStatus.Completed,
            ActionOutcome.Admitted or ActionOutcome.Unknown => StepStatus.Uncertain,
            ActionOutcome.NotDispatched or ActionOutcome.PostconditionFailed or ActionOutcome.Failed or ActionOutcome.Partial
                or ActionOutcome.SelectorCardinalityFailed or ActionOutcome.ResultBudgetExceeded => StepStatus.Failed,
            _ => throw new UnreachableException()
        };
    }

    // The handler gets its own copy of the arguments, so nothing it does to them can change what the
    // receipt and the postcondition read afterwards. The backend sees a decision as recorded only when the node applied it.
    private JsonObject Dispatch(ActionExecution execution)
    {
        ActionRequest request = execution.Request;
        execution.StartTiming();
        var arguments = (JsonObject)request.Arguments.DeepClone();
        bool decisionRecorded = request.Arguments["decision"] is JsonObject && !execution.DecisionIgnored;
        object handled = RunHandler(request, arguments, decisionRecorded, () => CaptureBeforeImage(execution));
        return JsonSerializer.SerializeToNode(handled)!.AsObject();
    }

    // Called by the handler once every check that does not need the desktop has passed. A request that
    // is refused for a foreign lease, a missing backend feature or a stale snapshot has not touched the
    // screen and gets no image of it.
    private void CaptureBeforeImage(ActionExecution execution)
    {
        if (!execution.RecordScreenshots)
        {
            return;
        }
        execution.BeforeImageAttempted = true;
        CaptureTraceScreenshot(execution.Request.OperationId, "before", execution.Screenshots);
    }

    private ActionOutcome DecideOutcome(ActionRequest request, JsonObject result, DialogWatch? watch)
    {
        bool endsInsideNode = request.Feature is ToolFeature.Filesystem or ToolFeature.Backup;
        ActionOutcome outcome = DeriveOutcome(endsInsideNode, request.Mutates, result);
        if (result["success"]?.GetValue<bool>() == true && request.Arguments["postcondition"] is JsonObject condition)
        {
            outcome = VerifyPostcondition(condition, result, watch);
        }
        if (result["outcome"] is { } specified)
        {
            // A handler that names its own outcome (a macro, a refused modal target) overrides the derived one.
            outcome = ActionOutcomeNames.Parse(specified.ToString(), ReportUnrecognizedOutcome);
        }
        return outcome;
    }

    private ActionOutcome VerifyPostcondition(JsonObject condition, JsonObject result, DialogWatch? watch)
    {
        JsonObject verification = Verify(condition, watch);
        result["verification"] = verification;
        if (verification["unexpected_dialog"] is { } dialog)
        {
            result["unexpected_dialog"] = dialog.DeepClone();
        }
        bool verified = verification["success"]?.GetValue<bool>() == true;
        result["success"] = verified;
        return verified ? ActionOutcome.Verified : ActionOutcome.PostconditionFailed;
    }

    private ActionReceipt BuildReceipt(ActionExecution execution, JsonObject result, ActionOutcome outcome)
    {
        ActionRequest request = execution.Request;
        var receipt = new ActionReceipt(request, _policy.NodeId, _desktop.Snapshot, execution.ElapsedMilliseconds, outcome);
        if (execution.BeforeImageAttempted)
        {
            CaptureTraceScreenshot(request.OperationId, "after", execution.Screenshots);
            receipt.AddScreenshots(execution.Screenshots);
        }
        if (request.Mutates)
        {
            receipt.AddOperation(request);
        }
        if (execution.Decision != null)
        {
            JsonNode decisionResult = result["verification"]?.DeepClone()
                ?? new JsonObject { ["outcome"] = outcome.ToWireName(), ["verified"] = false };
            receipt.AddDecision(execution.Decision, decisionResult);
        }
        bool success = result["success"]?.GetValue<bool>() == true;
        receipt.AddResult(success, result, request, _visibility?.State.Presentation, WindowInteraction);
        return receipt;
    }

    private void SaveTraceAndJournal(ActionExecution execution, JsonObject result)
    {
        JsonObject receipt = result["receipt"]!.AsObject();
        if (execution.RecordTrace)
        {
            try
            {
                result["trace_file"] = _trace.SaveStep(execution.Request.OperationId, receipt, execution.Screenshots);
            }
            catch (Exception exception)
            {
                // A recording failure must not hide that an OS action already ran.
                result["trace_error"] = exception.Message;
            }
        }
        Audit(receipt);
    }

    // A request that failed before or during execution still gets an answer and a journal entry. If the
    // journal cannot take the entry either, the answer is still given: an exception leaving Execute
    // would be reported by the worker as "not dispatched", which is wrong once a mutation was admitted.
    private JsonObject RejectRequest(ActionExecution execution, Exception exception)
    {
        ActionRequest request = execution.Request;
        ActionOutcome outcome = execution.OutcomeAfterFailure();
        JsonObject result = ActionResults.Failure(ErrorCodeForRejectedRequest(exception), outcome, exception.Message);
        AddIgnoredDecisionNotice(execution, result);
        StepStatus stepStatus = exception is PausedByPersonException ? StepStatus.Paused : StatusOfStep(outcome);
        FinishRejectedStep(request.OperationId, stepStatus, result);
        JsonObject rejected = RejectionEntry(request, outcome, exception);
        if (exception is DialogRoutingException routed)
        {
            AddDialogRouting(result, rejected, routed.Routing);
        }
        try
        {
            Audit(rejected);
        }
        catch (Exception journalFailure)
        {
            // The journal refuses every later entry after a failure, so the node stops accepting
            // mutations by itself; this answer only has to be truthful about the one in flight.
            result["journal_error"] = journalFailure.Message;
            return result;
        }
        if (RecordActionScreenshots)
        {
            try
            {
                result["trace_file"] = _trace.SaveRejectedStep(request.OperationId, rejected);
            }
            catch (Exception traceFailure)
            {
                result["trace_error"] = traceFailure.Message;
            }
        }
        return result;
    }

    // A broken indicator must not hide a rejection or turn an admitted mutation into an unrecorded exception.
    private void FinishRejectedStep(string operationId, StepStatus status, JsonObject result)
    {
        try
        {
            _visibility?.FinishStep(operationId, status);
        }
        catch (Exception displayFailure)
        {
            result["indicator_error"] = KLaniBot.Startup.Diagnostics.Describe(displayFailure);
        }
    }

    private static JsonObject RejectionEntry(ActionRequest request, ActionOutcome outcome, Exception exception)
    {
        return new JsonObject
        {
            ["operation_id"] = request.OperationId,
            ["action"] = request.Action,
            ["principal_id"] = request.Context.PrincipalId,
            ["session_id"] = request.Context.SessionId,
            ["success"] = false,
            ["outcome"] = outcome.ToWireName(),
            ["error"] = exception.Message,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    // The agent learns who handles the dialog, so it hands back instead of retrying; the journal keeps what the node saw.
    private void AddDialogRouting(JsonObject result, JsonObject rejected, DialogRouting routing)
    {
        result["dialog_class"] = WireNames<DialogClass>.Of(routing.Class);
        result["decider"] = WireNames<DialogDecider>.Of(routing.Decider);
        rejected["dialog_class"] = WireNames<DialogClass>.Of(routing.Class);
        rejected["decider"] = WireNames<DialogDecider>.Of(routing.Decider);
        rejected["evidence"] = DialogEvidence.ForJournal(routing.Evidence, _policy.DialogTextEvidence);
    }

    // The caller learns that its decision was not applied. The receipt keeps the decision itself, as it always did.
    private static void AddIgnoredDecisionNotice(ActionExecution execution, JsonObject result)
    {
        if (execution.DecisionIgnored)
        {
            result["decision_ignored"] = DecisionIgnoredMessage;
        }
    }

    // What the client may conclude from a finished handler. A handler that refused with a code never
    // touched the machine; one that failed without a code after a mutation may have.
    private static ActionOutcome DeriveOutcome(bool endsInsideNode, bool mutation, JsonObject result)
    {
        bool success = result["success"]?.GetValue<bool>() == true;
        if (success)
        {
            // File operations end inside the node. Anything else that mutates was only handed to the
            // operating system, which proves nothing about the application.
            bool handedToOperatingSystem = mutation && !endsInsideNode;
            return handedToOperatingSystem ? ActionOutcome.Dispatched : ActionOutcome.Completed;
        }
        if (result["code"] != null)
        {
            return ActionOutcome.NotDispatched;
        }
        return mutation ? ActionOutcome.Unknown : ActionOutcome.Failed;
    }

    private static ActionErrorCode ErrorCodeForRejectedRequest(Exception exception)
    {
        if (exception is PausedByPersonException)
        {
            return ActionErrorCode.PausedByPerson;
        }
        if (exception is OperationWindowException window)
        {
            return window.Code;
        }
        if (exception is BackupRequiredException)
        {
            return ActionErrorCode.BackupRequired;
        }
        return exception is UnauthorizedAccessException ? ActionErrorCode.Denied : ActionErrorCode.InvalidRequest;
    }

    private static ActionErrorCode ErrorCodeForHandlerFailure(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => ActionErrorCode.Denied,
            NotSupportedException => ActionErrorCode.Unsupported,
            _ => ActionErrorCode.InvalidRequest
        };
    }

    private static void ReportUnrecognizedOutcome(string wireName)
    {
        Console.Error.WriteLine("A handler reported an unrecognized outcome '" + wireName + "'; treating it as unknown.");
    }

    // The answer to a retry: what happened the first time, marked as a replay, without running it again.
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
    // Throws NotSupportedException without a screenshot backend, UnauthorizedAccessException when the person did not approve recording and
    // InvalidOperationException when the capture fails; the caller records each as the reason the image is missing.
    private void CaptureTraceScreenshot(string operationId, string phase, JsonObject screenshots)
    {
        try
        {
            _policy.Authorize(_context, Scope.Observe);
            UiActionGuard.Authorize(_policy, _policy.Get(_context.PrincipalId), _ui, Tool.TakeScreenshot, new JsonObject());
            _ui.GetDesktopSession()?.RequireInputAvailable();
            if (!_ui.Features.Contains(ToolFeature.Screenshot))
            {
                throw new NotSupportedException("Screenshot capability unavailable.");
            }
            var visible = _visibility?.Begin(Tool.TakeScreenshot, _context);
            if (visible?.Rejection != null)
            {
                throw new UnauthorizedAccessException("Screenshot recording was not approved by the local visibility gate.");
            }
            using var activity = visible?.Activity;
            // A trace must not add access after local consent was denied/revoked.
            _visibility?.RequireApproval(_context);
            var result = _ui.Screenshot();
            if (!result.Success || result.Png == null)
            {
                throw new InvalidOperationException(result.Message);
            }
            screenshots[phase] = new JsonObject
            {
                ["file"] = _trace.SaveScreenshot(operationId, phase, result.Png),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(result.Png)),
                ["bytes"] = result.Png.Length
            };
        }
        catch (Exception exception)
        {
            screenshots[phase + "_unavailable"] = exception.Message;
        }
    }
    // The journal gets the whole entry. The process log gets a copy for whoever watches the node run, for
    // example `docker logs` of a sandboxed actuator, but without the agent's own words: decision texts
    // belong in the journal, which has restricted access, and not in a log that other tools collect.
    private void Audit(JsonObject entry)
    {
        _journal.Append(entry);
        var processLogLine = (JsonObject)entry.DeepClone();
        processLogLine.Remove("decision");
        processLogLine.Remove("decision_result");
        Console.Error.WriteLine(processLogLine.ToJsonString());
    }
    private JsonObject Verify(JsonObject args, DialogWatch? watch)
    {
        _visibility?.RequireApproval(_context);
        UiActionGuard.Authorize(_policy, _policy.Get(_context.PrincipalId), _ui, Tool.VerifyElement, args);

        var wait = new PostconditionWait(_ui, () => _visibility?.IsApproved(_context) ?? true);
        return wait.Wait(args, watch);
    }

    // Only an action on an observed element names the program whose dialog could interrupt the wait for its result.
    private DialogWatch? WatchForDialogs(ActionRequest request)
    {
        bool waitsForResult = request.Arguments["postcondition"] is JsonObject;
        UiTargetIdentity? target = waitsForResult ? DialogEvidence.TargetOf(_ui, request.Arguments) : null;

        if (target == null)
        {
            return null;
        }
        return new DialogWatch(target.ProcessId, _ui.ShowingModalWindowTitle(target.ProcessId));
    }
    private WindowInteractionMode WindowInteraction => _services.WindowInteraction;

    // The nesting is counted per handler call: a macro runs its steps through Execute, and each of those
    // reaches the handler again.
    // Throws ArgumentException when macros nest deeper than allowed.
    private void EnterNestedExecution()
    {
        if (++_depth > MaximumMacroDepth)
        {
            throw new ArgumentException("Macro nesting limit exceeded.");
        }
    }

    // These two checks already ran when the request was authorized. They run again here because a
    // classifier call, a dialog or a queue can take seconds in between, and the person at the machine
    // may have revoked the session or the operator may have removed the scope in that time.
    private void RequireApprovalAndScopeAgain(ActionRequest request)
    {
        if (AccessVisibility.NeedsConsent(request.Tool))
        {
            _visibility?.RequireApproval(_context);
        }
        _policy.Authorize(_context, request.Scope);
    }

    // Throws NotSupportedException when this backend lacks the tool or its feature.
    private void RequireBackendSupport(ActionRequest request)
    {
        if (_ui.SupportedTools is { } supported && !supported.Contains(request.Tool))
        {
            throw new NotSupportedException("Tool unavailable on this backend.");
        }
        if (ToolFeatures.UsesDesktop(request.Feature) && !_ui.Features.Contains(request.Feature))
        {
            throw new NotSupportedException($"Backend capability '{WireNames<ToolFeature>.Of(request.Feature)}' is unavailable.");
        }
    }

    // Takes the desktop for this session, then checks that the target belongs to the snapshot it was
    // observed in. Both must hold before the screen is touched in any way, including the trace images.
    private void PrepareDesktopLease(ActionRequest request)
    {
        if (ToolFeatures.UsesDesktop(request.Feature))
        {
            TakeDesktopLease();
        }
        RequireCurrentSnapshot(request);
        RequireVisibleDesktop(request);
    }

    private void TakeDesktopLease()
    {
        string owner = _context.PrincipalId + ":" + _context.SessionId;
        _desktop.TakeLease(owner, DateTimeOffset.UtcNow);
    }

    // The earlier snapshot check only compares with the newest snapshot. This one also rejects a missing
    // snapshot, and it uses the chapter snapshot for sections.
    // Throws ArgumentException when the snapshot is missing or stale.
    private void RequireCurrentSnapshot(ActionRequest request)
    {
        bool isSection = request.Tool == Tool.InspectSection;
        if (!request.Arguments.ContainsKey("id") && !isSection)
        {
            return;
        }
        string expected = _desktop.SnapshotFor(isSection);
        if (string.IsNullOrEmpty(expected) || request.Arguments["snapshot_id"]?.ToString() != expected)
        {
            throw new ArgumentException("Stale or missing snapshot. Observe the target again.");
        }
    }

    // A cached accessibility tree in a disconnected session is not an observable desktop action. The
    // same visibility gate applies to screen reads as to input and screenshots.
    private void RequireVisibleDesktop(ActionRequest request)
    {
        if (AccessVisibility.NeedsBorder(request.Tool))
        {
            _ui.GetDesktopSession()?.RequireInputAvailable();
        }
    }

    // Runs the handler for one request. Everything before the handler checks that this node, its backend
    // and the desktop state allow the action. beforeEffect runs after those checks and before the handler,
    // which is the first place where anything can have an effect.
    private object RunHandler(ActionRequest request, JsonObject args, bool decisionRecorded, Action beforeEffect)
    {
        var call = new ActionCall(request.Context, request.Tool, args) { DecisionRecorded = decisionRecorded };
        try
        {
            EnterNestedExecution();
            RequireApprovalAndScopeAgain(request);
            RequireBackendSupport(request);
            PrepareDesktopLease(request);
            beforeEffect();
            if (request.Mutates && !HandlerMarksItself(request.Tool))
            {
                call.MarkBackendMayHaveMutated();
            }
            if (!_handlers.TryGetValue(request.Tool, out ActionHandler? handler))
            {
                return new
                {
                    success = false,
                    error = $"Unknown action: {request.Action}"
                };
            }
            return handler(call);
        }
        catch (Exception exception)
        {
            return new
            {
                success = false,
                code = ErrorCodeForHandlerFailure(exception).ToWireName(),
                outcome = (call.BackendMayHaveMutated ? ActionOutcome.Unknown : ActionOutcome.NotDispatched).ToWireName(),
                error = exception.Message
            };
        }
        finally
        {
            _depth--;
        }
    }

    // These actions mark the call themselves, right before they hand it to the operating system. A refusal
    // earlier in the sequence (a dialog, a changed element, a program or argument not allowed) must stay "not dispatched".
    private static bool HandlerMarksItself(Tool action)
    {
        return action is Tool.ClickElement or Tool.SilentClick or Tool.TypeText or Tool.MouseClick or Tool.LaunchProcess
            or Tool.ApplicationMcpCall;
    }

    public void Dispose()
    {
        _classifier?.Dispose();
        _services.ApplicationMcps.Dispose();
    }
}
