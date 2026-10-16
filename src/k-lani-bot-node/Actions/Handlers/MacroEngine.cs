using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KLaniBot.Common;
using KLaniBot.Guard;

namespace KLaniBot.Actions.Handlers;

// A macro version is immutable, so saving one that exists already is not a failure.
public enum MacroSaving
{
    Saved,
    VersionAlreadyExists
}

// One named value a macro step can use as {{name}}, with its type and an optional default; saved with the macro version.
public sealed class MacroParameter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "string";
    [JsonPropertyName("default")]
    public JsonNode? Default
    {
        get; set;
    }
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}
// How a run follows the person's pace: the delay slow motion adds to every step, and where the remaining steps are shown.
public sealed record MacroPacing(Func<TimeSpan> StepDelay, Action<IReadOnlyList<JsonObject>> ShowRemaining);

// A saved macro version as stored in the macro file and returned by get_macro: its parameters, steps, postconditions and content hash.
public sealed class MacroDefinition
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public List<MacroParameter> Parameters { get; set; } = [];
    [JsonPropertyName("steps")] public List<JsonObject> Steps { get; set; } = [];
    [JsonPropertyName("postconditions")] public List<JsonObject> Postconditions { get; set; } = [];
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}
// Use case: a sequence of steps that worked once (open the export dialog, type a file name, confirm) is
// saved and replayed by name. A stored macro is code the next run executes with the runner's permissions,
// so every version is immutable and addressed by its content hash: run_macro names the exact hash, and a
// changed file on disk fails the integrity check instead of running different steps. Agents can add
// versions but never change or delete one.
//
// The engine only orders steps and fills placeholders. Each step goes back through the dispatcher as a
// request of its own, so scopes, guard, consent, journal and receipts apply to it as if the client had sent
// it. A macro cannot call a macro or end the session, which keeps a run finite and inside one session.
// Result: Verified only when postconditions exist and all held; otherwise Dispatched at best.
public sealed class MacroEngine
{
    private readonly string _file;
    private List<MacroDefinition> _macros;
    // Throws InvalidDataException when the store is too large, unreadable or a macro fails its integrity check.
    public MacroEngine(string filePath)
    {
        _file = filePath;
        RejectLinks(_file);
        if (File.Exists(_file) && new FileInfo(_file).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Macro store size limit exceeded.");
        _macros = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<MacroDefinition>>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("Invalid macro store.")
            : [];
        foreach (var macro in _macros)
        {
            Validate(macro);
            if (macro.Sha256 != Hash(macro))
                throw new InvalidDataException("Macro integrity check failed.");
        }
    }
    public object Describe(int offset, int limit)
    {
        int? nextOffset = offset + limit < _macros.Count ? offset + limit : null;
        var page = _macros.Skip(offset).Take(limit).Select(DescribeMacro);
        return new
        {
            success = true,
            total = _macros.Count,
            offset,
            next_offset = nextOffset,
            macros = page
        };
    }
    private object DescribeMacro(MacroDefinition macro)
    {
        return new
        {
            name = macro.Name,
            description = macro.Description,
            sha256 = macro.Sha256,
            parameters = macro.Parameters,
            step_count = macro.Steps.Count,
            postcondition_count = macro.Postconditions.Count,
            required_scopes = RequiredScopes(macro.Name, macro.Sha256).Select(WireNames<Scope>.Of)
        };
    }
    // Throws ArgumentException when no macro has that name and hash.
    public MacroDefinition Definition(string name, string hash)
    {
        return _macros.SingleOrDefault(m => m.Name == name && m.Sha256 == hash)
            ?? throw new ArgumentException("Unknown macro version.");
    }
    // Throws ArgumentException for an invalid macro, InvalidDataException when the store is full, and rethrows the Exception of a failed
    // write after taking the macro out of the list again.
    public MacroSaving SaveMacro(MacroDefinition macro)
    {
        Validate(macro);
        macro.Sha256 = Hash(macro);
        if (_macros.Any(m => m.Sha256 == macro.Sha256))
            return MacroSaving.VersionAlreadyExists;
        if (_macros.Count >= 256)
            throw new InvalidDataException("Macro version capacity exhausted; operator maintenance required.");
        _macros.Add(macro);
        string temporary = _file + "." + Guid.NewGuid() + ".tmp";
        try
        {
            // Stored steps can hold literal values (paths, text), so the store is private like the rest of the state.
            PrivateFile.WriteAllText(temporary, JsonSerializer.Serialize(_macros));
            File.Move(temporary, _file, true);
        }
        catch
        {
            // Undo for every failure type: the list must match the file, whatever stopped the write.
            _macros.Remove(macro);
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return MacroSaving.Saved;
    }
    public static string Hash(MacroDefinition macro)
    {
        var node = JsonSerializer.SerializeToNode(macro)!.AsObject();
        node.Remove("sha256");
        return CanonicalJson.Hash(node);
    }
    // Throws ArgumentException naming the first rule the macro breaks.
    private static void Validate(MacroDefinition macro)
    {
        bool exceedsBudget = macro.Description.Length > 512
            || macro.Parameters.Count > 32
            || macro.Parameters.Any(p => p.Description.Length > 256)
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(macro)) > 128 * 1024;
        if (exceedsBudget)
            throw new ArgumentException("Macro definition exceeds its budget.");
        bool invalidFormat = macro.Version != 2
            || !Regex.IsMatch(macro.Name, "^[a-zA-Z0-9_-]{1,80}$")
            || macro.Steps.Count is < 1 or > 100
            || macro.Postconditions.Count > 20;
        if (invalidFormat)
            throw new ArgumentException("Invalid macro format or size.");
        if (macro.Parameters.Select(p => p.Name).Distinct().Count() != macro.Parameters.Count)
            throw new ArgumentException("Duplicate parameter.");
        foreach (var p in macro.Parameters)
        {
            if (!Regex.IsMatch(p.Name, "^[a-zA-Z0-9_]{1,80}$") || p.Type is not ("string" or "integer" or "boolean"))
                throw new ArgumentException("Invalid parameter.");
            if (p.Default != null)
                ToolContract.Validate(new JsonObject { ["type"] = p.Type }, p.Default, p.Name);
        }
        foreach (var step in macro.Steps)
        {
            bool hasUnknownKey = step.Any(p => p.Key is not ("action" or "args" or "capture" or "stop_on_error" or "expect_count"));
            if (hasUnknownKey || step["args"] is not JsonObject)
                throw new ArgumentException("Invalid step.");
            var contract = ToolContract.Get(step["action"]!.ToString());
            Tool stepTool = ToolNames.Parse(step["action"]!.ToString());
            if (stepTool is Tool.RunMacro or Tool.SaveMacro or Tool.EndSession)
                throw new ArgumentException("Nested macro and session administration are not permitted.");
            if (step["capture"] is { } capture && !Regex.IsMatch(capture.ToString(), "^[a-zA-Z][a-zA-Z0-9_]{0,79}$"))
                throw new ArgumentException("Invalid capture name.");
            if (step["expect_count"] is { } count && !IsBoundedCardinality(stepTool, count))
                throw new ArgumentException("expect_count requires a bounded find_elements cardinality.");
        }
    }
    // Only find_elements can report a match count; zero asserts absence, and the bound keeps a typo from demanding an absurd cardinality.
    private static bool IsBoundedCardinality(Tool action, JsonNode count)
    {
        if (action != Tool.FindElements)
            return false;
        return count is JsonValue value && value.TryGetValue<int>(out int expected) && expected is >= 0 and <= 200;
    }
    // Throws ArgumentException for an unknown macro and InvalidDataException when its content changed on disk.
    public IEnumerable<Scope> RequiredScopes(string name, string sha256)
    {
        var macro = Definition(name, sha256);
        if (Hash(macro) != sha256)
            throw new InvalidDataException("Macro content changed.");
        return macro.Steps.Select(step => ToolScope.Of(ToolContract.Get(step["action"]!.ToString()))).Append(Scope.Observe).Distinct();
    }
    // Throws InvalidDataException when the macro changed on disk and ArgumentException for unknown, missing or clashing parameters and
    // captures.
    public object RunMacro(
        string name,
        JsonObject? arguments,
        Func<JsonObject, JsonObject> dispatcher,
        string sha256,
        string principalId = "",
        bool trace = false,
        MacroPacing? pacing = null)
    {
        var macro = Definition(name, sha256);
        if (Hash(macro) != sha256)
            throw new InvalidDataException("Macro content changed.");
        JsonObject values = BindParameters(macro, arguments ?? new());

        var run = new MacroRun(this, dispatcher, values, sha256, principalId, trace, pacing);
        ActionOutcome success = macro.Postconditions.Count > 0 ? ActionOutcome.Verified : ActionOutcome.Dispatched;
        return run.RunSteps(macro.Steps) ?? run.CheckPostconditions(macro.Postconditions) ?? run.Finish(true, success);
    }

    // Throws ArgumentException for an argument the macro does not declare, a missing required one or one of the wrong type.
    private static JsonObject BindParameters(MacroDefinition macro, JsonObject arguments)
    {
        foreach (var key in arguments.Select(p => p.Key))
            if (!macro.Parameters.Any(p => p.Name == key))
                throw new ArgumentException("Unknown macro parameter.");

        var values = new JsonObject();
        foreach (var parameter in macro.Parameters)
        {
            var value = arguments[parameter.Name]?.DeepClone() ?? parameter.Default?.DeepClone();
            if (value == null && parameter.Required)
                throw new ArgumentException("Missing parameter: " + parameter.Name);
            if (value != null)
                ToolContract.Validate(new JsonObject { ["type"] = parameter.Type }, value, parameter.Name);
            values[parameter.Name] = value;
        }
        return values;
    }

    // One run of one macro: the values steps may refer to, the snapshot the last observation returned and the recorded
    // step results. RunSteps and CheckPostconditions return the final answer when the run stops early, otherwise null.
    private sealed class MacroRun(
        MacroEngine engine,
        Func<JsonObject, JsonObject> dispatcher,
        JsonObject values,
        string sha256,
        string principalId,
        bool trace,
        MacroPacing? pacing)
    {
        private const int MaximumRecordedStepBytes = 4096;

        // The actuator ends a request after 60 s by default. In slow motion a run stops before a step that could not finish
        // inside this budget, and the agent continues from the next step itself.
        private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(50);
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        private const int MaximumRecordedErrorCharacters = 512;
        private const long MaximumResultBytes = 4 * 1024 * 1024;

        private readonly JsonArray _results = new();
        private readonly string _runId = Guid.CreateVersion7().ToString();
        private string? _snapshot;
        private long _resultBytes;

        // Throws ArgumentException when a step uses a cached target before any observation, or a capture overwrites a value.
        public JsonObject? RunSteps(List<JsonObject> steps)
        {
            for (int index = 0; index < steps.Count; index++)
            {
                var step = Resolve(steps[index], values)!.AsObject();
                var args = step["args"]!.AsObject();
                string action = step["action"]!.ToString();
                pacing?.ShowRemaining(steps.Skip(index + 1).ToList());
                if (WouldOverrunTimeBudget(args))
                    return FinishBeforeTimeBudget();
                // A step may use an element id from an earlier observation of the same run; the run supplies that
                // observation's snapshot id, so the dispatcher can still refuse an id from an older snapshot.
                if (args.ContainsKey("id") && !args.ContainsKey("snapshot_id"))
                    args["snapshot_id"] = _snapshot ?? throw new ArgumentException("Observe before using a cached target.");
                // Each mutating step is an operation of its own in the ledger and the journal.
                if (ToolContract.Get(action)["mutates"]!.GetValue<bool>())
                    args["request_id"] = Guid.CreateVersion7().ToString();
                var result = dispatcher(new JsonObject { ["action"] = action, ["args"] = args.DeepClone() });
                JsonObject? stopped = Accept(step, result);
                if (stopped != null)
                    return stopped;
            }
            return null;
        }

        private bool WouldOverrunTimeBudget(JsonObject args)
        {
            TimeSpan delay = pacing?.StepDelay() ?? TimeSpan.Zero;
            if (delay == TimeSpan.Zero)
                return false;
            int waitMilliseconds = args["postcondition"] is JsonObject condition
                ? condition["timeout_ms"]?.GetValue<int?>() ?? PostconditionDefaults.WaitMilliseconds
                : 0;
            return _elapsed.Elapsed + delay + TimeSpan.FromMilliseconds(waitMilliseconds) > TimeBudget;
        }

        private JsonObject FinishBeforeTimeBudget()
        {
            JsonObject stopped = Finish(false, ActionOutcome.Partial, _results.Count);
            stopped["time_budget_reached"] = true;
            return stopped;
        }

        // Throws ArgumentException when the step's capture would overwrite a parameter or an earlier capture.
        private JsonObject? Accept(JsonObject step, JsonObject result)
        {
            Record(result);
            _resultBytes += Encoding.UTF8.GetByteCount(result.ToJsonString());
            if (_resultBytes > MaximumResultBytes)
                return Finish(false, ActionOutcome.ResultBudgetExceeded, _results.Count);
            if (result["snapshot_id"] is { } next)
                _snapshot = next.ToString();
            if (result["success"]?.GetValue<bool>() != true)
                return Finish(false, ActionOutcome.Partial, _results.Count);
            // Never turn an ambiguous selector into a first-match mutation. Compare the
            // total count, not just the possibly truncated returned matches array.
            if (step["expect_count"] is { } expectedCount && !ReportsExactMatchCount(result, expectedCount.GetValue<int>()))
                return Finish(false, ActionOutcome.SelectorCardinalityFailed, _results.Count);

            if (step["capture"] is { } capture)
            {
                if (values.ContainsKey(capture.ToString()))
                    throw new ArgumentException("Capture may not overwrite a parameter or prior capture.");
                values[capture.ToString()] = result.DeepClone();
            }
            return null;
        }

        public JsonObject? CheckPostconditions(List<JsonObject> postconditions)
        {
            foreach (var condition in postconditions)
            {
                var args = Resolve(condition, values)!.AsObject();
                if (args.ContainsKey("id"))
                    args["snapshot_id"] = _snapshot;
                var result = dispatcher(new JsonObject { ["action"] = ToolNames.Of(Tool.VerifyElement), ["args"] = args });
                Record(result);
                if (result["success"]?.GetValue<bool>() != true)
                    return Finish(false, ActionOutcome.PostconditionFailed, _results.Count);
            }
            return null;
        }

        public JsonObject Finish(bool success, ActionOutcome outcome, int? failedStep = null)
        {
            if (trace)
                engine.SaveTrace(_runId, principalId, _results);
            return new JsonObject
            {
                ["success"] = success,
                ["outcome"] = outcome.ToWireName(),
                ["sha256"] = sha256,
                ["run_id"] = _runId,
                ["steps_completed"] = _results.Count,
                ["failed_step"] = failedStep,
                ["trace_available"] = trace
            };
        }

        // A large step result is kept as its outcome and a shortened error, so the trace stays small.
        private void Record(JsonObject result)
        {
            var detail = (JsonObject)result.DeepClone();
            detail.Remove("image");
            if (Encoding.UTF8.GetByteCount(detail.ToJsonString()) > MaximumRecordedStepBytes)
            {
                string? error = result["error"]?.ToString();
                string? shortenedError = error == null ? null : error[..Math.Min(error.Length, MaximumRecordedErrorCharacters)];
                detail = new JsonObject
                {
                    ["success"] = result["success"]?.DeepClone(),
                    ["outcome"] = result["outcome"]?.DeepClone(),
                    ["error"] = shortenedError,
                    ["receipt"] = result["receipt"]?.DeepClone(),
                    ["details_omitted"] = true
                };
            }
            _results.Add(detail);
        }
    }

    // Throws UnauthorizedAccessException when a part of the path is a link.
    private static void RejectLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Macro store links are forbidden.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private void SaveTrace(string id, string principal, JsonArray results)
    {
        string root = Path.Combine(Path.GetDirectoryName(_file)!, "macro-runs");
        RejectLinks(root);
        PrivateFile.CreateDirectory(root);
        var bounded = new JsonArray();
        int size = 0;
        foreach (var result in results)
        {
            int bytes = Encoding.UTF8.GetByteCount(result!.ToJsonString());
            if ((size += bytes) > 60000)
                break;
            bounded.Add(result.DeepClone());
        }
        var value = new JsonObject
        {
            ["principal_id"] = principal,
            ["run_id"] = id,
            ["results"] = bounded,
            ["truncated"] = bounded.Count != results.Count
        };
        string path = Path.Combine(root, id + ".json");
        using (var stream = PrivateFile.OpenWrite(path, FileMode.CreateNew))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        foreach (string old in Directory.EnumerateFiles(root, "*.json").Order(StringComparer.Ordinal).Reverse().Skip(64))
            File.Delete(old);
    }
    // Throws ArgumentException for an id that is no UUIDv7, InvalidDataException for an oversized trace and UnauthorizedAccessException for
    // another principal's trace.
    public JsonObject ReadTrace(string id, string principal, int offset, int limit)
    {
        if (!Guid.TryParse(id, out var run) || run.Version != 7 || run.ToString() != id)
            throw new ArgumentException("UUIDv7 run ID required.");
        string file = Path.Combine(Path.GetDirectoryName(_file)!, "macro-runs", id + ".json");
        RejectLinks(file);
        if (new FileInfo(file).Length > 65536)
            throw new InvalidDataException("Macro trace exceeds its storage budget.");
        var data = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        if (data["principal_id"]?.ToString() != principal)
            throw new UnauthorizedAccessException("Macro trace belongs to another principal.");
        var items = data["results"]!.AsArray();
        var page = new JsonArray(items.Skip(offset).Take(limit).Select(v => v!.DeepClone()).ToArray());
        return new JsonObject
        {
            ["success"] = true,
            ["run_id"] = id,
            ["results"] = page,
            ["total"] = items.Count,
            ["next_offset"] = offset + limit < items.Count ? offset + limit : null,
            ["truncated"] = data["truncated"]!.DeepClone()
        };
    }
    private static bool ReportsExactMatchCount(JsonObject result, int expected)
    {
        return result["total_matches"]?.GetValue<int>() == expected
            && result["matches"] is JsonArray matches
            && matches.Count == expected;
    }
    // A path segment that does not fit the node (bad index, missing key, scalar) yields null, which the caller reports.
    private static JsonNode? Descend(JsonNode? item, string segment)
    {
        if (item is JsonArray array)
            return int.TryParse(segment, out int index) && index >= 0 && index < array.Count ? array[index] : null;
        if (item is JsonObject obj)
            return obj[segment];
        return null;
    }
    // Throws ArgumentException for a placeholder without a value.
    private static JsonNode? Resolve(JsonNode? node, JsonObject values)
    {
        if (node is JsonObject obj)
            return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Resolve(p.Value, values))));
        if (node is JsonArray array)
            return new JsonArray(array.Select(p => Resolve(p, values)).ToArray());
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return node?.DeepClone();
        JsonNode? Lookup(string path)
        {
            JsonNode? item = values;
            foreach (var segment in path.Split('.'))
                item = Descend(item, segment);
            return item ?? throw new ArgumentException("Unresolved placeholder: " + path);
        }
        var exact = Regex.Match(text, @"^\{\{([a-zA-Z0-9_.]+)\}\}$");
        if (exact.Success)
            return Lookup(exact.Groups[1].Value)!.DeepClone();
        return JsonValue.Create(Regex.Replace(text, @"\{\{([a-zA-Z0-9_.]+)\}\}", m => Lookup(m.Groups[1].Value)!.ToString()));
    }
}
