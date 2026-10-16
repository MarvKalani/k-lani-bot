using KLaniBot.Transport.CompactUi;

// Native capture tests assert privacy and authority invariants beyond the shared reference's compilation checks.
internal static class CaptureCompilerCases
{
    private const string Input = "SYNTHETIC_TRAINING_VALUE_NEVER_IN_RECIPE";

    public static void Run()
    {
        var target = new CompactSemanticTarget(10, "Text", CompactRole.Parse("Edit"), "text", CompactWindowKind.Main, [], 99, "start");
        CompactDemonstration demonstration = Demonstration(target);
        var request = new CompactCaptureRequest("editor.write", new(CompactTopicCategory.Use, "Editor"), ["text"],
            [new("value", "field", CompactOutputField.Value)]);
        var references = new Dictionary<string, CompactSemanticTarget> { ["field"] = target };
        Require(CompactCaptureCompiler.TryCompile(demonstration, request, references, out var compiled), "verified draft");
        string wire = CompactJsonText.Serialize(CompactCaptureWire.Draft(compiled.Value!));
        Require(!wire.Contains(Input) && !wire.Contains("\"id\"") && !wire.Contains("windowHandle")
            && !wire.Contains("processStarted") && !wire.Contains(demonstration.Evidence.ToString()), "no volatile capture data persisted");
        Require(compiled.Value!.Steps[0].Parameter == "text"
            && compiled.Value.Outputs[0].Check == CompactOutputCheckKind.EqualsParameter, "parameterized exact postcondition");
        Authority(demonstration, request, references, target);
        FullOutput(compiled.Value.Outputs, demonstration);
    }

    private static void Authority(CompactDemonstration demonstration, CompactCaptureRequest request,
        Dictionary<string, CompactSemanticTarget> references, CompactSemanticTarget target)
    {
        references["field"] = target with
        {
            ProcessId = 11
        };
        Require(!CompactCaptureCompiler.TryCompile(demonstration, request, references, out var foreign)
            && foreign.Error == CompactCaptureError.ForeignReference, "foreign output reference");
        references["field"] = target with
        {
            UnverifiedWindow = true
        };
        Require(!CompactCaptureCompiler.TryCompile(demonstration, request, references, out var unbound)
            && unbound.Error == CompactCaptureError.InvalidSelector, "unbound window reference");
        references["field"] = target;
        CompactDemonstration wrongRoute = demonstration with
        {
            Steps = [demonstration.Steps[0] with { Route = (CompactRecipeRoute)99 }]
        };
        Require(!CompactCaptureCompiler.TryCompile(wrongRoute, request, references, out var route)
            && route.Error == CompactCaptureError.InvalidDefinition, "unknown observed route");
        CompactDemonstration replaced = demonstration with
        {
            Steps = [demonstration.Steps[0] with { Target = target with { ProcessId = 11 } }]
        };
        Require(!CompactCaptureCompiler.TryCompile(replaced, request, references, out var step)
            && step.Error == CompactCaptureError.ForeignReference, "foreign action binding");
    }

    private static void FullOutput(IReadOnlyList<CompactRecipeOutput> outputs, CompactDemonstration demonstration)
    {
        string text = new('x', CompactCaptureCodec.MaximumTextCharacters);
        CompactTreeNode[] after = demonstration.After.ToArray();
        after[1] = after[1] with
        {
            Value = text
        };
        var parameters = new Dictionary<string, string> { ["text"] = text };
        Require(CompactOutputVerifier.TryVerify(outputs, parameters, demonstration.Before, after, false, out var verified)
            && verified.Value!["value"] == text, "full native value verified before clipping");
        after[1] = after[1] with
        {
            Value = text[..^1] + "y"
        };
        Require(!CompactOutputVerifier.TryVerify(outputs, parameters, demonstration.Before, after, false, out var mismatch)
            && mismatch.Error == CompactCaptureError.PostconditionFailed, "equal visible prefix cannot hide a changed tail");
        CompactStoredSelector selector = outputs[0].Selector;
        after[1] = after[1] with
        {
            Value = "old" + text
        };
        Require(!CompactWritePostcondition.TryCheck(after, selector, text, out var appended)
            && appended.Error == CompactCaptureError.WrittenValueMismatch, "append is not replacement");
        after[1] = after[1] with
        {
            Value = ""
        };
        Require(!CompactWritePostcondition.TryCheck(after, selector, text, out _), "empty is not requested text");
        Require(CompactWritePostcondition.TryCheck(after, selector, "", out _), "explicit clearing is verified exactly too");
    }

    private static CompactDemonstration Demonstration(CompactSemanticTarget target)
    {
        CompactTreeNode[] before = [new(0, 1, CompactRole.Parse("Window"), "Fixture", "", "", true, false),
            new(2, 2, CompactRole.Parse("Edit"), "Text", "text", "old", true, false)];
        CompactTreeNode[] after = [before[0], before[1] with { Id = 77, Value = Input }];

        return new(Guid.NewGuid(), 10, [new(CompactRecipeAction.Write, target, CompactRecipeRoute.NativeText, Input)], before, after);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Capture compiler regression failed: " + name);
        }
    }
}
