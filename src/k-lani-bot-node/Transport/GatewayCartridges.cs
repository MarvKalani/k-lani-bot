using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Localization;
using KLaniBot.Visibility;

namespace KLaniBot.Transport;

// An operator loads a reviewed local cartridge into the controller's MCP gateway. Its pinned recipes become tools;
// every step still goes to the selected node with the caller's existing permissions and local consent.
public sealed class GatewayCartridges : IDisposable
{
    internal const int MaximumRecipeBytes = 128 * 1024;
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCoverageBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, GatewayRecipe> _recipes = new(StringComparer.Ordinal);
    private readonly string _temporaryDirectory;
    private readonly MacroEngine _engine;

    // Throws InvalidDataException for an unsupported or modified bundle, ArgumentException for invalid or duplicate recipes,
    // and rethrows Exception after removing the private store when loading fails.
    public GatewayCartridges(string manifestPath)
    {
        JsonObject manifest = ReadManifest(manifestPath);
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), "k-lani-bot-cartridges-" + Guid.CreateVersion7());
        PrivateFile.CreateDirectory(_temporaryDirectory);
        _engine = new MacroEngine(Path.Combine(_temporaryDirectory, "macros.json"));
        try
        {
            ReadRecipes(manifest, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            if (manifest["coverage"] is { } coverage)
            {
                byte[] bytes = ReadPinnedFile(manifest, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
                    coverage.ToString(), MaximumCoverageBytes);
                Coverage = new CartridgeCoverage(bytes, Recipes);
            }
        }
        catch
        {
            Directory.Delete(_temporaryDirectory, true);
            throw;
        }
    }

    public string ApplicationName { get; private set; } = "";
    public string CartridgeVersion { get; private set; } = "";
    public string ProcessName { get; private set; } = "";
    public string TestedVersion { get; private set; } = "";
    public CartridgeCoverage? Coverage
    {
        get;
    }
    public IReadOnlyList<GatewayRecipe> Recipes => _recipes.Values.ToArray();

    public bool TryGet(string name, out GatewayRecipe? recipe)
    {
        return _recipes.TryGetValue(name, out recipe);
    }

    // Throws InvalidDataException for an unsupported or oversized local bundle.
    internal static JsonObject ReadManifest(string path)
    {
        CartridgeManifest.RejectLinks(path);
        if (new FileInfo(path).Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Cartridge manifest exceeds its size limit.");
        }
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (manifest["format"]?.ToString() != "k-lani-bot.local-macro-bundle.v1")
        {
            throw new InvalidDataException("Only a reviewed local macro bundle can supply gateway tools.");
        }

        return manifest;
    }

    // Throws InvalidDataException for missing application identity or a recipe whose bytes differ from the manifest.
    // Throws ArgumentException for invalid or duplicate recipe names.
    private void ReadRecipes(JsonObject manifest, string directory)
    {
        JsonObject target = manifest["target"]!.AsObject();
        string process = target["process_name"]?.ToString() ?? "";
        string version = target["application_version"]?.ToString() ?? "";
        ApplicationName = target["application"]!.ToString();
        CartridgeVersion = manifest["version"]!.ToString();
        ProcessName = process;
        TestedVersion = version;
        if (process.Length == 0 || version.Length == 0)
        {
            throw new InvalidDataException("Gateway cartridges require an exact process name and application version.");
        }
        foreach (JsonObject entry in manifest["entrypoints"]!.AsArray().OfType<JsonObject>())
        {
            if (entry["testing_only"]?.GetValue<bool>() == true)
            {
                continue;
            }
            string relative = entry["recipe"]!.ToString();
            byte[] bytes = ReadPinnedFile(manifest, directory, relative, MaximumRecipeBytes);
            MacroDefinition macro = JsonSerializer.Deserialize<MacroDefinition>(bytes)!;
            RequireRecipeParameters(macro);
            _engine.SaveMacro(macro);
            CartridgeTopic topic = CartridgeTopic.Parse(entry["topic"]?.ToString() ?? "Use/General");
            CartridgeCompatibility compatibility = Enum.Parse<CartridgeCompatibility>(
                entry["compatibility"]?.ToString() ?? "ExactBuild");
            var recipe = new GatewayRecipe(macro, process, version, topic, compatibility, ReadValidation(entry));
            _recipes.Add(macro.Name, recipe);
        }
    }

    private static CartridgeValidation ReadValidation(JsonObject entry)
    {
        return entry["validation"]?.ToString() switch
        {
            "fixture_tested" => CartridgeValidation.FixtureTested,
            "real_app_observation_tested" => CartridgeValidation.ObservationTested,
            "real_app_tested" => CartridgeValidation.RealAppTested,
            _ => CartridgeValidation.Unreviewed
        };
    }

    // Throws ArgumentException when a recipe hides a core tool, overrides routing or lacks a process selector.
    private static void RequireRecipeParameters(MacroDefinition macro)
    {
        MacroParameter? process = macro.Parameters.SingleOrDefault(parameter => parameter.Name == "pid");
        bool startsProgram = GatewayRecipe.StartsProgram(macro);
        if (ToolNames.TryParse(macro.Name, out _) || macro.Parameters.Any(parameter => parameter.Name == "node")
            || (!startsProgram && (process == null || process.Type != "integer" || !process.Required)))
        {
            throw new ArgumentException("A gateway recipe needs a required integer pid, no node parameter, and its own tool name.");
        }
    }

    // Throws InvalidDataException for an unpinned, changed, oversized or escaping recipe path.
    internal static byte[] ReadPinnedFile(JsonObject manifest, string directory, string relative, int maximumBytes)
    {
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        string prefix = directory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Cartridge recipe must stay inside its bundle.");
        }
        CartridgeManifest.RejectLinks(path);
        JsonObject? pin = manifest["files"]!.AsArray().OfType<JsonObject>()
            .SingleOrDefault(file => file["path"]?.ToString() == relative);
        long length = new FileInfo(path).Length;
        if (pin == null || length > maximumBytes || length != pin["bytes"]?.GetValue<long>())
        {
            throw new InvalidDataException("Cartridge recipe is not pinned with its exact size.");
        }
        byte[] bytes = File.ReadAllBytes(path);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != pin["sha256"]?.ToString())
        {
            throw new InvalidDataException("Cartridge recipe differs from its reviewed bytes.");
        }

        return bytes;
    }

    // Throws ArgumentException for an invalid topic path.
    public JsonObject Browse(string path, IReadOnlySet<Tool> available)
    {
        GatewayRecipe[] recipes = Recipes.Where(recipe => recipe.Tools.All(available.Contains)).ToArray();
        if (path.Length == 0)
        {
            return RootTopics(recipes);
        }
        CartridgeTopic.Parse(path);
        string prefix = path + "/";
        string[] branches = recipes.Where(recipe => recipe.Topic.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(recipe => prefix + recipe.Topic.Path[prefix.Length..].Split('/')[0]).Distinct().Order().ToArray();
        var functions = new JsonArray(recipes.Where(recipe => recipe.Topic.Path == path).Select(DescribeRecipe).ToArray());

        return new JsonObject
        {
            ["success"] = true,
            ["path"] = path,
            ["topics"] = new JsonArray(branches.Select(branch => (JsonNode?)JsonValue.Create(branch)).ToArray()),
            ["functions"] = functions
        };
    }

    private static JsonObject RootTopics(GatewayRecipe[] recipes)
    {
        var topics = new JsonArray();
        foreach (CartridgeCategory category in Enum.GetValues<CartridgeCategory>())
        {
            topics.Add(new JsonObject
            {
                ["path"] = category.ToString(),
                ["function_count"] = recipes.Count(recipe => recipe.Topic.Category == category)
            });
        }

        return new JsonObject { ["success"] = true, ["topics"] = topics };
    }

    private static JsonNode DescribeRecipe(GatewayRecipe recipe)
    {
        return new JsonObject
        {
            ["name"] = recipe.Macro.Name,
            ["description"] = recipe.Macro.Description,
            ["compatibility"] = recipe.Compatibility.ToString(),
            ["validation"] = recipe.Validation.ToString()
        };
    }

    public JsonObject Run(GatewayRecipe recipe, JsonObject arguments, Func<JsonObject, JsonObject> dispatch)
    {
        var run = new GatewayRecipeRun(dispatch);
        JsonObject result = JsonSerializer.SerializeToNode(_engine.RunMacro(recipe.Macro.Name, arguments, run.Dispatch,
            recipe.Macro.Sha256, pacing: new MacroPacing(() => TimeSpan.Zero, run.ShowRemaining)))!.AsObject();
        result["last_result"] = run.LastResult?.DeepClone();

        return result;
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, true);
    }
}

