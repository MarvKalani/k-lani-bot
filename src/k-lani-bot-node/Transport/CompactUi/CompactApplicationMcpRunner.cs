using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Use case: the gateway runs a stored app MCP function, for example FilmCraft's import, sequence and GIF export, as one call of
// the agent. Each step goes to the node as its own app_mcp_call, so scope, backup requirement, audit and request id apply per step;
// the runner fills templates, stops at the first failing step, never retries, and checks the files the run wrote on the node.
public sealed class CompactApplicationMcpRunner(ICompactNodeClient client)
{
    public const int AnswerCharacters = 16000;
    public const int LastTextCharacters = 300;

    // Throws what the node connection throws for cancellation or a broken transport; every program answer becomes a run result.
    // A probe read just before is reused, so a call that looked up the function by the program's version lists the program once.
    public async Task<CompactApplicationMcpRun> RunAsync(string program, string? expectedVersion, CompactApplicationMcpFunction function,
        JsonObject? supplied, CompactApplicationMcpProbe? recentProbe, CancellationToken cancellation)
    {
        if (!CompactApplicationMcpTemplate.TryValues(supplied, function.Parameters, out var values)
            || !TryBindChecks(function.Checks, values, out var checks))
        {
            return Refused(program, null, CompactApplicationMcpFailure.InvalidParameters, ParametersDetail(function));
        }
        var probe = recentProbe ?? await ProbeAsync(program, cancellation);
        CompactApplicationMcpRun? refusal = ProbeRefusal(program, expectedVersion, function, probe);
        if (refusal != null)
        {
            return refusal;
        }
        var before = await FactsAsync(program, checks, cancellation);
        if (before.Failure != null)
        {
            return Refused(program, probe.Version, before.Failure.Value, before.Detail);
        }

        var steps = await RunStepsAsync(program, probe.Version!, function, values, cancellation);
        if (steps.Failure != null)
        {
            return new(program, probe.Version, steps.Failure, steps.Detail, steps.FailedStep, steps.Dispatched, [], steps.LastText);
        }
        return await CheckAsync(program, probe.Version!, checks, before.Facts, steps, cancellation);
    }

