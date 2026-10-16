using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Guard;
using KLaniBot.Transport;
using KLaniBot.Visibility;

// The worker loop used to sit inside Program and could only be reached through a real process. These
// cases drive it with streams: one answer per frame in arrival order, and a broken frame costs only itself.
internal static class StdioWorkerLoopCases
{
    public static async Task<int> RunAsync(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Stdio worker loop: " + name);
            }
            passed++;
        }
        var fixture = new LoopFixture(temporaryRoot);
        await AnswersKeepTheirOrder(Check, fixture);
        await BrokenFramesCostOnlyThemselves(Check, fixture);
        await OversizedFrameEndsTheLoop(Check, fixture);
        await BackupRequirementIsWrittenToThePolicy(Check, new LoopFixture(Path.Combine(temporaryRoot, "backup-requirement")));
        return passed;
    }

    private static async Task AnswersKeepTheirOrder(Action<bool, string> check, LoopFixture fixture)
    {
        List<JsonObject> answers = await fixture.RunAsync(
            fixture.Action("first"),
            fixture.Control("heartbeat"),
            fixture.Control("session_details", new ControlField("details", SessionPresentation.Create("Client", "Task").ToJson())),
            fixture.Control("inventory_permission", new ControlField("enabled", true)),
            fixture.Control("close"),
            fixture.Action("second"));
        check(answers.Count == 7, "one ready line and one answer per frame");
        bool readyFirst = answers[0]["ready"]?.GetValue<bool>() == true && answers[0]["visibility_protocol"]?.GetValue<int>() == 1;
        check(readyFirst, "ready comes first");
        check(answers[1]["echo"]?.ToString() == "first" && answers[6]["echo"]?.ToString() == "second", "actions are answered in order");
        bool controlsSucceeded = answers.Skip(2).Take(4).All(answer => answer["success"]?.GetValue<bool>() == true);
        check(controlsSucceeded, "every control frame is acknowledged");
        check(fixture.ExecutedActions.SequenceEqual(["first", "second"]), "control frames never reach the dispatcher");
        NodePolicy stored = NodePolicy.Load(fixture.PolicyPath);
        check(stored.Get("operator").Scopes.Contains(Scope.InventoryRead), "the inventory permission is written to the policy file");
    }

    private static async Task BrokenFramesCostOnlyThemselves(Action<bool, string> check, LoopFixture fixture)
    {
        fixture.FailNextAction = true;
        List<JsonObject> answers = await fixture.RunAsync(
            "not json",
            fixture.Control("heartbeat", new ControlField("extra", 1)),
            fixture.Control("session_details"),
            fixture.Control("inventory_permission", new ControlField("other", true)),
            fixture.Control("reboot"),
            fixture.Action("refused by the dispatcher"),
            fixture.Action("after the failures"));
        check(answers.Count == 8, "every broken frame still gets its answer");
        bool allRefused = answers.Skip(1).Take(6).All(answer => answer["outcome"]?.ToString() == "not_dispatched");
        check(allRefused, "broken frames are answered as not dispatched");
        check(answers[2]["error"]?.ToString() == "Invalid visibility control frame.", "a heartbeat with an extra field is refused");
        check(answers[3]["error"]?.ToString() == "Invalid presentation frame.", "presentation details are required");
        check(answers[4]["error"]?.ToString() == "Invalid inventory permission frame.", "the permission value is required");
        check(answers[5]["error"]?.ToString() == "Invalid visibility control frame.", "an unknown control is refused");
        check(answers[7]["echo"]?.ToString() == "after the failures", "the loop continues after broken frames");
    }

    private static async Task BackupRequirementIsWrittenToThePolicy(Action<bool, string> check, LoopFixture fixture)
    {
        List<JsonObject> answers = await fixture.RunAsync(
            fixture.Control("backup_requirement", new ControlField("requirement", "required")),
            fixture.Control("backup_requirement", new ControlField("requirement", "sometimes")));
        check(answers[1]["success"]?.GetValue<bool>() == true, "the backup requirement control is acknowledged");
        check(NodePolicy.Load(fixture.PolicyPath).CurrentBackupRequirement() == BackupRequirement.Required,
            "the backup requirement is written to the policy file");
        check(answers[2]["error"]?.ToString() == "Invalid backup requirement frame.", "only required or off is accepted");
    }

    private static async Task OversizedFrameEndsTheLoop(Action<bool, string> check, LoopFixture fixture)
    {
        string oversized = "{\"padding\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";
        var output = new StringWriter();
        bool ended = false;
        try
        {
            await fixture.RunAsync(output, fixture.Action("before"), oversized, fixture.Action("after"));
        }
        catch (InvalidDataException)
        {
            ended = true;
        }
        List<JsonObject> answers = LoopFixture.ReadAnswers(output.ToString());
        check(ended, "an oversized frame ends the loop");
        check(answers.Count == 2 && answers[1]["echo"]?.ToString() == "before", "answers before it were written");
        check(!fixture.ExecutedActions.Contains("after"), "nothing after it is executed");
    }

    // A worker loop with its policy, visibility gate and session, fed with lines the way the supervisor writes them.
    private sealed class LoopFixture
    {
        private readonly NodePolicy _policy;
        private readonly AccessVisibility _visibility;
        private readonly string _sessionId = Guid.CreateVersion7().ToString();

        public LoopFixture(string temporaryRoot)
        {
            string state = Path.Combine(temporaryRoot, "stdio-loop-state");
            string workspace = Path.Combine(temporaryRoot, "stdio-loop-workspace");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(workspace);
            PolicyPath = Path.Combine(state, "policy.json");
            var policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals = [new PrincipalPolicy("operator", new string('a', 64), [Scope.Observe], [], DateTimeOffset.UtcNow.AddHours(1))
                {
                    UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                }]
            };
            File.WriteAllText(PolicyPath, JsonSerializer.Serialize(policy));
            _policy = NodePolicy.Load(PolicyPath);
            _visibility = new AccessVisibility(new VisibilityOptions(), new QuietDisplay(), _ => { });
        }

        public string PolicyPath
        {
            get;
        }
        public List<string> ExecutedActions { get; } = [];
        public bool FailNextAction
        {
            get; set;
        }

        public string Action(string name)
        {
            return Frame(new JsonObject { ["action"] = name, ["args"] = new JsonObject() });
        }

        public string Control(string control, params ControlField[] fields)
        {
            var frame = new JsonObject { ["control"] = control };
            foreach ((string name, JsonNode? value) in fields)
            {
                frame[name] = value;
            }
            return Frame(frame);
        }

        private string Frame(JsonObject frame)
        {
            frame["principal_id"] = "operator";
            frame["session_id"] = _sessionId;
            return frame.ToJsonString();
        }

        public async Task<List<JsonObject>> RunAsync(params string[] lines)
        {
            var output = new StringWriter();
            await RunAsync(output, lines);
            return ReadAnswers(output.ToString());
        }

        public async Task RunAsync(StringWriter output, params string[] lines)
        {
            ExecutedActions.Clear();
            byte[] input = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            var loop = new StdioWorkerLoop(_policy, PolicyPath, _visibility, ExecuteAsync);
            await loop.RunAsync(new MemoryStream(input), output, CancellationToken.None);
        }

        public static List<JsonObject> ReadAnswers(string output)
        {
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        }

        private Task<JsonObject> ExecuteAsync(JsonObject frame, RequestContext context)
        {
            string action = frame["action"]!.ToString();
            if (FailNextAction)
            {
                FailNextAction = false;
                throw new ArgumentException("Unknown action.");
            }
            ExecutedActions.Add(action);
            return Task.FromResult(new JsonObject { ["success"] = true, ["echo"] = action });
        }
    }

    // A working indicator that draws nothing, so the worker loop can be driven without windows.
    private sealed class QuietDisplay : IVisibilityDisplay
    {
        public void Render(VisibilityState state)
        {
        }

        public void Dispose()
        {
        }
    }
}

// One extra field of a control frame, so a test can send fields the protocol does not know.
internal sealed record ControlField(string Name, JsonNode? Value);
