using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_apply changes one window read with ui_observe as state; every request is checked before any input is sent,
// then the checked steps run in order through CompactGatewayApplySteps.
internal sealed class CompactGatewayApply
{
    private const int MaximumSteps = 12;
    private const string WaitField = "wait";
    private const string WaitValueField = "value";
    private const string WaitTimeoutField = "timeout_ms";
    private const int RequiredWaitFieldCount = 2;
    private const int NoTimeoutMilliseconds = 0;
    private const int DefaultWaitTimeoutMilliseconds = 5000;
    private const int MinimumWaitTimeoutMilliseconds = 1;
    private const int MaximumWaitTimeoutMilliseconds = 30000;

    // The only spellings of a wait condition; any other until word makes the step malformed.
    private static readonly IReadOnlyDictionary<string, CompactApplyCondition> ConditionWords =
        new Dictionary<string, CompactApplyCondition>
        {
            ["exists"] = CompactApplyCondition.Exists,
            ["visible"] = CompactApplyCondition.Visible,
            ["hidden"] = CompactApplyCondition.Hidden,
            ["absent"] = CompactApplyCondition.Absent,
            ["enabled"] = CompactApplyCondition.Enabled,
            ["disabled"] = CompactApplyCondition.Disabled,
            ["value"] = CompactApplyCondition.Value
        };

    private readonly CompactGatewayObservation _observation;
    private readonly CompactGatewayState _state;
    private readonly CompactNodeAccess _node;

    public CompactGatewayApply(CompactGatewayObservation observation, CompactGatewayState state, CompactNodeAccess node)
    {
        _observation = observation;
        _state = state;
        _node = node;
    }

    // Throws CompactNodeException when the window tree has no window row or cannot be bound; malformed requests get typed replies.
    public async Task<JsonObject> RunAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (!TryReadSteps(arguments, out var steps))
        {
            return InvalidStepsReply();
        }

        var selection = _state.SelectWindow(arguments);
        var reading = await _observation.ReadTreeAsync(selection.ProcessId, selection, cancellation);
        var current = BuildCurrentState(reading);
        var bindings = BindMembers(reading);
        var refusal = RefusalBeforeInput(arguments, steps, current, bindings);
        if (refusal != null)
        {
            return refusal;
        }

        var progress = new CompactActProgress(steps.Count, _node.PossibleDispatches);
        var decision = arguments["decision"] as JsonObject;
        var runner = new CompactGatewayApplySteps(_observation, _state, _node, selection, decision, progress, reading);

