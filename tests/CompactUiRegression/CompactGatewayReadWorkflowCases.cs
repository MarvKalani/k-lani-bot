using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Read-source bindings keep complete synthetic values in private jobs and block target input when independently owned source state changes.
internal static class CompactGatewayReadWorkflowCases
{
    public static async Task RunAsync()
    {
        await CurrentReadAsync();
        await ChangedSourceAsync();
        await EmptySourceAsync();
    }

    private static async Task<JsonObject> CaptureReadAsync(CompactGatewayCompatibilityEnvironment environment,
        CompactGatewaySession session)
    {
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["full"] = true }, CancellationToken.None);
        var source = view["elements"]!.AsArray().OfType<JsonObject>().Single(element => element["name"]!.ToString() == "Source");

        return await environment.CallAsync("Programming.learn_read", new JsonObject
        {
            ["pid"] = 10,
            ["name"] = "read_source",
            ["topic"] = "Info/Source",
            ["ref"] = source["ref"]!.ToString(),
            ["field"] = "value",
            ["output"] = "source"
        }, session);
    }

    private static async Task CurrentReadAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        var captured = await CaptureReadAsync(environment, environment.Session);
        Require(captured["success"]?.ToString() == "true" && environment.Client.Mutations.Count == 1,
            "read capture dispatches no application input");
        string document = System.IO.File.ReadAllText(environment.File);
        Require(!document.Contains("original source", StringComparison.Ordinal), "source contents are never exported in a recipe");
        environment.Client.Source = "fresh source for reuse";
        var read = await environment.CallAsync("read_source", new JsonObject
        {
            ["pid"] = 10,
            ["parameters"] = new JsonObject()
        });
        Require(read["outputs"]!["source"]?.ToString() == "fresh source for reuse" && environment.Client.Mutations.Count == 1,
            "read function returns actual fresh full source rather than a training value");
    }

    private static async Task ChangedSourceAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        var settings = new CompactWorkflowSettings(Path.Combine(environment.Root, "jobs"), environment.Authority);
        using var session = new CompactGatewaySession(environment.Client, "fixture", environment.Roots, () => "2025-11-25",
            configuration: new(Workflow: settings));
        await CaptureReadAsync(environment, session);
        var captured = await environment.CallAsync("Programming.workflow_capture", new JsonObject
        {
            ["definition"] = Definition(environment.Name)
        }, session);
        Require(captured["success"]?.ToString() == "true", "read→confirmed-output→write workflow installed");
        var awaiting = await environment.CallAsync("workflow.run", new JsonObject
        {
            ["workflow_key"] = "source_transfer",
            ["revision"] = 1,
            ["client_request_id"] = "source",
            ["parameters"] = new JsonObject()
        }, session);
        Require(awaiting["status"]?.ToString() == "awaiting_input" && environment.Client.Mutations.Count == 1,
            "source is read and verified before explicit target decision");
        environment.Client.Source = "externally changed source";
        var refused = await environment.CallAsync("workflow.resume", new JsonObject
        {
            ["job_ref"] = awaiting["job_ref"]!.ToString(),
            ["expected_revision"] = awaiting["revision"]!.DeepClone(),
            ["answers"] = new JsonObject { ["confirm"] = true }
        }, session);
        Require(refused["status"]?.ToString() == "blocked" && environment.Client.Mutations.Count == 1,
            "fresh changed source digest blocks all target input despite a decision");
    }

    private static JsonObject Definition(string writer)
    {
        return new JsonObject
        {
            ["format"] = CompactWorkflowDefinitionCodec.Format,
            ["key"] = "source_transfer",
            ["revision"] = 1,
            ["inputs"] = new JsonObject { ["confirm"] = new JsonObject { ["type"] = "boolean" } },
            ["applications"] = new JsonObject
            {
                ["editor"] = new JsonObject
                {
                    ["key"] = "fixture-editor",
                    ["version"] = "1.0",
                    ["process_name"] = "fixture-editor"
                }
            },
            ["stages"] = new JsonArray(Stage("read", "read_source", new JsonObject()), Stage("write", writer,
                new JsonObject { ["text"] = new JsonObject { ["stage"] = "read", ["output"] = "source" } }, true)),
            ["outputs"] = new JsonObject { ["result"] = new JsonObject { ["stage"] = "write", ["output"] = "value" } }
        };
    }

    private static JsonObject Stage(string id, string function, JsonObject bindings, bool decision = false)
    {
        var stage = new JsonObject
        {
            ["id"] = id,
            ["function_ref"] = new JsonObject { ["application"] = "editor", ["name"] = function },
            ["bindings"] = bindings,
            ["preconditions"] = "fresh_unique_enabled_targets",
            ["postconditions"] = "recorded_function_outputs"
        };
        if (decision)
        {
            stage["decision"] = new JsonObject { ["input"] = "confirm", ["prompt"] = "Transfer the observed source?" };
        }

        return stage;
    }

    private static async Task EmptySourceAsync()
    {
        using var environment = new CompactGatewayCompatibilityEnvironment();
        await environment.CaptureAsync(false);
        environment.Client.Source = "";
        var refused = await CaptureReadAsync(environment, environment.Session);
        Require(refused["success"]?.ToString() == "false" && environment.Client.Mutations.Count == 1,
            "empty source cannot be approved from read dispatch alone");
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCompatibilityEnvironment.Require(condition, name);
    }
}