    // The program's version and tool names, as app_mcp_read lists them; listing starts the program for this session.
    public async Task<CompactApplicationMcpProbe> ProbeAsync(string program, CancellationToken cancellation)
    {
        JsonObject listing = await client.CallAsync(Tool.ApplicationMcpRead, new JsonObject { ["program"] = program }, cancellation);
        if (listing["success"]?.GetValue<bool>() != true || listing["tools"] is not JsonArray tools)
        {
            return new(null, new HashSet<string>(), ErrorOf(listing));
        }
        var names = tools.OfType<JsonObject>().Select(tool => tool["name"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal);

        return new(listing["version"]?.ToString(), names, null);
    }

    private static CompactApplicationMcpRun? ProbeRefusal(string program, string? expectedVersion, CompactApplicationMcpFunction function,
        CompactApplicationMcpProbe probe)
    {
        if (probe.Error != null)
        {
            return Refused(program, null, CompactApplicationMcpFailure.ProgramUnavailable, probe.Error);
        }
        if (!CompactApplicationMcpFunctionCodec.KnownVersion(probe.Version))
        {
            return Refused(program, probe.Version, CompactApplicationMcpFailure.UnknownVersion,
                "The node cannot read this program's version; a function is only stored and run for a known version.");
        }
        if (expectedVersion != null && probe.Version != expectedVersion)
        {
            return Refused(program, probe.Version, CompactApplicationMcpFailure.VersionChanged,
                "The function was verified on " + expectedVersion + "; this node runs " + probe.Version + ".");
        }
        int absent = function.Steps.ToList().FindIndex(step => !probe.Tools.Contains(step.Tool));
        if (absent >= 0)
        {
            var refused = Refused(program, probe.Version, CompactApplicationMcpFailure.ToolAbsent,
                "The program lists no tool '" + function.Steps[absent].Tool + "'.");
            return refused with
            {
                FailedStep = absent + 1
            };
        }

        return null;
    }

    private async Task<CompactApplicationMcpSteps> RunStepsAsync(string program, string version, CompactApplicationMcpFunction function,
        IReadOnlyDictionary<string, JsonNode> values, CancellationToken cancellation)
    {
        var answers = new List<JsonNode?>();
        string? lastText = null;
        for (int index = 0; index < function.Steps.Count; index++)
        {
            CompactApplicationMcpStep step = function.Steps[index];
            if (!CompactApplicationMcpTemplate.TryBind(step.Arguments, values, answers, out JsonNode? arguments))
            {
                return new(CompactApplicationMcpFailure.StepValueAbsent, "A value this step takes from an earlier answer is absent.",
                    index + 1, index, lastText);
            }
            JsonObject call = CallArguments(program, step.Tool, arguments!);
            JsonObject answer = await client.CallAsync(Tool.ApplicationMcpCall, call, cancellation);
            lastText = Shorten(answer["text"]?.ToString());
            CompactApplicationMcpFailure? failure = StepFailure(answer, version, step, out string? detail);
            if (failure != null)
            {
                int dispatched = answer["outcome"]?.ToString() == "not_dispatched" ? index : index + 1;
                return new(failure, detail, index + 1, dispatched, lastText);
            }
            answers.Add(AnswerJson(answer));
        }

        return new(null, null, null, function.Steps.Count, lastText);
    }

    // A refused, failed or updated program stops the run; an expectation compares a value of the answer read as JSON.
    private static CompactApplicationMcpFailure? StepFailure(JsonObject answer, string version, CompactApplicationMcpStep step,
        out string? detail)
    {
        detail = null;
        if (answer["success"]?.GetValue<bool>() != true)
        {
            string code = answer["code"]?.ToString() ?? "program_refused";
            detail = code + ": " + Shorten(answer["error"]?.ToString() ?? answer["text"]?.ToString());
            return CompactApplicationMcpFailure.StepFailed;
        }
        if (answer["version"]?.ToString() is { } running && running != version)
        {
            detail = "The program changed to " + running + " during the run.";
            return CompactApplicationMcpFailure.VersionChanged;
        }
        JsonNode? parsed = AnswerJson(answer);
        foreach (CompactApplicationMcpExpectation expectation in step.Expectations)
        {
            if (!CompactApplicationMcpTemplate.TryResolve(parsed, expectation.Pointer, out JsonNode? value)
                || !JsonNode.DeepEquals(value, expectation.Value))
            {
                detail = "The answer does not hold the expected value at " + expectation.Pointer + ".";
                return CompactApplicationMcpFailure.ExpectationFailed;
            }
        }

        return null;
    }

    private async Task<CompactApplicationMcpRun> CheckAsync(string program, string version,
        IReadOnlyList<CompactApplicationMcpBoundCheck> checks, IReadOnlyDictionary<string, CompactApplicationMcpFileFacts> before,
        CompactApplicationMcpSteps steps, CancellationToken cancellation)
    {
        var after = await FactsAsync(program, checks, cancellation);
        if (after.Failure != null)
        {
            CompactApplicationMcpFailure unreadable = CompactApplicationMcpFailure.CheckFailed;
            return new(program, version, unreadable, after.Detail, null, steps.Dispatched, [], steps.LastText);
        }
        var results = checks.Select(check => Evaluate(check, before[check.Path], after.Facts[check.Path])).ToArray();

        bool passed = results.All(result => result.Passed);
        CompactApplicationMcpFailure? failure = passed ? null : CompactApplicationMcpFailure.CheckFailed;
        string? detail = passed ? null : "A result check did not hold; the steps ran, so look at the program before running again.";
        return new(program, version, failure, detail, null, steps.Dispatched, results, steps.LastText);
    }

    // Every check reads a file this run wrote: absent before or changed in size or time, and not empty.
    private static CompactApplicationMcpCheckResult Evaluate(CompactApplicationMcpBoundCheck check, CompactApplicationMcpFileFacts before,
        CompactApplicationMcpFileFacts after)
    {
        bool written = after.Exists && after.Bytes > 0
            && (!before.Exists || before.Bytes != after.Bytes || before.Modified != after.Modified);
        bool passed = check.Check.Kind switch
        {
            CompactApplicationMcpCheckKind.FileWritten => written,
            CompactApplicationMcpCheckKind.GifFrames => written && after.GifFrames is { } frames
                && frames >= (check.Minimum ?? 1) && (check.Maximum == null || frames <= check.Maximum),
            _ => throw new System.Diagnostics.UnreachableException()
        };

        return new(check.Check, check.Path, passed, after);
    }

    private async Task<CompactApplicationMcpFactsRead> FactsAsync(string program, IReadOnlyList<CompactApplicationMcpBoundCheck> checks,
        CancellationToken cancellation)
    {
        var facts = new Dictionary<string, CompactApplicationMcpFileFacts>(StringComparer.Ordinal);
        foreach (string path in checks.Select(check => check.Path).Distinct(StringComparer.Ordinal))
        {
            var arguments = new JsonObject { ["program"] = program, ["file"] = path };
            JsonObject answer = await client.CallAsync(Tool.ApplicationMcpRead, arguments, cancellation);
            if (answer["success"]?.GetValue<bool>() != true || answer["file"] is not JsonObject file)
            {
                return new(facts, CompactApplicationMcpFailure.InvalidParameters, path + ": " + ErrorOf(answer));
            }
            facts.Add(path, ReadFacts(path, file));
        }

        return new(facts, null, null);
    }

    private static CompactApplicationMcpFileFacts ReadFacts(string path, JsonObject file)
    {
        bool exists = file["exists"]?.GetValue<bool>() == true;
        long bytes = file["bytes"] is JsonValue size && size.TryGetValue(out long length) ? length : 0;
        int? frames = file["gif_frames"] is JsonValue count && count.TryGetValue(out int number) ? number : null;

        return new(path, exists, bytes, file["modified"]?.ToString(), frames);
    }

    private static bool TryBindChecks(IReadOnlyList<CompactApplicationMcpCheck> checks, IReadOnlyDictionary<string, JsonNode> values,
        out IReadOnlyList<CompactApplicationMcpBoundCheck> bound)
    {
        var result = new List<CompactApplicationMcpBoundCheck>();
        bound = result;
        foreach (CompactApplicationMcpCheck check in checks)
        {
            if (!CompactApplicationMcpTemplate.TryBind(JsonValue.Create(check.Path), values, [], out JsonNode? path)
                || path is not JsonValue pathValue || !pathValue.TryGetValue(out string? text) || !Path.IsPathFullyQualified(text)
                || !TryBound(check.Minimum, values, out int? minimum) || !TryBound(check.Maximum, values, out int? maximum))
            {
                return false;
            }
            result.Add(new(check, text, minimum, maximum));
        }

        return true;
    }

    private static bool TryBound(JsonNode? template, IReadOnlyDictionary<string, JsonNode> values, out int? bound)
    {
        bound = null;
        if (template == null)
        {
            return true;
        }
        if (!CompactApplicationMcpTemplate.TryBind(template, values, [], out JsonNode? value) || value is not JsonValue number
            || !int.TryParse(number.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
        {
            return false;
        }
        bound = parsed;

        return true;
    }

    private static JsonObject CallArguments(string program, string tool, JsonNode arguments)
    {
        return new JsonObject
        {
            ["program"] = program,
            ["tool"] = tool,
            ["arguments"] = arguments,
            ["max_chars"] = AnswerCharacters,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
    }

    // The answer text read as JSON; null for a text that is cut into pages or is no JSON, so a reference to it fails.
    private static JsonNode? AnswerJson(JsonObject answer)
    {
        if (answer["next_offset"] != null || answer["text"]?.ToString() is not { } text)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CompactApplicationMcpRun Refused(string program, string? version, CompactApplicationMcpFailure failure, string? detail)
    {
        return new(program, version, failure, detail, null, 0, [], null);
    }

    private static string ParametersDetail(CompactApplicationMcpFunction function)
    {
        var names = function.Parameters.Select(parameter => parameter.Name + ": "
            + CompactApplicationMcpFunctionCodec.TypeName(parameter.Type));
        return "Supply exactly these parameters with absolute check paths: {" + string.Join(", ", names) + "}.";
    }

    private static string ErrorOf(JsonObject answer)
    {
        return Shorten(answer["error"]?.ToString() ?? answer["code"]?.ToString() ?? "no answer") ?? "no answer";
    }

    private static string? Shorten(string? text)
    {
        return text == null || text.Length <= LastTextCharacters ? text : text[..LastTextCharacters] + " …";
    }
}

// The listing of a program for a run or a drift probe: its version and tool names, or why it could not be read.
public sealed record CompactApplicationMcpProbe(string? Version, IReadOnlySet<string> Tools, string? Error);

// A check whose path and frame bounds are filled with this run's parameter values.
internal sealed record CompactApplicationMcpBoundCheck(CompactApplicationMcpCheck Check, string Path, int? Minimum, int? Maximum);

// What the steps of a run did: how many reached the program, the first failure and the last answer text.
internal sealed record CompactApplicationMcpSteps(CompactApplicationMcpFailure? Failure, string? Detail, int? FailedStep, int Dispatched,
    string? LastText);

// The file facts of all checked paths at one moment, or why they could not be read.
internal sealed record CompactApplicationMcpFactsRead(IReadOnlyDictionary<string, CompactApplicationMcpFileFacts> Facts,
    CompactApplicationMcpFailure? Failure, string? Detail);
