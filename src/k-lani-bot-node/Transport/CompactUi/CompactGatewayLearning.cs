using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Learning compiles only the current verified demonstration and installs its parameterized body with actual selected node contracts.
public sealed class CompactGatewayLearning(CompactNodeAccess node, CompactGatewayObservation observation,
    CompactGatewayState state, CompactGatewayPrograms programs, Func<string> protocol)
{
    // Throws CompactNodeException for missing demonstration/identity/compatibility evidence or failed atomic installation.
    public async Task<JsonObject> LearnAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactCaptureCodec.TryRequest(arguments, state.Demonstration, out var request))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(request.Error!.Value));
        }
        var demonstration = state.Demonstration!;
        var windows = await observation.RequireInstanceAsync(demonstration.ProcessId, cancellation);
        var instance = RequireInstance(windows, demonstration.WindowHandle);
        var identity = programs.BoundIdentity(instance);
        var references = new Dictionary<string, CompactSemanticTarget>(StringComparer.Ordinal);
        foreach (var output in request.Value!.Outputs)
        {
            references.Add(output.Reference, await observation.TargetAsync(output.Reference, demonstration.ProcessId, cancellation));
        }
        if (!CompactCaptureCompiler.TryCompile(demonstration, request.Value, references, out var draft))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(draft.Error!.Value));
        }
        var fresh = await node.ObserveAsync(new CompactWindowSelection(instance.ProcessId, instance.Handle), false, cancellation);
        await observation.RequireInstanceAsync(instance.ProcessId, cancellation);
        if (!CompactOutputVerifier.TryVerify(draft.Value!.Outputs, Parameters(demonstration, request.Value.Parameters),
            demonstration.Before, fresh.Nodes, false, out var verified))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(verified.Error!.Value));
        }
        return await InstallAsync(draft.Value, identity, cancellation);
    }

    // Throws CompactNodeException for stale/empty/ambiguous read targets or missing contracts;
    // ArgumentException for invalid capture inputs.
    public async Task<JsonObject> ReadAsync(JsonObject arguments, CancellationToken cancellation)
    {
        int process = arguments["pid"]!.GetValue<int>();
        if (!CompactCaptureCodec.TryTopic(arguments["topic"]!.ToString(), out var topic)
            || topic!.Category is not CompactTopicCategory.Use and not CompactTopicCategory.Info
            || !CompactCaptureCodec.TryField(arguments["field"]!.ToString(), out var field))
        {
            throw new ArgumentException("Invalid observed read capture.");
        }
        var selection = state.SelectWindow(arguments);
        var target = await observation.TargetAsync(arguments["ref"]!.ToString(), process, cancellation);
        var windows = await observation.RequireInstanceAsync(process, cancellation);
        var instance = RequireInstance(windows, selection.Handle);
        if (target.WindowHandle != instance.Handle)
        {
            throw new CompactNodeException(CompactWindowChoice.ReferenceWindowRefusal);
        }
        var identity = programs.BoundIdentity(instance);
        var fresh = await node.ObserveAsync(new CompactWindowSelection(process, instance.Handle), false, cancellation);
        await observation.RequireInstanceAsync(process, cancellation);
        if (!CompactCaptureCompiler.TryRead(arguments["name"]!.ToString(), topic, arguments["output"]!.ToString(), process,
                target, field, fresh.Nodes, out var compiled))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(compiled.Error!.Value));
        }
        var reply = await InstallAsync(compiled.Value!, identity, cancellation);
        reply["topic"] = CompactCaptureNames.Topic(topic);
        reply["inputSchema"] = CompactFunctionCallSchema.Create(compiled.Value!);

        return reply;
    }

    // Throws CompactNodeException for missing actual compatibility evidence or failed atomic installation.
    private async Task<JsonObject> InstallAsync(CompactFunctionDraft draft, CompactProgramIdentity identity, CancellationToken cancellation)
    {
        var capabilities = await node.CapabilitiesAsync(CompactContractHash.RequiredActions(draft), cancellation);
        var current = new CompactObservedCompatibility(capabilities.Version, CompactCompatibilityWire.Wire(protocol()),
            capabilities.Platform,
            identity, capabilities.Actions, capabilities.Implementation, CompactGatewayPrograms.Implementation());
        if (!CompactCompatibility.TryCapture(draft, current, out var compatibility))
        {
            throw new CompactNodeException("Actual compatible node/wire/implementation/action evidence is required before saving.");
        }
        var library = programs.Writable(identity);
        if (!library.TryInstall(draft, compatibility.Value!, out var error))
        {
            throw new CompactNodeException(error!);
        }

        return new JsonObject
        {
            ["success"] = true,
            ["function"] = draft.Name,
            ["revision"] = library.Revision,
            ["verified"] = true,
            ["stored_values"] = false
        };
    }

    // Throws CompactNodeException when the named window is not a current window with a known start, or when no window is named
    // and the process does not have exactly one such window. The windows are those of one process.
    public static CompactNativeWindow RequireInstance(IReadOnlyList<CompactNativeWindow> windows, long? windowHandle)
    {
        if (windowHandle is long handle)
        {
            return RequireNamedInstance(windows, handle);
        }
        if (windows.Count != 1 || string.IsNullOrEmpty(windows[0].Started))
        {
            throw new CompactNodeException(CompactWindowChoice.UniqueWindowRefusal);
        }

        return windows[0];
    }

    // Throws CompactNodeException when the process is absent or no current window of it has a known start. Any window proves the process.
    public static CompactNativeWindow RequireProcessInstance(IReadOnlyList<CompactNativeWindow> windows, int? process)
    {
        if (process is not int processId)
        {
            throw new CompactNodeException(CompactWindowChoice.ProcessInstanceRefusal);
        }
        var selection = new CompactWindowSelection(processId, null);
        if (!CompactWindowChoice.TryProcessInstance(windows, selection, out var instance) || string.IsNullOrEmpty(instance.Started))
        {
            throw new CompactNodeException(CompactWindowChoice.ProcessInstanceRefusal);
        }

        return instance;
    }

    // Throws CompactNodeException when the named window is no longer current or has no known process start.
    private static CompactNativeWindow RequireNamedInstance(IReadOnlyList<CompactNativeWindow> windows, long handle)
    {
        CompactNativeWindow? named = windows.FirstOrDefault(window => window.Handle == handle);
        if (named == null || string.IsNullOrEmpty(named.Started))
        {
            throw new CompactNodeException(CompactWindowChoice.DemonstrationWindowRefusal);
        }

        return named;
    }

    // Throws CompactNodeException for unknown app metadata; build/variant values are never invented from the requested task.
    public static CompactProgramIdentity Identity(CompactNativeWindow instance)
    {
        if (!TryIdentity(instance, out var identity))
        {
            throw new CompactNodeException("Actual application variant/build is unknown; persistence and reuse are unavailable.");
        }

        return identity;
    }

    // A window proves its program only when both the process name and the build are actually known.
    public static bool TryIdentity(CompactNativeWindow instance, [NotNullWhen(true)] out CompactProgramIdentity? identity)
    {
        identity = null;
        if (instance.Build == "unknown" || instance.ProcessName == "unknown" || string.IsNullOrWhiteSpace(instance.Build)
            || string.IsNullOrWhiteSpace(instance.ProcessName))
        {
            return false;
        }
        identity = new(instance.ProcessName, instance.Build);

        return true;
    }

    private static IReadOnlyDictionary<string, string> Parameters(CompactDemonstration demonstration, IReadOnlyList<string> names)
    {
        var writes = demonstration.Steps.Where(step => step.Action == CompactRecipeAction.Write).ToArray();

        return names.Select((name, index) => new KeyValuePair<string, string>(name, writes[index].Text!))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }
}
