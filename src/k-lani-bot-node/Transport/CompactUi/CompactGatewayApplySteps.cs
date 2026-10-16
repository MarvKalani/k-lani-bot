using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_apply runs the checked steps of one request in order; each step reads the window again, acts through the
// ui_act writer, confirms a written value by reading it back, and reports only the change that its own step made.
internal sealed class CompactGatewayApplySteps
{
    private const string RetryText = "Do not automatically retry mutations or replay completed steps; read the state again.";
    private const int WaitPollMilliseconds = 250;

    private readonly CompactGatewayObservation _observation;
    private readonly CompactGatewayState _state;
    private readonly CompactNodeAccess _node;
    private readonly CompactWindowSelection _selection;
    private readonly JsonObject? _decision;
    private readonly CompactActProgress _progress;
    private readonly List<JsonObject> _entries = new();
    private IReadOnlyList<CompactTreeNode> _previousNodes;
    private CompactWindowTreeReading _newest;
    private bool _waitRidesOnNode;

    public CompactGatewayApplySteps(CompactGatewayObservation observation, CompactGatewayState state, CompactNodeAccess node,
        CompactWindowSelection selection, JsonObject? decision, CompactActProgress progress, CompactWindowTreeReading initial)
    {
        _observation = observation;
        _state = state;
        _node = node;
        _selection = selection;
        _decision = decision;
        _progress = progress;
        _newest = initial;
        _previousNodes = initial.Tree.Nodes;
    }

    // Runs the steps in order and stops at the first failing step; the failure reply lists the steps completed before it.
    public async Task<JsonObject> RunAsync(IReadOnlyList<CompactApplyStep> steps, CancellationToken cancellation)
    {
        var initialShape = CompactGatewayApply.StringValue(CompactGatewayApply.BuildCurrentState(_newest)["shape"]);
        _state.LatestTree = _newest.Tree;
        for (var index = 0; index < steps.Count; index++)
        {
            var windowsBefore = await WindowHandlesAsync(cancellation);
            var failure = await RunGuardedStepAsync(steps[index], FollowingStep(steps, index), index + 1, cancellation);
            if (failure != null)
            {
                return failure;
            }

            var dialog = await OpenedDialogAsync(steps[index], windowsBefore, cancellation);
            if (dialog != null)
            {
                return await DialogStopReplyAsync(dialog, index + 1, steps.Count, initialShape, cancellation);
            }
        }

        return SuccessReply(initialShape);
    }

    private static CompactApplyStep? FollowingStep(IReadOnlyList<CompactApplyStep> steps, int index)
    {
        return index + 1 < steps.Count ? steps[index + 1] : null;
    }

    // A step that throws ends the request like a refused step does; the exception message becomes the error of the reply.
    private async Task<JsonObject?> RunGuardedStepAsync(CompactApplyStep step, CompactApplyStep? following, int stepNumber,
        CancellationToken cancellation)
    {
        _progress.StartStep(_node.PossibleDispatches);
        try
        {
            return await ExecuteStepAsync(step, following, stepNumber, cancellation);
        }
        catch (Exception exception) when (exception is CompactNodeException or ArgumentException)
        {
            return FailureReply(ErrorReply(exception.Message), stepNumber);
        }
    }

    // Returns the failure reply when the step is refused or its write is not confirmed; null means the step completed.
    private async Task<JsonObject?> ExecuteStepAsync(CompactApplyStep step, CompactApplyStep? following, int stepNumber,
        CancellationToken cancellation)
    {
        if (step.Kind == CompactApplyKind.Wait)
        {
            return await WaitForStateAsync(step, stepNumber, cancellation);
        }

        _waitRidesOnNode = false;

        var reading = stepNumber == 1 ? _newest : await ReadWindowAsync(cancellation);
        var bindings = CompactGatewayApply.BindMembers(reading);
        var refusal = CompactGatewayApply.StepRefusal(step, bindings);
        if (refusal != null)
        {
            return FailureReply(refusal, stepNumber);
        }

        var binding = bindings[step.Path];
        var selector = SelectorFor(reading, binding);
        var postcondition = PostconditionFor(following, bindings, reading);
        _waitRidesOnNode = postcondition != null;
        await DispatchAsync(step, reading, selector, postcondition, cancellation);
        var after = await ReadWindowAsync(cancellation);
        if (!WriteConfirmed(step, binding, selector, after))
        {
            return FailureReply(ErrorReply("write_not_confirmed"), stepNumber);
        }

        RecordStep(step, binding, after);
        _progress.FinishStep();

        return null;
    }

