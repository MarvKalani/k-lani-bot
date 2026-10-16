using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// The trace had one budget for everything and stopped recording when it was full. Refused requests
// filled it as well, so about 200 bad requests switched off the record of the real work.
internal static class ActionTraceCases
{
    private static readonly byte[] Image = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Action trace: " + name);
            }
            passed++;
        }
        RefusalsCannotDisplaceExecutedSteps(Check, temporaryRoot);
        RotationIsJournaledAndBounded(Check, temporaryRoot);
        StopKeepsTheOldGuarantee(Check, temporaryRoot);
        return passed;
    }

    private static void RefusalsCannotDisplaceExecutedSteps(Action<bool, string> check, string temporaryRoot)
    {
        NodePolicy policy = RecordingPolicy(temporaryRoot, "trace-refusals");
        var ui = new FakeUi { ScreenshotEnabled = true };
        using var dispatcher = new ActionDispatcher(ui, policy);
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject Call(string action, JsonObject arguments)
        {
            return dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, caller);
        }
        for (int step = 0; step < 3; step++)
        {
            string snapshot = Call("get_ui_tree", new JsonObject())["snapshot_id"]!.ToString();
            JsonObject clicked = Call("click_element", Click(snapshot));
            check(clicked["success"]?.GetValue<bool>() == true, "executed click " + step);
        }
        string traces = Path.Combine(policy.StateRoot, "action-traces");
        string[] executedBefore = Directory.GetFiles(traces);
        check(executedBefore.Length > 0, "the executed steps were recorded");
        for (int refusal = 0; refusal < 1000; refusal++)
        {
            JsonObject refused = Call("click_element", Click("00000000-0000-4000-8000-000000000000"));
            check(refused["outcome"]?.ToString() == "not_dispatched", "refused click " + refusal);
        }

        string[] executedSteps = Directory.GetFiles(traces, "*.json");
        bool allKept = executedBefore.All(File.Exists);
        check(allKept, "1000 refusals leave every executed step and its images in place");
        int rejectedSteps = Directory.GetFiles(Path.Combine(traces, "rejected")).Length;
        check(rejectedSteps == ActionTrace.RejectedStepLimit, "refusals stay in their own budget");
        check(!Directory.Exists(Path.Combine(traces, "archive")), "refusals never rotate executed steps");
        string viewer = File.ReadAllText(Path.Combine(traces, "index.html"));
        int shownSteps = Regex.Matches(viewer, "<details").Count;
        check(shownSteps == executedSteps.Length + ActionTrace.RejectedStepLimit, "the page shows both budgets");
    }

    // A desktop principal whose actions are recorded with screenshots, in its own state and workspace.
    private static NodePolicy RecordingPolicy(string temporaryRoot, string name)
    {
        string state = Path.Combine(temporaryRoot, name + "-state");
        string workspace = Path.Combine(temporaryRoot, name + "-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);
        var policy = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = state,
            WorkspaceRoot = workspace,
            Principals = [new PrincipalPolicy(
                "operator",
                new string('a', 64),
                [Scope.Observe, Scope.Ui],
                [],
                DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
            }]
        };
        policy.Visibility = policy.Visibility with
        {
            RecordActionScreenshots = true
        };
        return policy;
    }

    private static JsonObject Click(string snapshot)
    {
        return new JsonObject
        {
            ["id"] = 1,
            ["snapshot_id"] = snapshot,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
    }

    private static void RotationIsJournaledAndBounded(Action<bool, string> check, string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "trace-rotation-state");
        Directory.CreateDirectory(state);
        var audit = new List<JsonObject>();
        var trace = new ActionTrace(state, TraceRetention.Rotate, audit.Add);
        var recorded = new List<string>();
        int total = ActionTrace.ExecutedStepLimit + ActionTrace.ArchivedStepLimit + 5;
        for (int step = 0; step < total; step++)
        {
            recorded.Add(SaveExecutedStep(trace));
        }
        string traces = Path.Combine(state, "action-traces");
        string archive = Path.Combine(traces, "archive");
        check(Directory.GetFiles(traces, "*.json").Length == ActionTrace.ExecutedStepLimit, "the executed budget holds");
        check(Directory.GetFiles(traces, "*.png").Length == ActionTrace.ExecutedStepLimit * 2, "images rotate with their step");
        check(Directory.GetFiles(archive, "*.json").Length == ActionTrace.ArchivedStepLimit, "the archive is bounded");
        List<JsonObject> rotated = audit.Where(entry => entry["event"]?.ToString() == "trace_rotated").ToList();
        List<JsonObject> discarded = audit.Where(entry => entry["event"]?.ToString() == "trace_discarded").ToList();
        check(rotated.Count == total - ActionTrace.ExecutedStepLimit, "every rotation is journaled");
        check(discarded.Count == 5, "every deletion from the archive is journaled");
        check(rotated[0]["operation_id"]?.ToString() == recorded[0], "the oldest step rotates first");
        check(discarded[0]["operation_id"]?.ToString() == recorded[0], "the oldest archived step is deleted first");
        JsonObject lastRotation = rotated[^1];
        foreach (JsonNode? file in lastRotation["files"]!.AsArray())
        {
            string archived = Path.Combine(archive, file!["file"]!.ToString());
            string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archived)));
            check(digest == file["sha256"]!.ToString(), "the journal holds the digest of " + file["file"]);
        }
        check(lastRotation["files"]!.AsArray().Count == 3, "a rotation names the JSON and both images");
        string viewer = File.ReadAllText(Path.Combine(traces, "index.html"));
        MatchCollection links = Regex.Matches(viewer, "src='([^']+)'");
        check(links.Count == ActionTrace.ExecutedStepLimit * 2, "the page links the images of the steps on disk");
        bool allLinksExist = links.All(link => File.Exists(Path.Combine(traces, link.Groups[1].Value)));
        check(allLinksExist, "no image link of the page points to a rotated file");
        check(!viewer.Contains(recorded[0], StringComparison.Ordinal), "rotated steps leave the page");
    }

    private static void StopKeepsTheOldGuarantee(Action<bool, string> check, string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "trace-stop-state");
        Directory.CreateDirectory(state);
        var audit = new List<JsonObject>();
        var trace = new ActionTrace(state, TraceRetention.Stop, audit.Add);
        for (int step = 0; step < ActionTrace.ExecutedStepLimit; step++)
        {
            SaveExecutedStep(trace);
        }
        string? refusal = null;
        try
        {
            SaveExecutedStep(trace);
        }
        catch (IOException exception)
        {
            refusal = exception.Message;
        }
        check(refusal == "Local trace budget reached. Archive traces before recording more.", "a full trace stops with a visible error");
        string traces = Path.Combine(state, "action-traces");
        check(Directory.GetFiles(traces, "*.json").Length == ActionTrace.ExecutedStepLimit, "nothing was displaced");
        check(!Directory.Exists(Path.Combine(traces, "archive")) && audit.Count == 0, "nothing was moved or deleted");
        var policyFile = new JsonObject { ["traceRetention"] = "stop" };
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<NodePolicy>(policyFile.ToJsonString(), options);
        check(parsed!.TraceRetention == TraceRetention.Stop, "the policy spells the mode stop");
        check(new NodePolicy().TraceRetention == TraceRetention.Rotate, "rotation is the default");
    }

    private static string SaveExecutedStep(ActionTrace trace)
    {
        string operationId = Guid.CreateVersion7().ToString();
        var screenshots = new JsonObject
        {
            ["before"] = new JsonObject { ["file"] = trace.SaveScreenshot(operationId, "before", Image) },
            ["after"] = new JsonObject { ["file"] = trace.SaveScreenshot(operationId, "after", Image) }
        };
        var receipt = new JsonObject
        {
            ["operation_id"] = operationId,
            ["action"] = "click_element",
            ["success"] = true,
            ["outcome"] = "dispatched"
        };
        trace.SaveStep(operationId, receipt, screenshots);
        return operationId;
    }
}
