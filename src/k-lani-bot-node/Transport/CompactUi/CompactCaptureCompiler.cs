namespace KLaniBot.Transport.CompactUi;

// Capture compilation parameterizes a successful demonstration and checks its observed goal effects without sending more input.
public static class CompactCaptureCompiler
{
    public static bool TryCompile(CompactDemonstration demonstration, CompactCaptureRequest request,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, out CompactCaptureAttempt<CompactFunctionDraft> attempt)
    {
        if (!TrySteps(demonstration, request, out var steps))
        {
            attempt = new(null, steps.Error);
            return false;
        }
        var outputs = new List<CompactRecipeOutput>();
        foreach (CompactCaptureOutput output in request.Outputs)
        {
            if (!TryOutput(demonstration, output, references, steps.Value!, out var compiled))
            {
                attempt = new(null, compiled.Error);
                return false;
            }
            outputs.Add(compiled.Value!);
        }
        if (steps.Value!.Count is < 1 or > CompactCaptureCodec.MaximumSteps)
        {
            attempt = new(null, CompactCaptureError.InvalidDefinition);
            return false;
        }
        attempt = new(new(request.Name, request.Topic, steps.Value, outputs, CompactCaptureValidation.DemonstratedMutation,
            Description: request.Description));

        return true;
    }

    private static bool TrySteps(CompactDemonstration demonstration, CompactCaptureRequest request,
        out CompactCaptureAttempt<IReadOnlyList<CompactRecipeStep>> attempt)
    {
        var steps = new List<CompactRecipeStep>();
        int parameter = 0;
        foreach (CompactDemonstrationStep step in demonstration.Steps)
        {
            if (!Enum.IsDefined(step.Route))
            {
                attempt = new(null, CompactCaptureError.InvalidDefinition);
                return false;
            }
            if (step.Action is not CompactRecipeAction.Write and not CompactRecipeAction.Click)
            {
                attempt = new(null, CompactCaptureError.UnsupportedSequence);
                return false;
            }
            if (!CompactCapturedSelectors.TryCreate(step.Target, demonstration.ProcessId, out var selector))
            {
                attempt = new(null, selector.Error);
                return false;
            }
            if (step.Action == CompactRecipeAction.Write && parameter >= request.Parameters.Count)
            {
                attempt = new(null, CompactCaptureError.InvalidParameters);
                return false;
            }
            string? name = step.Action == CompactRecipeAction.Write ? request.Parameters[parameter++] : null;
            bool enabled = EnabledBeforeFirstAction(steps, step.Action, demonstration, selector.Value!);
            steps.Add(new(step.Action, selector.Value!, step.Route, name, enabled));
        }
        attempt = parameter == request.Parameters.Count ? new(steps) : new(null, CompactCaptureError.InvalidParameters);

        return attempt.Error == null;
    }

    // The stored-function codec refuses a false flag unless an earlier non-read step exists, so the compiler records one only then.
    // Otherwise the first observation counts only when it identifies the target uniquely in the demonstrated window, which is the
    // only window the demonstration observed.
    private static bool EnabledBeforeFirstAction(IReadOnlyList<CompactRecipeStep> earlier, CompactRecipeAction action,
        CompactDemonstration demonstration, CompactStoredSelector selector)
    {
        if (action == CompactRecipeAction.Read || !earlier.Any(step => step.Action != CompactRecipeAction.Read))
        {
            return true;
        }
        if (!CompactTargetResolver.TryFindUnique(demonstration.Before, selector.Target(demonstration.ProcessId), true, out var found))
        {
            return true;
        }

        return found.Value!.Enabled;
    }

