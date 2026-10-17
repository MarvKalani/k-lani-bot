using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Native compact services observe and invoke through the node's dispatcher/guard/ledger and retain all runtime data privately.
public sealed class CompactNodeAccess(ICompactNodeClient client, string expectedNode)
{
    public const int MaximumDepth = 32;
    public const int MaximumNodes = 2000;
    // The gateway parses only common rows, so it asks every backend for them explicitly.
    public const string CommonRowFormat = "common";

    // Mutations that may have reached the application: every attempt, less those the node proves it refused before dispatch.
    public int PossibleDispatches
    {
        get; private set;
    }

    // Throws CompactNodeException when actual node authority or capability evidence is unavailable.
    public async Task<CompactNativeCapabilities> CapabilitiesAsync(IReadOnlyList<Tool>? actions, CancellationToken cancellation)
    {
        var arguments = new JsonObject();
        if (actions != null)
        {
            var names = actions.Select(action => (JsonNode?)JsonValue.Create(ToolNames.Of(action))).ToArray();
            arguments["action_schema_names"] = new JsonArray(names);
        }
        JsonObject result = CompactNodeResponse.Require(await client.CallAsync(Tool.GetMachineCapabilities, arguments, cancellation));
        if (result["capabilities"] is not JsonObject capabilities || capabilities["nodeId"]?.ToString() != expectedNode)
        {
            throw new CompactNodeException("Workflow node authority changed or is unknown.");
        }
        CompactContractHash.TryDigest(capabilities["nativeImplementationSha256"]?.ToString(), out var implementation);
        CompactActionSchemas? schemas = null;
        if (capabilities.ContainsKey("actionSchemas"))
        {
            if (!CompactContractHash.TrySchemas(capabilities["actionSchemas"], out var parsed))
            {
                throw new CompactNodeException("Actual node action schema observation is invalid.");
            }
            schemas = parsed.Value;
        }

        return new(expectedNode, capabilities["version"]?.ToString(), capabilities["platform"]?.ToString(), implementation, schemas);
    }

    // Throws CompactNodeException when current windows cannot be observed safely.
    public async Task<IReadOnlyList<CompactNativeWindow>> WindowsAsync(CancellationToken cancellation)
    {
        JsonObject result = CompactNodeResponse.Require(await client.CallAsync(Tool.ListWindows, new(), cancellation));

        return CompactNodeObservation.Windows(result);
    }

    // Throws CompactNodeException for an incomplete native tree or absent snapshot/metadata evidence.
    public async Task<CompactNativeObservation> ObserveAsync(CompactWindowSelection selection, bool metadata,
        CancellationToken cancellation)
    {
        var arguments = new JsonObject
        {
            ["pid"] = selection.ProcessId,
            ["max_depth"] = MaximumDepth,
            ["max_nodes"] = MaximumNodes,
            ["field_metadata"] = metadata,
            ["row_format"] = CommonRowFormat
        };
        if (selection.Handle is long handle)
        {
            arguments["window"] = handle;
        }
        JsonObject response = await client.CallAsync(Tool.GetUiTree, arguments, cancellation);
        RequireTree(response);

        return CompactNodeObservation.Tree(response);
    }

    // Throws CompactNodeException for a failed tree read; a window without any tree names the last-resort route that can operate it.
    private static void RequireTree(JsonObject response)
    {
        var result = CompactNodeResponse.Read(response);
        if (result.Code == ActionErrorCode.NoAccessibilityTree)
        {
            string error = response["error"]?.ToString() ?? "";
            throw new CompactNodeException(error + " " + CompactInputNames.NoTreeHint, result);
        }
        CompactNodeResponse.Require(response);
    }

    // Throws CompactNodeException if intent/response persistence fails; no send occurs unless the original UUIDv7 is durable.
    public async Task<JsonObject> MutateAsync(Tool action, JsonObject arguments, CompactWorkflowCheckpoint? checkpoint,
        CancellationToken cancellation)
    {
        if (ToolContract.Get(action)["mutates"]?.GetValue<bool>() != true)
        {
            throw new CompactNodeException("Only a declared native mutation can use an operation checkpoint.");
        }
        JsonObject request = arguments.DeepClone().AsObject();
        if (checkpoint != null)
        {
            if (!checkpoint.TryBefore(action, request, out var prepared))
            {
                throw new CompactNodeException(CompactWorkflowJobNames.Error(prepared.Error!.Value, prepared.Detail));
            }
            request = prepared.Value!;
        }
        else
        {
            request["request_id"] = Guid.CreateVersion7().ToString();
        }
        PossibleDispatches++;
        JsonObject response = await client.CallAsync(action, request, cancellation);
        if (CompactNodeResponse.Read(response).Outcome == ActionOutcome.NotDispatched)
        {
            PossibleDispatches--;
        }
        if (checkpoint != null && !checkpoint.TryAfter(Guid.Parse(request["request_id"]!.GetValue<string>()),
            CompactNodeResponse.Read(response), out var saved))
        {
            throw new CompactNodeException(CompactWorkflowJobNames.Error(saved.Error!.Value, saved.Detail));
        }

        return response;
    }

    public async Task<CompactWorkflowOperationResult> OperationStatusAsync(Guid originalRequestId, CancellationToken cancellation)
    {
        var arguments = new JsonObject { ["request_id"] = originalRequestId.ToString() };
        var response = await client.CallAsync(Tool.GetActionStatus, arguments, cancellation);

        return CompactNodeResponse.Read(response);
    }
}
