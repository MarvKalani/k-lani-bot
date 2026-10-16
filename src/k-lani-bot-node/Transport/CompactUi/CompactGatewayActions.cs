using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compact batches stop at the first uncertain effect and verify complete literal replacements before later steps or response clipping.
public sealed class CompactGatewayActions(CompactNodeAccess node, CompactGatewayObservation observation, CompactGatewayState state,
    Func<bool, CancellationToken, Task<JsonObject>>? capture = null)
{
    // The same words as the JavaScript gateway, so both twins name the separate keys identically.
    private const string KeyNameHint = " Name each key separately, for example [\"ctrl\", \"shift\", \"s\"].";

    public async Task<JsonObject> RunAsync(JsonObject arguments, CancellationToken cancellation)
    {
        IReadOnlyList<CompactUiStep> steps = CompactUiStepCodec.Read(arguments);
        if (!CompactResultOptionsCodec.TryParse(arguments, null, out var options, out var error))
        {
            return Failure(error!, 0, false, false);
        }
        state.Demonstration = null;
        var progress = new CompactActProgress(steps.Count, node.PossibleDispatches);
        var routes = new JsonArray();
        var bindings = new List<CompactDemonstrationStep>();
        var selection = state.SelectWindow(arguments);
        var visible = state.Projection.ExposedReferences;
        try
        {
            var windows = await observation.RequireInstanceAsync(selection.ProcessId, cancellation);
            var before = await node.ObserveAsync(selection, false, cancellation);
            await PerformStepsAsync(selection, steps, bindings, routes, progress, arguments["decision"] as JsonObject, cancellation);
            var result = await SuccessAsync(arguments, routes, progress, cancellation);
            RecordDemonstration(selection, bindings, before, windows);
            result["evidence"] = state.Demonstration!.Evidence.ToString();
            var checks = AddOutputs(result, bindings, state.LatestTree!.Nodes, options!.Mode);
            if (capture != null)
            {
                result["mcp"] = await capture(options.Mode != CompactResultMode.Legacy, cancellation);
            }
            return Format(result, options, visible, selection.ProcessId, checks);
        }
        catch (Exception exception)
        {
            state.Demonstration = null;
            bool mayHaveChangedUi = progress.MayHaveChangedUi(node.PossibleDispatches);
            var failure = Failure(exception.Message, progress.Completed, mayHaveChangedUi, progress.AfterAllSteps);
            return Format(failure, options!, visible, selection.ProcessId, null);
        }
    }

    // Each step runs after the progress records its start, so a failure of one step is judged by that step's native calls only.
    private async Task PerformStepsAsync(CompactWindowSelection selection, IReadOnlyList<CompactUiStep> steps,
        List<CompactDemonstrationStep> bindings, JsonArray routes, CompactActProgress progress, JsonObject? decision,
        CancellationToken cancellation)
    {
        foreach (CompactUiStep step in steps)
        {
            progress.StartStep(node.PossibleDispatches);
            var performed = await StepAsync(selection, step, bindings, decision, cancellation);
            progress.FinishStep();
            bindings.Add(performed);
            routes.Add(CompactCaptureNames.Route(performed.Route));
        }
    }

    // The success reply names the finished steps, their routes and the view observed after the last step.
    private async Task<JsonObject> SuccessAsync(JsonObject arguments, JsonArray routes, CompactActProgress progress,
        CancellationToken cancellation)
    {
        var view = await observation.ObserveAsync(ViewArguments(arguments), cancellation);

        return new JsonObject
        {
            ["success"] = true,
            ["completed_steps"] = progress.Completed,
            ["routes"] = routes,
            ["ui"] = view
        };
    }

    // Only a successful action becomes the demonstration that learning and automatic capture may use.
    private void RecordDemonstration(CompactWindowSelection selection, IReadOnlyList<CompactDemonstrationStep> bindings,
        CompactNativeObservation before, IReadOnlyList<CompactNativeWindow> windows)
    {
        state.Demonstration = new(Guid.NewGuid(), selection.ProcessId, bindings, before.Nodes, state.LatestTree!.Nodes,
            ChosenWindowHandle(windows, selection));
    }

    // The window an action acts in is the one the choice rule proves when the action starts; an unproved choice stays absent.
    private static long? ChosenWindowHandle(IReadOnlyList<CompactNativeWindow> windows, CompactWindowSelection selection)
    {
        return CompactWindowChoice.TryChoose(windows, selection, out var chosen) ? chosen.Handle : null;
    }

    // Throws CompactNodeException for a stale target, replaced instance or mismatching complete write; native refusal never causes replay.
    private async Task<CompactDemonstrationStep> StepAsync(CompactWindowSelection selection, CompactUiStep step,
        IReadOnlyList<CompactDemonstrationStep> preceding, JsonObject? decision, CancellationToken cancellation)
    {
        int process = selection.ProcessId;
        if (step.Action == CompactUiAction.Key)
        {
            return await KeysAsync(selection, step, preceding, decision, cancellation);
        }
        var target = await observation.TargetAsync(step.Reference!, process, cancellation);
        var windows = await observation.RequireInstanceAsync(process, cancellation);
        var instance = windows.Single(window => window.Handle == target.WindowHandle && window.Started == target.ProcessStarted);
        var current = await node.ObserveAsync(selection, false, cancellation);
        RequirePriorWrites(preceding, current.Nodes);
        await observation.RequireInstanceAsync(process, cancellation);
        var selector = SelectorOf(target);
        var action = step.Action == CompactUiAction.Write ? CompactRecipeAction.Write : CompactRecipeAction.Click;
        var route = action == CompactRecipeAction.Write ? CompactRecipeRoute.NativeText : CompactRecipeRoute.Semantic;
        var recipe = new CompactRecipeStep(action, selector, route, action == CompactRecipeAction.Write ? "text" : null);
        var parameters = new Dictionary<string, string> { ["text"] = step.Text ?? "" };
        route = await new CompactGatewayWriter(node, observation).PerformAsync(recipe, selector, current, instance, selection,
            parameters, decision, (observed, _) => RequirePriorWrites(preceding, observed.Nodes), cancellation);
        if (action == CompactRecipeAction.Write)
        {
            target = await VerifyWriteAsync(selection, step, selector, cancellation);
        }

        return new(action, target, route, step.Text, step.Reference);
    }

    // Throws CompactNodeException for unknown native keyboard outcomes, drift in previously written fields or a stale referenced target.
    // A referenced element gets keyboard focus without being activated, because some controls must be opened by a real key.
    private async Task<CompactDemonstrationStep> KeysAsync(CompactWindowSelection selection, CompactUiStep step,
        IReadOnlyList<CompactDemonstrationStep> preceding, JsonObject? decision, CancellationToken cancellation)
    {
        int process = selection.ProcessId;
        CompactSemanticTarget? target = null;
        JsonObject? focusArguments = null;
        if (step.Reference != null)
        {
            target = await observation.TargetAsync(step.Reference, process, cancellation);
        }
        var windows = await observation.RequireInstanceAsync(process, cancellation);
        var current = await node.ObserveAsync(selection, false, cancellation);
        RequirePriorWrites(preceding, current.Nodes);
        if (target != null)
        {
            var instance = windows.Single(window => window.Handle == target.WindowHandle && window.Started == target.ProcessStarted);
            focusArguments = CompactRecipeTarget.Require(current, SelectorOf(target), instance, CompactRecipeAction.Click);
            focusArguments["focus_only"] = true;
            if (decision != null)
            {
                focusArguments["decision"] = decision.DeepClone();
            }
        }
        RequireMutation(await node.MutateAsync(Tool.FocusWindow, new JsonObject { ["pid"] = process }, null, cancellation));
        if (focusArguments != null)
        {
            RequireMutation(await node.MutateAsync(Tool.ClickElement, focusArguments, null, cancellation));
        }
        RequireHotkey(await node.MutateAsync(Tool.Hotkey, new JsonObject
        {
            ["keys"] = new JsonArray(step.Keys!.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray())
        }, null, cancellation), step.Keys!);

        return new(CompactRecipeAction.Key, target, CompactRecipeRoute.FocusKeyboard, null, step.Reference);
    }

    // Throws CompactNodeException for a refused key step; a chord written as one string gets the separate-key hint.
    private static void RequireHotkey(JsonObject response, IReadOnlyList<string> keys)
    {
        try
        {
            RequireMutation(response);
        }
        catch (CompactNodeException exception) when (exception.Result is { Success: false }
            && keys.Any(key => key.Contains('+', StringComparison.Ordinal)))
        {
            throw new CompactNodeException(exception.Message + SentenceEnd(exception.Message) + KeyNameHint, exception.Result);
        }
    }

    // Throws CompactNodeException for missing or unconfirmed native mutation outcomes.
    private static void RequireMutation(JsonObject response)
    {
        CompactNodeResponse.Require(response);
        if (!CompactNodeResponse.Read(response).Outcome.ReportsSuccess())
        {
            throw new CompactNodeException("Native keyboard outcome remains unconfirmed.");
        }
    }

    // Throws CompactNodeException for stale targets or full-value mismatches after a setter, before any later action.
    private async Task<CompactSemanticTarget> VerifyWriteAsync(CompactWindowSelection selection, CompactUiStep step,
        CompactStoredSelector selector, CancellationToken cancellation)
    {
        int process = selection.ProcessId;
        var target = await observation.TargetAsync(step.Reference!, process, cancellation);
        var after = await node.ObserveAsync(selection, false, cancellation);
        await observation.RequireInstanceAsync(process, cancellation);
        if (!CompactWritePostcondition.TryCheck(after.Nodes, selector, step.Text!, out var verified))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
        }

        return target;
    }

    private JsonObject Format(JsonObject result, CompactResultOptions options, IReadOnlySet<string> visible, int process,
        IReadOnlyDictionary<string, CompactOutputCheck>? checks)
    {
        var formatted = new CompactResultFormatter(state.Results).Format(state.Authority, result, options, checks);
        if (options.Mode != CompactResultMode.Legacy && !formatted.ContainsKey("ui"))
        {
            state.Projection.HideUnseen(visible, process);
        }

        return formatted;
    }

    // Throws CompactNodeException when previously verified, still-present field contents changed before a later action.
    private static void RequirePriorWrites(IReadOnlyList<CompactDemonstrationStep> steps, IReadOnlyList<CompactTreeNode> nodes)
    {
        var writes = steps.Where(step => step.Action == CompactRecipeAction.Write)
            .GroupBy(step => step.Reference).Select(group => group.Last());
        foreach (var step in writes)
        {
            var selector = SelectorOf(step.Target!);
            if (!CompactCapturedOutputs.TryRead(nodes, selector, CompactOutputField.Value, out var current))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(current.Error!.Value));
            }
            if (current.Value!.Exists && current.Value.Value != step.Text)
            {
                throw new CompactNodeException("A previously written field changed before the next action.");
            }
        }
    }

    // Throws CompactNodeException for a target whose owning window was never attributed to a kind, because such a selector would
    // not say which window it means.
    internal static CompactStoredSelector SelectorOf(CompactSemanticTarget target)
    {
        if (target.WindowKind is not CompactWindowKind kind)
        {
            throw new CompactNodeException("The reference's owning window is unverified.");
        }

        return new(target.Role, target.Name, target.AutoId, kind, target.ContainerPath);
    }

    // Throws CompactNodeException for a failed full output check before summary/value/debug formatting.
    private static IReadOnlyDictionary<string, CompactOutputCheck> AddOutputs(JsonObject result,
        IReadOnlyList<CompactDemonstrationStep> steps, IReadOnlyList<CompactTreeNode> nodes, CompactResultMode mode)
    {
        var checks = new Dictionary<string, CompactOutputCheck>();
        if (mode == CompactResultMode.Legacy)
        {
            return checks;
        }
        var outputs = new JsonObject();
        foreach (var item in steps.Select((step, index) => new CompactIndexedDemonstration(index, step))
            .Where(item => item.Step.Action == CompactRecipeAction.Write))
        {
            if (steps.Skip(item.Index + 1).Any(step => step.Action == CompactRecipeAction.Write && step.Reference == item.Step.Reference))
            {
                continue;
            }
            var selector = SelectorOf(item.Step.Target!);
            if (!CompactWritePostcondition.TryCheck(nodes, selector, item.Step.Text!, out var verified))
            {
                throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
            }
            string name = "step_" + (item.Index + 1);
            outputs[name] = verified.Value;
            checks[name] = new(true, true);
        }
        result["outputs"] = outputs;

        return checks;
    }

    private static JsonObject ViewArguments(JsonObject arguments)
    {
        var view = new JsonObject();
        foreach (string name in new[] { "pid", "window", "root", "contains", "offscreen", "full", "limit", "max_chars" })
        {
            if (arguments.ContainsKey(name))
            {
                view[name] = arguments[name]?.DeepClone();
            }
        }

        return view;
    }

    // A failure after the last step names its phase rather than a failing step, because no step index exists for it.
    private static JsonObject Failure(string error, int completed, bool mayHaveChangedUi, bool afterAllSteps)
    {
        var failure = new JsonObject
        {
            ["success"] = false,
            ["verified"] = false,
            ["completed_steps"] = completed,
            ["failed_step_may_have_changed_ui"] = mayHaveChangedUi,
            ["error"] = error,
            ["retry"] = "Do not automatically retry mutations or replay completed steps; observe current state."
        };
        if (afterAllSteps)
        {
            failure["phase"] = "after_steps";
        }
        else
        {
            failure["failed_step"] = completed;
        }

        return failure;
    }

    // A refusal without closing punctuation gets a period, so the separate-key hint starts its own sentence.
    private static string SentenceEnd(string message)
    {
        return message.Length == 0 || ".!?".Contains(message[^1]) ? string.Empty : ".";
    }
}