    private static bool TryOutput(CompactDemonstration demonstration, CompactCaptureOutput output,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, IReadOnlyList<CompactRecipeStep> steps,
        out CompactCaptureAttempt<CompactRecipeOutput> attempt)
    {
        attempt = new(null, CompactCaptureError.UnknownReference);
        if (!references.TryGetValue(output.Reference, out CompactSemanticTarget? target))
        {
            return false;
        }
        if (!CompactCapturedSelectors.TryCreate(target, demonstration.ProcessId, out var captured))
        {
            attempt = new(null, captured.Error);
            return false;
        }
        CompactStoredSelector selector = captured.Value!;
        if (output.NamePrefix != null)
        {
            if (!target.Name.StartsWith(output.NamePrefix, StringComparison.Ordinal))
            {
                attempt = new(null, CompactCaptureError.PrefixMismatch);
                return false;
            }
            selector = selector with
            {
                Name = null,
                NamePrefix = output.NamePrefix
            };
        }

        return TryEffect(demonstration, output, selector, steps, out attempt);
    }

    private static bool TryEffect(CompactDemonstration demonstration, CompactCaptureOutput output, CompactStoredSelector selector,
        IReadOnlyList<CompactRecipeStep> steps, out CompactCaptureAttempt<CompactRecipeOutput> attempt)
    {
        if (!CompactCapturedOutputs.TryRequired(demonstration.After, selector, output.Field, out var after))
        {
            attempt = new(null, after.Error);
            return false;
        }
        if (!CompactCapturedOutputs.TryRead(demonstration.Before, selector, output.Field, out var before))
        {
            attempt = new(null, before.Error);
            return false;
        }
        if (after.Value!.Value == before.Value!.Value)
        {
            attempt = new(null, CompactCaptureError.OutputUnchanged);
            return false;
        }
        CompactRecipeStep? write = steps.FirstOrDefault(step => step.Action == CompactRecipeAction.Write
            && CompactJsonText.Serialize(CompactDiscoveryWire.Selector(step.Selector))
            == CompactJsonText.Serialize(CompactDiscoveryWire.Selector(selector)));
        string? parameter = write != null && output.Field == CompactOutputField.Value ? write.Parameter : null;
        if (parameter != null && after.Value.Value != demonstration.Steps[steps.ToList().IndexOf(write!)].Text)
        {
            attempt = new(null, CompactCaptureError.WrittenValueMismatch);
            return false;
        }
        CompactOutputCheckKind check = parameter != null ? CompactOutputCheckKind.EqualsParameter
            : !before.Value.Exists && selector.NamePrefix == null ? CompactOutputCheckKind.Exists : CompactOutputCheckKind.Changed;
        attempt = new(new(output.Name, selector, output.Field, check, parameter));

        return true;
    }

    public static bool TryRead(string name, CompactCartridgeTopic topic, string output, int processId,
        CompactSemanticTarget target, CompactOutputField field, IReadOnlyList<CompactTreeNode> fresh,
        out CompactCaptureAttempt<CompactFunctionDraft> attempt)
    {
        if (!CompactDiscoveryCodec.ValidName(name) || !CompactDiscoveryCodec.ValidName(output)
            || topic.Category is not CompactTopicCategory.Use and not CompactTopicCategory.Info || processId < 1)
        {
            attempt = new(null, CompactCaptureError.InvalidRead);
            return false;
        }
        if (!CompactCapturedSelectors.TryCreate(target, processId, out var selector))
        {
            attempt = new(null, selector.Error);
            return false;
        }
        if (!CompactCapturedOutputs.TryRequired(fresh, selector.Value!, field, out var source))
        {
            attempt = new(null, source.Error);
            return false;
        }
        if (!CompactCapturedText.HasContent(source.Value!.Value))
        {
            attempt = new(null, CompactCaptureError.EmptyRead);
            return false;
        }
        attempt = new(new(name, topic, [new(CompactRecipeAction.Read, selector.Value!, CompactRecipeRoute.NativeRead)],
            [new(output, selector.Value!, field, CompactOutputCheckKind.NotEmpty)], CompactCaptureValidation.ObservedRead));

        return true;
    }
}
