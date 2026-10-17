using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compact observation binds references to Info-established lifetimes and emits bounded views without weakening native snapshot enforcement.
public sealed class CompactGatewayObservation(ICompactNodeClient client, CompactGatewayState state)
{
    // Throws CompactNodeException for failed native capability/window discovery; malformed observations never establish application access.
    public async Task<JsonObject> InfoAsync(CancellationToken cancellation)
    {
        var response = CompactNodeResponse.Require(await client.CallAsync(Tool.GetMachineCapabilities, new(), cancellation));
        var capabilities = response["capabilities"] as JsonObject;
        string? node = capabilities?["nodeId"]?.ToString();
        if (string.IsNullOrEmpty(node) || state.Node != null && state.Node != node)
        {
            throw new CompactNodeException("Node identity is unknown or changed.");
        }
        var windowResponse = CompactNodeResponse.Require(await client.CallAsync(Tool.ListWindows, new(), cancellation));
        var windows = CompactNodeObservation.Windows(windowResponse);
        state.Node = node;
        state.Version = capabilities?["version"]?.ToString();
        state.Platform = capabilities?["platform"]?.ToString();
        state.FormCapabilities = capabilities?["formCapabilities"]?.DeepClone();
        state.ObservedWindows = windows;
        state.Demonstration = null;

        var info = new JsonObject
        {
            ["success"] = true,
            ["node"] = new JsonObject { ["name"] = "k-lani-bot-node", ["version"] = state.Version },
            ["platform"] = state.Platform ?? "unknown",
            ["routes"] = new JsonArray("native UI actions", "semantic focus and keyboard",
                "optional screenshots"),
            ["form_capabilities"] = state.FormCapabilities?.DeepClone() ?? JsonValue.Create("unsupported"),
            ["windows"] = new JsonArray(windows.Select(window => (JsonNode?)Window(window)).ToArray())
        };
        if (capabilities?["applicationMcps"] is JsonArray { Count: > 0 } programs)
        {
            info[CompactApplicationMcpNames.Field] = programs.DeepClone();
        }
        return info;
    }

    // Throws CompactNodeException for an unobserved/replaced application, incomplete tree or failed native screenshot;
    // ArgumentException for paging and for keys the state view does not accept;
    // the state view also throws CompactNodeException when the tree has no window row.
    public async Task<JsonObject> ObserveAsync(JsonObject arguments, CancellationToken cancellation)
    {
        var selection = state.SelectWindow(arguments);
        int process = selection.ProcessId;
        if (arguments["as"]?.GetValue<string>() == "state")
        {
            return await StateAsync(arguments, process, selection, cancellation);
        }
        if (arguments["image"]?.GetValue<bool>() == true)
        {
            return await ImageAsync(arguments, process, cancellation);
        }
        var current = await RequireInstanceAsync(process, cancellation);
        CompactNativeWindow observed = ChooseWindow(current, selection);
        var node = new CompactNodeAccess(client, state.Node!);
        var tree = await node.ObserveAsync(new CompactWindowSelection(process, observed.Handle), false, cancellation);
        await RequireInstanceAsync(process, cancellation);
        if (!CompactViewCodec.TryOptions(arguments, out var page))
        {
            throw new ArgumentException("Invalid view page arguments.");
        }
        string instance = InstanceFingerprint(current);
        string? contains = arguments["contains"]?.GetValue<string>();
        bool offscreenRequested = arguments["offscreen"]?.GetValue<bool>() == true;
        var options = new CompactProjectionOptions(state.Node!, instance, arguments["root"]?.GetValue<string>(),
            CompactViewCodec.ShowsOffscreen(offscreenRequested, contains), contains,
            arguments["max_chars"]?.GetValue<int>() ?? 2000, page, observed);
        if (!state.Projection.TryObserve(process, tree.Nodes, options, out var view))
        {
            throw new CompactNodeException(view.TreeError != null ? CompactTreeNames.Error(view.TreeError.Value, view.Detail)
                : CompactViewNames.Error(view.PageError!.Value));
        }
        state.SelectedProcess = process;
        state.LatestTree = tree;
        JsonObject reply = CompactViewCodec.Serialize(view.Value!);
        reply["success"] = true;
        reply["pid"] = process;

        return reply;
    }

