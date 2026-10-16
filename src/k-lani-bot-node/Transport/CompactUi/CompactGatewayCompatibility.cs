using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// One assessment binds its loaded revision to the actual current node, application lifetime and selected native contracts.
public sealed record CompactGatewayCompatibilityObservation(CompactLoadedFunction Function, CompactProgramIdentity Application,
    long Revision, CompactObservedCompatibility Current);

// Native compatibility routes assess one selected function and consume independently issued signed receipts without application input.
public sealed class CompactGatewayCompatibility(CompactNodeAccess node, CompactGatewayObservation observation,
    CompactGatewayState state, CompactGatewayPrograms programs, Func<CompactProgramIdentity> selectedIdentity,
    Func<string> protocol, CompactAcceptanceSettings? settings)
{
    // Throws ArgumentException for malformed routing arguments; CompactNodeException for missing actual application/receipt evidence.
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactCompatibilityCommandCodec.TryRead(name, arguments, out var command))
        {
            throw new ArgumentException("Invalid compatibility arguments.");
        }
        var current = await ObserveAsync(command!.Function, cancellation);

        return command.Kind switch
        {
            CompactCompatibilityCommandKind.Info => Info(current),
            CompactCompatibilityCommandKind.Revalidate => Revalidate(command, current),
            _ => throw new UnreachableException()
        };
    }

    // Throws CompactNodeException for absent/ambiguous selection, unverified definitions or missing native observations.
    private async Task<CompactGatewayCompatibilityObservation> ObserveAsync(string name, CancellationToken cancellation)
    {
        var application = selectedIdentity();
        var document = programs.ReadDocument(application);
        var matches = document?.Functions.Where(function => function.Draft.Name == name).ToArray() ?? [];
        if (matches.Length != 1 || matches[0].Document["validation"]?.ToString()
                .StartsWith("demonstrated_once", StringComparison.Ordinal) != true)
        {
            throw new CompactNodeException("Selected generated function is absent or unverified.");
        }
        int process = state.SelectedProcess!.Value;
        var original = CompactGatewayLearning.RequireProcessInstance(state.ObservedWindows, process);
        var windows = await observation.RequireInstanceAsync(process, cancellation);
        var current = CompactGatewayLearning.RequireProcessInstance(windows, process);
        var capabilities = await node.CapabilitiesAsync(CompactContractHash.RequiredActions(matches[0].Draft), cancellation);
        windows = await observation.RequireInstanceAsync(process, cancellation);
        current = CompactGatewayLearning.RequireProcessInstance(windows, process);
        var actualIdentity = new CompactProgramIdentity(current.ProcessName == original.ProcessName ? application.Key : current.ProcessName,
            current.Build);
        var implementation = CompactGatewayPrograms.Implementation();
        var observed = new CompactObservedCompatibility(capabilities.Version, CompactCompatibilityWire.Wire(protocol()),
            capabilities.Platform, actualIdentity, capabilities.Actions, capabilities.Implementation, implementation);

        return new(matches[0], application, document!.Revision, observed);
    }

    // Throws CompactNodeException when bounded private evidence cannot retain this exact assessment.
    private JsonObject Info(CompactGatewayCompatibilityObservation observed)
    {
        var assessment = CompactCompatibilityNames.Verdict(CompactCompatibility.Assess(observed.Function.Draft,
            observed.Function.Compatibility, observed.Current));
        var debug = new JsonObject
        {
            ["function"] = observed.Function.Draft.Name,
            ["assessment"] = assessment.DeepClone(),
            ["recorded"] = observed.Function.Document["compatibility"]?.DeepClone(),
            ["observed"] = Current(observed.Current),
            ["acceptance_receipt"] = false
        };
        if (!state.Results.TryPut(state.Authority, new Dictionary<string, string>(), CompactJsonText.Serialize(debug), out var evidence))
        {
            throw new CompactNodeException(CompactResultErrors.ToWireMessage(evidence.Error!.Value));
        }
        assessment["success"] = true;
        assessment["function"] = observed.Function.Draft.Name;
        assessment["revision"] = observed.Revision;
        assessment["evidence_ref"] = evidence.Value!.Value.ToString();
        if (observed.Function.Compatibility.Contracts?.Acceptance is Guid acceptance)
        {
            assessment["acceptance_ref"] = acceptance.ToString();
        }

        return assessment;
    }

    // Throws CompactNodeException for absent configured evidence, unauthorized receipts or failed atomic revision replacement.
    private JsonObject Revalidate(CompactCompatibilityCommand command, CompactGatewayCompatibilityObservation observed)
    {
        if (settings == null)
        {
            throw new CompactNodeException("Configure private evidence issued by a reviewed isolated acceptance runner.");
        }
        if (state.Node != settings.Authority.Node)
        {
            throw new CompactNodeException(CompactAcceptanceNames.Error(CompactAcceptanceError.AuthorityChanged));
        }
        if (!CompactAcceptanceStore.TryOpen(settings.EvidenceRoot, programs.LibraryRoot,
                settings.Authority, out var evidence))
        {
            throw new CompactNodeException(CompactAcceptanceNames.Error(evidence.Error!.Value, evidence.Detail));
        }
        var revalidation = new CompactRevalidation(programs.Writable(observed.Application), evidence.Value!);
        if (!revalidation.TryAccept(command.Function, command.Revision!.Value, command.Evidence!, state.Node!,
                observed.Current, out var accepted))
        {
            throw new CompactNodeException(CompactAcceptanceNames.Error(accepted.Error!.Value, accepted.Detail));
        }

        return new JsonObject
        {
            ["success"] = true,
            ["function"] = accepted.Value!.Function,
            ["status"] = "reusable",
            ["revision"] = accepted.Value.Revision,
            ["evidence_ref"] = accepted.Value.Evidence.Value.ToString(),
            ["stored_values"] = false
        };
    }

    private static JsonObject Current(CompactObservedCompatibility observed)
    {
        return new JsonObject
        {
            ["node_version"] = observed.NodeVersion,
            ["wire_version"] = observed.Wire?.Value,
            ["platform"] = observed.Platform,
            ["application"] = new JsonObject { ["key"] = observed.Application?.Key, ["version"] = observed.Application?.Build },
            ["action_schemas"] = observed.Actions == null ? null : CompactCompatibilityWire.Schemas(observed.Actions),
            ["native_implementation"] = observed.Native?.Hex,
            ["gateway_implementation"] = observed.Gateway?.Hex
        };
    }
}
