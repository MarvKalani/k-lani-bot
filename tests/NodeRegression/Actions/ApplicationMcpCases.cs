using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Guard;

// Programmable App MCPs: a program's own MCP server listed in the policy is used through app_mcp_read and app_mcp_call.
// The checks run the dispatcher against FakeApplicationMcp in a child process: listing over two pages, read-only tools only
// through app_mcp_read, changes behind the backup requirement, state kept per session, bounded text and images, and a program
// that crashes, misses its deadline or asks the node a question.
internal static class ApplicationMcpCases
{
    private const string Program = "fake";

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Programmable App MCPs");
        string root = Path.Combine(temporaryRoot, "app-mcp-" + Guid.CreateVersion7().ToString("N"));
        CheckPolicy(checks);
        CheckAvailability(checks, root);
        CheckReadAndBackup(checks, root);
        CheckSessions(checks, root);
        CheckBoundedAnswers(checks, root);
        CheckFailures(checks, root);

        return checks.Passed;
    }

    private static void CheckPolicy(Checks checks)
    {
        checks.That(Refuses(new ApplicationMcpPolicy { Id = "fake", Command = ["relative/tool"] }), "a relative program path is refused");
        checks.That(Refuses(new ApplicationMcpPolicy { Id = "Fake!", Command = ["/bin/true"] }), "an id with other characters is refused");
        checks.That(Refuses(new ApplicationMcpPolicy { Id = "fake", Command = ["/bin/true"], ReadTools = ["a", "a"] }),
            "repeated read tools are refused");
        checks.That(Refuses(new ApplicationMcpPolicy { Id = "fake", Command = ["/bin/true"], CallTimeoutSeconds = 0 }),
            "a call timeout outside 1 to 3600 seconds is refused");
        checks.That(!Refuses(Settings()), "the fake program's settings are valid");
    }

    private static void CheckAvailability(Checks checks, string root)
    {
        NodePolicy without = Policy(root, "without", [], BackupRequirement.Off);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), without))
            checks.That(!ActiveTools(dispatcher).Contains("app_mcp_read"), "a node without listed programs does not offer the tools");
        NodePolicy with = Policy(root, "with", [Settings()], BackupRequirement.Off);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), with))
            checks.That(ActiveTools(dispatcher).Contains("app_mcp_call"), "listed programs and granted scopes offer the tools");
        NodePolicy readOnly = Policy(root, "read-only", [Settings()], BackupRequirement.Off, [Scope.Observe, Scope.ApplicationRead]);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), readOnly))
            checks.That(Execute(dispatcher, Call("state_add"), Caller())["code"]?.ToString() == "denied", "app_mcp_call needs app.call");
        using (var dispatcher = new ActionDispatcher(new FakeUi(), with))
            checks.That(Capabilities(dispatcher)["applicationMcps"]?.AsArray().Single()?.ToString() == Program,
                "the capabilities name the listed program to a principal with the app scopes");
        NodePolicy observer = Policy(root, "observer", [Settings()], BackupRequirement.Off, [Scope.Observe]);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), observer))
            checks.That(Capabilities(dispatcher)["applicationMcps"]?.AsArray().Count == 0,
                "a principal without the app scopes is told of no program");
    }

    private static void CheckReadAndBackup(Checks checks, string root)
    {
        using var dispatcher = new ActionDispatcher(new FakeUi(), Policy(root, "backup", [Settings()], BackupRequirement.Required));
        RequestContext caller = Caller();
        JsonObject listing = Execute(dispatcher, Read(null), caller);
        checks.That(listing["version"]?.ToString() == FakeApplicationMcp.Version, "the listing names the program's version");
        checks.That(listing["tools"]?.AsArray().Count == 9, "the listing joins both pages of the program's tool list");
        checks.That(Execute(dispatcher, Read("state_read"), caller)["text"]?.ToString() == "items: ", "a read tool runs through read");
        JsonObject notRead = Execute(dispatcher, Read("state_add"), caller);
        checks.That(notRead["success"]?.GetValue<bool>() == false && notRead["error"]?.ToString().Contains("app_mcp_call") == true,
            "a tool the operator did not mark read-only is refused by app_mcp_read");
        JsonObject refused = Execute(dispatcher, Call("state_add"), caller);
        checks.That(refused["code"]?.ToString() == "backup_required" && refused["outcome"]?.ToString() == "not_dispatched",
            "a change through the program needs a current backup first");
        checks.That(Execute(dispatcher, Read("state_read"), caller)["text"]?.ToString() == "items: ", "the refused change did nothing");
        JsonObject schema = Execute(dispatcher, Schema("state_add"), caller);
        checks.That(schema["inputSchema"]?["type"]?.ToString() == "object", "one tool's schema loads on demand");

        ApplicationMcpPolicy copiesOnly = Settings();
        copiesOnly.BackupRequirement = BackupRequirement.Off;
        using var exempted = new ActionDispatcher(new FakeUi(), Policy(root, "program-off", [copiesOnly], BackupRequirement.Required));
        checks.That(Execute(exempted, Call("state_add"), Caller())["success"]?.GetValue<bool>() == true,
            "the administrator can switch the backup requirement off for one program");
        checks.That(Refuses(new ApplicationMcpPolicy { Id = "fake", Command = ["/bin/true"], BackupRequirement = (BackupRequirement)7 }),
            "an unknown backup setting for a program is refused");
    }

    private static void CheckSessions(Checks checks, string root)
    {
        using var dispatcher = new ActionDispatcher(new FakeUi(), Policy(root, "sessions", [Settings()], BackupRequirement.Off));
        RequestContext caller = Caller();
        Execute(dispatcher, Call("state_add", "one"), caller);
        JsonObject second = Execute(dispatcher, Call("state_add", "two"), caller);
        checks.That(second["text"]?.ToString() == "count 2", "the program keeps its state between calls of one session");
        checks.That(Execute(dispatcher, Read("state_read"), Caller())["text"]?.ToString() == "items: ",
            "another session gets its own program");
        var ending = new JsonObject { ["request_id"] = Guid.CreateVersion7().ToString() };
        checks.That(dispatcher.Execute(new JsonObject { ["action"] = "end_session", ["args"] = ending }, caller)["success"]
            ?.GetValue<bool>() == true, "the session ends");
        checks.That(Execute(dispatcher, Read("state_read"), caller)["text"]?.ToString() == "items: ",
            "ending the session stops its program");
        JsonObject unknown = Execute(dispatcher, Call("missing"), caller);
        checks.That(unknown["success"]?.GetValue<bool>() == false && unknown["outcome"]?.ToString() == "not_dispatched",
            "an unknown tool is refused before anything runs");
        JsonObject other = Execute(dispatcher, new JsonObject { ["program"] = "other" }, caller, "app_mcp_read");
        checks.That(other["error"]?.ToString().Contains("no program") == true, "a program the policy does not list is refused");
    }

    private static void CheckBoundedAnswers(Checks checks, string root)
    {
        using var dispatcher = new ActionDispatcher(new FakeUi(), Policy(root, "bounded", [Settings()], BackupRequirement.Off));
        RequestContext caller = Caller();
        JsonObject first = Execute(dispatcher, Call("long_text", window: new JsonObject { ["max_chars"] = 1000 }), caller);
        checks.That(first["text"]?.ToString().Length == 1000 && first["next_offset"]?.GetValue<int>() == 1000
            && first["total_chars"]?.GetValue<int>() == FakeApplicationMcp.LongTextCharacters, "a long text comes in pages");
        JsonObject last = Execute(dispatcher, Call("long_text", window: new JsonObject { ["offset"] = 9500 }), caller);
        checks.That(last["text"]?.ToString().Length == 500 && last["next_offset"] == null, "the last page has no next offset");
        JsonObject plain = Execute(dispatcher, Call("picture"), caller);
        checks.That(plain["images"] == null && plain["images_omitted"]?.GetValue<int>() == 1, "images are left out unless asked");
        checks.That(plain["serverOnlyField"] == null, "fields the program adds do not reach the agent");
        JsonObject withImage = Execute(dispatcher, Call("picture", window: new JsonObject { ["images"] = true }), caller);
        checks.That(withImage["images"]?.AsArray().Count == 1, "an image comes when the agent asks for it");
    }

    private static void CheckFailures(Checks checks, string root)
    {
        ApplicationMcpPolicy quick = Settings();
        quick.CallTimeoutSeconds = 1;
        using var dispatcher = new ActionDispatcher(new FakeUi(), Policy(root, "failures", [quick], BackupRequirement.Off));
        RequestContext caller = Caller();
        Execute(dispatcher, Call("state_add", "kept"), caller);
        checks.That(Execute(dispatcher, Call("crash"), caller)["code"]?.ToString() == "program_stopped", "a crash is reported");
        checks.That(Execute(dispatcher, Read("state_read"), caller)["text"]?.ToString() == "items: ",
            "after a crash the program starts fresh; nothing is replayed");
        checks.That(Execute(dispatcher, Call("slow"), caller)["code"]?.ToString() == "program_timed_out",
            "a program that misses its deadline is stopped");
        checks.That(Execute(dispatcher, Read("state_read"), caller)["success"]?.GetValue<bool>() == true, "the next call works again");
        checks.That(Execute(dispatcher, Call("ask"), caller)["text"]?.ToString() == "refused with -32601",
            "a question the program asks the node is refused");
    }

    private static bool Refuses(ApplicationMcpPolicy settings)
    {
        try
        {
            settings.Validate();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    // The next version serves the fake's later release, so a stored function of the first version drifts.
    public static ApplicationMcpPolicy Settings(bool next = false)
    {
        string self = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        bool hosted = Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet";
        string[] prefix = hosted ? [Environment.ProcessPath!, self] : [Environment.ProcessPath!];
        return new ApplicationMcpPolicy
        {
            Id = Program,
            Command = [.. prefix, next ? FakeApplicationMcp.NextServeArgument : FakeApplicationMcp.ServeArgument],
            VersionCommand = [.. prefix, next ? FakeApplicationMcp.NextVersionArgument : FakeApplicationMcp.VersionArgument],
            ReadTools = ["state_read"],
            CallTimeoutSeconds = 30
        };
    }

    public static NodePolicy Policy(string root, string name, ApplicationMcpPolicy[] programs, BackupRequirement requirement,
        Scope[]? scopes = null)
    {
        Scope[] granted = scopes ?? [Scope.Observe, Scope.ApplicationRead, Scope.ApplicationCall];
        return new NodePolicy
        {
            NodeId = "app-mcp-node",
            StateRoot = Directory.CreateDirectory(Path.Combine(root, name, "state")).FullName,
            WorkspaceRoot = Directory.CreateDirectory(Path.Combine(root, name, "workspace")).FullName,
            BackupRequirement = requirement,
            ApplicationMcps = programs,
            Principals = [new PrincipalPolicy("operator", new string('a', 64), granted, [], DateTimeOffset.UtcNow.AddHours(48))]
        };
    }

    private static string[] ActiveTools(ActionDispatcher dispatcher)
    {
        return (Capabilities(dispatcher)["activeTools"] as JsonArray ?? []).Select(tool => tool!.ToString()).ToArray();
    }

    private static JsonObject Capabilities(ActionDispatcher dispatcher)
    {
        JsonObject answer = dispatcher.Execute(new JsonObject { ["action"] = "get_machine_capabilities", ["args"] = new JsonObject() },
            Caller());
        return answer["capabilities"] as JsonObject ?? new JsonObject();
    }

    private static JsonObject Read(string? tool)
    {
        var arguments = new JsonObject { ["program"] = Program };
        if (tool != null)
            arguments["tool"] = tool;
        return arguments;
    }

    private static JsonObject Schema(string tool)
    {
        return new JsonObject { ["program"] = Program, ["schema"] = tool };
    }

    private static JsonObject Call(string tool, string? text = null, JsonObject? window = null)
    {
        var arguments = window ?? new JsonObject();
        arguments["program"] = Program;
        arguments["tool"] = tool;
        arguments["request_id"] = Guid.CreateVersion7().ToString();
        if (text != null)
            arguments["arguments"] = new JsonObject { ["text"] = text };
        return arguments;
    }

    private static JsonObject Execute(ActionDispatcher dispatcher, JsonObject arguments, RequestContext caller, string? tool = null)
    {
        string action = tool ?? (arguments.ContainsKey("request_id") ? "app_mcp_call" : "app_mcp_read");
        return dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, caller);
    }

    public static RequestContext Caller()
    {
        return new RequestContext("operator", Guid.CreateVersion7().ToString());
    }
}
