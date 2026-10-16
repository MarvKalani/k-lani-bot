using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Native recipe execution uses fresh semantic targets and full value verification through the existing node dispatcher on every step.
// Every window is read on its own, so a tree never mixes rows from two native windows.
public sealed class CompactFunctionExecutor(CompactNodeAccess node, CompactMcpWireVersion wire, CompactContractDigest gatewayImplementation)
{
    // Throws CompactNodeException when compatibility, identity, input schemas or any initial mutation target cannot be established.
    // A named window binds to its own kind; every other step kind must have exactly one current window.
    public async Task<CompactFunctionPreflight> PreflightAsync(CompactLoadedFunction function, CompactProgramIdentity application,
        CompactNativeWindow instance, CompactNativeWindow? named, CancellationToken cancellation)
    {
        await RequireCompatibilityAsync(function, application, cancellation);
        var windows = await node.WindowsAsync(cancellation);
        var current = CompactFunctionBinding.RequireInstance(windows, instance, application);
        var bindings = CompactFunctionBinding.Bind(function.Draft, windows, current, named);
        var trees = await ObserveTreesAsync(function.Draft, bindings, cancellation);
        foreach (CompactRecipeStep step in function.Draft.Steps.Where(step => step.Action != CompactRecipeAction.Read))
        {
            CompactNativeObservation tree = trees[step.Selector.WindowKind];
            if (step.EnabledBeforeFirstAction)
            {
                CompactRecipeTarget.Require(tree, step.Selector, current, step.Action);
            }
            else
            {
                // An earlier recorded step enables this target; ExecuteAsync checks it enabled again before its own input.
                CompactRecipeTarget.RequirePresent(tree, step.Selector, current, step.Action);
            }
        }
        var outputs = CompactRecipeValues.Before(function.Draft, new(trees, bindings));
        CompactFunctionBinding.RequireInstance(await node.WindowsAsync(cancellation), current, application);

        return new(function, application, current, bindings, outputs);
    }

    // Throws CompactNodeException for unconfirmed input or output; never replays a refused/uncertain mutation automatically.
    public async Task<CompactFunctionExecutionResult> ExecuteAsync(CompactFunctionPreflight prepared,
        IReadOnlyDictionary<string, string> parameters, CompactWorkflowCheckpoint? checkpoint, JsonObject? decision,
        CancellationToken cancellation)
    {
        CompactRecipeValues.RequireParameters(prepared.Function.Draft, prepared.Instance.ProcessId, parameters);
        var writer = new CompactRecipeWriter(node);
        for (int index = 0; index < prepared.Function.Draft.Steps.Count; index++)
        {
            if (checkpoint != null && !checkpoint.TryStep(index, out var saved))
            {
                throw new CompactNodeException(CompactWorkflowJobNames.Error(saved.Error!.Value, saved.Detail));
            }
            CompactRecipeStep step = prepared.Function.Draft.Steps[index];
            await RequireCompatibilityAsync(prepared.Function, prepared.Application, cancellation);
            CompactFunctionObservation fresh = await ObserveAsync(prepared, cancellation);
            CompactRecipeValues.RequireFinalWrites(prepared.Function.Draft with
            {
                Steps = prepared.Function.Draft.Steps.Take(index).ToArray()
            }, parameters, fresh, index);
            CompactNativeObservation stepTree = await ObserveWindowAsync(prepared, step.Selector.WindowKind, cancellation);
            await writer.PerformAsync(step, step.Selector, stepTree, prepared, parameters, checkpoint, decision, cancellation);
            if (step.Action == CompactRecipeAction.Write)
            {
                var after = await ObserveAsync(prepared, cancellation);
                if (!CompactWritePostcondition.TryCheck(after.NodesOf(step.Selector.WindowKind), step.Selector,
                    parameters[step.Parameter!], out var verified))
                {
                    throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
                }
            }
        }

        return new(await CurrentOutputsAsync(prepared, parameters, prepared.Function.Draft.Steps.Count, cancellation));
    }