// One immutable controller recipe, pinned to the process and application version that were observed during authoring.
public sealed record GatewayRecipe(MacroDefinition Macro, string ProcessName, string ApplicationVersion,
    CartridgeTopic Topic, CartridgeCompatibility Compatibility, CartridgeValidation Validation = CartridgeValidation.Unreviewed)
{
    public bool IsProgramStart => StartsProgram(Macro);
    public IReadOnlyList<Tool> Tools => Macro.Steps.Select(step => ToolNames.Parse(step["action"]!.ToString()))
        .Concat(Macro.Postconditions.Count > 0 ? new[] { Tool.VerifyElement } : []).Distinct().ToArray();

    public static bool StartsProgram(MacroDefinition macro)
    {
        return macro.Parameters.Count == 0 && macro.Steps.Count == 1 && macro.Postconditions.Count == 0
            && macro.Steps[0]["action"]?.ToString() == ToolNames.Of(Tool.LaunchProcess);
    }

    // Throws UnauthorizedAccessException when the node cannot confirm the selected process and tested application version, and
    // ArgumentException carrying the node's own reason when the window list was refused with a code (for example access_paused
    // from the controller tray): the gateway passes only that exception's message to the client.
    public void RequireApplication(JsonObject response, int processId)
    {
        if (response["success"]?.GetValue<bool>() != true && response["code"] is { } code)
        {
            throw new ArgumentException("The node refused the window check (" + code + "): " + response["error"]);
        }
        JsonObject[] windows = response["windows"] as JsonArray is { } entries
            ? entries.OfType<JsonObject>().Where(window => window["ProcessId"]?.GetValue<int>() == processId).ToArray()
            : [];
        bool matches = response["success"]?.GetValue<bool>() == true && windows.Length > 0
            && windows.All(window => window["ProcessName"]?.ToString() == ProcessName
                && (Compatibility == CartridgeCompatibility.NativeProbe || window["AppVersion"]?.ToString() == ApplicationVersion));
        if (!matches)
        {
            throw new UnauthorizedAccessException("Cartridge target process or tested application version does not match the node.");
        }
    }

    public JsonObject Descriptor(bool needsNode)
    {
        var properties = new JsonObject { ["node"] = new JsonObject { ["type"] = "string" } };
        var required = new JsonArray();
        foreach (MacroParameter parameter in Macro.Parameters)
        {
            properties[parameter.Name] = new JsonObject { ["type"] = parameter.Type, ["description"] = parameter.Description };
            if (parameter.Required && parameter.Default == null)
            {
                required.Add(parameter.Name);
            }
        }
        if (needsNode)
        {
            required.Add("node");
        }
        bool mutates = Tools.Any(tool => ToolContract.Get(tool)["mutates"]!.GetValue<bool>());

        return new JsonObject
        {
            ["name"] = Macro.Name,
            ["description"] = Macro.Description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = !mutates,
                ["destructiveHint"] = mutates,
                ["idempotentHint"] = !mutates,
                ["openWorldHint"] = false
            }
        };
    }
}

