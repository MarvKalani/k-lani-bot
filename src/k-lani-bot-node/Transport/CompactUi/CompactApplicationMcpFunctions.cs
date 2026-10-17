using System.Globalization;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: the compact gateway session hands everything about stored app MCP functions to this class: the ui_info rows of
// functions verified on the version the node runs, the drift rows of other versions, app_mcp_save after a verified test run, and
// cartridge_call of a stored function by name with {program, parameters}. One instance lives as long as the gateway session.
public sealed class CompactApplicationMcpFunctions(ICompactNodeClient client, string libraryRoot)
{
    public const string SaveFunction = "app_mcp_save";

    // Agents call the ui_info field name as a function, so it answers with the same rows for one program.
    public const string ListFunction = CompactApplicationMcpFunctionNames.FunctionsField;

    // cartridge_info and cartridge_browse add this after a ui_info that listed app MCP functions, so no reply says there are none.
    public const string ListedSentence = "Stored app MCP functions are listed in ui_info app_mcp_functions; run one with "
        + "cartridge_call {function, arguments: {program, parameters: {name: value}}}.";
    private readonly CompactApplicationMcpStore _store = new(libraryRoot);
    private readonly CompactApplicationMcpRunner _runner = new(client);
    private readonly CompactApplicationMcpDrift _drift = new(client);
    private readonly Dictionary<string, CompactApplicationMcpProbe> _probes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompactApplicationMcpDocument> _listed = new(StringComparer.Ordinal);

    public bool FunctionsListed
    {
        get; private set;
    }

    public bool DriftListed
    {
        get; private set;
    }

    // Adds both rows for the programs ui_info names in app_mcps. A program without stored functions is not asked anything.
    public async Task AddToInfoAsync(JsonObject info, CancellationToken cancellation)
    {
        FunctionsListed = false;
        DriftListed = false;
        _listed.Clear();
        var functionRows = new JsonArray();
        var driftRows = new JsonArray();
        foreach (string program in Programs(info))
        {
            var documents = ReadAll(program);
            if (documents.Count > 0)
            {
                await AddProgramAsync(program, documents, functionRows, driftRows, cancellation);
            }
        }

        AddRows(info, CompactApplicationMcpFunctionNames.FunctionsField, functionRows);
        AddRows(info, CompactApplicationMcpFunctionNames.DriftField, driftRows);
        FunctionsListed = functionRows.Count > 0;
        DriftListed = driftRows.Count > 0;
    }