    // Throws CompactNodeException when the bound member has no row in the window tree.
    private CompactStoredSelector SelectorFor(CompactWindowTreeReading reading, CompactMemberBinding binding)
    {
        var node = reading.Tree.Nodes.FirstOrDefault(row => row.Id == binding.NodeId)
            ?? throw new CompactNodeException("The bound member has no row in the window tree.");
        var target = CompactSemanticIdentity.Target(_selection.ProcessId, reading.Tree.Nodes, node, reading.Window);

        return CompactGatewayActions.SelectorOf(target);
    }

    // The writer applies the native guards and decisions of ui_act. A later step may change a field an earlier step
    // wrote on purpose, such as a menu command that converts it, and every step reports its own change.
    private async Task DispatchAsync(CompactApplyStep step, CompactWindowTreeReading reading, CompactStoredSelector selector,
        JsonObject? postcondition, CancellationToken cancellation)
    {
        var action = step.Kind == CompactApplyKind.Set ? CompactRecipeAction.Write : CompactRecipeAction.Click;
        var route = action == CompactRecipeAction.Write ? CompactRecipeRoute.NativeText : CompactRecipeRoute.Semantic;
        var recipe = new CompactRecipeStep(action, selector, route, action == CompactRecipeAction.Write ? "text" : null);
        var parameters = new Dictionary<string, string> { ["text"] = step.Value ?? string.Empty };
        var writer = new CompactGatewayWriter(_node, _observation);
        await writer.PerformAsync(recipe, selector, reading.Tree, reading.Window, _selection, parameters, _decision,
            (_, _) => { }, cancellation, postcondition);
    }

    // A wait right after an action on a named member is handed to the node as the postcondition of that action, so the node
    // also stops for an unexpected dialog and for the person at the machine. Positional or not yet present members keep polling.
    private JsonObject? PostconditionFor(CompactApplyStep? following, IReadOnlyDictionary<string, CompactMemberBinding> bindings,
        CompactWindowTreeReading reading)
    {
        if (following is not { Kind: CompactApplyKind.Wait } wait || !bindings.TryGetValue(wait.Path, out var binding) || binding.Fallback)
        {
            return null;
        }

        var row = reading.Tree.Nodes.FirstOrDefault(node => node.Id == binding.NodeId);
        if (row == null)
        {
            return null;
        }

        var condition = wait.Until ?? throw new UnreachableException();
        var postcondition = new JsonObject
        {
            ["name"] = row.Name,
            ["role"] = row.Role.Name
        };
        if (!string.IsNullOrEmpty(row.AutoId))
        {
            postcondition["auto_id"] = row.AutoId;
        }
        postcondition["expected_state"] = ExpectedStateWord(condition);
        if (condition == CompactApplyCondition.Value)
        {
            postcondition["expected_text"] = wait.Value;
        }
        postcondition["timeout_ms"] = wait.TimeoutMilliseconds;

        return postcondition;
    }

    // The node names the awaited state with the same words as the until word; a value wait expects the element to exist with the text.
    private static string ExpectedStateWord(CompactApplyCondition condition)
    {
        return condition switch
        {
            CompactApplyCondition.Exists => "exists",
            CompactApplyCondition.Visible => "visible",
            CompactApplyCondition.Hidden => "hidden",
            CompactApplyCondition.Absent => "absent",
            CompactApplyCondition.Enabled => "enabled",
            CompactApplyCondition.Disabled => "disabled",
            CompactApplyCondition.Value => "exists",
            _ => throw new UnreachableException()
        };
    }

    // A text or number write is confirmed when the same member of the tree after the step holds the value; a secret is never read back.
    private static bool WriteConfirmed(CompactApplyStep step, CompactMemberBinding binding, CompactStoredSelector selector,
        CompactWindowTreeReading after)
    {
        if (step.Kind == CompactApplyKind.Call || binding.Member == CompactBoundMember.Secret)
        {
            return true;
        }

        return CompactWritePostcondition.TryCheck(after.Tree.Nodes, selector, step.Value!, out _);
    }

