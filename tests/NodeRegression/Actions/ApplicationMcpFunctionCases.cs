using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Transport.CompactUi;

// Stored app MCP functions: the gateway saves a sequence of app_mcp_call steps after a verified test run, lists it in ui_info for
// the version the program reports, runs it after a restart with fresh inputs and checks the written GIF on the node. Another
// version of the program hides the function and the drift rows say whether a test run or a repair is needed. The gateway
// classes run against the real dispatcher and FakeApplicationMcp in a child process.
internal static class ApplicationMcpFunctionCases
{
    private const string Program = "fake";

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Stored app MCP functions");
        string root = Path.Combine(temporaryRoot, "app-mcp-functions-" + Guid.CreateVersion7().ToString("N"));
        string library = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        CheckFileFacts(checks, root);
        CheckTemplates(checks);
        CheckSaveAndReuse(checks, root, library);
        CheckFailures(checks, root, library);
        CheckDrift(checks, root, library);
        CheckGuards(checks, root, library);

        return checks.Passed;
    }

    private static void CheckFileFacts(Checks checks, string root)
    {
        NodePolicy policy = ApplicationMcpCases.Policy(root, "facts", [ApplicationMcpCases.Settings()], BackupRequirement.Off);
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy);
        string gif = Path.Combine(policy.WorkspaceRoot, "seven.gif");
        File.WriteAllBytes(gif, FakeApplicationMcp.Gif(7));
        string text = Path.Combine(policy.WorkspaceRoot, "note.txt");
        File.WriteAllText(text, "GIF89a but not a picture");

        JsonObject counted = Facts(dispatcher, gif);
        checks.That(counted["file"]?["gif_frames"]?.GetValue<int>() == 7 && counted["file"]?["exists"]?.GetValue<bool>() == true,
            "the node counts the frames of a workspace GIF");
        checks.That(Facts(dispatcher, text)["file"]?["gif_frames"] == null, "a file that is no complete GIF has no frame count");
        checks.That(Facts(dispatcher, Path.Combine(policy.WorkspaceRoot, "absent.gif"))["file"]?["exists"]?.GetValue<bool>() == false,
            "an absent file is reported as absent");
        checks.That(Facts(dispatcher, "/etc/hostname")["success"]?.GetValue<bool>() != true, "a file outside the workspace is refused");
        var unlisted = new JsonObject { ["program"] = "other", ["file"] = gif };
        checks.That(Execute(dispatcher, Tool.ApplicationMcpRead, unlisted)["success"]?.GetValue<bool>() != true,
            "a file question for a program the policy does not list is refused");
        JsonObject call = Execute(dispatcher, Tool.ApplicationMcpCall, new JsonObject
        {
            ["program"] = Program,
            ["tool"] = "state_read",
            ["request_id"] = Guid.CreateVersion7().ToString()
        });
        checks.That(call["version"]?.ToString() == FakeApplicationMcp.Version, "a tool answer names the running program's version");
    }

    private static void CheckTemplates(Checks checks)
    {
        var values = new Dictionary<string, JsonNode> { ["name"] = "clip", ["fps"] = 10 };
        var answers = new List<JsonNode?> { JsonNode.Parse("{\"items\":[4]}") };
        var template = JsonNode.Parse("{\"path\":\"/w/{{name}}.gif\",\"fps\":\"{{fps}}\",\"item\":{\"$step\":1,\"pointer\":\"/items/0\"}}");
        checks.That(CompactApplicationMcpTemplate.TryBind(template, values, answers, out JsonNode? bound)
            && bound!["path"]!.ToString() == "/w/clip.gif" && bound["fps"]!.GetValue<int>() == 10 && bound["item"]!.GetValue<int>() == 4,
            "a template fills text, typed parameters and a value of an earlier answer");
        var absent = JsonNode.Parse("{\"item\":{\"$step\":1,\"pointer\":\"/items/3\"}}");
        checks.That(!CompactApplicationMcpTemplate.TryBind(absent, values, answers, out _), "a reference to an absent value does not bind");
        var names = new HashSet<string>(["name", "fps"], StringComparer.Ordinal);
        checks.That(!CompactApplicationMcpTemplate.IsValid(JsonValue.Create("{{other}}"), names, 1), "an undeclared parameter is refused");
        checks.That(!CompactApplicationMcpTemplate.IsValid(JsonNode.Parse("{\"$step\":1,\"pointer\":\"\"}"), names, 1),
            "a reference to the step itself or a later one is refused");
        checks.That(!CompactApplicationMcpTemplate.IsValid(JsonValue.Create("{{ name }"), names, 2),
            "a stray brace pair is refused so a template never means two things");
    }

    private static void CheckSaveAndReuse(Checks checks, string root, string library)
    {
        NodePolicy policy = ApplicationMcpCases.Policy(root, "reuse", [ApplicationMcpCases.Settings()], BackupRequirement.Off);
        string workspace = policy.WorkspaceRoot;
        using (var dispatcher = new ActionDispatcher(new FakeUi(), policy))
        {
            var functions = Functions(dispatcher, library);
            JsonObject saved = Await(functions.SaveAsync(ExportDefinition(Test(workspace, "first.gif", 3)), CancellationToken.None));
            checks.That(saved["saved"]?.GetValue<bool>() == true && saved["verified"]?.GetValue<bool>() == true
                && saved["revision"]?.GetValue<long>() == 1, "a verified test run stores the function as revision 1");
            checks.That(Frames(saved) == 3, "the save reply carries the frame count the node read");
            JsonObject info = Info(functions);
            JsonObject? row = info[CompactApplicationMcpFunctionNames.FunctionsField]?[0] as JsonObject;
            checks.That(row?["version"]?.ToString() == FakeApplicationMcp.Version && row["functions"]?[0]?["name"]?.ToString() == "export"
                && row["functions"]?[0]?["parameters"]?["frames"]?.ToString() == "integer", "ui_info lists the function for this version");
            checks.That(functions.FunctionsListed && !functions.DriftListed, "only the function row is listed");
            CheckDiscovery(checks, dispatcher, functions);
        }
        using (var restarted = new ActionDispatcher(new FakeUi(), policy))
        {
            var functions = Functions(restarted, library);
            JsonObject run = Call(functions, "export", Test(workspace, "second.gif", 5));
            checks.That(run["verified"]?.GetValue<bool>() == true && Frames(run) == 5 && File.Exists(Path.Combine(workspace, "second.gif")),
                "after a restart the stored function runs with fresh inputs and the node checks the new GIF");
            JsonObject again = Call(functions, "export", Test(workspace, "second.gif", 4));
            checks.That(again["verified"]?.GetValue<bool>() == true && Frames(again) == 4, "an output written again counts as written");
        }
    }

    // Wherever an agent looks after ui_info, the stored function is named: a browse, the tool listing and the field called as a function.
    private static void CheckDiscovery(Checks checks, ActionDispatcher dispatcher, CompactApplicationMcpFunctions functions)
    {
        JsonObject? described = functions.Describe("export");
        checks.That(described?["program"]?.ToString() == Program && described["steps"]?.AsArray().Count == 2
            && described["inputSchema"]?["properties"]?["parameters"]?["required"]?.AsArray().Count == 3,
            "a browse by name describes a listed function with the schema of its call");
        checks.That(functions.Describe("unlisted") == null, "a name the latest ui_info did not list is not described");
        var listingArguments = new JsonObject { ["program"] = Program };
        JsonObject listing = Execute(dispatcher, Tool.ApplicationMcpRead, (JsonObject)listingArguments.DeepClone());
        functions.AddToListing(listingArguments, listing);
        checks.That(listing["stored_functions"]?[0]?["name"]?.ToString() == "export" && listing["tools"] != null,
            "the program's tool listing names the stored functions of its version");
        JsonObject schemaAnswer = new JsonObject { ["success"] = true, ["version"] = FakeApplicationMcp.Version };
        functions.AddToListing(new JsonObject { ["program"] = Program, ["schema"] = "import" }, schemaAnswer);
        checks.That(schemaAnswer["stored_functions"] == null, "a schema answer is left as it is");
        JsonObject listed = Await(functions.ListAsync(new JsonObject { ["program"] = Program }, CancellationToken.None));
        checks.That(listed[CompactApplicationMcpFunctionNames.FunctionsField]?[0]?["functions"]?[0]?["name"]?.ToString() == "export",
            "app_mcp_functions called as a function lists the same rows as ui_info");
    }

    private static void CheckFailures(Checks checks, string root, string library)
    {
        NodePolicy policy = ApplicationMcpCases.Policy(root, "failures", [ApplicationMcpCases.Settings()], BackupRequirement.Off);
        string workspace = policy.WorkspaceRoot;
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy);
        var functions = Functions(dispatcher, library);
        JsonObject missing = Call(functions, "export", new JsonObject { ["video"] = "/w/a.mp4" });
        checks.That(Code(missing) == "invalid_parameters" && missing["outcome"]?.ToString() == "not_dispatched",
            "missing parameters are refused before anything runs");
        JsonObject empty = Call(functions, "export", Test(workspace, "empty.gif", 2, video: ""));
        checks.That(Code(empty) == "expectation_failed" && empty["step"]?.GetValue<int>() == 1
            && empty["outcome"]?.ToString() == "may_have_changed", "a step whose answer misses the expected value stops the run");
        JsonObject refused = Call(functions, "export", Test(workspace, "none.gif", 0));
        bool written = File.Exists(Path.Combine(workspace, "none.gif"));
        checks.That(Code(refused) == "step_failed" && refused["step"]?.GetValue<int>() == 2 && !written,
            "a program error stops at its step and names it");
        string stale = Path.Combine(workspace, "stale.gif");
        File.WriteAllBytes(stale, FakeApplicationMcp.Gif(2));
        JsonObject claims = Await(functions.SaveAsync(ClaimsDefinition(stale), CancellationToken.None));
        checks.That(Code(claims) == "check_failed" && claims["saved"] == null, "a file that existed unchanged before does not pass");
        JsonObject unknown = Call(functions, "nothing", new JsonObject());
        checks.That(Code(unknown) == "unknown_function", "an unknown stored name is refused");
        var bad = ExportDefinition(Test(workspace, "bad.gif", 2));
        bad["steps"]![1]!["arguments"]!["path"] = "{{undeclared}}";
        checks.That(Code(Await(functions.SaveAsync(bad, CancellationToken.None))) == "invalid_definition",
            "a definition naming an undeclared parameter is refused");
    }

    private static void CheckDrift(Checks checks, string root, string library)
    {
        NodePolicy first = ApplicationMcpCases.Policy(root, "drift", [ApplicationMcpCases.Settings()], BackupRequirement.Off);
        string workspace = first.WorkspaceRoot;
        using (var dispatcher = new ActionDispatcher(new FakeUi(), first))
        {
            var functions = Functions(dispatcher, library);
            Await(functions.SaveAsync(RememberDefinition(workspace), CancellationToken.None));
            Await(functions.SaveAsync(ImportDefinition(workspace), CancellationToken.None));
        }
        NodePolicy next = ApplicationMcpCases.Policy(root, "drift-next", [ApplicationMcpCases.Settings(next: true)], BackupRequirement.Off);
        using var updated = new ActionDispatcher(new FakeUi(), next);
        var current = Functions(updated, library);
        JsonObject info = Info(current);
        JsonObject? row = info[CompactApplicationMcpFunctionNames.DriftField]?[0] as JsonObject;
        bool listed = info[CompactApplicationMcpFunctionNames.FunctionsField] != null;
        checks.That(!listed && row?["current"]?.ToString() == FakeApplicationMcp.NextVersion,
            "after an update no function of the old version is listed as callable");
        JsonObject? export = Repair(row, "export");
        checks.That(export?["reason"]?.ToString() == "argument_required" && export["step"]?.GetValue<int>() == 2,
            "a newly required argument names the step to repair");
        checks.That(Repair(row, "remember")?["reason"]?.ToString() == "tool_absent", "a removed tool names the step to repair");
        checks.That(row?["test_run_needed"]?.AsArray().Any(name => name?.ToString() == "import") == true,
            "a function whose tools still fit needs only a test run");
        checks.That(Code(Call(current, "export", Test(workspace, "old.gif", 2))) == "unknown_function",
            "a function of the old version is not callable");

        var qualify = new JsonObject
        {
            ["program"] = Program,
            ["name"] = "import",
            ["from_version"] = FakeApplicationMcp.Version,
            ["test"] = new JsonObject { ["video"] = Path.Combine(workspace, "b.mp4") }
        };
        JsonObject qualified = Await(current.SaveAsync(qualify, CancellationToken.None));
        JsonObject after = Info(current);
        checks.That(qualified["saved"]?.GetValue<bool>() == true && qualified["version"]?.ToString() == FakeApplicationMcp.NextVersion
            && after[CompactApplicationMcpFunctionNames.FunctionsField]?[0]?["functions"]?[0]?["name"]?.ToString() == "import",
            "a verified test run stores the old function for the new version");
        checks.That(after[CompactApplicationMcpFunctionNames.DriftField]?[0]?["test_run_needed"] == null,
            "the qualified function leaves the drift row");
    }

    private static void CheckGuards(Checks checks, string root, string library)
    {
        NodePolicy guarded = ApplicationMcpCases.Policy(root, "guarded", [ApplicationMcpCases.Settings()], BackupRequirement.Required);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), guarded))
        {
            JsonObject refused = Call(Functions(dispatcher, library), "export", Test(guarded.WorkspaceRoot, "guarded.gif", 2));
            checks.That(Code(refused) == "step_failed" && refused["error"]?.ToString().StartsWith("backup_required") == true
                && refused["outcome"]?.ToString() == "not_dispatched",
                "each step passes the backup requirement; a refusal changed nothing");
        }
        ApplicationMcpPolicy unversioned = ApplicationMcpCases.Settings();
        unversioned.VersionCommand = [];
        NodePolicy blind = ApplicationMcpCases.Policy(root, "unversioned", [unversioned], BackupRequirement.Off);
        string blindLibrary = Directory.CreateDirectory(Path.Combine(root, "blind-library")).FullName;
        using var noVersion = new ActionDispatcher(new FakeUi(), blind);
        var functions = Functions(noVersion, blindLibrary);
        JsonObject save = Await(functions.SaveAsync(ExportDefinition(Test(blind.WorkspaceRoot, "blind.gif", 2)), CancellationToken.None));
        checks.That(Code(save) == "version_unknown" && !Directory.Exists(Path.Combine(blindLibrary, "app-mcps")),
            "a program without a readable version stores nothing");
    }

    private static JsonObject ExportDefinition(JsonObject test)
    {
        return new JsonObject
        {
            ["program"] = Program,
            ["name"] = "export",
            ["description"] = "Imports a video and exports it as a GIF.",
            ["parameters"] = new JsonArray(Parameter("video", "string"), Parameter("gif", "string"), Parameter("frames", "integer")),
            ["steps"] = new JsonArray(ImportStep(), new JsonObject
            {
                ["tool"] = "export_gif",
                ["arguments"] = JsonNode.Parse("{\"item\":{\"$step\":1,\"pointer\":\"/items/0\"},"
                    + "\"path\":\"{{gif}}\",\"frames\":\"{{frames}}\"}")
            }),
            ["checks"] = JsonNode.Parse("[{\"kind\":\"file_written\",\"path\":\"{{gif}}\"},"
                + "{\"kind\":\"gif_frames\",\"path\":\"{{gif}}\",\"min\":\"{{frames}}\",\"max\":\"{{frames}}\"}]"),
            ["test"] = test
        };
    }

    private static JsonObject ClaimsDefinition(string stale)
    {
        return new JsonObject
        {
            ["program"] = Program,
            ["name"] = "claims",
            ["parameters"] = new JsonArray(Parameter("video", "string")),
            ["steps"] = new JsonArray(ImportStep()),
            ["checks"] = new JsonArray(new JsonObject { ["kind"] = "file_written", ["path"] = stale }),
            ["test"] = new JsonObject { ["video"] = "/w/c.mp4" }
        };
    }

    private static JsonObject RememberDefinition(string workspace)
    {
        return new JsonObject
        {
            ["program"] = Program,
            ["name"] = "remember",
            ["parameters"] = new JsonArray(Parameter("text", "string")),
            ["steps"] = new JsonArray(new JsonObject { ["tool"] = "state_add", ["arguments"] = new JsonObject { ["text"] = "{{text}}" } },
                ImportStep("{{text}}")),
            ["test"] = new JsonObject { ["text"] = Path.Combine(workspace, "r.mp4") }
        };
    }

    private static JsonObject ImportDefinition(string workspace)
    {
        return new JsonObject
        {
            ["program"] = Program,
            ["name"] = "import",
            ["parameters"] = new JsonArray(Parameter("video", "string")),
            ["steps"] = new JsonArray(ImportStep()),
            ["test"] = new JsonObject { ["video"] = Path.Combine(workspace, "a.mp4") }
        };
    }

    private static JsonObject ImportStep(string path = "{{video}}")
    {
        return new JsonObject
        {
            ["tool"] = "import",
            ["arguments"] = new JsonObject { ["paths"] = new JsonArray(path) },
            ["expect"] = JsonNode.Parse("[{\"pointer\":\"/errors\",\"equals\":[]}]")
        };
    }

    private static JsonObject Parameter(string name, string type)
    {
        return new JsonObject { ["name"] = name, ["type"] = type };
    }

    private static JsonObject Test(string workspace, string gif, int frames, string? video = null)
    {
        return new JsonObject
        {
            ["video"] = video ?? Path.Combine(workspace, "demo.mp4"),
            ["gif"] = Path.Combine(workspace, gif),
            ["frames"] = frames
        };
    }

    private static CompactApplicationMcpFunctions Functions(ActionDispatcher dispatcher, string library)
    {
        return new CompactApplicationMcpFunctions(new DispatcherNodeClient(dispatcher, ApplicationMcpCases.Caller()), library);
    }

    private static JsonObject Info(CompactApplicationMcpFunctions functions)
    {
        var info = new JsonObject { [CompactApplicationMcpNames.Field] = new JsonArray(Program) };
        functions.AddToInfoAsync(info, CancellationToken.None).GetAwaiter().GetResult();
        return info;
    }

    private static JsonObject Call(CompactApplicationMcpFunctions functions, string name, JsonObject parameters)
    {
        var arguments = new JsonObject { ["program"] = Program, ["parameters"] = parameters };
        return Await(functions.TryCallAsync(name, arguments, CancellationToken.None))!;
    }

    private static JsonObject? Repair(JsonObject? row, string name)
    {
        return (row?["repair_needed"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(entry => entry["name"]?.ToString() == name);
    }

    private static int? Frames(JsonObject reply)
    {
        var frames = (reply["checks"] as JsonArray ?? []).Select(check => check?["gif_frames"]).OfType<JsonNode>().FirstOrDefault();
        return frames?.GetValue<int>();
    }

    private static string? Code(JsonObject reply)
    {
        return reply["code"]?.ToString();
    }

    private static JsonObject Facts(ActionDispatcher dispatcher, string path)
    {
        return Execute(dispatcher, Tool.ApplicationMcpRead, new JsonObject { ["program"] = Program, ["file"] = path });
    }

    private static JsonObject Execute(ActionDispatcher dispatcher, Tool tool, JsonObject arguments)
    {
        return dispatcher.Execute(new JsonObject { ["action"] = ToolNames.Of(tool), ["args"] = arguments }, ApplicationMcpCases.Caller());
    }

    private static T Await<T>(Task<T> task)
    {
        return task.GetAwaiter().GetResult();
    }
}

// The gateway's view of a node, here the dispatcher in this process, so the gateway classes run against the real node tools.
internal sealed class DispatcherNodeClient(ActionDispatcher dispatcher, RequestContext caller) : ICompactNodeClient
{
    public Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation)
    {
        var request = new JsonObject { ["action"] = ToolNames.Of(tool), ["args"] = arguments.DeepClone() };
        return Task.FromResult(dispatcher.Execute(request, caller));
    }
}
