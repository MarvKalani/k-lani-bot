using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Recipe inputs call semantic/native text interfaces first; an explicitly recorded keyboard route retains its reason and never
// uses coordinates.
public sealed class CompactRecipeWriter(CompactNodeAccess node)
{
    public const int KeyboardMutationCount = 4;

    public static int ExpectedOperations(CompactRecipeStep step)
    {
        return step.Action switch
        {
            CompactRecipeAction.Read => 0,
            CompactRecipeAction.Write => step.Route == CompactRecipeRoute.FocusKeyboard ? KeyboardMutationCount : 1,
            CompactRecipeAction.Click or CompactRecipeAction.Select => 1,
            CompactRecipeAction.Key => 0,
            _ => throw new UnreachableException()
        };
    }

    // Throws CompactNodeException for refused/unknown input or unqualified routes; ArgumentException for malformed recorded parameters.
    public async Task PerformAsync(CompactRecipeStep step, CompactStoredSelector selector, CompactNativeObservation observation,
        CompactFunctionPreflight prepared, IReadOnlyDictionary<string, string> parameters, CompactWorkflowCheckpoint? checkpoint,
        JsonObject? decision, CancellationToken cancellation)
    {
        await PerformDirectAsync(step, selector, observation, prepared.Instance, parameters, checkpoint, decision, cancellation, null);
    }

    // Throws CompactNodeException for refused/unknown input or refused targets; ArgumentException for malformed literal input.
    // A postcondition rides on the native request so the node waits for it; the multi-request keyboard route carries none.
    public async Task PerformDirectAsync(CompactRecipeStep step, CompactStoredSelector selector, CompactNativeObservation observation,
        CompactNativeWindow instance, IReadOnlyDictionary<string, string> parameters, CompactWorkflowCheckpoint? checkpoint,
        JsonObject? decision, CancellationToken cancellation, JsonObject? postcondition = null)
    {
        if (step.Action == CompactRecipeAction.Read)
        {
            return;
        }
        JsonObject arguments = CompactRecipeTarget.Require(observation, selector, instance, step.Action);
        if (step.Action == CompactRecipeAction.Write && step.Route == CompactRecipeRoute.FocusKeyboard)
        {
            CompactRecipeTarget.RequireNamedForKeyboard(selector);
        }
        if (decision != null)
        {
            arguments["decision"] = decision.DeepClone();
        }
        if (postcondition != null && step.Route != CompactRecipeRoute.FocusKeyboard)
        {
            arguments["postcondition"] = postcondition.DeepClone();
        }
        switch (step.Action)
        {
            case CompactRecipeAction.Read:
                break;
            case CompactRecipeAction.Click:
                await SendAsync(Tool.ClickElement, arguments, checkpoint, cancellation);
                break;
            case CompactRecipeAction.Write:
                await WriteAsync(step, arguments, observation, parameters[step.Parameter!], checkpoint, cancellation);
                break;
            case CompactRecipeAction.Select:
                CompactRecipeTarget.Select(arguments, observation, parameters[step.Parameter!]);
                await SendAsync(Tool.ClickElement, arguments, checkpoint, cancellation);
                break;
            case CompactRecipeAction.Key:
                throw new CompactNodeException("Standalone key sequences require an explicit focus recipe.");
            default:
                throw new UnreachableException();
        }
    }

    // Throws CompactNodeException for unconfirmed input. Recorded keyboard routes retain
    // the native-interface limitation proved during their qualified demonstration.
    private async Task WriteAsync(CompactRecipeStep step, JsonObject target, CompactNativeObservation observation, string text,
        CompactWorkflowCheckpoint? checkpoint, CancellationToken cancellation)
    {
        var arguments = target.DeepClone().AsObject();
        arguments["text"] = text;
        arguments["clear"] = true;
        if (observation.Metadata.TryGetValue(target["id"]!.GetValue<int>(), out var field))
        {
            if (field.Visible == false || field.Enabled == false || field.ReadOnly == true)
            {
                throw new CompactNodeException("Current native field is not writable.");
            }
            if (field.Support == UiFormSupport.Available && field.Context != null)
            {
                arguments["field_context"] = field.Context;
            }
        }
        if (step.Route == CompactRecipeRoute.FocusKeyboard)
        {
            await KeyboardAsync(target, text, checkpoint, cancellation);
            return;
        }

        await SendAsync(Tool.TypeText, arguments, checkpoint, cancellation);
    }

    // Throws CompactNodeException if the recorded semantic-focus/keyboard sequence is refused or becomes uncertain.
    private async Task KeyboardAsync(JsonObject target, string text, CompactWorkflowCheckpoint? checkpoint, CancellationToken cancellation)
    {
        await SendAsync(Tool.ClickElement, target, checkpoint, cancellation);
        await SendAsync(Tool.SetClipboard, new JsonObject { ["text"] = text }, checkpoint, cancellation);
        await SendAsync(Tool.Hotkey, new JsonObject { ["keys"] = new JsonArray("ctrl", "a") }, checkpoint, cancellation);
        await SendAsync(Tool.Hotkey, new JsonObject { ["keys"] = new JsonArray("ctrl", "v") }, checkpoint, cancellation);
    }

    // Throws CompactNodeException for native refusal or a missing/unknown outcome, even if a success flag was reported.
    private async Task SendAsync(Tool tool, JsonObject arguments, CompactWorkflowCheckpoint? checkpoint, CancellationToken cancellation)
    {
        RequireMutation(await node.MutateAsync(tool, arguments, checkpoint, cancellation));
    }

    // Throws CompactNodeException for any mutation that the native ledger outcome does not establish as dispatched or completed.
    private static void RequireMutation(JsonObject response)
    {
        CompactNodeResponse.Require(response);
        var result = CompactNodeResponse.Read(response);
        if (!result.Outcome.ReportsSuccess())
        {
            throw new CompactNodeException("Native mutation outcome remains unconfirmed.", result);
        }
    }
}
