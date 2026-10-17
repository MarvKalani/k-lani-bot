using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// One compact MCP conversation serializes UI transitions while its existing registered client keeps policy/visibility on the operated node.
public sealed class CompactGatewaySession : IDisposable
{
    private readonly ICompactNodeClient _client;
    private readonly string _registeredName;
    private readonly CompactStorageRoots _roots;
    private readonly Func<string> _protocol;

    // Runnable stored functions of each ui_info row, keyed by window handle; a name resolves without selection only through these rows.
    private IReadOnlyDictionary<long, IReadOnlyList<CompactListedFunction>> _listedFunctions =
        new Dictionary<long, IReadOnlyList<CompactListedFunction>>();
    private CompactStoredFunctionDiscovery _discovery = CompactStoredFunctionDiscovery.NotObserved;
    private bool _storedFunctionListed;
    private bool _bundleFunctionListed;
    private bool _buildDriftListed;
    private bool _applicationMcpListed;
    // The evidence of an unchanged build-drift detail is reused, so repeated ui_info calls do not push other evidence out of the store.
    private readonly Dictionary<string, CompactEvidenceReference> _buildDriftEvidence = new(StringComparer.Ordinal);

    // The catalog program and application process the native cartridge route is currently bound to. A call by pid compares these
    // before it selects and binds again, so alternating between two programs costs a model no round trip.
    private string? _nativeProgramKey;
    private int? _nativeProcess;
    private readonly CompactGatewayState _state = new();
    private readonly SemaphoreSlim _serial = new(1);
    private readonly CompactGatewayObservation _observation;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>>? _nativeRoute;
    private GatewayProgramCartridge? _selectedProgram;
    private CompactGatewayForms? _forms;
    private readonly CompactGatewayConfiguration _configuration;
    private CompactGatewayWorkflows? _workflows;
    private readonly Dictionary<string, CompactFormScanner> _scanners = new(StringComparer.Ordinal);
    private readonly CompactApplicationMcpFunctions _applicationMcpFunctions;

    public CompactGatewaySession(ICompactNodeClient client, string registeredName, CompactStorageRoots roots, Func<string> protocol,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>>? nativeRoute = null,
        CompactGatewayConfiguration? configuration = null)
    {
        _client = client;
        _registeredName = registeredName;
        _roots = roots;
        _protocol = protocol;
        _observation = new(client, _state);
        _nativeRoute = nativeRoute;
        _configuration = configuration ?? new();
        _applicationMcpFunctions = new(client, roots.Library);
    }

