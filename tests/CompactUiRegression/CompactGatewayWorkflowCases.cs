using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Routed workflow tests use independently owned synthetic application/ledger state and actual private restartable job/definition files.
internal static class CompactGatewayWorkflowCases
{
    public static async Task RunAsync()
    {
        await RestartAsync();
        await LostResponseAsync();
        await UnknownLedgerAsync();
        await DecisionAsync();
        await InvalidDefinitionsAsync();
        await MissingConfigurationAsync();
        await CaptureSessionAsync();
        await StoreFailuresAsync();
        await FullWriteFailuresAsync();
        await UnsafeStoreAsync();
        await NoLearningSaveAsync();
    }

    private static CompactGatewaySession Session(CompactGatewayCompatibilityEnvironment environment, string? principal = null,
        bool learning = true)
    {
        var settings = new CompactWorkflowSettings(Path.Combine(environment.Root, "jobs"), environment.Authority with
        {
            Principal = principal ?? environment.Authority.Principal
        });

        return new(environment.Client, "fixture", learning ? environment.Roots : environment.Roots with
        {
            Learning = null
        },
            () => "2025-11-25", configuration: new(Workflow: settings));
    }

    private static JsonObject Definition(string function, bool decision = false)
    {
        var inputs = new JsonObject { ["text"] = new JsonObject { ["type"] = "string", ["maxLength"] = 16000 } };
        var stage = new JsonObject
        {
            ["id"] = "write",
            ["function_ref"] = new JsonObject { ["application"] = "editor", ["name"] = function },
            ["bindings"] = new JsonObject { ["text"] = new JsonObject { ["input"] = "text" } },
            ["preconditions"] = "fresh_unique_enabled_targets",
            ["postconditions"] = "recorded_function_outputs"
        };
        if (decision)
        {
            inputs["confirm"] = new JsonObject { ["type"] = "boolean" };
            stage["decision"] = new JsonObject { ["input"] = "confirm", ["prompt"] = "Write the requested field value?" };
        }

        return new JsonObject
        {
            ["format"] = CompactWorkflowDefinitionCodec.Format,
            ["key"] = "routed",
            ["revision"] = 1,
            ["inputs"] = inputs,
            ["applications"] = new JsonObject
            {
                ["editor"] = new JsonObject
                {
                    ["key"] = "fixture-editor",
                    ["version"] = "1.0",
                    ["process_name"] = "fixture-editor"
                }
            },
            ["stages"] = new JsonArray(stage),
            ["outputs"] = new JsonObject { ["result"] = new JsonObject { ["stage"] = "write", ["output"] = "value" } }
        };
    }

    private static async Task InstallAsync(CompactGatewayCompatibilityEnvironment environment, CompactGatewaySession session,
        bool decision = false)
    {
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var capture = await environment.CallAsync("Programming.workflow_capture", new JsonObject
        {
            ["definition"] = Definition(environment.Name, decision)
        }, session);
        Require(capture["success"]?.ToString() == "true" && capture["stored_values"]?.ToString() == "false", "verified workflow installed");
    }

    private static JsonObject Run(string request, string text)
    {
        return new JsonObject
        {
            ["workflow_key"] = "routed",
            ["revision"] = 1,
            ["client_request_id"] = request,
            ["parameters"] = new JsonObject { ["text"] = text }
        };
    }

    private static JsonObject StatusArguments(JsonObject progress)
    {
        return new JsonObject { ["job_ref"] = progress["job_ref"]!.ToString() };
    }

    private static JsonObject Resume(JsonObject progress)
    {
        return new JsonObject { ["job_ref"] = progress["job_ref"]!.ToString(), ["expected_revision"] = progress["revision"]!.DeepClone() };
    }