// Each recipe run has its own plan and last response. The node shows the remaining steps before each mutation.
internal sealed class GatewayRecipeRun(Func<JsonObject, JsonObject> dispatch)
{
    private const int MaximumReportedBytes = 64 * 1024;
    private JsonArray _remaining = [];
    public JsonObject? LastResult
    {
        get; private set;
    }

    public void ShowRemaining(IReadOnlyList<JsonObject> steps)
    {
        _remaining = new JsonArray(steps.Take(StepDescriber.MaximumAnnouncedSteps)
            .Select(step => (JsonNode?)JsonValue.Create(DescribePlannedStep(step))).ToArray());
    }

    private static string DescribePlannedStep(JsonObject definition)
    {
        StepView step = StepDescriber.DescribeMacroStep(definition);
        if (step.Title.Length > 0)
        {
            return step.Title;
        }
        UiText text = step.Kind switch
        {
            StepKind.Read => UiText.StepRead,
            StepKind.Click => UiText.StepClick,
            StepKind.Type => UiText.StepType,
            StepKind.Input => UiText.StepInput,
            StepKind.Window => UiText.StepWindow,
            StepKind.Launch => UiText.StepLaunch,
            StepKind.Clipboard => UiText.StepClipboard,
            StepKind.Files => UiText.StepFiles,
            StepKind.Command => UiText.StepCommand,
            StepKind.Macro => UiText.StepMacro,
            StepKind.Other or StepKind.Announced => UiText.StepOther,
            _ => throw new System.Diagnostics.UnreachableException()
        };
        string label = UiTexts.Get(text);

        return step.Detail.Length == 0 ? label : label + ": " + step.Detail;
    }

    public JsonObject Dispatch(JsonObject frame)
    {
        RemoveEmptySelectors(frame["args"]!.AsObject());
        Tool tool = ToolNames.Parse(frame["action"]!.ToString());
        if (ToolContract.Get(tool)["inputSchema"]!["properties"]?["next_steps"] != null)
        {
            frame["args"]!["next_steps"] ??= _remaining.DeepClone();
        }
        JsonObject result = dispatch(frame);
        JsonObject compact = CompactEvidence(result);
        LastResult = JsonSerializer.SerializeToUtf8Bytes(compact).Length <= MaximumReportedBytes
            ? compact
            : new JsonObject { ["outcome"] = result["outcome"]?.DeepClone(), ["details_omitted"] = true };

        return result;
    }

    private static JsonObject CompactEvidence(JsonObject result)
    {
        JsonObject compact = result.DeepClone().AsObject();
        if (compact.Remove("receipt", out JsonNode? receipt) && receipt is JsonObject recorded)
        {
            compact["evidence"] = new JsonObject
            {
                ["operation_id"] = recorded["operation_id"]?.DeepClone(),
                ["node_id"] = recorded["node_id"]?.DeepClone(),
                ["outcome"] = recorded["outcome"]?.DeepClone(),
                ["trace_file"] = result["trace_file"]?.DeepClone()
            };
        }
        compact.Remove("trace_file");
        compact.Remove("transport");

        return compact;
    }

    private static void RemoveEmptySelectors(JsonObject arguments)
    {
        foreach (string name in new[] { "name", "auto_id", "role", "title" })
        {
            if (arguments[name]?.ToString() == "")
            {
                arguments.Remove(name);
            }
        }
    }
}