    // Throws what the node connection throws for cancellation or a broken transport.
    public async Task<JsonObject> ListAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactDiscoveryDocumentCodec.Text(arguments, "program", out string program)
            || !CompactApplicationMcpFunctionCodec.ValidProgram(program))
        {
            return CompactApplicationMcpFunctionNames.Refusal(CompactApplicationMcpFailure.InvalidParameters,
                "app_mcp_functions takes {program}.");
        }

        var reply = new JsonObject { ["success"] = true, [CompactApplicationMcpNames.Field] = new JsonArray(program) };
        await AddToInfoAsync(reply, cancellation);
        reply.Remove(CompactApplicationMcpNames.Field);
        reply["next"] = CompactApplicationMcpFunctionNames.FunctionsHint;
        return reply;
    }

    // Throws what the node connection throws for cancellation or a broken transport.
    public async Task<JsonObject> SaveAsync(JsonObject arguments, CancellationToken cancellation)
    {
        if (!CompactDiscoveryDocumentCodec.Text(arguments, "program", out string program)
            || !CompactApplicationMcpFunctionCodec.ValidProgram(program)
            || arguments["test"] is not null and not JsonObject)
        {
            return CompactApplicationMcpFunctionNames.Refusal(CompactApplicationMcpFailure.InvalidDefinition,
                "app_mcp_save takes {program, name, description, parameters, steps, checks, test} or {program, name, from_version, test}.");
        }
        if (!TryDefinition(program, arguments, out var definition))
        {
            return CompactApplicationMcpFunctionNames.Refusal(definition.Failure!.Value, definition.Detail);
        }
        if (IsRoutedElsewhere(definition.Value!.Name))
        {
            return CompactApplicationMcpFunctionNames.Refusal(CompactApplicationMcpFailure.InvalidDefinition,
                "'" + definition.Value.Name + "' is a gateway function name; choose another name.");
        }

        var run = await _runner.RunAsync(program, null, definition.Value!, arguments["test"] as JsonObject, null, cancellation);
        Remember(program, run);
        if (!run.Verified)
        {
            return CompactApplicationMcpFunctionNames.Reply(definition.Value!.Name, run);
        }
        return Store(program, run, definition.Value!);
    }

    // Null unless the call has exactly {program, parameters} and some version of that program stores the name, so an installed
    // cartridge or stored UI function whose own parameters include "program" keeps its route.
    // Throws what the node connection throws for cancellation or a broken transport.
    public async Task<JsonObject?> TryCallAsync(string function, JsonObject arguments, CancellationToken cancellation)
    {
        bool shaped = CompactDiscoveryDocumentCodec.Text(arguments, "program", out string program)
            && CompactApplicationMcpFunctionCodec.ValidProgram(program)
            && arguments.All(entry => entry.Key is "program" or "parameters") && arguments["parameters"] is null or JsonObject;
        if (!shaped || !ReadAll(program).Any(document => document.Functions.Any(stored => stored.Name == function)))
        {
            return null;
        }

        CompactApplicationMcpProbe probe = await ProbeAsync(program, cancellation);
        CompactApplicationMcpFunction? stored = Find(program, probe.Version, function);
        if (stored == null)
        {
            return CompactApplicationMcpFunctionNames.Refusal(CompactApplicationMcpFailure.UnknownFunction, "No function '" + function
                + "' is stored for " + program + " " + (probe.Version ?? "(version unreadable)") + "; see ui_info app_mcp_functions.");
        }
        var run = await _runner.RunAsync(program, probe.Version, stored, arguments["parameters"] as JsonObject, probe, cancellation);
        Remember(program, run);
        return CompactApplicationMcpFunctionNames.Reply(function, run);
    }

    // An agent told about app MCPs lists the program's tools first, so that listing names the stored functions of its version too.
    public void AddToListing(JsonObject arguments, JsonObject listing)
    {
        bool toolListing = !arguments.ContainsKey("tool") && !arguments.ContainsKey("schema") && !arguments.ContainsKey("file");
        if (!toolListing || listing["success"]?.GetValue<bool>() != true
            || !CompactDiscoveryDocumentCodec.Text(arguments, "program", out string program)
            || listing["version"]?.ToString() is not { } version || Find(program, version) is not { Functions.Count: > 0 } document)
        {
            return;
        }

        listing[CompactApplicationMcpFunctionNames.ListingField] = new JsonArray(document.Functions
            .Take(CompactApplicationMcpFunctionNames.FunctionLimit)
            .Select(function => (JsonNode?)CompactApplicationMcpFunctionNames.Entry(function)).ToArray());
        listing["next"] = CompactApplicationMcpFunctionNames.ListingHint;
        _listed[program] = document;
    }

    // The browse descriptor of a function the latest ui_info listed, or null; a name several programs list is not described.
    public JsonObject? Describe(string name)
    {
        var matches = _listed.Values.SelectMany(document => document.Functions.Where(function => function.Name == name)
            .Select(function => new CompactApplicationMcpListedFunction(document, function))).ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        return CompactApplicationMcpFunctionNames.Descriptor(matches[0].Document, matches[0].Function);
    }

    private async Task AddProgramAsync(string program, IReadOnlyList<CompactApplicationMcpDocument> documents, JsonArray functionRows,
        JsonArray driftRows, CancellationToken cancellation)
    {
        if (!_probes.TryGetValue(program, out CompactApplicationMcpProbe? probe))
        {
            probe = await ProbeAsync(program, cancellation);
        }
        CompactApplicationMcpDocument? current = documents.FirstOrDefault(document => document.Version == probe.Version);
        if (current is { Functions.Count: > 0 })
        {
            functionRows.Add(FunctionRow(current));
            _listed[program] = current;
        }
        var currentNames = (current?.Functions ?? []).Select(function => function.Name).ToHashSet(StringComparer.Ordinal);
        foreach (CompactApplicationMcpDocument other in documents.Where(document => document.Version != probe.Version))
        {
            var report = await _drift.AssessAsync(other, probe, currentNames, cancellation);
            if (report.Functions.Count > 0)
            {
                driftRows.Add(CompactApplicationMcpFunctionNames.DriftRow(report));
            }
        }
    }

    // cartridge_call routes these names before stored app MCP functions, so a function saved under one could never run.
    private static bool IsRoutedElsewhere(string name)
    {
        return name is SaveFunction or ListFunction || name == GatewayCartridgeLibrary.SelectFunction
            || GatewayNodeFunctions.TryTool(name, out _) || CompactGatewayFunctionTools.Find(name) != null;
    }

    private bool TryDefinition(string program, JsonObject arguments,
        out CompactApplicationMcpAttempt<CompactApplicationMcpFunction> attempt)
    {
        string verifiedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (!arguments.ContainsKey("from_version"))
        {
            attempt = new(null, CompactApplicationMcpFailure.InvalidDefinition, "Unknown field; see the app_mcp_save fields.");
            return CompactDiscoveryDocumentCodec.Only(arguments, "program", "name", "description", "parameters", "steps", "checks", "test")
                && CompactApplicationMcpFunctionCodec.TryDefinition(arguments, verifiedAt, out attempt);
        }
        attempt = new(null, CompactApplicationMcpFailure.UnknownFunction, "from_version names no stored function of that name.");
        if (!CompactDiscoveryDocumentCodec.Only(arguments, "program", "name", "from_version", "test")
            || !CompactDiscoveryDocumentCodec.Text(arguments, "name", out string name)
            || !CompactDiscoveryDocumentCodec.Text(arguments, "from_version", out string version)
            || !_store.TryRead(program, version, out var document))
        {
            return false;
        }
        CompactApplicationMcpFunction? source = document.Value?.Functions.FirstOrDefault(function => function.Name == name);
        if (source == null)
        {
            return false;
        }
        attempt = new(source with
        {
            VerifiedAt = verifiedAt
        });

        return true;
    }

    private JsonObject Store(string program, CompactApplicationMcpRun run, CompactApplicationMcpFunction function)
    {
        if (!_store.TrySave(program, run.Version!, function, out var saved))
        {
            var reply = CompactApplicationMcpFunctionNames.Reply(function.Name, run);
            reply["saved"] = false;
            reply["error"] = saved.Detail;
            return reply;
        }

        var stored = CompactApplicationMcpFunctionNames.Reply(function.Name, run);
        stored["saved"] = true;
        stored["revision"] = saved.Value!.Revision;
        return stored;
    }

    private CompactApplicationMcpFunction? Find(string program, string? version, string function)
    {
        return Find(program, version)?.Functions.FirstOrDefault(stored => stored.Name == function);
    }

    // The stored document of this program version; null for an unknown version, none stored or an unreadable store.
    private CompactApplicationMcpDocument? Find(string program, string? version)
    {
        if (!CompactApplicationMcpFunctionCodec.ValidProgram(program) || !CompactApplicationMcpFunctionCodec.KnownVersion(version)
            || !_store.TryRead(program, version!, out var document))
        {
            return null;
        }

        return document.Value;
    }

    private async Task<CompactApplicationMcpProbe> ProbeAsync(string program, CancellationToken cancellation)
    {
        CompactApplicationMcpProbe probe = await _runner.ProbeAsync(program, cancellation);

        _probes[program] = probe;
        return probe;
    }

    // A run reports the version the program answered with; the next ui_info lists by that version without asking again.
    private void Remember(string program, CompactApplicationMcpRun run)
    {
        if (run.Version != null && _probes.TryGetValue(program, out CompactApplicationMcpProbe? probe) && probe.Version != run.Version)
        {
            _probes.Remove(program);
        }
    }

    // An unreadable library lists nothing instead of failing ui_info.
    private IReadOnlyList<CompactApplicationMcpDocument> ReadAll(string program)
    {
        try
        {
            return _store.ReadAll(program);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static JsonObject FunctionRow(CompactApplicationMcpDocument document)
    {
        var row = new JsonObject
        {
            ["program"] = document.Program,
            ["version"] = document.Version,
            ["functions"] = new JsonArray(document.Functions.Take(CompactApplicationMcpFunctionNames.FunctionLimit)
                .Select(function => (JsonNode?)CompactApplicationMcpFunctionNames.Entry(function)).ToArray())
        };
        int more = document.Functions.Count - CompactApplicationMcpFunctionNames.FunctionLimit;
        if (more > 0)
        {
            row["more_functions"] = more;
        }

        return row;
    }

    private static IEnumerable<string> Programs(JsonObject info)
    {
        return (info[CompactApplicationMcpNames.Field] as JsonArray ?? []).Select(program => program?.ToString()).OfType<string>()
            .Where(CompactApplicationMcpFunctionCodec.ValidProgram);
    }

    private static void AddRows(JsonObject info, string field, JsonArray rows)
    {
        if (rows.Count > 0)
        {
            info[field] = rows;
        }
    }
}

// A function of the latest ui_info listing together with the program version document it belongs to.
internal sealed record CompactApplicationMcpListedFunction(CompactApplicationMcpDocument Document, CompactApplicationMcpFunction Function);
