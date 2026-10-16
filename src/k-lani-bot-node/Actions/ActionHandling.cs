using System.Text.Json.Nodes;
using KLaniBot.Actions.Handlers;
using KLaniBot.Backup;
using KLaniBot.Guard;
using KLaniBot.Inventory;
using KLaniBot.Platforms;
using KLaniBot.Visibility;

namespace KLaniBot.Actions;

// What one handler gets to see of a request: who asks, which action with which arguments, and a way to
// tell the dispatcher that the operating system may already have been changed.
internal sealed class ActionCall
{
    public ActionCall(RequestContext caller, Tool tool, JsonObject arguments)
    {
        Caller = caller;
        Tool = tool;
        Arguments = arguments;
    }

    public RequestContext Caller
    {
        get;
    }
    public Tool Tool
    {
        get;
    }
    public JsonObject Arguments
    {
        get;
    }

    // True only when the request carries a decision that the node applies to this call. A decision the node found no dialog for
    // is not recorded here, so a dialog that opens before the input is refused as it would be without any decision.
    public bool DecisionRecorded
    {
        get; init;
    }

    // From this point a failure leaves the outcome "unknown" instead of "not dispatched". Handlers call
    // it just before they hand something to the operating system.
    public bool BackendMayHaveMutated
    {
        get; private set;
    }

    public void MarkBackendMayHaveMutated()
    {
        BackendMayHaveMutated = true;
    }
}

// The result of a handler is serialized at the boundary. Its field names are what clients read.
internal delegate object ActionHandler(ActionCall call);

// The collaborators that every group of handlers needs. They live as long as the dispatcher.
internal sealed class HandlerServices
{
    public required IUiService Ui
    {
        get; init;
    }
    public required NodePolicy Policy
    {
        get; init;
    }
    public required AccessVisibility? Visibility
    {
        get; init;
    }
    public required DesktopObservationState Desktop
    {
        get; init;
    }
    public required FileSystemModule Files
    {
        get; init;
    }
    public required TerminalModule Terminal
    {
        get; init;
    }
    public required InventoryPages Inventory
    {
        get; init;
    }
    public required MacroEngine Macros
    {
        get; init;
    }
    public required UiStateAtlas Atlas
    {
        get; init;
    }
    public required bool ClassifierEnabled
    {
        get; init;
    }
    public required NodeBackup Backup
    {
        get; init;
    }
    // The programs' own MCP servers started for agent sessions; one per session and program.
    public required ApplicationMcp.ApplicationMcpHost ApplicationMcps
    {
        get; init;
    }

    // A macro runs its steps as ordinary actions of the same caller, so every step passes the same checks.
    public required Func<JsonObject, RequestContext, JsonObject> RunNestedAction
    {
        get; init;
    }

    public WindowInteractionMode WindowInteraction => Visibility?.State.WindowInteraction ?? Policy.Visibility.WindowInteraction;
    public ModalDialogMode ModalDialogs => Visibility?.State.ModalDialogs ?? Policy.Visibility.ModalDialogs;

    public bool UsesGuardedInput(RequestContext caller)
    {
        return Policy.Get(caller.PrincipalId).UiGuard.Mode == UiGuardMode.Guarded;
    }

    // Under the guard, and in background mode, text and clicks go to the element itself and not through
    // the shared keyboard and mouse.
    public bool UsesSemanticInput(RequestContext caller)
    {
        return UsesGuardedInput(caller) || WindowInteraction == WindowInteractionMode.BackgroundSemantic;
    }

    public void RequireApproval(RequestContext caller)
    {
        Visibility?.RequireApproval(caller);
    }
}