    // The entry carries this step's own change and then the tree after it becomes the previous tree for the next step.
    private void RecordStep(CompactApplyStep step, CompactMemberBinding binding, CompactWindowTreeReading after)
    {
        _entries.Add(StepEntry(step, binding, after));
        _previousNodes = after.Tree.Nodes;
    }

    // The diff compares the tree before this step with the tree after it, so a change made by an earlier step is not repeated.
    private JsonObject StepEntry(CompactApplyStep step, CompactMemberBinding binding, CompactWindowTreeReading after)
    {
        var diff = CompactWindowDiff.Between(_previousNodes, after.Tree.Nodes, CompactGatewayApply.RequestFor(after));
        var entry = new JsonObject
        {
            [step.Kind == CompactApplyKind.Set ? "set" : "call"] = step.Path,
            ["changed"] = diff.Value ?? new JsonObject()
        };
        if (binding.Member == CompactBoundMember.Secret)
        {
            entry["write_only"] = true;
        }

        return entry;
    }

    // Reads the window with the instance checks of ui_observe, and makes the read the latest tree of the session.
    private async Task<CompactWindowTreeReading> ReadWindowAsync(CancellationToken cancellation)
    {
        var reading = await _observation.ReadTreeAsync(_selection.ProcessId, _selection, cancellation);
        _state.LatestTree = reading.Tree;
        _newest = reading;

        return reading;
    }

    // A wait sends no input: it reads the window at once, then every poll interval, until the awaited state holds or time runs out.
    private async Task<JsonObject?> WaitForStateAsync(CompactApplyStep step, int stepNumber, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var reading = await ReadWindowAsync(cancellation);
        while (!ConditionHolds(step, reading))
        {
            if (clock.ElapsedMilliseconds >= step.TimeoutMilliseconds)
            {
                return WaitNotMetReply(stepNumber);
            }

            await Task.Delay(WaitPollMilliseconds, cancellation);
            reading = await ReadWindowAsync(cancellation);
        }

        RecordWait(step, reading, clock.ElapsedMilliseconds);
        _waitRidesOnNode = false;
        _progress.FinishStep();

        return null;
    }

    private JsonObject WaitNotMetReply(int stepNumber)
    {
        var failure = ErrorReply("wait_not_met");
        failure["hint"] = CompactViewNames.WaitNotMetHint;

        return FailureReply(failure, stepNumber);
    }

    // Each condition is judged on one fresh window state; a path without a binding has no row, so only hidden and absent hold.
    private static bool ConditionHolds(CompactApplyStep step, CompactWindowTreeReading reading)
    {
        var condition = step.Until ?? throw new UnreachableException();
        var bindings = CompactGatewayApply.BindMembers(reading);
        if (!bindings.TryGetValue(step.Path, out var binding))
        {
            return condition is CompactApplyCondition.Hidden or CompactApplyCondition.Absent;
        }

        return condition switch
        {
            CompactApplyCondition.Exists => true,
            CompactApplyCondition.Visible => true,
            CompactApplyCondition.Hidden => false,
            CompactApplyCondition.Absent => false,
            CompactApplyCondition.Enabled => binding.Enabled,
            CompactApplyCondition.Disabled => !binding.Enabled,
            CompactApplyCondition.Value => BoundValue(reading, binding) == step.Value,
            _ => throw new UnreachableException()
        };
    }

    // The value that the bound row shows in this read; a binding whose row is missing holds no value.
    private static string? BoundValue(CompactWindowTreeReading reading, CompactMemberBinding binding)
    {
        var row = reading.Tree.Nodes.FirstOrDefault(node => node.Id == binding.NodeId);

        return row?.Value;
    }

