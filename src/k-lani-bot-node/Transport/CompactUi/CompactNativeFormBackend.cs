using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Native form operations reuse the conversation's observed lifetimes and the operated node's snapshot/context/option guards.
public sealed class CompactNativeFormBackend(CompactNodeAccess node, CompactGatewayObservation observation) : ICompactFormBackend
{
    // Throws CompactNodeException for replaced scope, incomplete tree or failed actual node observations. Only the form's own window
    // is read, so its tree cannot contain rows of another window.
    public async Task<CompactFormSnapshot> RefreshAsync(int processId, long windowHandle, CancellationToken cancellation)
    {
        await observation.RequireInstanceAsync(processId, cancellation);
        var tree = await node.ObserveAsync(new CompactWindowSelection(processId, windowHandle), true, cancellation);
        var windows = await observation.RequireInstanceAsync(processId, cancellation);

        return new(processId, tree.Snapshot, tree.Nodes, windows, tree.Metadata);
    }

    // Throws CompactNodeException when the original native mutation cannot be dispatched or observed.
    public async Task<CompactFormDispatch> DispatchAsync(CompactFormMutation mutation, CancellationToken cancellation)
    {
        var arguments = Arguments(mutation);
        var response = await node.MutateAsync(mutation.Tool, arguments, null, cancellation);
        var outcome = CompactNodeResponse.Read(response);
        if (!outcome.Success || !outcome.Outcome.ReportsSuccess())
        {
            return new(false, response["error"]?.ToString() ?? response["message"]?.ToString()
                ?? "Native form effect is unconfirmed.");
        }

        return new(true);
    }

    private static JsonObject Arguments(CompactFormMutation mutation)
    {
        var arguments = new JsonObject { ["id"] = mutation.NativeId, ["snapshot_id"] = mutation.SnapshotId };
        if (mutation.OpenOnly)
        {
            arguments["choice_action"] = "open";
            return arguments;
        }
        switch (mutation.Kind)
        {
            case CompactFormValueKind.Text:
                arguments["text"] = mutation.Text;
                arguments["clear"] = true;
                break;
            case CompactFormValueKind.Option:
                arguments["choice_action"] = "select";
                arguments["option_id"] = mutation.OptionId;
                arguments["options_revision"] = mutation.OptionsRevision;
                break;
            default:
                throw new UnreachableException();
        }
        if (mutation.Context != null)
        {
            arguments["field_context"] = mutation.Context;
        }

        return arguments;
    }
}
