using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Guard;
using KLaniBot.Visibility;

// The person pauses the agent with ESC or slows it down. A step that meets a pause is answered without being dispatched, a
// step in slow motion runs after the step time with the agent's plan on screen, and a macro stops before a step that could
// not finish inside its time budget.
internal static class PaceDispatchCases
{
    private const int StepMilliseconds = 500;
    private const int HoldMilliseconds = 2000;
    private const int LongestStepMilliseconds = 30000;
    private const int ResumeAfterMilliseconds = 200;

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Pace");
        using (var scenario = new PaceScenario(Path.Combine(temporaryRoot, "pace"), StepMilliseconds))
        {
            CheckPausedStep(checks, scenario);
            CheckSlowMotion(checks, scenario);
            CheckMacroPlan(checks, scenario);
            CheckStepResults(checks, scenario);
        }
        using (var slowest = new PaceScenario(Path.Combine(temporaryRoot, "pace-slowest"), LongestStepMilliseconds))
        {
            CheckMacroTimeBudget(checks, slowest);
        }
        return checks.Passed;
    }

    private static void CheckPausedStep(Checks checks, PaceScenario scenario)
    {
        string snapshot = scenario.Observe();
        scenario.Visibility.SetPace(Pace.Paused);
        var person = new Thread(() =>
        {
            Thread.Sleep(ResumeAfterMilliseconds);
            scenario.Visibility.SetPace(Pace.Normal);
        });
        int clicksBefore = scenario.Ui.Clicks;
        person.Start();

        JsonObject paused = scenario.Click(snapshot, null);
        person.Join();

        checks.That(paused["outcome"]!.ToString() == "not_dispatched" && paused["code"]!.ToString() == "paused_by_person"
            && scenario.Ui.Clicks == clicksBefore, "a step that met a pause is answered without being dispatched");
        checks.That(scenario.Click(null)["outcome"]!.ToString() == "dispatched", "after continuing, the next step runs");
    }

    private static void CheckSlowMotion(Checks checks, PaceScenario scenario)
    {
        scenario.Visibility.SetPace(Pace.SlowMotion);
        var plan = new JsonArray("Posteingang öffnen", "Neue Mail lesen");
        var waited = Stopwatch.StartNew();

        JsonObject slow = scenario.Click(plan);

        StepList steps = scenario.Visibility.State.Steps!;
        checks.That(slow["outcome"]!.ToString() == "dispatched" && waited.ElapsedMilliseconds >= StepMilliseconds,
            "in slow motion a step runs after the step time");
        checks.That(steps.Now?.Kind == StepKind.Click && steps.Now.Status == StepStatus.Completed
            && steps.NextFromAgent && steps.Next.Count == 2,
            "the box shows the click now and the agent's plan next");
    }

    private static void CheckStepResults(Checks checks, PaceScenario scenario)
    {
        scenario.Visibility.SetPace(Pace.Normal);
        scenario.Ui.RefuseForeground = true;
        JsonObject refused = scenario.Click(null);
        scenario.Ui.RefuseForeground = false;
        checks.That(refused["outcome"]!.ToString() == "not_dispatched"
            && scenario.Visibility.State.Steps!.Now!.Status == StepStatus.Failed, "a refused click is not checked as completed");
        scenario.Ui.ThrowAfterClick = true;
        JsonObject unknown = scenario.Click(null);
        scenario.Ui.ThrowAfterClick = false;
        checks.That(unknown["outcome"]!.ToString() == "unknown"
            && scenario.Visibility.State.Steps!.Now!.Status == StepStatus.Uncertain, "an unknown click remains unchecked");
    }

    private static void CheckMacroPlan(Checks checks, PaceScenario scenario)
    {
        int nextWhileClicking = -1;
        scenario.Ui.AfterClick = () =>
        {
            if (nextWhileClicking < 0)
            {
                nextWhileClicking = scenario.Visibility.State.Steps!.Next.Count;
            }
        };

        JsonObject run = scenario.RunMacro();

        scenario.Ui.AfterClick = null;
        checks.That(run["success"]!.GetValue<bool>() && nextWhileClicking == 1, "while a macro runs, the box shows its remaining steps");
        scenario.Visibility.SetPace(Pace.Normal);
    }

    private static void CheckMacroTimeBudget(Checks checks, PaceScenario scenario)
    {
        scenario.Click(null);
        scenario.Visibility.SetPace(Pace.SlowMotion);
        int clicksBefore = scenario.Ui.Clicks;

        JsonObject run = scenario.RunMacro();

        checks.That(run["success"]!.GetValue<bool>() == false && run["time_budget_reached"]?.GetValue<bool>() == true
            && scenario.Ui.Clicks == clicksBefore, "a macro stops before a step that could not finish inside its time budget");
    }

    // One unattended desktop principal, a fake desktop, a visible gate with the given slow-motion step and a saved macro.
    private sealed class PaceScenario : IDisposable
    {
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());
        private readonly string _macroHash;

        public PaceScenario(string root, int stepMilliseconds)
        {
            string state = Path.Combine(root, "state");
            string workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(workspace);
            var policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals =
                [
                    new PrincipalPolicy("operator", new string('a', 64), [Scope.Observe, Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                    }
                ]
            };
            _macroHash = SaveMacro(state);
            var options = new VisibilityOptions
            {
                ConsentMode = ConsentMode.Unattended,
                SlowMotionStepMs = stepMilliseconds,
                PauseHoldMs = HoldMilliseconds
            };
            Visibility = new AccessVisibility(options, new QuietDisplay(), _ => { });
            _dispatcher = new ActionDispatcher(Ui, policy, Visibility);
            Call("get_ui_tree", new JsonObject());
        }

        public FakeUi Ui { get; } = new();
        public AccessVisibility Visibility
        {
            get;
        }

        public string Observe()
        {
            return Call("get_ui_tree", new JsonObject())["snapshot_id"]!.ToString();
        }

        public JsonObject Click(JsonArray? plan)
        {
            return Click(Observe(), plan);
        }

        // Observing is a step of its own, so a click that must meet the pause uses a snapshot taken before it.
        public JsonObject Click(string snapshot, JsonArray? plan)
        {
            var arguments = new JsonObject
            {
                ["id"] = Ui.Id,
                ["snapshot_id"] = snapshot,
                ["request_id"] = Guid.CreateVersion7().ToString()
            };
            if (plan != null)
            {
                arguments["next_steps"] = plan;
            }
            return Call("click_element", arguments);
        }

        public JsonObject RunMacro()
        {
            return Call("run_macro", new JsonObject
            {
                ["name"] = "click_twice",
                ["sha256"] = _macroHash,
                ["request_id"] = Guid.CreateVersion7().ToString()
            });
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
            Visibility.Dispose();
        }

        private JsonObject Call(string action, JsonObject arguments)
        {
            return _dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, _caller);
        }

        private static string SaveMacro(string state)
        {
            var macro = new MacroDefinition
            {
                Name = "click_twice",
                Steps =
                [
                    new() { ["action"] = "get_ui_tree", ["args"] = new JsonObject() },
                    new() { ["action"] = "click_element", ["args"] = new JsonObject { ["id"] = 1 } },
                    new() { ["action"] = "click_element", ["args"] = new JsonObject { ["id"] = 1 } }
                ]
            };
            new MacroEngine(Path.Combine(state, "macros.json")).SaveMacro(macro);
            return macro.Sha256;
        }
    }

    // The cases need a working indicator but no windows, so frames are accepted and dropped.
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
