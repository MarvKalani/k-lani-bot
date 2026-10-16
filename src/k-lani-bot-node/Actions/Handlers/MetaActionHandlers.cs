using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Guard;
using KLaniBot.Startup;

namespace KLaniBot.Actions.Handlers;

// Use case: a client asks what this node can do, before or between actions, and a session says that it is
// finished. None of these touch the screen. end_session is here because it hands the desktop back, not
// because it acts on a window.
internal sealed class MetaActionHandlers
{
    private readonly HandlerServices _services;

    public MetaActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.ListAvailableMcps, ListModules);
        add(Tool.CollectInventory, CollectInventory);
        add(Tool.GetMachineCapabilities, DescribeMachine);
        add(Tool.EndSession, EndSession);
        add(Tool.GetActionStatus, RefuseStatusLookup);
    }

    private object ListModules(ActionCall call)
    {
        return new
        {
            success = true,
            modules = _services.Ui.Features,
            allowed_scopes = _services.Policy.Get(call.Caller.PrincipalId).Scopes
        };
    }

    private object CollectInventory(ActionCall call)
    {
        return _services.Inventory.Read(call.Arguments);
    }

    // What the caller is allowed to use here and now: the intersection of its scopes, what the backend
    // supports and what the node was started with. A client plans with this list instead of guessing.
    // Throws ArgumentException for invalid or duplicate requested action schema names.
    private object DescribeMachine(ActionCall call)
    {
        PrincipalPolicy principal = _services.Policy.Get(call.Caller.PrincipalId);
        var description = new
        {
            success = true,
            capabilities = new
            {
                nodeId = _services.Policy.NodeId,
                version = ProductIdentity.DisplayVersion,
                desktopSession = _services.Ui.GetDesktopSession(),
                windowInteraction = _services.WindowInteraction.ToString(),
                modalDialogs = _services.ModalDialogs.ToString(),
                postconditionRequired = _services.Policy.RequirePostcondition,
                dialogDeciders = _services.Policy.DialogDeciders,
                features = _services.Ui.Features,
                formCapabilities = _services.Ui.FormCapabilities,
                activeTools = ActiveToolNames(principal),
                uiGuardMode = principal.UiGuard.Mode.ToString(),
                classifier_veto_enabled = _services.ClassifierEnabled,
                semanticDataTrust = "untrusted",
                executionBoundary = "isolated OS account or sandbox required",
                platform = _services.Ui.Platform
            }
        };

        JsonObject result = AddImplementationFingerprint(description);
        if (call.Arguments.ContainsKey("action_schema_names"))
        {
            var available = ToolContract.Tools.OfType<JsonObject>().Where(tool => IsAvailableTo(principal, tool));
            if (!SelectedActionSchemas.TrySelect(call.Arguments["action_schema_names"], available, out var schemas))
            {
                throw new ArgumentException("Select bounded distinct known action schema names.");
            }
            result["capabilities"]!["actionSchemas"] = schemas;
        }

        return result;
    }

    private static JsonObject AddImplementationFingerprint(object description)
    {
        var result = JsonSerializer.SerializeToNode(description)!.AsObject();
        string? fingerprint = typeof(MetaActionHandlers).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "NativeImplementationSha256")?.Value;
        if (fingerprint?.Length == 64 && fingerprint.All(character => char.IsAsciiHexDigit(character)))
        {
            result["capabilities"]!["nativeImplementationSha256"] = fingerprint.ToLowerInvariant();
        }

        return result;
    }

    private string[] ActiveToolNames(PrincipalPolicy principal)
    {
        return ToolContract.Tools
            .OfType<JsonObject>()
            .Where(tool => IsAvailableTo(principal, tool))
            .Select(tool => tool["name"]!.ToString())
            .ToArray();
    }

    private bool IsAvailableTo(PrincipalPolicy principal, JsonObject tool)
    {
        ToolFeature feature = ToolFeatures.Of(tool);
        bool scopeGranted = principal.Scopes.Contains(ToolScope.Of(tool));
        IReadOnlySet<Tool>? supportedTools = _services.Ui.SupportedTools;
        bool backendSupportsTool = supportedTools == null || supportedTools.Contains(ToolNames.OfContractEntry(tool));
        bool featureAvailable = _services.Ui.Features.Contains(feature) || !ToolFeatures.UsesDesktop(feature);
        return scopeGranted && backendSupportsTool && featureAvailable;
    }

    private object EndSession(ActionCall call)
    {
        string requester = call.Caller.PrincipalId + ":" + call.Caller.SessionId;
        _services.Desktop.ReleaseLease(requester);
        return new
        {
            success = true
        };
    }

    // The dispatcher answers status requests from its ledger before any handler runs. Reaching this
    // handler means that path was bypassed, which must not silently return an empty answer.
    // Throws InvalidOperationException always.
    private object RefuseStatusLookup(ActionCall call)
    {
        throw new InvalidOperationException("Status lookup must use the operation ledger.");
    }
}