    private static async Task RestartAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session);
        var running = await environment.CallAsync("workflow.run", Run("once", "first workflow value"), session);
        Require(running["status"]?.ToString() == "completed" && environment.Client.Field == "first workflow value",
            "native routing verifies full workflow output");
        using var restarted = Session(environment, learning: false);
        var status = await environment.CallAsync("workflow.status", StatusArguments(running), restarted);
        Require(status["status"]?.ToString() == "completed", "private job status survives controller restart without node input");
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var duplicate = await environment.CallAsync("workflow.run", Run("once", "first workflow value"), restarted);
        Require(duplicate["job_ref"]!.ToString() == running["job_ref"]!.ToString() && environment.Client.Mutations.Count == 2,
            "same creation request deduplicates without replaying application writes");
        var conflict = await environment.CallAsync("workflow.run", Run("once", "different value"), restarted);
        Require(conflict["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 2,
            "request identity cannot change content");
        var reused = await environment.CallAsync("workflow.run", Run("fresh", "new workflow input"), restarted);
        Require(reused["status"]?.ToString() == "completed" && environment.Client.Field == "new workflow input", "reuse with new input");
        var browse = await restarted.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/Workflows" }, CancellationToken.None);
        Require(browse["workflows"]![0]!["stages"]?.ToString() == "1" && !browse.ToJsonString().Contains("function_ref"),
            "discovery returns counts without child recipes or schemas");
    }

    private static async Task LostResponseAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session);
        environment.Client.Fault = NativeAdapterFault.LostResponse;
        var unconfirmed = await environment.CallAsync("workflow.run", Run("lost", "written once"), session);
        Require(unconfirmed["status"]?.ToString() == "unconfirmed" && environment.Client.Mutations.Count == 2,
            "lost response stores uncertain original operation rather than replaying it");
        using var restarted = Session(environment);
        await restarted.CallAsync("ui_info", new(), CancellationToken.None);
        var resumed = await environment.CallAsync("workflow.resume", Resume(unconfirmed), restarted);
        Require(resumed["status"]?.ToString() == "completed" && environment.Client.Mutations.Count == 2
            && environment.Client.StatusQueries.Count == 1, "restart reconciles original node ledger and full field value without replay");
        var stale = await environment.CallAsync("workflow.resume", Resume(unconfirmed), restarted);
        Require(stale["error"]?.ToString() == "stale_job_revision" && environment.Client.Mutations.Count == 2,
            "stale job revision cannot repeat reconciled input");
    }

    private static async Task UnknownLedgerAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session);
        environment.Client.Fault = NativeAdapterFault.UnknownLedger;
        var unconfirmed = await environment.CallAsync("workflow.run", Run("unknown", "unconfirmed input"), session);
        var resumed = await environment.CallAsync("workflow.resume", Resume(unconfirmed), session);
        Require(resumed["status"]?.ToString() == "unconfirmed" && environment.Client.Mutations.Count == 2,
            "unknown node ledger cannot become implicit permission to retry");
        using var foreign = Session(environment, "other-owner");
        var status = await environment.CallAsync("workflow.status", StatusArguments(unconfirmed), foreign);
        Require(status["success"]?.ToString() == "false", "configured foreign authority cannot read another private job");
    }

    private static async Task DecisionAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session, true);
        var awaiting = await environment.CallAsync("workflow.run", Run("decision", "approved value"), session);
        Require(awaiting["status"]?.ToString() == "awaiting_input" && environment.Client.Mutations.Count == 1,
            "explicit decision is requested before native input");
        var answer = Resume(awaiting);
        answer["answers"] = new JsonObject { ["confirm"] = true };
        var completed = await environment.CallAsync("workflow.resume", answer, session);
        Require(completed["status"]?.ToString() == "completed" && environment.Client.Field == "approved value",
            "typed explicit decision resumes with current node/target checks");
    }

    private static async Task InvalidDefinitionsAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        var invalid = Definition(environment.Name);
        invalid["stages"]![0]!["bindings"]!["text"] = new JsonObject { ["literal"] = "never persist a training answer" };
        var refused = await environment.CallAsync("Programming.workflow_capture", new JsonObject { ["definition"] = invalid }, session);
        Require(refused["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 1, "literal workflow bindings refused");
        await InstallAsync(environment, session);
        var arguments = new JsonObject { ["definition"] = Definition(environment.Name) };
        var duplicate = await environment.CallAsync("Programming.workflow_capture", arguments, session);
        Require(duplicate["success"]?.ToString() == "false", "active workflow key cannot be overwritten by capture");
        string file = Directory.GetFiles(environment.Roots.Library, "workflows.json", SearchOption.AllDirectories).Single();
        Require(!System.IO.File.ReadAllText(file).Contains("synthetic demonstration"),
            "workflow export contains no runtime training values");
    }

    private static async Task MissingConfigurationAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        var refused = await environment.CallAsync("workflow.run", Run("missing", "not sent"));
        Require(refused["success"]?.ToString() == "false" && !Directory.Exists(Path.Combine(environment.Root, "jobs"))
            && environment.Client.Mutations.Count == 1, "absent workflow configuration cannot create jobs or send input");
    }

    private static async Task CaptureSessionAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        using var session = Session(environment);
        var definition = Definition("pending");
        var begin = new JsonObject();
        foreach (string field in new[] { "key", "revision", "inputs", "applications" })
        {
            begin[field] = definition[field]!.DeepClone();
        }
        var begun = await environment.CallAsync("Programming.workflow_begin", begin, session);
        Require(begun["capturing"]?.ToString() == "true", "workflow capture begins without guessed stages");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        string reference = view["elements"]!.AsArray().OfType<JsonObject>()
            .Single(element => element["name"]!.ToString() == "Field")["ref"]!.ToString();
        var written = await session.CallAsync("ui_act", new JsonObject
        {
            ["pid"] = 10,
            ["result_mode"] = "summary",
            ["steps"] = new JsonArray(new JsonObject { ["action"] = "write", ["ref"] = reference, ["text"] = "captured first use" })
        }, CancellationToken.None);
        Require(written["mcp"]!["functions"]!.AsArray().Count == 0, "automatic verified function is collected without being announced");
        var finish = await environment.CallAsync("Programming.workflow_finish", new JsonObject
        {
            ["outputs"] = new JsonObject { ["result"] = new JsonObject { ["stage"] = "stage.1", ["output"] = "value" } }
        }, session);
        Require(finish["success"]?.ToString() == "true" && finish["stages"]?.ToString() == "1",
            "only verified captured function becomes stage");
        var reused = await environment.CallAsync("workflow.run", Run("capture-reuse", "new captured workflow input"), session);
        Require(reused["status"]?.ToString() == "completed" && environment.Client.Field == "new captured workflow input",
            "captured definition reuses a new parameter value");
    }

    private static async Task StoreFailuresAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session);
        string file = Directory.GetFiles(environment.Roots.Library, "workflows.json", SearchOption.AllDirectories).Single();
        string source = System.IO.File.ReadAllText(file);
        var future = JsonNode.Parse(source)!;
        future["format"] = "k-lani-bot-workflows/999";
        System.IO.File.WriteAllText(file, future.ToJsonString());
        var refused = await environment.CallAsync("workflow.run", Run("future", "never sent"), session);
        Require(refused["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 1,
            "unknown store format is refused before native input");
        Require(System.IO.File.ReadAllText(file) == future.ToJsonString(), "unknown file remains untouched");
        System.IO.File.WriteAllText(file, "{");
        var malformed = await environment.CallAsync("workflow.run", Run("malformed", "never sent"), session);
        Require(malformed["success"]?.ToString() == "false" && System.IO.File.ReadAllText(file) == "{",
            "invalid JSON remains untouched and cannot start an application stage");
    }

    private static async Task FullWriteFailuresAsync()
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail })
        {
            using var environment = new CompactGatewayCompatibilityEnvironment();
            await environment.CaptureAsync(false);
            using var session = Session(environment);
            await InstallAsync(environment, session);
            environment.Client.Fault = fault;
            string text = fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "expected replacement";
            var result = await environment.CallAsync("workflow.run", Run("mismatch", text), session);
            Require(result["status"]?.ToString() == "unconfirmed" && environment.Client.Mutations.Count == 2,
                "full workflow field mismatch remains unconfirmed without retry");
        }
    }

    private static async Task UnsafeStoreAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment);
        await InstallAsync(environment, session);
        string file = Directory.GetFiles(environment.Roots.Library, "workflows.json", SearchOption.AllDirectories).Single();
        string outside = Path.Combine(environment.Root, "external.json");
        string source = System.IO.File.ReadAllText(file);
        System.IO.File.WriteAllText(outside, source);
        System.IO.File.Delete(file);
        System.IO.File.CreateSymbolicLink(file, outside);
        var refused = await environment.CallAsync("workflow.run", Run("linked", "not sent"), session);
        Require(refused["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 1
            && System.IO.File.ReadAllText(outside) == source, "linked workflow storage is neither read for execution nor overwritten");
    }

    private static async Task NoLearningSaveAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        using var session = Session(environment, learning: false);
        var refused = await environment.CallAsync("Programming.workflow_capture", new JsonObject
        {
            ["definition"] = Definition(environment.Name)
        }, session);
        Require(refused["success"]?.ToString() == "false"
            && Directory.GetFiles(environment.Roots.Library, "workflows.json", SearchOption.AllDirectories).Length == 0,
            "readonly workflow use cannot save new definitions without a learning root");
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCompatibilityEnvironment.Require(condition, name);
    }
}