// Live batch indexing keeps output names and full-value verification bound to the original requested step order.
internal sealed record CompactIndexedDemonstration(int Index, CompactDemonstrationStep Step);

// Use case: one ui_act batch reports how far it got. A step counts as finished only after its verification, and a failing step
// may have changed the UI only when its own native mutation calls may have reached the application.
internal sealed class CompactActProgress(int stepCount, int possibleDispatchesAtStart)
{
    private readonly int _stepCount = stepCount;
    private readonly int _possibleDispatchesAtStart = possibleDispatchesAtStart;
    private int _possibleDispatchesBeforeStep = possibleDispatchesAtStart;

    public int Completed
    {
        get; private set;
    }

    // Every step has finished, so a failure now belongs to the reply after the steps rather than to a step.
    public bool AfterAllSteps => Completed == _stepCount;

    // Records the mutation count when a step starts, so a failure of that step sees only the native calls it made.
    public void StartStep(int possibleDispatches)
    {
        _possibleDispatchesBeforeStep = possibleDispatches;
    }

    public void FinishStep()
    {
        Completed++;
    }

    // A failing step is compared with its own start; after the last step, every native call of the batch counts.
    public bool MayHaveChangedUi(int possibleDispatches)
    {
        int baseline = Completed < _stepCount ? _possibleDispatchesBeforeStep : _possibleDispatchesAtStart;

        return possibleDispatches > baseline;
    }
}