        return await runner.RunAsync(steps, cancellation);
    }

    // Parses every step before anything else; one malformed step refuses the whole request.
    private static bool TryReadSteps(JsonObject arguments, out IReadOnlyList<CompactApplyStep> steps)
    {
        steps = Array.Empty<CompactApplyStep>();
        if (arguments["steps"] is not JsonArray items || items.Count == 0 || items.Count > MaximumSteps)
        {
            return false;
        }

        var parsed = new List<CompactApplyStep>();
        foreach (var item in items)
        {
            if (!TryParseStep(item, out var step))
            {
                return false;
            }

            parsed.Add(step!);
        }
        steps = parsed;

        return true;
    }

    // Accepts exactly {set, value}, {call} or the wait form; any other shape is malformed.
    private static bool TryParseStep(JsonNode? node, out CompactApplyStep? step)
    {
        step = null;
        if (node is not JsonObject fields)
        {
            return false;
        }
        if (fields.Count == 2 && IsText(fields["set"]) && IsText(fields["value"]))
        {
            step = new CompactApplyStep(CompactApplyKind.Set, StringValue(fields["set"]), StringValue(fields["value"]), null,
                NoTimeoutMilliseconds);

            return true;
        }
        if (fields.Count == 1 && IsText(fields["call"]))
        {
            step = new CompactApplyStep(CompactApplyKind.Call, StringValue(fields["call"]), null, null, NoTimeoutMilliseconds);

            return true;
        }

        return TryParseWait(fields, out step);
    }

    // A wait names a member path and one condition; value is required exactly for the value condition, timeout_ms is optional.
    private static bool TryParseWait(JsonObject fields, out CompactApplyStep? step)
    {
        step = null;
        if (!IsText(fields[WaitField]) || !TryReadCondition(fields["until"], out var condition))
        {
            return false;
        }
        if (!HasExactWaitFields(fields, condition) || !TryReadTimeout(fields, out var timeoutMilliseconds))
        {
            return false;
        }

        var awaitedValue = condition == CompactApplyCondition.Value ? StringValue(fields[WaitValueField]) : null;
        step = new CompactApplyStep(CompactApplyKind.Wait, StringValue(fields[WaitField]), awaitedValue, condition,
            timeoutMilliseconds);

        return true;
    }

    // The value field belongs to the value condition only, and every key must be one that the wait form names.
    private static bool HasExactWaitFields(JsonObject fields, CompactApplyCondition condition)
    {
        var hasValue = fields.ContainsKey(WaitValueField);
        var valueFits = condition == CompactApplyCondition.Value ? IsText(fields[WaitValueField]) : !hasValue;
        var expectedCount = RequiredWaitFieldCount + (hasValue ? 1 : 0) + (fields.ContainsKey(WaitTimeoutField) ? 1 : 0);

        return valueFits && fields.Count == expectedCount;
    }

    // The until word must be one of the spellings in ConditionWords; any other word is malformed.
    private static bool TryReadCondition(JsonNode? node, out CompactApplyCondition condition)
    {
        condition = CompactApplyCondition.Exists;

        return IsText(node) && ConditionWords.TryGetValue(StringValue(node), out condition);
    }

    // timeout_ms is optional; when present it must be a whole number of milliseconds within the allowed range.
    private static bool TryReadTimeout(JsonObject fields, out int timeoutMilliseconds)
    {
        timeoutMilliseconds = DefaultWaitTimeoutMilliseconds;
        if (!fields.ContainsKey(WaitTimeoutField))
        {
            return true;
        }
        if (fields[WaitTimeoutField] is not JsonValue number || !number.TryGetValue<int>(out var requested))
        {
            return false;
        }

        timeoutMilliseconds = requested;

        return requested >= MinimumWaitTimeoutMilliseconds && requested <= MaximumWaitTimeoutMilliseconds;
    }

    private static bool IsText(JsonNode? node)
    {
        return node?.GetValueKind() == JsonValueKind.String;
    }

    internal static string StringValue(JsonNode? node)
    {
        return node!.GetValue<string>();
    }

    // Throws CompactNodeException when the window tree has no window row, as ui_observe as state does.
    internal static JsonObject BuildCurrentState(CompactWindowTreeReading reading)
    {
        var attempt = CompactWindowState.Build(reading.Tree.Nodes, RequestFor(reading));
        if (attempt.Value == null)
        {
            throw new CompactNodeException("The window tree has no window row.");
        }

        return attempt.Value;
    }

    // The request that ui_observe as state builds for this window, so a diff and a state read name the same window.
    internal static CompactWindowStateRequest RequestFor(CompactWindowTreeReading reading)
    {
        return new CompactWindowStateRequest(reading.Window.Handle, reading.Window.Kind,
            CompactWindowStateLimits.DefaultMaxChars, null);
    }

    // Throws CompactNodeException when the members of the window cannot be bound.
    internal static IReadOnlyDictionary<string, CompactMemberBinding> BindMembers(CompactWindowTreeReading reading)
    {
        var attempt = CompactWindowState.Bind(reading.Tree.Nodes);
        if (attempt.Value == null)
        {
            throw new CompactNodeException("The window members could not be bound.");
        }

        return attempt.Value;
    }

    // The shape check comes first because a positional name is unsafe under any other state.
    private static JsonObject? RefusalBeforeInput(JsonObject arguments, IReadOnlyList<CompactApplyStep> steps,
        JsonObject current, IReadOnlyDictionary<string, CompactMemberBinding> bindings)
    {
        var shapeRefusal = ShapeRefusal(arguments, steps, current, bindings);

        return shapeRefusal ?? FirstStepRefusal(steps[0], current, bindings);
    }

    // Unnamed fields are named by position, so a step on one must carry the shape of the state it was read from.
    private static JsonObject? ShapeRefusal(JsonObject arguments, IReadOnlyList<CompactApplyStep> steps,
        JsonObject current, IReadOnlyDictionary<string, CompactMemberBinding> bindings)
    {
        if (!steps.Any(step => IsFallbackPath(step.Path, bindings)))
        {
            return null;
        }
        if (arguments["shape"] == null)
        {
            return ShapeReply("shape_required", current);
        }
        if (!IsText(arguments["shape"]) || StringValue(arguments["shape"]) != StringValue(current["shape"]))
        {
            return ShapeReply("shape_changed", current);
        }

        return null;
    }

    private static bool IsFallbackPath(string path, IReadOnlyDictionary<string, CompactMemberBinding> bindings)
    {
        return bindings.TryGetValue(path, out var binding) && binding.Fallback;
    }

    // Only the first step is checked before input; a later step may name a member that an earlier step makes visible,
    // so each later step is checked against a fresh read when it runs.
    private static JsonObject? FirstStepRefusal(CompactApplyStep first, JsonObject current,
        IReadOnlyDictionary<string, CompactMemberBinding> bindings)
    {
        // A wait may name a member that does not exist yet, so it is checked when it runs.
        if (first.Kind == CompactApplyKind.Wait)
        {
            return null;
        }

        var refusal = StepRefusal(first, bindings);
        if (refusal != null)
        {
            refusal["failed_step"] = 1;
            refusal["declaration"] = current["declaration"]?.DeepClone();
        }

        return refusal;
    }

    // Names the reason a step cannot act on the bound member, or returns null when the step may act.
    internal static JsonObject? StepRefusal(CompactApplyStep step, IReadOnlyDictionary<string, CompactMemberBinding> bindings)
    {
        if (!bindings.TryGetValue(step.Path, out var binding))
        {
            return FailedStep("unknown_path", step);
        }
        if (step.Kind == CompactApplyKind.Set && !IsSettable(binding.Member))
        {
            var settingReply = FailedStep("not_settable", step);
            settingReply["hint"] = CompactViewNames.ApplySetHint;

            return settingReply;
        }
        if (step.Kind == CompactApplyKind.Call && !IsCallable(binding.Member))
        {
            var callingReply = FailedStep("not_callable", step);
            callingReply["hint"] = CompactViewNames.ApplyCallHint;

            return callingReply;
        }

        return null;
    }

    private static JsonObject FailedStep(string error, CompactApplyStep step)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = error,
            ["path"] = step.Path
        };
    }

    private static bool IsSettable(CompactBoundMember member)
    {
        return member is CompactBoundMember.Text or CompactBoundMember.Secret or CompactBoundMember.Number;
    }

    private static bool IsCallable(CompactBoundMember member)
    {
        return member is CompactBoundMember.Method or CompactBoundMember.Toggle;
    }

    private static JsonObject InvalidStepsReply()
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = "invalid_steps",
            ["hint"] = CompactViewNames.ApplyStepsHint
        };
    }

    private static JsonObject ShapeReply(string error, JsonObject current)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["error"] = error,
            ["hint"] = CompactViewNames.ApplyShapeHint,
            ["shape"] = current["shape"]?.DeepClone(),
            ["declaration"] = current["declaration"]?.DeepClone()
        };
    }
}

// Use case: the three forms of a ui_apply step; set writes a text, number or password property, call invokes a method,
// and wait pauses until a member reaches the awaited state.
internal enum CompactApplyKind
{
    Set,
    Call,
    Wait
}

// Use case: the state that a wait step awaits, named by the until word of the request.
internal enum CompactApplyCondition
{
    Exists,
    Visible,
    Hidden,
    Absent,
    Enabled,
    Disabled,
    Value
}

// Use case: one checked step of a ui_apply request; Value is the text of a set step or the awaited text of a wait step,
// and Until and TimeoutMilliseconds are used only by a wait step.
internal sealed record CompactApplyStep(CompactApplyKind Kind, string Path, string? Value, CompactApplyCondition? Until,
    int TimeoutMilliseconds);

// Use case: a window tree read for one ui_apply request, kept with the window it came from.
internal sealed record CompactWindowTreeReading(CompactNativeWindow Window, CompactNativeObservation Tree);