    // Throws CompactNodeException when actual function compatibility cannot be established; absent schemas/digests remain unknown.
    public async Task RequireCompatibilityAsync(CompactLoadedFunction function, CompactProgramIdentity application,
        CancellationToken cancellation)
    {
        var current = await node.CapabilitiesAsync(CompactContractHash.RequiredActions(function.Draft), cancellation);
        var observed = new CompactObservedCompatibility(current.Version, wire, current.Platform, application, current.Actions,
            current.Implementation, gatewayImplementation);
        var verdict = CompactCompatibility.Assess(function.Draft, function.Compatibility, observed);
        if (verdict.Status != CompactCompatibilityStatus.Reusable)
        {
            throw new CompactNodeException("Generated function compatibility changed: " + CompactCompatibilityNames.Reason(verdict.Reason)
                + "; revalidate before input.");
        }
    }

    // Throws CompactNodeException for replaced windows, incomplete trees, mismatching written fields or unverified complete outputs.
    public async Task<IReadOnlyDictionary<string, string>> CurrentOutputsAsync(CompactFunctionPreflight prepared,
        IReadOnlyDictionary<string, string> parameters, int verifiedBeforeStep, CancellationToken cancellation)
    {
        await RequireCompatibilityAsync(prepared.Function, prepared.Application, cancellation);
        var current = await ObserveAsync(prepared, cancellation, verifiedBeforeStep);
        CompactRecipeValues.RequireFinalWrites(prepared.Function.Draft, parameters, current, verifiedBeforeStep);

        return CompactRecipeValues.Verify(prepared, parameters, current);
    }

    // Throws CompactNodeException when the instance or a bound window was replaced around the observation, or a tree is incomplete.
    public async Task<CompactFunctionObservation> ObserveAsync(CompactFunctionPreflight prepared, CancellationToken cancellation,
        int? verifiedBeforeStep = null)
    {
        var windows = await node.WindowsAsync(cancellation);
        CompactFunctionBinding.RequireInstance(windows, prepared.Instance, prepared.Application);
        var retired = verifiedBeforeStep == null ? null
            : CompactRecipeValues.RetiredInputWindows(prepared.Function.Draft, verifiedBeforeStep.Value);
        var bindings = new Dictionary<CompactWindowKind, CompactNativeWindow>(
            CompactFunctionBinding.Refresh(prepared.Windows, windows, retired));
        CompactFunctionBinding.AddOutputs(prepared.Function.Draft, windows, prepared.Instance, null, bindings);
        var trees = await ObserveTreesAsync(prepared.Function.Draft, bindings, cancellation);
        var after = await node.WindowsAsync(cancellation);
        CompactFunctionBinding.RequireInstance(after, prepared.Instance, prepared.Application);
        CompactFunctionBinding.Refresh(bindings, after);

        return new(trees, bindings);
    }

    // Reads the step's own window last: the node accepts a semantic action only against the snapshot issued by its newest tree.
    private async Task<CompactNativeObservation> ObserveWindowAsync(CompactFunctionPreflight prepared, CompactWindowKind kind,
        CancellationToken cancellation)
    {
        var windows = await node.WindowsAsync(cancellation);
        CompactFunctionBinding.RequireInstance(windows, prepared.Instance, prepared.Application);
        var bindings = CompactFunctionBinding.Refresh(prepared.Windows, windows);
        CompactNativeWindow window = bindings[kind];
        var selection = new CompactWindowSelection(window.ProcessId, window.Handle);
        var tree = await node.ObserveAsync(selection, MetadataRequested(prepared.Function.Draft), cancellation);
        CompactFunctionBinding.Refresh(bindings, await node.WindowsAsync(cancellation));

        return tree;
    }

    // One tree per bound window, read for that window alone, so each row is attributed to exactly one native window.
    private async Task<Dictionary<CompactWindowKind, CompactNativeObservation>> ObserveTreesAsync(CompactFunctionDraft function,
        IReadOnlyDictionary<CompactWindowKind, CompactNativeWindow> bindings, CancellationToken cancellation)
    {
        var trees = new Dictionary<CompactWindowKind, CompactNativeObservation>();
        foreach (var binding in bindings)
        {
            var selection = new CompactWindowSelection(binding.Value.ProcessId, binding.Value.Handle);
            trees.Add(binding.Key, await node.ObserveAsync(selection, MetadataRequested(function), cancellation));
        }

        return trees;
    }

    private static bool MetadataRequested(CompactFunctionDraft function)
    {
        return function.Steps.Any(step => step.Action == CompactRecipeAction.Select);
    }
}