    // Throws ArgumentException for unknown tools or invalid routing fields; native refusals remain explicit failed tool results.
    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactGatewayTools.TryTool(name, out var tool) || !CompactGatewayTools.TryDescriptor(name, out var descriptor))
        {
            throw new ArgumentException("Unknown compact UI tool.");
        }
        ToolContract.Validate(descriptor!["inputSchema"]!.AsObject(), arguments);
        await _serial.WaitAsync(cancellation);
        try
        {
            return await RouteAsync(tool, arguments, cancellation);
        }
        catch (CompactNodeException exception)
        {
            return new JsonObject { ["success"] = false, ["error"] = exception.Message };
        }
        finally
        {
            _serial.Release();
        }
    }

    // Throws ArgumentException for invalid compact routing fields; CompactNodeException for missing native observation evidence.
    private Task<JsonObject> RouteAsync(CompactUiTool tool, JsonObject arguments, CancellationToken cancellation)
    {
        switch (tool)
        {
            case CompactUiTool.Info:
                return InfoAsync(cancellation);
            case CompactUiTool.CartridgeInfo:
                return CartridgeInfoAsync(cancellation);
            case CompactUiTool.Observe:
                return _observation.ObserveAsync(arguments, cancellation);
            case CompactUiTool.Act:
                _state.SelectedProcess = _state.SelectWindow(arguments).ProcessId;
                return ActAsync(arguments, cancellation);
            case CompactUiTool.Apply:
                _state.SelectedProcess = _state.SelectWindow(arguments).ProcessId;
                return new CompactGatewayApply(_observation, _state, new CompactNodeAccess(_client, _state.Node!))
                    .RunAsync(arguments, cancellation);
            case CompactUiTool.CartridgeBrowse:
                return BrowseAsync(arguments, cancellation);
            case CompactUiTool.CartridgeCall:
                return CartridgeCallAsync(arguments["function"]!.GetValue<string>(),
                    arguments["arguments"] as JsonObject ?? new(), cancellation);
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    // Throws CompactNodeException for failed native window discovery; stored names are advisory and never fail the observation.
    private async Task<JsonObject> InfoAsync(CancellationToken cancellation)
    {
        var info = await _observation.InfoAsync(cancellation);
        await ListFunctionsAsync(info, cancellation);
        _applicationMcpListed = info.ContainsKey(CompactApplicationMcpNames.Field);
        if (_applicationMcpListed)
        {
            await _applicationMcpFunctions.AddToInfoAsync(info, cancellation);
        }
        info["next"] = NextSentence();

        return info;
    }

    // The sentence after the rows names the one call that runs each kind of function the rows list.
    private string NextSentence()
    {
        var sentences = new List<string>();
        if (_storedFunctionListed)
        {
            sentences.Add(CompactStoredFunctionListing.UsageHint);
        }
        if (_bundleFunctionListed)
        {
            sentences.Add(BundleUsageHint);
        }
        if (_buildDriftListed)
        {
            sentences.Add(CompactBuildDriftNames.Hint);
        }
        if (_applicationMcpListed)
        {
            sentences.Add(CompactApplicationMcpNames.Hint);
        }
        if (_applicationMcpListed && _applicationMcpFunctions.FunctionsListed)
        {
            sentences.Add(CompactApplicationMcpFunctionNames.FunctionsHint);
        }
        if (_applicationMcpListed && _applicationMcpFunctions.DriftListed)
        {
            sentences.Add(CompactApplicationMcpFunctionNames.DriftHint);
        }
        sentences.Add(CompactViewNames.StateHint);

        return string.Join(" ", sentences);
    }

    // Adds the runnable stored functions to the observed window rows and records whether any row names one.
    private async Task ListFunctionsAsync(JsonObject info, CancellationToken cancellation)
    {
        var listing = new CompactStoredFunctionListing(new CompactNodeAccess(_client, _state.Node!),
            new CompactGatewayPrograms(_roots), CompactCompatibilityWire.Wire(_protocol()), CompactGatewayPrograms.Implementation());
        _listedFunctions = await listing.ListAsync(_state.ObservedWindows, _selectedProgram, cancellation);
        var installed = new CompactInstalledCartridges(_roots.Library);
        _storedFunctionListed = false;
        _bundleFunctionListed = false;
        foreach (var row in info["windows"]!.AsArray().OfType<JsonObject>())
        {
            long handle = row["window"]!.GetValue<long>();
            if (_listedFunctions.TryGetValue(handle, out var functions))
            {
                var summary = CompactFunctionSummary.Summarize(functions);
                if (summary.Functions.Count > 0)
                {
                    AddFunctions(row, summary);
                    _storedFunctionListed = true;
                }
            }
            var window = _state.ObservedWindows.FirstOrDefault(candidate => candidate.Handle == handle);
            if (window != null && AddBundleFunctions(row, installed.FunctionsOf(window)))
            {
                _bundleFunctionListed = true;
            }
        }

        _discovery = _storedFunctionListed || _bundleFunctionListed ? CompactStoredFunctionDiscovery.Listed
            : CompactStoredFunctionDiscovery.NoneListed;
        await AddBuildDriftAsync(info, cancellation);
    }

    // The row of an instance whose build has no cartridge, while another build of its variant has stored functions, carries the
    // probe report of those functions. They stay out of the function list and out of name resolution, so none becomes callable.
    private async Task AddBuildDriftAsync(JsonObject info, CancellationToken cancellation)
    {
        _buildDriftListed = false;
        var listing = new CompactBuildDriftListing(new CompactNodeAccess(_client, _state.Node!), new CompactGatewayPrograms(_roots));
        var reports = await listing.ListAsync(_state.ObservedWindows, cancellation);
        foreach (var row in info["windows"]!.AsArray().OfType<JsonObject>())
        {
            if (!reports.TryGetValue(row["window"]!.GetValue<long>(), out var instanceReports))
            {
                continue;
            }
            string detail = CompactJsonText.Serialize(CompactBuildDriftNames.Detail(instanceReports));
            if (!TryBuildDriftEvidence(detail, out var evidence))
            {
                continue;
            }
            string reference = evidence!.Value.ToString();
            row["other_builds"] = new JsonArray(instanceReports.Select(report => (JsonNode?)CompactBuildDriftNames.Row(report, reference))
                .ToArray());
            _buildDriftListed = true;
        }
    }

    // An identical detail that the store still holds keeps its reference; otherwise the detail is stored once more.
    private bool TryBuildDriftEvidence(string detail, out CompactEvidenceReference? evidence)
    {
        var debug = new CompactResultSection(CompactResultSectionKind.Debug);
        if (_buildDriftEvidence.TryGetValue(detail, out evidence)
            && _state.Results.TryRead(_state.Authority, new(evidence, debug, 0, 1), out _))
        {
            return true;
        }
        if (_buildDriftEvidence.Count >= CompactResultStore.MaximumEntries)
        {
            _buildDriftEvidence.Clear();
        }
        if (!_state.Results.TryPut(_state.Authority, new Dictionary<string, string>(), detail, out var stored))
        {
            evidence = null;
            return false;
        }
        evidence = stored.Value!;
        _buildDriftEvidence[detail] = evidence;

        return true;
    }

    // Functions of an installed program bundle follow the stored ones; they are called by pid with their parameters directly.
    private static bool AddBundleFunctions(JsonObject row, IReadOnlyList<GatewayBundleFunction> functions)
    {
        if (functions.Count == 0)
        {
            return false;
        }
        if (row["functions"] is not JsonArray entries)
        {
            entries = new JsonArray();
            row["functions"] = entries;
        }
        foreach (var function in functions.Take(CompactInstalledCartridges.FunctionLimit))
        {
            entries.Add(CompactInstalledCartridges.ToJson(function));
        }
        int more = functions.Count - CompactInstalledCartridges.FunctionLimit;
        if (more > 0)
        {
            row["more_functions"] = (row["more_functions"]?.GetValue<int>() ?? 0) + more;
        }

        return true;
    }

    // Rows without stored functions keep their existing shape; a row with them gets the bounded function list and the count of the rest.
    private static void AddFunctions(JsonObject row, CompactFunctionSummary summary)
    {
        row["functions"] = new JsonArray(summary.Functions.Select(function => (JsonNode?)function.ToJson()).ToArray());
        if (summary.MoreFunctions > 0)
        {
            row["more_functions"] = summary.MoreFunctions;
        }
    }

    private Task<JsonObject> ActAsync(JsonObject arguments, CancellationToken cancellation)
    {
        var node = new CompactNodeAccess(_client, _state.Node!);
        var automatic = _roots.Learning == null ? null
            : new CompactGatewayAutomaticCapture(node, _observation, _state, new(_roots), _protocol);
        var actions = new CompactGatewayActions(node, _observation, _state,
            automatic == null ? null : (compact, token) => AutomaticCaptureAsync(automatic, compact, token));

        return actions.RunAsync(arguments, cancellation);
    }

    private async Task<JsonObject> AutomaticCaptureAsync(CompactGatewayAutomaticCapture automatic, bool compact,
        CancellationToken cancellation)
    {
        var reply = await automatic.CaptureAsync(compact, cancellation);
        foreach (var function in reply["functions"]!.AsArray().OfType<JsonObject>().Where(function =>
            function["verified"]?.ToString() == "true"))
        {
            RecordWorkflowCapture(function["function"]!.ToString(), reply);
        }

        return WithoutGeneratedNames(reply);
    }

    // Generated names stay out of the reply the model reads. A verified generated function remains reachable by topic through
    // cartridge_browse, and a candidate keeps its status and reason without a name; the instruction names only listed functions.
    private static JsonObject WithoutGeneratedNames(JsonObject reply)
    {
        var announced = new JsonArray();
        foreach (JsonObject item in reply["functions"]!.AsArray().OfType<JsonObject>())
        {
            JsonObject? kept = AnnouncedItem(item);
            if (kept != null)
            {
                announced.Add(kept);
            }
        }
        reply["functions"] = announced;
        if (!announced.OfType<JsonObject>().Any(item => item.ContainsKey("function")))
        {
            reply.Remove("instruction");
        }

        return reply;
    }

    // Null for an available generated function; a candidate loses only its generated name.
    private static JsonObject? AnnouncedItem(JsonObject item)
    {
        string? name = item["function"]?.ToString();
        if (name == null || !CompactAutomaticNames.IsGenerated(name))
        {
            return item.DeepClone().AsObject();
        }
        if (item["status"]?.ToString() == "available")
        {
            return null;
        }
        var candidate = item.DeepClone().AsObject();
        candidate.Remove("function");

        return candidate;
    }

    private void RecordWorkflowCapture(string name, JsonObject reply)
    {
        if (_workflows == null)
        {
            return;
        }
        try
        {
            var identity = SelectedIdentity();
            var function = new CompactGatewayPrograms(_roots).Get(identity, name);
            if (!_workflows.TryRecord(function, identity, out string? error))
            {
                reply["workflow_capture_error"] = error;
            }
        }
        catch (Exception exception)
        {
            reply["workflow_capture_error"] = exception.Message;
        }
    }

    // Throws CompactNodeException when private workflow configuration was not supplied at native startup.
    private CompactGatewayWorkflows Workflows()
    {
        if (_configuration.Workflow == null)
        {
            throw new CompactNodeException("Configure separate private workflow state and trusted authority through --ui-config.");
        }
        _workflows ??= new(_client, _state, _roots, _configuration.Workflow, _protocol);

        return _workflows;
    }

    // Throws ArgumentException for invalid native selection; generated branch metadata remains bounded behind the same logical entry.
    private async Task<JsonObject> BrowseAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (arguments["window"] != null || arguments["pid"] != null)
        {
            _state.SelectedProcess = _state.SelectWindow(arguments).ProcessId;
        }
        string? name = arguments["function"]?.ToString();
        string topic = arguments["topic"]?.ToString() ?? "";
        if (_selectedProgram?.Native != null && _nativeRoute != null && topic != "Catalog"
            && !topic.StartsWith("Catalog/", StringComparison.Ordinal))
        {
            if (name != null && _selectedProgram.Native.EntryPoints.Contains(name, StringComparer.Ordinal))
            {
                return await _nativeRoute("cartridge_browse", new JsonObject { ["function"] = name }, cancellation);
            }
            if (name == null)
            {
                var native = await _nativeRoute("cartridge_browse", new JsonObject { ["path"] = topic }, cancellation);
                var generated = Browse(arguments);
                MergeArrays(native, generated, "topics");
                MergeArrays(native, generated, "functions");
                if (topic.Length == 0)
                {
                    native["stored_functions"] = CompactStoredFunctionListing.Sentence(_discovery);
                }
                return native;
            }
        }

        return Browse(arguments);
    }

    private static void MergeArrays(JsonObject target, JsonObject source, string name)
    {
        var current = target[name] as JsonArray ?? new JsonArray();
        foreach (JsonNode? entry in source[name] as JsonArray ?? new JsonArray())
        {
            if (!current.Any(existing => JsonNode.DeepEquals(existing, entry)))
            {
                current.Add(entry?.DeepClone());
            }
        }
        if (!target.ContainsKey(name))
        {
            target[name] = current;
        }
    }

    // Throws ArgumentException for an unknown topic/function, without exposing unsupported or unrelated schemas.
    private JsonObject Browse(JsonObject arguments)
    {
        string? function = arguments["function"]?.GetValue<string>();
        if (function != null)
        {
            return new JsonObject { ["success"] = true, ["function"] = Descriptor(function).DeepClone() };
        }
        string topic = arguments["topic"]?.GetValue<string>() ?? "";
        if (topic == "Catalog" || topic.StartsWith("Catalog/", StringComparison.Ordinal))
        {
            return new GatewayCartridgeLibrary(_roots.Library).Browse(topic);
        }
        var available = BuiltInFunctions(topic);
        foreach (var entry in SelectedFunctions(topic))
        {
            available.Add(new JsonObject { ["name"] = entry.Draft.Name, ["topic"] = CompactCaptureNames.Topic(entry.Draft.Topic) });
        }

        return BrowseReply(topic, available);
    }

    // Built-in functions keep their exact topic; only stored functions are matched by topic prefix.
    private static JsonArray BuiltInFunctions(string topic)
    {
        var functions = CompactGatewayFunctionTools.All.OfType<JsonObject>().Where(function => FunctionTopic(function) == topic)
            .Select(function => (JsonNode?)new JsonObject
            {
                ["name"] = function["name"]!.DeepClone(),
                ["topic"] = FunctionTopic(function)
            });

        return new JsonArray(functions.ToArray());
    }

    // The hint names the one call that lists stored Use functions; it appears only while no process has been selected.
    private JsonObject BrowseReply(string topic, JsonArray available)
    {
        var workflows = topic == "Use/Workflows" && _configuration.Workflow != null ? Workflows().Browse()["workflows"] : null;
        var reply = new JsonObject
        {
            ["success"] = true,
            ["workflows"] = workflows?.DeepClone(),
            ["topics"] = new JsonArray("Administration", "Use", "Info", "Programming"),
            ["functions"] = available,
            ["next_cursor"] = null
        };
        if (topic.Length == 0)
        {
            reply["stored_functions"] = CompactStoredFunctionListing.Sentence(_discovery);
        }
        // Built-in tools of the topic may still be listed; the hint says that none of the entries is a stored function.
        if (IsUseTopic(topic) && _discovery == CompactStoredFunctionDiscovery.NoneListed)
        {
            reply["hint"] = CompactStoredFunctionListing.NoStoredFunctionsSentence;
        }
        else if (_state.SelectedProcess == null && IsUseTopic(topic))
        {
            reply["hint"] = CompactStoredFunctionListing.SelectHint;
        }

        return reply;
    }

    // Throws CompactNodeException for unsafe stored definitions; a process with several main windows lists its program's functions.
    private IEnumerable<CompactLoadedFunction> SelectedFunctions(string topic)
    {
        if (_state.SelectedProcess == null || !IsUseTopic(topic))
        {
            return [];
        }
        var window = _state.ObservedWindows.FirstOrDefault(candidate => candidate.ProcessId == _state.SelectedProcess);
        if (window == null || !CompactGatewayLearning.TryIdentity(window, out _))
        {
            return [];
        }

        return new CompactGatewayPrograms(_roots).Read(SelectedIdentity())
            .Where(entry => CompactGatewayPrograms.IsDemonstrated(entry)
                && TopicIncludes(topic, CompactCaptureNames.Topic(entry.Draft.Topic)));
    }

    // A topic includes itself and its subtopics, so `Use` includes `Use/Editor` while `Use/Edit` does not include it.
    private static bool TopicIncludes(string topic, string candidate)
    {
        return candidate == topic || candidate.StartsWith(topic + "/", StringComparison.Ordinal);
    }

    // The Use category and each of its subtopics: the only topics whose stored functions depend on the selected process.
    private static bool IsUseTopic(string topic)
    {
        return topic == "Use" || topic.StartsWith("Use/", StringComparison.Ordinal);
    }

    // Throws CompactNodeException for an unavailable selected program/function.
    private JsonObject Descriptor(string name)
    {
        var descriptor = CompactGatewayFunctionTools.Find(name);
        if (descriptor != null)
        {
            return descriptor;
        }
        var identity = _state.SelectedProcess == null ? ListedIdentity(name) : SelectedIdentity();
        var entry = new CompactGatewayPrograms(_roots).Get(identity, name);

        return StoredDescriptor(entry.Draft);
    }

    // A browsed stored function carries its description, when stored, and its step labels, as its ui_info row does.
    private static JsonObject StoredDescriptor(CompactFunctionDraft draft)
    {
        var descriptor = new JsonObject { ["name"] = draft.Name };
        if (draft.Description != null)
        {
            descriptor["description"] = draft.Description;
        }
        descriptor["topic"] = CompactCaptureNames.Topic(draft.Topic);
        descriptor["steps"] = new JsonArray(draft.Steps.Select(step => (JsonNode?)CompactStepLabel.Of(step)).ToArray());
        descriptor["inputSchema"] = CompactFunctionCallSchema.Create(draft);

        return descriptor;
    }

    // The reply names the call for installed cartridge functions, whose parameters are the recipe's own and sit beside pid.
    public const string BundleUsageHint = "Run a listed installed-cartridge function with "
        + "cartridge_call {function, arguments: {pid, name: value}}; no cartridge_select is needed.";

    // Throws CompactNodeException when the ui_info rows name no program, or several programs, that list the function.
    // Processes of one program share its identity, so a listed name resolves to that program and selects nothing.
    private CompactProgramIdentity ListedIdentity(string name)
    {
        var programs = new CompactGatewayPrograms(_roots);
        var identities = _listedFunctions.Where(row => row.Value.Any(function => function.Name == name))
            .Select(row => programs.BoundIdentity(_state.ObservedWindows.First(window => window.Handle == row.Key)))
            .Distinct()
            .ToArray();
        if (identities.Length == 0)
        {
            throw new CompactNodeException(CompactStoredFunctionListing.SelectHint);
        }
        if (identities.Length > 1)
        {
            throw new CompactNodeException(CompactStoredFunctionListing.SeveralApplicationsRefusal);
        }

        return identities[0];
    }

    // Throws ArgumentException for unknown functions or invalid fields; CompactNodeException for unavailable native evidence.
    private async Task<JsonObject> CartridgeCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        if (function == GatewayCartridgeLibrary.SelectFunction)
        {
            return await SelectProgramAsync(arguments, cancellation);
        }
        if (GatewayNodeFunctions.TryTool(function, out Tool backup))
        {
            return await _client.CallAsync(backup, GatewayNodeFunctions.Arguments(backup, arguments), cancellation);
        }
        if (function == CompactApplicationMcpFunctions.SaveFunction)
        {
            return await _applicationMcpFunctions.SaveAsync(arguments, cancellation);
        }
        if (await _applicationMcpFunctions.TryCallAsync(function, arguments, cancellation) is { } applicationMcpRun)
        {
            return applicationMcpRun;
        }
        var descriptor = CompactGatewayFunctionTools.Find(function);
        if (descriptor == null)
        {
            return await NonBuiltInCallAsync(function, arguments, cancellation);
        }
        return await BuiltInCallAsync(function, descriptor, arguments, cancellation);
    }

    // A function that is not built in runs from an installed cartridge, resolved by the pid's window, or from the stored functions.
    // Throws ArgumentException for unknown functions or invalid fields; CompactNodeException for refused or unavailable evidence.
    private async Task<JsonObject> NonBuiltInCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        if (_nativeRoute == null)
        {
            return await ExecuteFunctionAsync(function, arguments, cancellation);
        }
        var window = ObservedWindowOf(arguments);
        if (window == null)
        {
            return _selectedProgram?.Native?.EntryPoints.Contains(function, StringComparer.Ordinal) == true
                ? await SelectedProgramCallAsync(function, arguments, cancellation)
                : await ExecuteFunctionAsync(function, arguments, cancellation);
        }
        var choice = new CompactInstalledCartridges(_roots.Library).Choose(window, function, _selectedProgram);
        if (choice.Outcome == CompactCartridgeOutcome.Several)
        {
            throw new CompactNodeException("Several cartridges offer " + function + "; select one with cartridge_select.");
        }
        if (choice.Outcome == CompactCartridgeOutcome.Unique)
        {
            return await InstalledCallAsync(choice.Program!, window.ProcessId, function, arguments, cancellation);
        }
        if (!StoredFunctionExists(window, function))
        {
            throw new CompactNodeException("No installed cartridge of this build offers " + function + ".");
        }

        return await ExecuteFunctionAsync(function, arguments, cancellation);
    }

    // The window of a pid the latest ui_info listed, or null when the call names no such pid.
    private CompactNativeWindow? ObservedWindowOf(JsonObject arguments)
    {
        if (arguments["pid"] is not JsonValue value || !value.TryGetValue<int>(out var process))
        {
            return null;
        }

        return _state.ObservedWindows.FirstOrDefault(window => window.ProcessId == process);
    }

    // An unreadable store counts as present, so that the stored-function route reports its own reason.
    private bool StoredFunctionExists(CompactNativeWindow window, string function)
    {
        try
        {
            var programs = new CompactGatewayPrograms(_roots);
            var identity = _selectedProgram?.Generated?.Application ?? programs.BoundIdentity(window);

            return programs.Read(identity).Any(stored => stored.Draft.Name == function);
        }
        catch (CompactNodeException)
        {
            return true;
        }
    }

    // The window's exact process name and build selected this cartridge, so the call needs neither cartridge_select nor cartridge_info.
    // Throws CompactNodeException when the cartridge cannot be bound to the pid; ArgumentException for refused native calls.
    private async Task<JsonObject> InstalledCallAsync(GatewayProgramCartridge program, int process, string function,
        JsonObject arguments, CancellationToken cancellation)
    {
        if (program.Generated is { Status: not CompactGeneratedCatalogStatus.Available })
        {
            throw new CompactNodeException("Selected extension is ambiguous, invalid or unsupported.");
        }
        await SelectNativeAsync(program, cancellation);
        if (_nativeProcess != process)
        {
            var info = await _nativeRoute!("cartridge_info", new JsonObject { ["pid"] = process }, cancellation);
            if (info["success"]?.GetValue<bool>() != true)
            {
                throw new CompactNodeException(info["error"]?.ToString() ?? "The cartridge could not be bound to the observed PID.");
            }
            _nativeProcess = process;
        }

        return await RouteNativeCallAsync(function, arguments, cancellation);
    }

    // The explicitly selected program serves a call without pid; the native route may have moved to another program since.
    private async Task<JsonObject> SelectedProgramCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        await SelectNativeAsync(_selectedProgram!, cancellation);
        // A program start drops the native pid binding, so the next call by pid binds again.
        _nativeProcess = null;

        return await RouteNativeCallAsync(function, arguments, cancellation);
    }

    // Selecting is skipped while the native route already holds the program, so its pid binding survives repeated calls.
    private async Task SelectNativeAsync(GatewayProgramCartridge program, CancellationToken cancellation)
    {
        if (_nativeProgramKey == program.Key)
        {
            return;
        }
        await _nativeRoute!("cartridge_call", new JsonObject
        {
            ["function"] = GatewayCartridgeLibrary.SelectFunction,
            ["arguments"] = new JsonObject { ["key"] = program.Key }
        }, cancellation);
        _nativeProgramKey = program.Key;
        _nativeProcess = null;
    }

    private Task<JsonObject> RouteNativeCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        return _nativeRoute!("cartridge_call", new JsonObject
        {
            ["function"] = function,
            ["arguments"] = arguments.DeepClone()
        }, cancellation);
    }

    // Throws ArgumentException for invalid fields; CompactNodeException for unavailable native evidence.
    private async Task<JsonObject> BuiltInCallAsync(string function, JsonObject descriptor, JsonObject arguments,
        CancellationToken cancellation)
    {
        if (function.StartsWith("form.", StringComparison.Ordinal))
        {
            return await FormCallAsync(function, arguments, cancellation);
        }
        if (function is "Info.compatibility" or "Programming.revalidate")
        {
            var compatibility = new CompactGatewayCompatibility(new(_client, _state.Node!), _observation, _state,
                new(_roots), SelectedIdentity, _protocol, _configuration.Compatibility);
            return await compatibility.CallAsync(function, arguments, cancellation);
        }
        if (function.StartsWith("workflow.", StringComparison.Ordinal)
            || function.StartsWith("Programming.workflow_", StringComparison.Ordinal))
        {
            return await Workflows().CallAsync(function, arguments, cancellation);
        }
        if (function == "Programming.learn" && !CompactCaptureCodec.HasCurrentEvidence(arguments, _state.Demonstration))
        {
            throw new CompactNodeException(CompactCaptureNames.Error(CompactCaptureError.EvidenceMissingOrUnknown));
        }
        ToolContract.Validate(descriptor["inputSchema"]!.AsObject(), arguments);
        if (function == "Info.result")
        {
            return ReadResult(arguments);
        }
        if (function == "Info.form")
        {
            return ReadForm(arguments);
        }
        if (function is "Programming.learn" or "Programming.learn_read")
        {
            return await LearnAsync(function, arguments, cancellation);
        }
        if (function == "Info.preflight")
        {
            return await PreflightAsync(arguments, cancellation);
        }

        return await ScanAsync(arguments, cancellation);
    }

    // Throws CompactNodeException for missing demonstration or unverified native access; ArgumentException for invalid capture inputs.
    private async Task<JsonObject> LearnAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        _state.RequireObserved(function == "Programming.learn_read" ? arguments["pid"]!.GetValue<int>()
            : _state.Demonstration?.ProcessId ?? 0);
        var learning = new CompactGatewayLearning(new(_client, _state.Node!), _observation, _state,
            new(_roots), _protocol);

        var reply = function == "Programming.learn_read" ? await learning.ReadAsync(arguments, cancellation)
            : await learning.LearnAsync(arguments, cancellation);
        RecordWorkflowCapture(reply["function"]!.ToString(), reply);

        return reply;
    }

    // Throws CompactNodeException when no actual node has been observed before form access.
    private async Task<JsonObject> FormCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        if (_state.Node == null)
        {
            throw new CompactNodeException("Begin with ui_info before form operations.");
        }
        var node = new CompactNodeAccess(_client, _state.Node);
        _forms ??= new(new(node, _observation), _observation, _state);

        return await _forms.CallAsync(function, arguments, cancellation);
    }

    // Throws ArgumentException for invalid catalog keys; CompactNodeException for uncallable generated entries.
    private async Task<JsonObject> SelectProgramAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (arguments.Count != 1 || arguments["key"] is not JsonValue value || !value.TryGetValue<string>(out var key))
        {
            throw new ArgumentException("cartridge_select requires one exact catalog key.");
        }
        var program = new GatewayProgramCatalog(_roots.Library).Resolve(key);
        if (program.Generated is { Status: not CompactGeneratedCatalogStatus.Available })
        {
            throw new CompactNodeException("Selected extension is ambiguous, invalid or unsupported.");
        }
        if (program.Native != null && _nativeRoute != null)
        {
            await _nativeRoute("cartridge_call", new JsonObject
            {
                ["function"] = GatewayCartridgeLibrary.SelectFunction,
                ["arguments"] = arguments.DeepClone()
            }, cancellation);
            _nativeProgramKey = program.Key;
            _nativeProcess = null;
        }
        _selectedProgram = program;
        _state.SelectedProcess = null;
        _state.SelectedWindow = null;
        _state.Demonstration = null;
        _forms?.Clear();

        return new JsonObject
        {
            ["success"] = true,
            ["selected"] = program.Key,
            ["application"] = program.Application,
            ["next"] = "cartridge_info"
        };
    }

    // Throws CompactNodeException for absent/ambiguous native instance binding; generated program Info retains actual metadata.
    private async Task<JsonObject> CartridgeInfoAsync(CancellationToken cancellation)
    {
        var info = await CartridgeInfoReplyAsync(cancellation);
        info["stored_functions"] = CompactStoredFunctionListing.Sentence(_discovery);

        return info;
    }

    // Throws CompactNodeException for absent/ambiguous native instance binding; generated program Info retains actual metadata.
    private async Task<JsonObject> CartridgeInfoReplyAsync(CancellationToken cancellation)
    {
        if (_selectedProgram?.Native != null && _nativeRoute != null)
        {
            var arguments = new JsonObject();
            if (_state.SelectedProcess != null)
            {
                arguments["pid"] = _state.SelectedProcess;
            }
            await SelectNativeAsync(_selectedProgram, cancellation);
            _nativeProcess = null;

            return await _nativeRoute("cartridge_info", arguments, cancellation);
        }

        var info = await _observation.InfoAsync(cancellation);
        await ListFunctionsAsync(info, cancellation);
        if (_selectedProgram == null)
        {
            return info;
        }
        var windows = _state.ObservedWindows.Where(window => window.ProcessName == _selectedProgram.ProcessName
            && window.Build == _selectedProgram.Identity.Build).ToArray();
        var processes = windows.Select(window => window.ProcessId).Distinct().ToArray();
        if (processes.Length != 1)
        {
            throw new CompactNodeException("Selected application instance is absent or ambiguous; observe one matching PID.");
        }
        _state.SelectedProcess = processes[0];
        info["selected"] = _selectedProgram.Key;
        info["application"] = _selectedProgram.Application;
        info["build"] = _selectedProgram.Identity.Build;
        info["pid"] = processes[0];

        return info;
    }

    // Throws CompactNodeException for missing native selection, without guessing a variant/build.
    private CompactProgramIdentity SelectedIdentity()
    {
        var window = CompactGatewayLearning.RequireProcessInstance(_state.ObservedWindows, _state.SelectedProcess);
        RequireSelected(window);

        return _selectedProgram?.Generated?.Application ?? new CompactGatewayPrograms(_roots).BoundIdentity(window);
    }

    private static string FunctionTopic(JsonObject function)
    {
        string name = function["name"]!.ToString();

        return function["topic"]?.ToString() ?? (name.StartsWith("Programming.", StringComparison.Ordinal)
            ? "Programming/Learning" : "Info");
    }

    // Throws CompactNodeException for unproved actual selection, compatibility or semantic targets; ArgumentException for invalid inputs.
    private async Task<CompactFunctionPreflight> FunctionPreflightAsync(string name, JsonObject arguments,
        CompactWindowSelection selection, CancellationToken cancellation)
    {
        var current = await _observation.RequireInstanceAsync(selection.ProcessId, cancellation);
        if (!CompactWindowChoice.TryProcessInstance(current, selection, out var instance))
        {
            throw new CompactNodeException(CompactWindowChoice.UnknownHandleRefusal);
        }
        RequireSelected(instance);
        var identity = _selectedProgram?.Generated?.Application ?? new CompactGatewayPrograms(_roots).BoundIdentity(instance);
        var function = new CompactGatewayPrograms(_roots).Get(identity, name);
        var executor = new CompactFunctionExecutor(new(_client, _state.Node!), CompactCompatibilityWire.Wire(_protocol()),
            CompactGatewayPrograms.Implementation());
        CompactNativeWindow? named = arguments["window"] == null ? null : instance;
        var prepared = await executor.PreflightAsync(function, identity, instance, named, cancellation);
        // A call refused by its window binding or its targets selects nothing; only a fully prepared call selects its process.
        _state.SelectedProcess = selection.ProcessId;

        return prepared;
    }

    // Throws CompactNodeException when an observed PID belongs to a different selected catalog variant/build.
    private void RequireSelected(CompactNativeWindow window)
    {
        if (_selectedProgram != null && (window.ProcessName != _selectedProgram.ProcessName
            || window.Build != _selectedProgram.Identity.Build))
        {
            throw new CompactNodeException("Observed PID does not match the selected catalog variant/build; select and refresh Info.");
        }
    }

    // Throws CompactNodeException for unavailable selected-function prerequisites.
    private async Task<JsonObject> PreflightAsync(JsonObject arguments, CancellationToken cancellation)
    {
        string function = arguments["function"]!.GetValue<string>();
        var selection = _state.SelectWindow(arguments);
        var prepared = await FunctionPreflightAsync(function, arguments, selection, cancellation);

        return new JsonObject { ["success"] = true, ["ready"] = true, ["function"] = prepared.Function.Draft.Name, ["input_sent"] = false };
    }

    // Throws CompactNodeException for stale/unknown recipes or inputs; ArgumentException for invalid requested function arguments.
    private async Task<JsonObject> ExecuteFunctionAsync(string name, JsonObject arguments, CancellationToken cancellation)
    {
        var selection = _state.SelectWindow(arguments, _state.SelectedProcess);
        var prepared = await FunctionPreflightAsync(name, arguments, selection, cancellation);
        ToolContract.Validate(CompactFunctionCallSchema.Invocation(prepared.Function.Draft), arguments);
        var names = prepared.Function.Draft.Outputs.Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
        if (!CompactResultOptionsCodec.TryParse(arguments, names, out var options, out var error))
        {
            throw new ArgumentException(error);
        }
        // A function that declares no parameters may omit the parameters object; the schema already requires it otherwise.
        var supplied = arguments["parameters"] as JsonObject ?? new JsonObject();
        var parameters = supplied.ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<string>(), StringComparer.Ordinal);
        var executor = new CompactFunctionExecutor(new(_client, _state.Node!), CompactCompatibilityWire.Wire(_protocol()),
            CompactGatewayPrograms.Implementation());

        return await RunStoredFunctionAsync(name, prepared, executor, parameters, options!, cancellation);
    }

    // The reply names the steps, reports the verified outputs and returns the window's change since the call began. Afterwards only the
    // references the reply showed stay usable, and the next observation of this process is a full view.
    private async Task<JsonObject> RunStoredFunctionAsync(string name, CompactFunctionPreflight prepared, CompactFunctionExecutor executor,
        IReadOnlyDictionary<string, string> parameters, CompactResultOptions options, CancellationToken cancellation)
    {
        var visible = _state.Projection.ExposedReferences;
        JsonObject? reply = null;
        try
        {
            var run = await ExecuteWithChangesAsync(prepared, executor, parameters, cancellation);
            reply = FunctionReply(name, prepared, run, options);

            return reply;
        }
        finally
        {
            HideAfterFunction(visible, prepared.Instance.ProcessId, reply);
        }
    }

    // Throws CompactNodeException for a window that cannot be recorded before input, and for a step or output that is not confirmed.
    private async Task<CompactStoredRun> ExecuteWithChangesAsync(CompactFunctionPreflight prepared, CompactFunctionExecutor executor,
        IReadOnlyDictionary<string, string> parameters, CancellationToken cancellation)
    {
        int process = prepared.Instance.ProcessId;
        long handle = prepared.Instance.Handle;
        string baseline = await _observation.RecordBaselineAsync(process, handle, cancellation);
        var result = await executor.ExecuteAsync(prepared, parameters, null, null, cancellation);
        try
        {
            return new(result, await _observation.ChangesAsync(process, handle, baseline, cancellation));
        }
        catch (CompactNodeException exception)
        {
            return new(result, null, exception.Message);
        }
    }

    // Steps are named from stored data only; the change list follows the result mode exactly as a ui_act reply does.
    private JsonObject FunctionReply(string name, CompactFunctionPreflight prepared, CompactStoredRun run, CompactResultOptions options)
    {
        var response = new JsonObject
        {
            ["success"] = true,
            ["function"] = name,
            ["completed_steps"] = prepared.Function.Draft.Steps.Count,
            ["verified"] = true,
            ["outputs"] = new JsonObject(run.Result.Outputs.Select(entry =>
                new KeyValuePair<string, JsonNode?>(entry.Key, JsonValue.Create(entry.Value)))),
            ["steps"] = new JsonArray(prepared.Function.Draft.Steps.Select(step => (JsonNode?)JsonValue.Create(CompactStepLabel.Of(step)))
                .ToArray())
        };
        if (run.Changes != null)
        {
            response["ui"] = run.Changes;
        }
        else
        {
            response["observation_required"] = true;
            response["observation_reason"] = "changes_unreadable";
            response["ui_error"] = run.ChangesError;
        }
        var checks = run.Result.Outputs.Keys.ToDictionary(output => output, output => new CompactOutputCheck(true));

        return new CompactResultFormatter(_state.Results).Format(_state.Authority, response, options, checks);
    }

    // The window is then observed again by the model's next request, so the references it was not sent are withdrawn.
    private void HideAfterFunction(IReadOnlySet<string> visible, int process, JsonObject? reply)
    {
        var shown = new HashSet<string>(visible, StringComparer.Ordinal);
        if (reply?["ui"] is JsonObject changes)
        {
            shown.UnionWith(CompactViewCodec.ShownReferences(changes));
        }
        _state.Projection.HideUnseen(shown, process);
    }

    // Throws ArgumentException for invalid or unavailable session-owned evidence references/pages.
    private JsonObject ReadResult(JsonObject arguments)
    {
        if (!CompactResultQueryCodec.TryParse(arguments, out var query)
            || !_state.Results.TryRead(_state.Authority, query.Value!, out var page))
        {
            throw new ArgumentException("Invalid or unavailable result evidence/page.");
        }
        var value = page.Value!;

        return new JsonObject
        {
            ["text"] = value.Text,
            ["offset"] = value.Offset,
            ["next_offset"] = value.NextOffset,
            ["complete"] = value.Complete,
            ["total_chars"] = value.TotalCharacters
        };
    }

    // Throws ArgumentException for invalid scan fields or unsupported scope; CompactNodeException for a replaced process.
    private async Task<JsonObject> ScanAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactDiscoveryCodec.TryScan(arguments, out var parsed))
        {
            throw new ArgumentException(CompactDiscoveryNames.Error(parsed.Error!.Value));
        }
        var options = parsed.Value!;
        var windows = await _observation.RequireInstanceAsync(options.ProcessId, cancellation);
        var selection = _state.SelectWindow(arguments);
        if (!CompactWindowChoice.TryChoose(windows, selection, out var observed))
        {
            throw new ArgumentException(selection.Handle is null ? CompactWindowChoice.AmbiguousRefusal
                : CompactWindowChoice.UnknownHandleRefusal);
        }
        var node = new CompactNodeAccess(_client, _state.Node!);
        var tree = await node.ObserveAsync(new CompactWindowSelection(options.ProcessId, observed.Handle), false, cancellation);
        await _observation.RequireInstanceAsync(options.ProcessId, cancellation);
        var scanner = Scanner(observed);
        string build = observed.Build;
        var compatibility = new CompactScanCompatibility(_state.Version ?? "unknown", _state.Platform ?? "unknown", build);
        if (!scanner.TryScan(tree.Nodes, options, observed, compatibility, out var result))
        {
            throw new ArgumentException(CompactDiscoveryNames.Error(result.Error!.Value));
        }
        _state.SelectedProcess = options.ProcessId;
        JsonObject response = CompactDiscoveryWire.Scan(result.Value!);
        response["success"] = true;
        response["persisted"] = scanner.IsPersistent;

        return new CompactResultFormatter(_state.Results).Format(_state.Authority, response, options.Result,
            scan: new(result.Value!.Observation.Fields.Count, result.Value.Observation.Contents?.Fields.Count ?? 0));
    }

    // Throws ArgumentException for unknown forms; only this selected process's observed structural material is exposed.
    private JsonObject ReadForm(JsonObject arguments)
    {
        if (!CompactDiscoveryCodec.TryLookup(arguments, out var lookup) || _state.SelectedProcess == null)
        {
            throw new ArgumentException("Unknown form state in this selected application session.");
        }
        var window = _state.ObservedWindows.First(candidate => candidate.ProcessId == _state.SelectedProcess);
        if (!Scanner(window).TryRead(lookup.Value!, out var read))
        {
            throw new ArgumentException(CompactDiscoveryNames.Error(read.Error!.Value));
        }

        return read.Value!;
    }

    // Throws CompactNodeException for unsafe persistent structure state; unknown versions retain isolated session-only discovery.
    private CompactFormScanner Scanner(CompactNativeWindow window)
    {
        var identity = new CompactProgramIdentity(window.ProcessName, window.Build);
        string key = CompactFormFields.Fingerprint(new JsonObject
        {
            ["application"] = identity.Key,
            ["build"] = identity.Build,
            ["instance"] = identity.Build == "unknown" ? window.Started : null
        });
        if (_scanners.TryGetValue(key, out var existing))
        {
            return existing;
        }
        CompactObservedFormStore? store = null;
        if (_roots.Learning != null && identity.Build != "unknown" && identity.Key != "unknown")
        {
            var directory = new CompactGatewayPrograms(_roots).Directories(identity).Learning!;
            if (!CompactObservedFormStore.TryOpen(directory, identity, out var opened))
            {
                throw new CompactNodeException(CompactDiscoveryNames.Error(opened.Error!.Value));
            }
            store = opened.Value;
        }
        var scanner = new CompactFormScanner(store);
        _scanners.Add(key, scanner);

        return scanner;
    }

    public void Dispose()
    {
        _forms?.Clear();
        _workflows?.Clear();
        _serial.Dispose();
    }
}
