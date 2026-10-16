using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_info calls this once per observation so a model can run a stored Use function without first browsing for it.
// A window row names only the functions that cartridge_call would run now; an unreadable store or unproved compatibility names none.
public sealed class CompactStoredFunctionListing(CompactNodeAccess node, CompactGatewayPrograms programs,
    CompactMcpWireVersion wire, CompactContractDigest gatewayImplementation)
{
    // The reply sends this after the rows, so the model knows the one call that runs a listed function; the JS gateway matches it.
    public const string UsageHint = "Run a listed function with cartridge_call {function, arguments: {pid, parameters: {name: value}}}; "
        + "a reply with verified true is final.";

    // A Use browse before any process was selected cannot know which program's functions apply; the JS gateway sends the same words.
    public const string SelectHint = "Call ui_info first; each window row lists its stored functions with their parameters.";

    // cartridge_info and a browse without a topic name where stored functions are listed, so the model does not look for them elsewhere.
    public const string StoredFunctionsSentence = "Listed per window in ui_info.";

    // After a ui_info that listed no function, the model is sent to the state route instead of searching for a list that is empty.
    public const string NoStoredFunctionsSentence = "No stored functions for the current windows; "
        + "read a window with ui_observe {pid, as: \"state\"} and change it with ui_apply.";

    // The words that tell the model where stored functions are, for the latest ui_info; the JS gateway chooses the same words.
    public static string Sentence(CompactStoredFunctionDiscovery discovery)
    {
        switch (discovery)
        {
            case CompactStoredFunctionDiscovery.NotObserved:
                return SelectHint;
            case CompactStoredFunctionDiscovery.NoneListed:
                return NoStoredFunctionsSentence;
            case CompactStoredFunctionDiscovery.Listed:
                return StoredFunctionsSentence;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    // The same words as the JavaScript gateway; a name that several applications list needs a pid or window.
    public const string SeveralApplicationsRefusal = "Several applications list this function; pass pid or window from ui_info.";

    // Throws only for cancellation; an unreadable store or a node that cannot prove compatibility leaves its rows without functions.
    // Every runnable name is kept, generated ones included, so that a name resolves through the listing; the rows that the model reads
    // are summarized separately.
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CompactListedFunction>>> ListAsync(IReadOnlyList<CompactNativeWindow> windows,
        GatewayProgramCartridge? selected, CancellationToken cancellation)
    {
        var rows = new Dictionary<long, IReadOnlyList<CompactListedFunction>>();
        var functionsByProgram = new Dictionary<CompactProgramIdentity, IReadOnlyList<CompactListedFunction>>();
        foreach (var window in windows)
        {
            if (!CompactGatewayLearning.TryIdentity(window, out var observed) || !BelongsTo(window, selected))
            {
                continue;
            }
            if (!functionsByProgram.TryGetValue(observed, out var functions))
            {
                functions = await RunnableFunctionsAsync(window, selected, cancellation);
                functionsByProgram.Add(observed, functions);
            }
            if (functions.Count > 0)
            {
                rows.Add(window.Handle, functions);
            }
        }

        return rows;
    }

    // With a selected catalog program, only that program's windows can run its functions, as cartridge_call requires.
    private static bool BelongsTo(CompactNativeWindow window, GatewayProgramCartridge? selected)
    {
        return selected == null || window.ProcessName == selected.ProcessName && window.Build == selected.Identity.Build;
    }

    // Functions come only from demonstrated Use functions, the same test Get applies, so no listed function is refused for validation.
    private async Task<IReadOnlyList<CompactListedFunction>> RunnableFunctionsAsync(CompactNativeWindow window,
        GatewayProgramCartridge? selected, CancellationToken cancellation)
    {
        CompactProgramIdentity identity;
        IReadOnlyList<CompactLoadedFunction> functions;
        try
        {
            identity = selected?.Generated?.Application ?? programs.BoundIdentity(window);
            functions = programs.Read(identity).Where(function => CompactGatewayPrograms.IsDemonstrated(function)
                && function.Draft.Topic.Category == CompactTopicCategory.Use).ToArray();
        }
        catch (Exception exception) when (Unreadable(exception))
        {
            return [];
        }
        if (functions.Count == 0)
        {
            return [];
        }

        return await CompatibleFunctionsAsync(identity, functions, cancellation);
    }

    // The node proves the actions of every listed function at once; a function it cannot prove compatible is not listed.
    private async Task<IReadOnlyList<CompactListedFunction>> CompatibleFunctionsAsync(CompactProgramIdentity identity,
        IReadOnlyList<CompactLoadedFunction> functions, CancellationToken cancellation)
    {
        var actions = functions.SelectMany(function => CompactContractHash.RequiredActions(function.Draft)).Distinct().ToArray();
        CompactNativeCapabilities capabilities;
        try
        {
            capabilities = await node.CapabilitiesAsync(actions, cancellation);
        }
        catch (CompactNodeException)
        {
            return [];
        }
        var observed = new CompactObservedCompatibility(capabilities.Version, wire, capabilities.Platform, identity,
            capabilities.Actions, capabilities.Implementation, gatewayImplementation);

        return functions.Where(function => CompactCompatibility.Assess(function.Draft, function.Compatibility, observed).Status
            == CompactCompatibilityStatus.Reusable)
            .Select(function => CompactListedFunction.Of(function.Draft, DeclaredParameters(function.Draft))).ToArray();
    }

    // The parameter names the call schema accepts, in the order the schema declares them, so a row can be run without a browse.
    private static IReadOnlyList<string> DeclaredParameters(CompactFunctionDraft draft)
    {
        var schema = CompactCaptureWire.InputSchema(draft);
        if (schema["properties"]?["parameters"]?["properties"] is not JsonObject parameters)
        {
            return [];
        }

        return parameters.Select(parameter => parameter.Key).ToArray();
    }

    // Each reader reports damage with one of these exceptions; the listing treats the damage as no functions, not a failed ui_info.
    private static bool Unreadable(Exception exception)
    {
        return exception is CompactNodeException or InvalidDataException or JsonException or IOException;
    }
}

// What the latest ui_info showed of stored functions; cartridge_info and cartridge_browse word their pointer to the list by it.
public enum CompactStoredFunctionDiscovery
{
    NotObserved,
    NoneListed,
    Listed
}

// One stored function as a window row names it: its name, its stored description, its parameter names in the order the schema
// declares them, and the labels of its steps, so a reusing model sees what the function does before it trusts it.
public sealed record CompactListedFunction(string Name, string? Description, IReadOnlyList<string> Parameters,
    IReadOnlyList<string> Steps)
{
    // A stored function holds at most this many steps; the listing keeps the same bound so a row stays small.
    public const int StepLimit = CompactCaptureCodec.MaximumSteps;

    public static CompactListedFunction Of(CompactFunctionDraft draft, IReadOnlyList<string> parameters)
    {
        return new(draft.Name, draft.Description, parameters, draft.Steps.Select(CompactStepLabel.Of).ToArray());
    }

    // A function without a description or parameters leaves those keys out, so no row carries an empty value.
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["name"] = Name };
        if (Description != null)
        {
            json["description"] = Description;
        }
        if (Parameters.Count > 0)
        {
            json["parameters"] = Texts(Parameters);
        }
        json["steps"] = Texts(Steps.Take(StepLimit));

        return json;
    }

    private static JsonArray Texts(IEnumerable<string> texts)
    {
        return new JsonArray(texts.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray());
    }
}

// The functions one window row offers: ordered by name, at most twelve, and a count of the rest so that the reply stays small.
public sealed record CompactFunctionSummary(IReadOnlyList<CompactListedFunction> Functions, int MoreFunctions)
{
    // Twelve functions fit one reply; a larger store is counted, so the model reads the list without a second call.
    public const int Limit = 12;

    // Generated names are left out of what the model reads; they stay reachable through cartridge_browse by topic and name.
    // Ordinal order gives the same names in every gateway, whatever the culture of the operated node.
    public static CompactFunctionSummary Summarize(IEnumerable<CompactListedFunction> functions)
    {
        CompactListedFunction[] sorted = functions.Where(function => !CompactAutomaticNames.IsGenerated(function.Name))
            .OrderBy(function => function.Name, StringComparer.Ordinal).ToArray();

        return new(sorted.Take(Limit).ToArray(), Math.Max(sorted.Length - Limit, 0));
    }
}
