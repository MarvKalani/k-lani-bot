using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Use case: the ui_apply regression; refusals are checked before any input, and an applied request reports what each step changed.
internal static class CompactGatewayApplyCases
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-compact-apply-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await InvalidStepsAsync(root);
            await UnknownPathAsync(root);
            await MemberKindsAsync(root);
            await PositionalNamesAsync(root);
            await AppliedStepsAsync(root);
            await LaterStepRefusedAsync(root);
            await WaitStepsAsync(root);
            await NodePostconditionAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // A set step needs a value; a malformed step refuses the whole request.
    private static async Task InvalidStepsAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        using var session = await StartAsync(root, "invalid", client);
        var reply = await session.CallAsync("ui_apply", Request(new JsonObject { ["set"] = "field" }), CancellationToken.None);
        Require(reply["error"]?.ToString() == "invalid_steps" && reply["hint"]?.ToString() == CompactViewNames.ApplyStepsHint,
            "a set step without a value is refused as invalid_steps");
    }

    // An unknown path is refused with the declaration, so the caller can see which paths exist.
    private static async Task UnknownPathAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        using var session = await StartAsync(root, "unknown", client);
        var reply = await session.CallAsync("ui_apply", Request(new JsonObject { ["set"] = "nothing", ["value"] = "x" }),
            CancellationToken.None);
        Require(reply["error"]?.ToString() == "unknown_path" && reply["failed_step"]?.GetValue<int>() == 1
            && reply["declaration"] != null, "an unknown path is refused with its declaration and failed step");
        Require(client.Field == "old", "an unknown path sends no input to the window");
    }

    // A choice cannot be set yet and a text field cannot be called; both are refused with the reason.
    private static async Task MemberKindsAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        using var session = await StartAsync(root, "members", client);
        var choice = await session.CallAsync("ui_apply", Request(new JsonObject { ["set"] = "choice", ["value"] = "x" }),
            CancellationToken.None);
        Require(choice["error"]?.ToString() == "not_settable" && choice["hint"]?.ToString() == CompactViewNames.ApplySetHint,
            "a choice cannot be set and says why");
        var field = await session.CallAsync("ui_apply", Request(new JsonObject { ["call"] = "field" }), CancellationToken.None);
        Require(field["error"]?.ToString() == "not_callable" && field["hint"]?.ToString() == CompactViewNames.ApplyCallHint,
            "a text field cannot be called and says why");
        Require(client.Field == "old", "refused member steps send no input to the window");
    }

    // An unnamed field is named by position, so a step on it must carry the shape of the state it was read from;
    // the state read names the apply step as its next call.
    private static async Task PositionalNamesAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true, UnnamedField = true };
        using var session = await StartAsync(root, "positional", client);
        var missing = await session.CallAsync("ui_apply", Request(new JsonObject { ["set"] = "text", ["value"] = "x" }),
            CancellationToken.None);
        Require(missing["error"]?.ToString() == "shape_required" && missing["shape"] != null
            && missing["declaration"] != null, "a positional name without a shape is refused as shape_required");
        var stale = await session.CallAsync("ui_apply",
            Request(new JsonObject { ["set"] = "text", ["value"] = "x" }, "0000000000000000"), CancellationToken.None);
        Require(stale["error"]?.ToString() == "shape_changed", "a positional name with another shape is refused");
        Require(client.Field == "old", "refused positional steps send no input to the window");
        var observed = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["as"] = "state" },
            CancellationToken.None);
        Require(observed["next"]?.ToString() == CompactViewNames.ApplyHint, "a state read names the apply step as its next call");
        var passed = await session.CallAsync("ui_apply",
            Request(new JsonObject { ["set"] = "text", ["value"] = "z" }, observed["shape"]!.GetValue<string>()),
            CancellationToken.None);
        Require(passed["success"]?.GetValue<bool>() == true && passed["verified"]?.GetValue<bool>() == true,
            "the shape of the state read passes the guard and the write is verified");
    }

    // Each step reports only its own change, so the write and the save after it stay visible as two separate changes.
    private static async Task AppliedStepsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, ClosedMenu = true, FileMenu = true, RenameAfterWrite = true };
        using var session = await StartAsync(root, "applied", client);
        var reply = await session.CallAsync("ui_apply",
            StepsRequest(SetStep("field", "applied"), new JsonObject { ["call"] = "file.save" }), CancellationToken.None);
        Require(reply["success"]?.GetValue<bool>() == true && reply["verified"]?.GetValue<bool>() == true,
            "a valid request of two steps is applied and verified");
        Require(StepAt(reply, 0)?["changed"]?["values"]?["field"]?.ToJsonString() == "[\"old\",\"applied\"]",
            "the write reports the field value it changed");
        Require(StepAt(reply, 0)?["changed"]?["title"] != null, "the write reports the window title it changed");
        Require(StepAt(reply, 1)?["changed"]?["enabled"]?["file.save"]?.ToJsonString() == "false",
            "the save reports that Save is disabled again");
        Require(client.Field == "saved", "the save writes the field value to the window");
    }

    // A later step with an unknown path stops the request after the completed step, and the completed write stays.
    private static async Task LaterStepRefusedAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        using var session = await StartAsync(root, "later", client);
        var reply = await session.CallAsync("ui_apply",
            StepsRequest(SetStep("field", "x"), new JsonObject { ["set"] = "nothing", ["value"] = "y" }), CancellationToken.None);
        Require(reply["error"]?.ToString() == "unknown_path" && reply["failed_step"]?.GetValue<int>() == 2
            && reply["completed_steps"]?.GetValue<int>() == 1 && StepCount(reply) == 1,
            "a later unknown path stops after the completed step and names the failed step");
        Require(reply["success"]?.GetValue<bool>() == false && reply["verified"]?.GetValue<bool>() == false,
            "the stopped request is neither successful nor verified");
        Require(client.Field == "x", "the completed step keeps its write and the refused step sends no input");
    }

    // A wait pauses until the awaited state holds; a state that never holds ends as wait_not_met and sends no input.
    private static async Task WaitStepsAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        using var session = await StartAsync(root, "waits", client);
        var applied = await session.CallAsync("ui_apply",
            StepsRequest(SetStep("field", "w"), WaitStep("field", "value", "w", 500)), CancellationToken.None);
        Require(applied["success"]?.GetValue<bool>() == true && StepAt(applied, 1)?["wait"]?.ToString() == "field",
            "a wait for the value that the previous step wrote succeeds and is reported as a step");
        var never = await session.CallAsync("ui_apply", Request(WaitStep("field", "value", "never", 300)),
            CancellationToken.None);
        Require(never["error"]?.ToString() == "wait_not_met" && never["failed_step"]?.GetValue<int>() == 1
            && never["failed_step_may_have_changed_ui"]?.GetValue<bool>() == false,
            "a wait that never reaches its value ends as wait_not_met without possible UI change");
        Require(client.Field == "w", "the wait that timed out sends no input, so the field keeps the earlier write");
        var absent = await session.CallAsync("ui_apply", Request(WaitStep("nothing", "absent", null, 300)),
            CancellationToken.None);
        Require(absent["success"]?.GetValue<bool>() == true, "a wait for a path that is absent succeeds");
        var malformed = await session.CallAsync("ui_apply", Request(new JsonObject { ["wait"] = "field", ["until"] = "value" }),
            CancellationToken.None);
        Require(malformed["error"]?.ToString() == "invalid_steps", "a value wait without a value is refused as invalid_steps");
    }

    // A wait on a named member right after an action rides on the node postcondition; a positional member keeps polling.
    private static async Task NodePostconditionAsync(string root)
    {
        var client = new NativeAdapterFixture { ClosedMenu = true, Dialog = true };
        var received = new List<JsonObject>();
        client.BeforeMutation = arguments => received.Add(arguments.DeepClone().AsObject());
        using var session = await StartAsync(root, "postcondition", client);
        var named = await session.CallAsync("ui_apply",
            StepsRequest(SetStep("field", "p"), WaitStep("field", "value", "p", 700)), CancellationToken.None);
        var sent = received.Count == 1 ? received[0]["postcondition"] as JsonObject : null;
        Require(named["success"]?.GetValue<bool>() == true && StepAt(named, 1)?["postcondition"]?.ToString() == "node",
            "a wait on a named member after a set is marked as ridden on the node");
        Require(sent != null && sent["name"] != null && sent["role"] != null && sent["pid"] == null
            && sent["expected_state"]?.ToString() == "exists" && sent["expected_text"]?.ToString() == "p"
            && sent["timeout_ms"]?.GetValue<int>() == 700, "the set request carries the postcondition with the expected words");

        var declaredKeys = DeclaredPostconditionKeys();
        Require(sent != null && sent.All(pair => declaredKeys.Contains(pair.Key)),
            "every postcondition key sent is declared by the node schema in contracts/tools.json");

        var positionalClient = new NativeAdapterFixture { ClosedMenu = true, Dialog = true, UnnamedField = true };
        var positionalReceived = new List<JsonObject>();
        positionalClient.BeforeMutation = arguments => positionalReceived.Add(arguments.DeepClone().AsObject());
        using var positionalSession = await StartAsync(root, "postcondition-positional", positionalClient);
        var observed = await positionalSession.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["as"] = "state" },
            CancellationToken.None);
        var request = StepsRequest(SetStep("text", "z"), WaitStep("text", "value", "z", 700));
        request["shape"] = observed["shape"]!.GetValue<string>();
        var positional = await positionalSession.CallAsync("ui_apply", request, CancellationToken.None);
        Require(positional["success"]?.GetValue<bool>() == true && StepAt(positional, 1)?["postcondition"] == null
            && positionalReceived.Count == 1 && positionalReceived[0]["postcondition"] == null,
            "a wait on a positional member keeps polling and sends no postcondition");
    }

    // The node refuses unknown postcondition arguments, so the allowed keys come from its published schema.
    private static HashSet<string> DeclaredPostconditionKeys()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "contracts", "tools.json")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? throw new FileNotFoundException("contracts/tools.json"),
            "contracts", "tools.json");
        var tools = JsonNode.Parse(File.ReadAllText(path))!["tools"]!.AsArray();
        var clickElement = tools.First(tool => tool!["name"]!.ToString() == "click_element")!;
        var properties = clickElement["inputSchema"]!["properties"]!["postcondition"]!["properties"]!.AsObject();

        return properties.Select(pair => pair.Key).ToHashSet();
    }

    private static JsonObject WaitStep(string path, string until, string? value, int timeoutMilliseconds)
    {
        var step = new JsonObject { ["wait"] = path, ["until"] = until, ["timeout_ms"] = timeoutMilliseconds };
        if (value != null)
        {
            step["value"] = value;
        }

        return step;
    }

    private static async Task<CompactGatewaySession> StartAsync(string root, string name, NativeAdapterFixture client)
    {
        var session = new CompactGatewaySession(client, "fixture", CompactGatewayCases.WindowRoots(root, name), () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);

        return session;
    }

    private static JsonObject Request(JsonObject step, string? shape = null)
    {
        var request = new JsonObject { ["pid"] = 10, ["steps"] = new JsonArray(step.DeepClone()) };
        if (shape != null)
        {
            request["shape"] = shape;
        }

        return request;
    }

    private static JsonObject StepsRequest(params JsonObject[] steps)
    {
        var array = new JsonArray();
        foreach (var step in steps)
        {
            array.Add(step);
        }

        return new JsonObject { ["pid"] = 10, ["steps"] = array };
    }

    private static JsonObject SetStep(string path, string value)
    {
        return new JsonObject { ["set"] = path, ["value"] = value };
    }

    private static JsonNode? StepAt(JsonObject reply, int index)
    {
        var steps = reply["steps"] as JsonArray;

        return steps != null && index < steps.Count ? steps[index] : null;
    }

    private static int StepCount(JsonObject reply)
    {
        return (reply["steps"] as JsonArray)?.Count ?? 0;
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}
