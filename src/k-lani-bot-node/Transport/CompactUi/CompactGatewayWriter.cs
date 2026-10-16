using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// First-use writes qualify keyboard fallback only after an explicit native-interface refusal that proves no input was dispatched.
public sealed class CompactGatewayWriter(CompactNodeAccess node, CompactGatewayObservation observation)
{
    // Throws CompactNodeException for uncertain/refused input or stale native targets; ArgumentException for invalid parameters.
    public async Task<CompactRecipeRoute> PerformAsync(CompactRecipeStep step, CompactStoredSelector selector,
        CompactNativeObservation current, CompactNativeWindow instance, CompactWindowSelection observed,
        IReadOnlyDictionary<string, string> parameters, JsonObject? decision,
        Action<CompactNativeObservation, IReadOnlyList<CompactNativeWindow>> verifyPriorWrites, CancellationToken cancellation,
        JsonObject? postcondition = null)
    {
        var writer = new CompactRecipeWriter(node);
        try
        {
            await writer.PerformDirectAsync(step, selector, current, instance, parameters, null, decision, cancellation, postcondition);
            return step.Route;
        }
        catch (CompactNodeException exception) when (step.Action == CompactRecipeAction.Write && MissingInterface(exception))
        {
            CompactRecipeTarget.RequireNamedForKeyboard(selector);
            var windows = await observation.RequireInstanceAsync(instance.ProcessId, cancellation);
            var refreshed = windows.Single(window => window.Handle == instance.Handle && window.Started == instance.Started);
            current = await node.ObserveAsync(observed, true, cancellation);
            await observation.RequireInstanceAsync(instance.ProcessId, cancellation);
            verifyPriorWrites(current, windows);
            await writer.PerformDirectAsync(step with
            {
                Route = CompactRecipeRoute.FocusKeyboard
            }, selector, current, refreshed, parameters, null, decision, cancellation, postcondition);

            return CompactRecipeRoute.FocusKeyboard;
        }
    }

    private static bool MissingInterface(CompactNodeException exception)
    {
        var result = exception.Result;
        var source = result?.Source;

        return result is { Success: false, Outcome: ActionOutcome.NotDispatched, Code: null }
            && source?["error"] == null && source?["code"] == null && source?["toolError"] == null
            && source?["message"]?.ToString() == "Target has no editable text interface.";
    }
}
