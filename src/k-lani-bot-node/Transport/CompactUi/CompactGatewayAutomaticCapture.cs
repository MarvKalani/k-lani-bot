using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Configured first-use capture verifies fresh application output and actual node contracts without sending additional input.
public sealed class CompactGatewayAutomaticCapture(CompactNodeAccess node, CompactGatewayObservation observation,
    CompactGatewayState state, CompactGatewayPrograms programs, Func<string> protocol)
{
    private static readonly CompactCartridgeTopic DefaultTopic = new(CompactTopicCategory.Use, "UI");

    public async Task<JsonObject> CaptureAsync(bool compact, CancellationToken cancellation)
    {
        try
        {
            return await CaptureCurrentAsync(compact, cancellation);
        }
        catch (Exception exception)
        {
            return CompactAutomaticWire.Reply([new("capture", CompactAutomaticStatus.StorageFailed,
                Reason: exception.Message)], compact);
        }
    }

    // Throws CompactNodeException for missing actual scope, identity or compatibility evidence before any installation.
    private async Task<JsonObject> CaptureCurrentAsync(bool compact, CancellationToken cancellation)
    {
        var demonstration = state.Demonstration;
        if (demonstration == null)
        {
            throw new CompactNodeException("A current successful demonstration is required for automatic capture.");
        }
        var windows = await observation.RequireInstanceAsync(demonstration.ProcessId, cancellation);
        var instance = CompactGatewayLearning.RequireInstance(windows, demonstration.WindowHandle);
        var identity = programs.BoundIdentity(instance);
        var capabilities = await node.CapabilitiesAsync(Actions(demonstration), cancellation);
        var after = await node.ObserveAsync(new CompactWindowSelection(demonstration.ProcessId, instance.Handle), false, cancellation);
        windows = await observation.RequireInstanceAsync(demonstration.ProcessId, cancellation);
        instance = CompactGatewayLearning.RequireInstance(windows, demonstration.WindowHandle);
        if (programs.BoundIdentity(instance) != identity)
        {
            throw new CompactNodeException("Application identity changed before automatic installation.");
        }
        var references = References(demonstration.ProcessId, instance);
        var current = new CompactObservedCompatibility(capabilities.Version, CompactCompatibilityWire.Wire(protocol()),
            capabilities.Platform, identity, capabilities.Actions, capabilities.Implementation, CompactGatewayPrograms.Implementation());
        var capture = new CompactAutomaticCapture(programs.Writable(identity));
        var results = capture.Capture(demonstration with
        {
            After = after.Nodes
        }, references, current, DefaultTopic, compact);

        return CompactAutomaticWire.Reply(results, compact);
    }

    private IReadOnlyDictionary<string, CompactSemanticTarget> References(int process, CompactNativeWindow instance)
    {
        var references = new Dictionary<string, CompactSemanticTarget>(StringComparer.Ordinal);
        foreach (string reference in state.Projection.ExposedReferences)
        {
            if (state.Projection.TryTarget(reference, process, out var target) && target.Value!.WindowHandle == instance.Handle
                && target.Value.ProcessStarted == instance.Started)
            {
                references.Add(reference, target.Value with
                {
                    WindowKind = instance.Kind
                });
            }
        }

        return references;
    }

    private static IReadOnlyList<Tool> Actions(CompactDemonstration demonstration)
    {
        var actions = new HashSet<Tool> { Tool.GetMachineCapabilities, Tool.ListWindows, Tool.GetUiTree };
        foreach (var step in demonstration.Steps)
        {
            if (step.Action == CompactRecipeAction.Write)
            {
                actions.Add(Tool.TypeText);
            }
            if (step.Action == CompactRecipeAction.Click || step.Route == CompactRecipeRoute.FocusKeyboard)
            {
                actions.Add(Tool.ClickElement);
            }
            if (step.Route == CompactRecipeRoute.FocusKeyboard)
            {
                actions.UnionWith([Tool.SetClipboard, Tool.Hotkey]);
            }
        }

        return actions.OrderBy(ToolNames.Of, StringComparer.Ordinal).ToArray();
    }
}
