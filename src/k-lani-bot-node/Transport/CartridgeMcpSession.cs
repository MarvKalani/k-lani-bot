using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

namespace KLaniBot.Transport;

// A cartridge client sees three routing tools. The selected program's identity binds the session; schemas and native
// library metadata are loaded only for the requested task branch, while raw node tools remain a diagnostic interface.
internal sealed class CartridgeMcpSession(
    GatewayCartridgeLibrary library,
    IReadOnlyDictionary<string, NodeTransportClient> clients,
    string? defaultNode,
    Func<GatewayCartridges, GatewayRecipe, JsonObject, CancellationToken, Task<JsonObject>> run,
    bool advisoryAvailable = false, Func<string>? protocol = null) : IDisposable
{
    // A fresh identity check is required when the model changes its selected application process.
    private sealed record Binding(int ProcessId, string Build, HashSet<Tool> Available);
    // Platform and node identity accompany the capabilities used for this application binding.
    private sealed record CapabilityProbe(HashSet<Tool> Tools, string Platform, string NodeVersion);
    // An absent window is not proof that the process is stopped; native status must establish that separately.
    private sealed record ProgramLookup(JsonObject? Window, bool NoWindow);
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectedGatewayCartridge> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeneratedCartridgeSession> _generated = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _selectionGate = new(1);
    private readonly CartridgeApplicationMcpFunctions _applicationMcpFunctions = new(library.DirectoryPath);
    private GatewayCartridges? _current;
    private GatewayCartridges Current => _current ?? throw new UnreachableException();

    public static bool Handles(string name)
    {
        return name is CartridgeGatewayTools.InfoName or CartridgeGatewayTools.BrowseName or CartridgeGatewayTools.CallName;
    }

    // Throws ArgumentException for an unregistered node, unknown function, invalid routing fields or missing program identity.
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        await _selectionGate.WaitAsync(cancellation);
        try
        {
            return await CallSelectedAsync(name, arguments, cancellation);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    // Throws ArgumentException for invalid routing, an unknown node, or application functions requested before Info.
    private async Task<JsonObject> CallSelectedAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        JsonObject descriptor = CartridgeGatewayTools.Descriptors(defaultNode == null).OfType<JsonObject>()
            .Single(item => item["name"]!.ToString() == name);
        ToolContract.Validate(descriptor["inputSchema"]!.AsObject(), arguments);
        string node = arguments["node"]?.ToString() ?? defaultNode
            ?? throw new ArgumentException("Select a registered node.");
        if (!clients.TryGetValue(node, out NodeTransportClient? client))
        {
            throw new ArgumentException("The selected node is not registered.");
        }
        if (name == CartridgeGatewayTools.CallName && arguments["function"]?.ToString() == "cartridge_node_status")
        {
            return await NodeStatusAsync(node, client, cancellation);
        }
        if (name == CartridgeGatewayTools.CallName && GatewayNodeFunctions.TryTool(arguments["function"]?.ToString(), out Tool backup))
        {
            return await NodeFunctionAsync(node, client, backup, arguments["arguments"], cancellation);
        }
        if (await ApplicationMcpFunctionAsync(name, arguments, node, client, cancellation) is { } stored)
        {
            return stored;
        }
        JsonObject? local = RouteLibrary(name, arguments, node);
        if (local != null)
        {
            return local;
        }
        if (_generated.TryGetValue(node, out var generated) && !_selected.ContainsKey(node))
        {
            return name == CartridgeGatewayTools.InfoName ? await generated.InfoAsync(arguments, cancellation)
                : await generated.CallAsync(name, arguments, cancellation);
        }
        return await CallBoundAsync(name, arguments, node, client, cancellation);
    }

    // Throws ArgumentException for missing program identity or unavailable selected functions.
    private async Task<JsonObject> CallBoundAsync(string name, JsonObject arguments, string node, NodeTransportClient client,
        CancellationToken cancellation)
    {
        SetCurrent(node);
        if (name == CartridgeGatewayTools.BrowseName && IsCoveragePath(arguments["path"]?.ToString()))
        {
            return Coverage(arguments["path"]!.ToString(), node);
        }
        if (name == CartridgeGatewayTools.InfoName)
        {
            JsonObject info = await IdentifyAsync(node, client, arguments, cancellation);
            if (info["success"]?.ToString() == "true" && _generated.TryGetValue(node, out var extension))
            {
                await extension.InfoAsync(arguments, cancellation);
            }
            return info;
        }
        if (!_bindings.TryGetValue(node, out Binding? binding))
        {
            throw new ArgumentException("Call cartridge_info before loading or executing application functions.");
        }
        string? function = arguments["function"]?.ToString();
        if (function != null && !Current.TryGet(function, out _) && _generated.TryGetValue(node, out var additional))
        {
            return await additional.CallAsync(name, arguments, cancellation);
        }

        return name == CartridgeGatewayTools.BrowseName ? Browse(arguments, binding)
            : await RunAsync(node, arguments, binding, cancellation);
    }

    // Throws what the node connection throws for cancellation or a broken transport.
    private async Task<JsonObject> NodeStatusAsync(string node, NodeTransportClient client, CancellationToken cancellation)
    {
        JsonObject response = await client.CallAsync(Tool.GetMachineCapabilities, new JsonObject(), cancellation);

        var status = new JsonObject
        {
            ["success"] = response["success"]?.DeepClone(),
            ["node_version"] = response["capabilities"]?["version"]?.DeepClone(),
            ["connected"] = response["connected"]?.DeepClone(),
            ["management"] = response["management"]?.DeepClone(),
            ["update_report"] = response["update_report"]?.DeepClone(),
            ["diagnostics_supported"] = response["management"] != null || response["update_report"] != null,
            ["error"] = response["error"]?.DeepClone()
        };
        var compactClient = new CompactRegisteredNodeClient(client);
        await _applicationMcpFunctions.AddToStatusAsync(node, compactClient, response["capabilities"], status, cancellation);
        return status;
    }

    // An agent lists a program's tools first, so that listing names the stored functions of the program's version too.
    // Throws what the node connection throws for cancellation or a broken transport.
    private async Task<JsonObject> NodeFunctionAsync(string node, NodeTransportClient client, Tool tool, JsonNode? values,
        CancellationToken cancellation)
    {
        JsonObject answer = await client.CallAsync(tool, GatewayNodeFunctions.Arguments(tool, values), cancellation);

        if (tool == Tool.ApplicationMcpRead && values is JsonObject read)
        {
            _applicationMcpFunctions.AddToListing(node, new CompactRegisteredNodeClient(client), read, answer);
        }
        return answer;
    }

    // Stored app MCP functions share the cartridge library with the compact gateway; null leaves the call to the cartridge routes.
    // Throws what the node connection throws for cancellation or a broken transport.
    private async Task<JsonObject?> ApplicationMcpFunctionAsync(string name, JsonObject arguments, string node,
        NodeTransportClient client, CancellationToken cancellation)
    {
        if (arguments["function"]?.ToString() is not { } function)
        {
            return null;
        }

        if (name == CartridgeGatewayTools.BrowseName)
        {
            return _applicationMcpFunctions.Describe(node, function);
        }
        if (name != CartridgeGatewayTools.CallName)
        {
            return null;
        }
        JsonObject values = arguments["arguments"] as JsonObject ?? new JsonObject();
        return await _applicationMcpFunctions.TryCallAsync(node, new CompactRegisteredNodeClient(client), function, values, cancellation);
    }

    private static bool IsCoveragePath(string? path)
    {
        return path == "Info/Coverage" || path?.StartsWith("Info/Coverage/") == true || path?.StartsWith("Info/Coverage@") == true;
    }

    private JsonObject Coverage(string path, string node)
    {
        bool checkedAccess = _bindings.TryGetValue(node, out Binding? binding);
        IReadOnlySet<Tool> available = binding?.Available ?? new HashSet<Tool>();
        JsonObject result = Current.Coverage?.Browse(path, Current.Recipes, available)
            ?? new JsonObject { ["success"] = false, ["error"] = "This cartridge has no reference coverage inventory." };
        result["availability_checked"] = checkedAccess;
        if (!checkedAccess && result["requirements"] is JsonArray requirements)
        {
            result["summary"]!["available_recipe_count"] = null;
            foreach (JsonObject requirement in requirements.OfType<JsonObject>())
            {
                requirement["available"] = null;
            }
        }

        return result;
    }

    private JsonObject? RouteLibrary(string name, JsonObject arguments, string node)
    {
        if (name == CartridgeGatewayTools.BrowseName && !_selected.ContainsKey(node) && library.DefaultManifest == null
            && string.IsNullOrEmpty(arguments["path"]?.ToString()) && arguments["function"] == null)
        {
            return library.Browse(GatewayCartridgeLibrary.CatalogPath);
        }
        if (name == CartridgeGatewayTools.BrowseName && IsCatalogPath(arguments["path"]?.ToString()))
        {
            return library.Browse(arguments["path"]!.ToString());
        }
        if (name == CartridgeGatewayTools.CallName && arguments["function"]?.ToString() == GatewayCartridgeLibrary.SelectFunction)
        {
            return Select(node, arguments);
        }
        if (name == CartridgeGatewayTools.CallName && arguments["function"]?.ToString() == GatewayCartridgeLibrary.UnloadFunction)
        {
            return Unload(node);
        }
        return null;
    }

    private static bool IsCatalogPath(string? path)
    {
        return path == GatewayCartridgeLibrary.CatalogPath || path?.StartsWith(GatewayCartridgeLibrary.CatalogPath + "/") == true;
    }

    // Throws ArgumentException when no cartridge was selected and no default was configured.
    private void SetCurrent(string node)
    {
        if (!_selected.TryGetValue(node, out SelectedGatewayCartridge? selected))
        {
            string path = library.DefaultManifest ?? throw new ArgumentException("Browse Catalog and select a cartridge first.");
            selected = new SelectedGatewayCartridge(path);
            _selected[node] = selected;
        }
        if (selected.Refresh() == CartridgeReload.Replaced)
        {
            _bindings.Remove(node);
        }
        _current = selected.Bundle;
    }

    // Throws ArgumentException unless the request contains exactly one catalog key.
    private JsonObject Select(string node, JsonObject arguments)
    {
        JsonObject values = arguments["arguments"]?.AsObject() ?? new JsonObject();
        if (values.Count != 1 || values["key"] is not JsonValue key || !key.TryGetValue(out string? text) || text == null)
        {
            throw new ArgumentException("cartridge_select requires exactly arguments.key from Catalog.");
        }
        var program = new GatewayProgramCatalog(library.DirectoryPath).Resolve(text);
        if (program.Generated is { Status: not CompactGeneratedCatalogStatus.Available })
        {
            throw new ArgumentException("Selected generated extension is ambiguous, invalid or unsupported.");
        }
        SelectedGatewayCartridge? selected = program.Native == null ? null : new SelectedGatewayCartridge(program.Native.Path);
        Unload(node);
        if (selected != null)
        {
            _selected[node] = selected;
        }
        if (program.Generated != null)
        {
            _generated[node] = new(program, clients[node], node, library.DirectoryPath, protocol ?? (() => "2025-06-18"));
        }

        return new JsonObject
        {
            ["success"] = true,
            ["selected"] = text,
            ["application"] = program.Application,
            ["next"] = CartridgeGatewayTools.InfoName
        };
    }

    private JsonObject Unload(string node)
    {
        if (_generated.Remove(node, out var generated))
        {
            generated.Dispose();
        }
        if (_selected.Remove(node, out SelectedGatewayCartridge? selected))
        {
            selected.Dispose();
        }
        _bindings.Remove(node);

        return new JsonObject { ["success"] = true, ["unloaded"] = true };
    }

    public void Dispose()
    {
        foreach (SelectedGatewayCartridge selected in _selected.Values)
        {
            selected.Dispose();
        }
        _selected.Clear();
        foreach (var generated in _generated.Values)
        {
            generated.Dispose();
        }
        _generated.Clear();
        _selectionGate.Dispose();
    }

    private async Task<JsonObject> IdentifyAsync(string node, NodeTransportClient client, JsonObject arguments,
        CancellationToken cancellation)
    {
        _bindings.Remove(node);
        JsonObject windows = await client.CallAsync(Tool.ListWindows, new(), cancellation);
        if (windows["success"]?.GetValue<bool>() != true && windows["code"] != null)
        {
            // A refusal such as access_paused says why better than "no target found" would.
            return windows;
        }
        ProgramLookup lookup = FindProgram(windows, arguments["pid"]?.GetValue<int>());
        if (lookup.Window == null && !lookup.NoWindow)
        {
            return new JsonObject { ["success"] = false, ["error"] = "No unique running cartridge target was found." };
        }
        CapabilityProbe probe = await AvailableAsync(client, cancellation);
        if (lookup.NoWindow)
        {
            return await IdentifyWithoutWindowAsync(node, client, probe, cancellation);
        }
        JsonObject window = lookup.Window!;
        int process = window["ProcessId"]!.GetValue<int>();
        HashSet<Tool> available = probe.Tools;
        JsonObject? native = null;
        if (available.Contains(Tool.InspectApplication))
        {
            native = await client.CallAsync(Tool.InspectApplication, new JsonObject { ["pid"] = process, ["area"] = "Info" }, cancellation);
        }
        if (native?["success"]?.GetValue<bool>() != true)
        {
            available.Remove(Tool.InspectApplication);
            available.Remove(Tool.OperateApplication);
        }
        string build = window["AppVersion"]!.ToString();
        _bindings[node] = new(process, build, available);

        return IdentityResult(window, probe, native, available);
    }

    private async Task<JsonObject> IdentifyWithoutWindowAsync(string node, NodeTransportClient client, CapabilityProbe probe,
        CancellationToken cancellation)
    {
        if (Current.ProcessName != "OUTLOOK" || !probe.Tools.Contains(Tool.InspectApplication))
        {
            return new JsonObject
            {
                ["success"] = false,
                ["running"] = null,
                ["error"] = "No target window found; native process status is unavailable for this cartridge. Running status is unknown."
            };
        }
        JsonObject status = await client.CallAsync(Tool.InspectApplication, new JsonObject { ["area"] = "Status" }, cancellation);
        if (status["success"]?.GetValue<bool>() != true || status["status"]?["Running"] == null)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["running"] = null,
                ["error"] = "Native process status could not be established."
            };
        }
        if (status["status"]!["Running"]!.GetValue<bool>())
        {
            return new JsonObject
            {
                ["success"] = false,
                ["running"] = true,
                ["window_found"] = false,
                ["error"] = "Outlook is running without an operable window. Observe Info again before starting or operating it."
            };
        }

        return BindStopped(node, probe);
    }

    private JsonObject IdentityResult(JsonObject window, CapabilityProbe probe, JsonObject? native, HashSet<Tool> available)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["running"] = true,
            ["application"] = Current.ApplicationName,
            ["pid"] = window["ProcessId"]!.DeepClone(),
            ["build"] = window["AppVersion"]!.DeepClone(),
            ["platform"] = probe.Platform,
            ["node_version"] = probe.NodeVersion,
            ["cartridge_version"] = Current.CartridgeVersion,
            ["ui_build_matches"] = window["AppVersion"]!.ToString() == Current.TestedVersion,
            ["native_info"] = native?["info"]?.DeepClone(),
            ["native_available"] = native?["success"]?.GetValue<bool>() == true,
            ["native_error"] = native?["success"]?.GetValue<bool>() == false ? native["message"]?.DeepClone() : null,
            ["coverage"] = Current.Coverage?.Summary(Current.Recipes, available),
            ["catalog_path"] = GatewayCartridgeLibrary.CatalogPath,
            ["categories"] = Current.Browse("", available)["topics"]!.DeepClone()
        };
    }

    private JsonObject BindStopped(string node, CapabilityProbe probe)
    {
        var available = probe.Tools.Where(tool => tool == Tool.LaunchProcess).ToHashSet();
        _bindings[node] = new(0, "", available);

        return new JsonObject
        {
            ["success"] = true,
            ["running"] = false,
            ["application"] = Current.ApplicationName,
            ["platform"] = probe.Platform,
            ["node_version"] = probe.NodeVersion,
            ["cartridge_version"] = Current.CartridgeVersion,
            ["can_start"] = Current.Recipes.Any(recipe => recipe.IsProgramStart && recipe.Tools.All(available.Contains)),
            ["start_requires"] = "process permission and the cartridge executable in the node's program allow-list",
            ["coverage"] = Current.Coverage?.Summary(Current.Recipes, available),
            ["categories"] = Current.Browse("", available)["topics"]!.DeepClone()
        };
    }

    private ProgramLookup FindProgram(JsonObject response, int? processId)
    {
        if (response["success"]?.GetValue<bool>() != true || response["windows"] is not JsonArray windows)
        {
            return new(null, false);
        }
        JsonObject[] matches = windows.OfType<JsonObject>().Where(window =>
            string.Equals(window["ProcessName"]?.ToString(), Current.ProcessName, StringComparison.OrdinalIgnoreCase)
            && (processId == null || window["ProcessId"]?.GetValue<int>() == processId))
            .GroupBy(window => window["ProcessId"]!.GetValue<int>()).Select(group => group.First()).ToArray();

        bool noWindow = !windows.OfType<JsonObject>().Any(window =>
            string.Equals(window["ProcessName"]?.ToString(), Current.ProcessName, StringComparison.OrdinalIgnoreCase));

        return new(matches.Length == 1 ? matches[0] : null, noWindow);
    }

    private async Task<CapabilityProbe> AvailableAsync(NodeTransportClient client, CancellationToken cancellation)
    {
        JsonObject response = await client.CallAsync(Tool.GetMachineCapabilities, new(), cancellation);
        var tools = new HashSet<Tool>();
        if (advisoryAvailable)
        {
            tools.Add(Tool.EvaluateTrace);
        }
        if (response["success"]?.GetValue<bool>() == true && response["capabilities"]?["activeTools"] is JsonArray names)
        {
            foreach (JsonNode? name in names)
            {
                if (ToolNames.TryParse(name?.ToString(), out Tool tool))
                {
                    tools.Add(tool);
                }
            }
        }

        return new(tools, response["capabilities"]?["platform"]?.ToString() ?? "unknown",
            response["capabilities"]?["version"]?.ToString() ?? "unknown");
    }

    // Throws ArgumentException for a function outside the selected program's discovered capabilities.
    private JsonObject Browse(JsonObject arguments, Binding binding)
    {
        string? name = arguments["function"]?.ToString();
        if (name == null)
        {
            return Current.Browse(arguments["path"]?.ToString() ?? "", binding.Available);
        }
        GatewayRecipe recipe = RequireRecipe(name, binding);
        JsonObject descriptor = recipe.Descriptor(false);
        descriptor["inputSchema"]!["properties"]!.AsObject().Remove("pid");
        descriptor["inputSchema"]!["properties"]!.AsObject().Remove("node");
        JsonArray required = descriptor["inputSchema"]!["required"]!.AsArray();
        JsonNode? process = required.FirstOrDefault(item => item?.ToString() == "pid");
        if (process != null)
        {
            required.Remove(process);
        }

        return new JsonObject { ["success"] = true, ["topic"] = recipe.Topic.Path, ["function"] = descriptor };
    }

    // Throws ArgumentException for a different process or a function unavailable on this node.
    private async Task<JsonObject> RunAsync(string node, JsonObject arguments, Binding binding, CancellationToken cancellation)
    {
        GatewayRecipe recipe = RequireRecipe(arguments["function"]!.ToString(), binding);
        JsonObject values = (JsonObject)(arguments["arguments"]?.DeepClone() ?? new JsonObject());
        if (values["pid"] is { } selected && selected.GetValue<int>() != binding.ProcessId)
        {
            throw new ArgumentException("Call cartridge_info to select a different application process.");
        }
        if (!recipe.IsProgramStart)
        {
            values["pid"] = binding.ProcessId;
        }
        values["node"] = node;
        JsonObject result = await run(Current, recipe, values, cancellation);
        if (recipe.IsProgramStart)
        {
            _bindings.Remove(node);
        }

        return result;
    }

    // Throws ArgumentException for an unknown recipe or one the node does not offer.
    private GatewayRecipe RequireRecipe(string name, Binding binding)
    {
        if (!Current.TryGet(name, out GatewayRecipe? recipe) || recipe == null || !recipe.Tools.All(binding.Available.Contains))
        {
            throw new ArgumentException("The selected function is not available on this node.");
        }
        if (!recipe.IsProgramStart && recipe.Compatibility == CartridgeCompatibility.ExactBuild
            && recipe.ApplicationVersion != binding.Build)
        {
            throw new ArgumentException("This function requires the cartridge's tested application build.");
        }

        return recipe;
    }
}