    // The entry of a wait reports the change since the previous step, as a set or call entry does, and the time it waited.
    private void RecordWait(CompactApplyStep step, CompactWindowTreeReading after, long waitedMilliseconds)
    {
        var diff = CompactWindowDiff.Between(_previousNodes, after.Tree.Nodes, CompactGatewayApply.RequestFor(after));
        var entry = new JsonObject
        {
            ["wait"] = step.Path,
            ["until"] = step.Until?.ToString().ToLowerInvariant(),
            ["waited_ms"] = waitedMilliseconds,
            ["changed"] = diff.Value ?? new JsonObject()
        };
        if (_waitRidesOnNode)
        {
            entry["postcondition"] = "node";
        }
        _entries.Add(entry);
        _previousNodes = after.Tree.Nodes;
    }

    private async Task<IReadOnlyList<long>> WindowHandlesAsync(CancellationToken cancellation)
    {
        var windows = await _observation.RequireInstanceAsync(_selection.ProcessId, cancellation);

        return windows.Select(window => window.Handle).ToList();
    }

    // A set or call step opens a dialog when a dialog window appears that was not listed before the step; a wait opens none.
    private async Task<CompactNativeWindow?> OpenedDialogAsync(CompactApplyStep step, IReadOnlyList<long> handlesBefore,
        CancellationToken cancellation)
    {
        if (step.Kind == CompactApplyKind.Wait)
        {
            return null;
        }
        var windows = await _observation.RequireInstanceAsync(_selection.ProcessId, cancellation);

        return windows.FirstOrDefault(window => window.Kind == CompactWindowKind.Dialog && !handlesBefore.Contains(window.Handle));
    }

    // The dialog is read as a window state of its own, so the next call can change it; the request stops before its next step.
    private async Task<JsonObject> DialogStopReplyAsync(CompactNativeWindow dialog, int completedStep, int totalSteps,
        string initialShape, CancellationToken cancellation)
    {
        // The reply tells the model to change the dialog with ui_apply, so its window must be selectable without another ui_info.
        if (_state.ObservedWindows.All(window => window.Handle != dialog.Handle))
        {
            _state.ObservedWindows = [.. _state.ObservedWindows, dialog];
        }

        var selection = new CompactWindowSelection(_selection.ProcessId, dialog.Handle);
        var dialogReading = await _observation.ReadTreeAsync(_selection.ProcessId, selection, cancellation);
        var reply = completedStep < totalSteps
            ? FailureReply(ErrorReply("dialog_opened"), completedStep + 1)
            : SuccessReply(initialShape);
        reply["dialog"] = CompactGatewayApply.BuildCurrentState(dialogReading);
        reply["hint"] = CompactViewNames.ApplyDialogHint;

        return reply;
    }

    // The reply names the step that stopped the request and lists the steps that completed before it.
    private JsonObject FailureReply(JsonObject failure, int failedStep)
    {
        failure["verified"] = false;
        failure["failed_step"] = failedStep;
        failure["completed_steps"] = _progress.Completed;
        failure["failed_step_may_have_changed_ui"] = _progress.MayHaveChangedUi(_node.PossibleDispatches);
        failure["steps"] = EntriesArray();
        failure["retry"] = RetryText;

        return failure;
    }

    // The state and shape come from the newest tree; the declaration is added only when the shape has moved.
    private JsonObject SuccessReply(string initialShape)
    {
        var newest = CompactGatewayApply.BuildCurrentState(_newest);
        var reply = new JsonObject
        {
            ["success"] = true,
            ["verified"] = true,
            ["steps"] = EntriesArray(),
            ["state"] = newest["state"]?.DeepClone(),
            ["shape"] = newest["shape"]?.DeepClone(),
            ["pid"] = _selection.ProcessId,
            ["window"] = _newest.Window.Handle,
            ["kind"] = CompactWindowState.KindName(_newest.Window.Kind),
            ["title"] = newest["title"]?.DeepClone()
        };
        if (CompactGatewayApply.StringValue(newest["shape"]) != initialShape)
        {
            reply["declaration"] = newest["declaration"]?.DeepClone();
        }
        if (newest["more"] != null)
        {
            reply["more"] = newest["more"]?.DeepClone();
        }

        return reply;
    }

    private JsonArray EntriesArray()
    {
        return new JsonArray(_entries.Select(entry => (JsonNode?)entry).ToArray());
    }

    private static JsonObject ErrorReply(string error)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = error
        };
    }
}