    // The only arguments the state view accepts; a name outside this set would look applied while it is ignored.
    private static readonly HashSet<string> StateArgumentNames = new() { "pid", "window", "as", "cursor", "max_chars" };

    // Reads the selected window with the instance checks ui_observe uses, so a shape names the tree it was built from.
    internal async Task<CompactWindowTreeReading> ReadTreeAsync(int process, CompactWindowSelection selection,
        CancellationToken cancellation)
    {
        var current = await RequireInstanceAsync(process, cancellation);
        CompactNativeWindow observed = ChooseWindow(current, selection);
        var node = new CompactNodeAccess(client, state.Node!);
        var tree = await node.ObserveAsync(new CompactWindowSelection(process, observed.Handle), false, cancellation);
        await RequireInstanceAsync(process, cancellation);

        return new CompactWindowTreeReading(observed, tree);
    }

    // Renders the whole window as a typed class, or one part named by a cursor, so a routine task reads it in one call.
    // Throws CompactNodeException when the window has no row in its tree or the native snapshot fails;
    // ArgumentException for keys the state view does not accept, checked before any native call.
    private async Task<JsonObject> StateAsync(JsonObject arguments, int process, CompactWindowSelection selection,
        CancellationToken cancellation)
    {
        RequireStateArguments(arguments);

        var reading = await ReadTreeAsync(process, selection, cancellation);
        CompactNativeWindow observed = reading.Window;
        var tree = reading.Tree;

        int maximumChars = arguments["max_chars"]?.GetValue<int>() ?? CompactWindowStateLimits.DefaultMaxChars;
        string? cursor = arguments["cursor"]?.GetValue<string>();
        var request = new CompactWindowStateRequest(observed.Handle, observed.Kind, maximumChars, cursor);
        var attempt = CompactWindowState.Build(tree.Nodes, request);

        if (attempt.Error == CompactWindowStateError.NoWindow)
        {
            throw new CompactNodeException("The window tree has no window row.");
        }
        if (attempt.Error == CompactWindowStateError.UnknownPart)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["error"] = "unknown_part",
                ["hint"] = CompactViewNames.StateCursorHint
            };
        }
        state.SelectedProcess = process;
        state.LatestTree = tree;
        JsonObject reply = attempt.Value!;
        reply["success"] = true;
        reply["pid"] = process;
        reply["next"] = CompactViewNames.ApplyHint;

        return reply;
    }

    // Throws ArgumentException for a key the state view does not accept, so a filter is never ignored silently.
    private static void RequireStateArguments(JsonObject arguments)
    {
        foreach (string name in arguments.Select(argument => argument.Key))
        {
            if (!StateArgumentNames.Contains(name))
            {
                throw new ArgumentException("as:\"state\" accepts pid, window, cursor and max_chars.");
            }
        }
    }

    // A replaced window instance changes the fingerprint, so a view of the old instance is not taken for the current one.
    private static string InstanceFingerprint(IReadOnlyList<CompactNativeWindow> windows)
    {
        return CompactFormFields.Fingerprint(new JsonArray(windows.Select(window => (JsonNode?)new JsonObject
        {
            ["handle"] = window.Handle,
            ["started"] = window.Started
        }).ToArray()));
    }

    // Records the window's complete current state, so a stored function's reply describes only what that function changed.
    // Throws CompactNodeException when the window cannot be read completely; the caller refuses before any input is sent.
    public async Task<string> RecordBaselineAsync(int process, long handle, CancellationToken cancellation)
    {
        var arguments = new JsonObject
        {
            ["pid"] = process,
            ["window"] = handle,
            ["full"] = true,
            ["limit"] = CompactViewStore.MaximumPageSize
        };
        var page = await ObserveAsync(arguments, cancellation);
        while (page["next_cursor"] is JsonValue cursor)
        {
            arguments = new JsonObject
            {
                ["pid"] = process,
                ["window"] = handle,
                ["limit"] = CompactViewStore.MaximumPageSize,
                ["cursor"] = cursor.ToString()
            };
            page = await ObserveAsync(arguments, cancellation);
        }

        return page["view_ref"]!.ToString();
    }

    // The window's changes since the recorded baseline. A change list is never filtered by offscreen state.
    public Task<JsonObject> ChangesAsync(int process, long handle, string baseline, CancellationToken cancellation)
    {
        return ObserveAsync(new JsonObject { ["pid"] = process, ["window"] = handle, ["since_view"] = baseline }, cancellation);
    }

    // Throws ArgumentException for image cursors; CompactNodeException for missing instance/screenshot evidence.
    private async Task<JsonObject> ImageAsync(JsonObject arguments, int process, CancellationToken cancellation)
    {
        if (arguments.ContainsKey("cursor") || arguments.ContainsKey("since_view"))
        {
            throw new ArgumentException("Images have no view cursors.");
        }
        await RequireInstanceAsync(process, cancellation);

        return CompactNodeResponse.Require(await client.CallAsync(Tool.TakeScreenshot, new(), cancellation));
    }

    // Throws CompactNodeException if none of Info's original window lifetimes remain; fresh window captions may change.
    public async Task<IReadOnlyList<CompactNativeWindow>> RequireInstanceAsync(int process, CancellationToken cancellation)
    {
        state.RequireObserved(process);
        await new CompactNodeAccess(client, state.Node!).CapabilitiesAsync(null, cancellation);
        var response = CompactNodeResponse.Require(await client.CallAsync(Tool.ListWindows, new(), cancellation));
        var current = CompactNodeObservation.Windows(response)
            .Where(window => window.ProcessId == process).ToArray();
        if (!current.Any(window => window.Started != null && state.ObservedWindows.Any(previous => previous.Handle == window.Handle
            && previous.ProcessId == process && previous.Started == window.Started)))
        {
            throw new CompactNodeException("The observed application window was replaced. Refresh Info before input.");
        }
        return current;
    }

    // Throws CompactNodeException when no single current window is proved; the observation is never read for a guessed window.
    private static CompactNativeWindow ChooseWindow(IReadOnlyList<CompactNativeWindow> current, CompactWindowSelection selection)
    {
        if (!CompactWindowChoice.TryChoose(current, selection, out var window))
        {
            throw new CompactNodeException(selection.Handle is null ? CompactWindowChoice.AmbiguousRefusal
                : CompactWindowChoice.UnknownHandleRefusal);
        }

        return window;
    }

    // Throws CompactNodeException when a reference's owning window was replaced, even if another window of the process survives.
    public async Task<CompactSemanticTarget> TargetAsync(string reference, int process, CancellationToken cancellation)
    {
        var windows = await RequireInstanceAsync(process, cancellation);
        if (!state.Projection.TryTarget(reference, process, out var target))
        {
            throw new CompactNodeException(CompactTreeNames.Error(target.Error!.Value));
        }
        var original = target.Value!;
        var current = windows.Where(window => window.Handle == original.WindowHandle
            && window.Started == original.ProcessStarted).ToArray();
        if (current.Length != 1)
        {
            throw new CompactNodeException("The reference's owning window was replaced or is ambiguous.");
        }

        return original with
        {
            WindowKind = current[0].Kind
        };
    }

    private static JsonObject Window(CompactNativeWindow window)
    {
        return new JsonObject
        {
            ["pid"] = window.ProcessId,
            ["title"] = window.Title,
            ["version"] = window.Build,
            ["process_name"] = window.ProcessName,
            ["window"] = window.Handle,
            ["kind"] = CompactWindowKindWords.Text(window.Kind)
        };
    }
}
