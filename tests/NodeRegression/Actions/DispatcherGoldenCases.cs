using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Guard;
using KLaniBot.Startup;
using KLaniBot.Visibility;

// Characterization of ActionDispatcher.Execute: for each situation the complete answer, the journal
// entries it caused and the effects on the fake desktop are written to one golden file. The dispatcher
// is restructured step by step against this file, so every difference shows up as a diff and has to be
// explained. Nothing here asserts that the recorded behavior is right; the cases only pin what it is.
//
// Set K_LANI_BOT_UPDATE_GOLDEN=1 to rewrite the file after a change that was meant to happen.
internal static class DispatcherGoldenCases
{
    private const string GoldenRelativePath = "tests/NodeRegression/golden/action-dispatcher.json";
    private const string NodeVersionPlaceholder = "<node-version>";

    // Placeholders contain angle brackets and timestamps a plus sign; the default encoder would escape both.
    private static readonly JsonSerializerOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions IndentedPlainText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static int Run(string temporaryRoot)
    {
        var scenarios = new JsonObject();
        var errorOutput = Console.Error;
        try
        {
            // The dispatcher prints every receipt to stderr; the golden file records the journal instead.
            Console.SetError(TextWriter.Null);
            foreach ((string name, Action<GoldenScenario> play, bool recordScreenshots) in Scenarios())
            {
                using var scenario = new GoldenScenario(name, temporaryRoot, recordScreenshots);
                play(scenario);
                scenarios[name] = scenario.ToJson();
            }
        }
        finally
        {
            Console.SetError(errorOutput);
        }
        string actual = SerializeJournal(scenarios);
        string goldenPath = Path.Combine(RepositoryLocator.Root(), GoldenRelativePath);
        if (Environment.GetEnvironmentVariable("K_LANI_BOT_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
        }
        string expected = File.Exists(goldenPath) ? File.ReadAllText(goldenPath) : "";
        if (expected != actual)
        {
            string actualPath = Path.Combine(temporaryRoot, "action-dispatcher.actual.json");
            File.WriteAllText(actualPath, actual);
            throw new Exception(
                "Dispatcher characterization differs from "
                    + GoldenRelativePath
                    + ". Compare with: diff "
                    + GoldenRelativePath
                    + " "
                    + actualPath);
        }
        return scenarios.Count;
    }

    // The node version changes with every release; the golden records a placeholder, so a bump keeps it green.
    private static string SerializeJournal(JsonObject scenarios)
    {
        string journal = new JsonObject { ["scenarios"] = scenarios }.ToJsonString(IndentedPlainText) + "\n";

        string quotedVersion = "\"" + ProductIdentity.DisplayVersion + "\"";
        string quotedPlaceholder = "\"" + NodeVersionPlaceholder + "\"";

        return journal.Replace(quotedVersion, quotedPlaceholder, StringComparison.Ordinal);
    }

    // One golden scenario: its name in the golden file, the steps it plays and whether screenshots are recorded.
    private sealed record Scenario(string Name, Action<GoldenScenario> Play, bool RecordScreenshots);

    private static IEnumerable<Scenario> Scenarios()
    {
        yield return new Scenario("mutation_succeeds", MutationSucceeds, false);
        yield return new Scenario("read_only_action", ReadOnlyAction, false);
        yield return new Scenario("same_id_is_replayed", SameIdIsReplayed, false);
        yield return new Scenario("same_id_with_other_content", SameIdWithOtherContent, false);
        yield return new Scenario("missing_scope", MissingScope, false);
        yield return new Scenario("guard_rejection", GuardRejection, false);
        yield return new Scenario("stale_snapshot", StaleSnapshot, true);
        yield return new Scenario("lease_held_by_another_session", LeaseHeldByAnotherSession, true);
        yield return new Scenario("unsupported_backend_feature", UnsupportedBackendFeature, true);
        yield return new Scenario("consent_missing", ConsentMissing, false);
        yield return new Scenario("postcondition_met", PostconditionMet, false);
        yield return new Scenario("postcondition_not_met", PostconditionNotMet, false);
        yield return new Scenario("modal_dialog_decision", ModalDialogDecision, true);
        yield return new Scenario("failure_after_handoff", FailureAfterHandoff, false);
        yield return new Scenario("trace_cannot_be_written", TraceCannotBeWritten, true);
        yield return new Scenario("classifier_veto", ClassifierVeto, false);
        yield return new Scenario("macro_stops_halfway", MacroStopsHalfway, false);
        yield return new Scenario("observation_tools", ObservationTools, false);
        yield return new Scenario("window_and_input_tools", WindowAndInputTools, false);
        yield return new Scenario("files_clipboard_process_and_macro_tools", FilesClipboardProcessAndMacroTools, false);
    }

    private static void MutationSucceeds(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call("click", "click_element", scenario.ClickArguments(snapshot, requestId));
        scenario.Call("status of the click", "get_action_status", new JsonObject { ["request_id"] = requestId });
    }

    private static void ReadOnlyAction(GoldenScenario scenario)
    {
        scenario.Call("read the tree", "get_ui_tree", new JsonObject());
        scenario.Call("status of an unknown id", "get_action_status", new JsonObject { ["request_id"] = scenario.NextRequestId() });
    }

    private static void SameIdIsReplayed(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call("first click", "click_element", scenario.ClickArguments(snapshot, requestId));
        scenario.Call("same click again", "click_element", scenario.ClickArguments(snapshot, requestId));
    }

    private static void SameIdWithOtherContent(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call("first click", "click_element", scenario.ClickArguments(snapshot, requestId));
        JsonObject changed = scenario.ClickArguments(snapshot, requestId);
        changed["auto_wait_ms"] = 1;
        scenario.Call("same id, other arguments", "click_element", changed);
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId });
    }

    private static void MissingScope(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call("click without the ui scope", "click_element", scenario.ClickArguments(snapshot, requestId), scenario.ReaderCaller);
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId }, scenario.ReaderCaller);
    }

    private static void GuardRejection(GoldenScenario scenario)
    {
        string requestId = scenario.NextRequestId();
        var arguments = new JsonObject { ["keys"] = new JsonArray("win", "r"), ["request_id"] = requestId };
        scenario.Call("raw key chord under the guard", "hotkey", arguments, scenario.GuardedCaller);
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId }, scenario.GuardedCaller);
    }

    private static void StaleSnapshot(GoldenScenario scenario)
    {
        scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call(
            "click with an unknown snapshot",
            "click_element",
            scenario.ClickArguments("00000000-0000-4000-8000-000000000000", requestId));
        scenario.Call("same click again", "click_element", scenario.ClickArguments("00000000-0000-4000-8000-000000000000", requestId));
    }

    private static void LeaseHeldByAnotherSession(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        string requestId = scenario.NextRequestId();
        scenario.Call(
            "click from the second session",
            "click_element",
            scenario.ClickArguments(snapshot, requestId),
            scenario.SecondSessionCaller);
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId }, scenario.SecondSessionCaller);
    }

    private static void UnsupportedBackendFeature(GoldenScenario scenario)
    {
        scenario.Call("read the clipboard", "get_clipboard", new JsonObject());
    }

    private static void ConsentMissing(GoldenScenario scenario)
    {
        scenario.RequireLocalConsent();
        string requestId = scenario.NextRequestId();
        scenario.Call("tree before the person approved", "get_ui_tree", new JsonObject());
        var click = new JsonObject { ["id"] = 1, ["snapshot_id"] = "00000000-0000-4000-8000-000000000000", ["request_id"] = requestId };
        scenario.Call("click before the person approved", "click_element", click);
    }

    private static void PostconditionMet(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        JsonObject arguments = scenario.ClickArguments(snapshot, scenario.NextRequestId());
        arguments["postcondition"] = new JsonObject { ["name"] = "button" };
        scenario.Call("click with a postcondition that holds", "click_element", arguments);
    }

    private static void PostconditionNotMet(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        JsonObject arguments = scenario.ClickArguments(snapshot, scenario.NextRequestId());
        arguments["postcondition"] = new JsonObject { ["name"] = "button", ["expected_text"] = "not present" };
        scenario.Call("click with a postcondition that fails", "click_element", arguments);
    }

    private static void ModalDialogDecision(GoldenScenario scenario)
    {
        scenario.Ui.ModalTarget = true;
        string snapshot = scenario.Observe();
        scenario.Call(
            "click on a modal target, default policy",
            "click_element",
            scenario.ClickArguments(snapshot, scenario.NextRequestId()));
        scenario.Policy.Visibility = scenario.Policy.Visibility with
        {
            ModalDialogs = ModalDialogMode.AllowInteraction
        };
        scenario.Call(
            "click on a modal target without a decision",
            "click_element",
            scenario.ClickArguments(snapshot, scenario.NextRequestId()));
        JsonObject withDecision = scenario.ClickArguments(snapshot, scenario.NextRequestId());
        withDecision["decision"] = new JsonObject
        {
            ["severity"] = "medium",
            ["occasion"] = "Synthetic certificate dialog",
            ["choice"] = "Accept the approved fixture certificate",
            ["reason"] = "Fixture issuer matches.",
            ["expected_result"] = "Fixture dialog closes"
        };
        scenario.Call("click on a modal target with a decision, no advisor", "click_element", (JsonObject)withDecision.DeepClone());
        // Without an advisor every dialog is unclear; this administrator gives those to the agent.
        scenario.Policy.DialogDeciders = new DialogDeciders { Unclear = DialogDecider.Agent };
        withDecision["request_id"] = scenario.NextRequestId();
        scenario.Call("click on a modal target with a decision", "click_element", withDecision);
    }

    private static void FailureAfterHandoff(GoldenScenario scenario)
    {
        string snapshot = scenario.Observe();
        scenario.Ui.ThrowAfterClick = true;
        string requestId = scenario.NextRequestId();
        scenario.Call("click that fails after the hand-off", "click_element", scenario.ClickArguments(snapshot, requestId));
        scenario.Call("same click again", "click_element", scenario.ClickArguments(snapshot, requestId));
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId });
    }

    private static void TraceCannotBeWritten(GoldenScenario scenario)
    {
        File.WriteAllText(Path.Combine(scenario.StateRoot, "action-traces"), "not a directory");
        string snapshot = scenario.Observe();
        scenario.Call(
            "click while the trace cannot be saved",
            "click_element",
            scenario.ClickArguments(snapshot, scenario.NextRequestId()));
    }

    private static void ClassifierVeto(GoldenScenario scenario)
    {
        scenario.UseClassifier(new FixedAnswerAdvisor("block"));
        string requestId = scenario.NextRequestId();
        var arguments = new JsonObject { ["path"] = scenario.ApprovedProgram, ["request_id"] = requestId };
        scenario.Call("launch the approved program, classifier blocks", "launch_process", arguments, scenario.DesktopCaller);
        scenario.Call("status of the id", "get_action_status", new JsonObject { ["request_id"] = requestId }, scenario.DesktopCaller);
    }

    private static void MacroStopsHalfway(GoldenScenario scenario)
    {
        var steps = new JsonArray(
            new JsonObject { ["action"] = "get_ui_tree", ["args"] = new JsonObject() },
            new JsonObject { ["action"] = "fs_read_file", ["args"] = new JsonObject { ["path"] = "missing.txt" } });
        JsonObject saved = scenario.Call(
            "save the macro",
            "save_macro",
            new JsonObject { ["name"] = "two-steps", ["steps"] = steps, ["request_id"] = scenario.NextRequestId() });
        var run = new JsonObject
        {
            ["name"] = "two-steps",
            ["sha256"] = saved["macro"]!["sha256"]!.ToString(),
            ["request_id"] = scenario.NextRequestId()
        };
        scenario.Call("run the macro", "run_macro", run);
    }

    private static readonly ToolFeature[] AllDesktopFeatures =
    [
        ToolFeature.Windows,
        ToolFeature.WindowControl,
        ToolFeature.PhysicalInput,
        ToolFeature.BackgroundInvoke,
        ToolFeature.Clipboard
    ];

    // Every tool that observes the desktop, once, with the answer the fake desktop gives.
    private static void ObservationTools(GoldenScenario scenario)
    {
        scenario.Ui.ExtraFeatures.UnionWith(AllDesktopFeatures);
        scenario.Ui.ScreenshotEnabled = true;
        string snapshot = scenario.Observe();
        scenario.Call("find a unique element", "find_elements", new JsonObject { ["name"] = "button" });
        scenario.Call("find nothing", "find_elements", new JsonObject { ["name"] = "absent" });
        scenario.Call("changes since the first tree", "get_ui_delta", new JsonObject { ["since_snapshot"] = snapshot });
        scenario.Call("changes since an unknown tree", "get_ui_delta", new JsonObject { ["since_snapshot"] = "unknown" });
        JsonObject chapters = scenario.Call("list chapters", "get_chapters", new JsonObject());
        var section = new JsonObject { ["chapter_id"] = "K1", ["snapshot_id"] = chapters["snapshot_id"]!.ToString() };
        scenario.Call("inspect a chapter", "inspect_section", section);
        scenario.Call("wait for an element", "wait_element", new JsonObject { ["name"] = "button" });
        scenario.Call("verify an element", "verify_element", new JsonObject { ["name"] = "button" });
        scenario.Call("take a screenshot", "take_screenshot", new JsonObject());
        scenario.Call("list the modules", "list_available_mcps", new JsonObject());
        scenario.Call("describe the machine", "get_machine_capabilities", new JsonObject());
        scenario.Call("end the session", "end_session", new JsonObject());
        scenario.Call("the lease is free for another session", "get_ui_tree", new JsonObject(), scenario.SecondSessionCaller);
    }

    private static void WindowAndInputTools(GoldenScenario scenario)
    {
        scenario.Ui.ExtraFeatures.UnionWith(AllDesktopFeatures);
        scenario.Call("list windows", "list_windows", new JsonObject());
        scenario.Call("focus a window", "focus_window", new JsonObject { ["pid"] = 1 });
        scenario.Call("read a window's geometry", "get_window_geometry", new JsonObject { ["pid"] = 1 });
        scenario.Call("change a window's geometry", "set_window_geometry", new JsonObject { ["pid"] = 1, ["width"] = 100 });
        scenario.Call("close a window", "close_window", new JsonObject { ["pid"] = 1 });
        scenario.Call("move the mouse", "mouse_move", new JsonObject { ["x"] = 1, ["y"] = 2 });
        scenario.Call("drag and drop", "drag_and_drop", new JsonObject { ["from_x"] = 1, ["from_y"] = 2, ["to_x"] = 3, ["to_y"] = 4 });
        scenario.Call("press a chord", "hotkey", new JsonObject { ["keys"] = new JsonArray("a") });
        string snapshot = scenario.Observe();
        scenario.Call("click without the mouse", "silent_click", scenario.ClickArguments(snapshot, scenario.NextRequestId()));
        scenario.Call("click at a point", "mouse_click", new JsonObject { ["x"] = 1, ["y"] = 2 });
        JsonObject typed = scenario.ClickArguments(snapshot, scenario.NextRequestId());
        typed["text"] = "hello";
        scenario.Call("type text", "type_text", typed);
        scenario.Policy.Visibility = scenario.Policy.Visibility with
        {
            WindowInteraction = WindowInteractionMode.BackgroundSemantic
        };
        scenario.Call("click in the background", "click_element", scenario.ClickArguments(snapshot, scenario.NextRequestId()));
        JsonObject typedInBackground = scenario.ClickArguments(snapshot, scenario.NextRequestId());
        typedInBackground["text"] = "hello";
        scenario.Call("type text in the background", "type_text", typedInBackground);
    }

    private static void FilesClipboardProcessAndMacroTools(GoldenScenario scenario)
    {
        scenario.Ui.ExtraFeatures.UnionWith(AllDesktopFeatures);
        scenario.Call("write a file", "fs_write_file", new JsonObject { ["path"] = "notes.txt", ["content"] = "hello" });
        scenario.Call(
            "write it again without overwrite",
            "fs_write_file",
            new JsonObject { ["path"] = "notes.txt", ["content"] = "again" });
        scenario.Call("list the workspace", "fs_list_directory", new JsonObject());
        scenario.Call("read the file", "fs_read_file", new JsonObject { ["path"] = "notes.txt" });
        scenario.Call("search the workspace", "fs_search_files", new JsonObject { ["pattern"] = "*.txt" });
        scenario.Call("read the file's metadata", "fs_get_metadata", new JsonObject { ["path"] = "notes.txt" });
        scenario.Call("set the clipboard", "set_clipboard", new JsonObject { ["text"] = "copied" });
        scenario.Call("read the clipboard", "get_clipboard", new JsonObject());
        string snapshot = scenario.Observe();
        scenario.Call("paste the clipboard", "paste_clipboard", new JsonObject { ["id"] = scenario.Ui.Id, ["snapshot_id"] = snapshot });
        scenario.Call(
            "launch the approved program",
            "launch_process",
            new JsonObject { ["path"] = scenario.ApprovedProgram, ["arguments"] = new JsonArray("--flag") },
            scenario.DesktopCaller);
        JsonObject saved = scenario.Call(
            "save a macro",
            "save_macro",
            new JsonObject
            {
                ["name"] = "read-tree",
                ["steps"] = new JsonArray(new JsonObject { ["action"] = "get_ui_tree", ["args"] = new JsonObject() })
            });
        string macroHash = saved["macro"]!["sha256"]!.ToString();
        scenario.Call("list macros", "list_macros", new JsonObject());
        scenario.Call("read a macro", "get_macro", new JsonObject { ["name"] = "read-tree", ["sha256"] = macroHash });
        JsonObject run = scenario.Call(
            "run the macro with a trace",
            "run_macro",
            new JsonObject { ["name"] = "read-tree", ["sha256"] = macroHash, ["trace"] = true });
        scenario.Call("read the macro's trace", "get_macro_trace", new JsonObject { ["run_id"] = run["run_id"]!.ToString() });
    }

    // One independent run: its own state directory, clock, fake desktop and dispatcher.
    private sealed class GoldenScenario : IDisposable
    {
        private const string FixedSession = "session-aaaaaaaaaaaa";
        private readonly string _temporaryRoot;
        private readonly JsonArray _steps = new();
        private readonly Dictionary<string, string> _hashPlaceholders = new();
        private readonly Dictionary<string, string> _idPlaceholders = new();
        private readonly HashSet<string> _pinnedIds = new();
        private readonly string _name;
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        private int _requestCounter;
        private int _auditLinesSeen;
        private ActionDispatcher? _dispatcher;
        private AccessVisibility? _visibility;
        private NodeRiskClassifier? _classifier;
        private readonly List<string> _visibilityAudit = new();

        public GoldenScenario(string name, string temporaryRoot, bool recordScreenshots)
        {
            _name = name;
            _temporaryRoot = temporaryRoot;
            StateRoot = Path.Combine(temporaryRoot, "golden-" + name + "-state");
            string workspace = Path.Combine(temporaryRoot, "golden-" + name + "-workspace");
            Directory.CreateDirectory(StateRoot);
            Directory.CreateDirectory(workspace);
            ApprovedProgram = Path.Combine(workspace, "approved-program");
            Policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                NodeId = "golden-node",
                StateRoot = StateRoot,
                WorkspaceRoot = workspace,
                Visibility = new VisibilityOptions { RecordActionScreenshots = recordScreenshots },
                Principals = Principals(ApprovedProgram)
            };
            Ui = new FakeUi { ScreenshotEnabled = recordScreenshots };
            Caller = new RequestContext("operator", FixedSession);
            SecondSessionCaller = new RequestContext("operator", "session-bbbbbbbbbbbb");
            ReaderCaller = new RequestContext("reader", "session-rrrrrrrrrrrr");
            GuardedCaller = new RequestContext("guarded", "session-gggggggggggg");
            DesktopCaller = new RequestContext("desktop", "session-dddddddddddd");
            _pinnedIds.Add(FixedSession);
        }

        // operator and reader use the desktop profile; guarded has no rules; desktop may launch the approved program.
        private static PrincipalPolicy[] Principals(string approvedProgram)
        {
            Scope[] operatorScopes =
            [
                Scope.Observe,
                Scope.Ui,
                Scope.Process,
                Scope.Clipboard,
                Scope.FilesRead,
                Scope.FilesWrite,
                Scope.MacroWrite
            ];
            var guard = new UiGuardOptions { Mode = UiGuardMode.Guarded, Intent = "Edit the approved synthetic field only" };
            var desktopGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop, Intent = "Synthetic task: launch the approved program" };

            return
            [
                new PrincipalPolicy("operator", new string('a', 64), operatorScopes, [], DateTimeOffset.UtcNow.AddYears(1))
                {
                    UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                },
                new PrincipalPolicy("reader", new string('b', 64), [Scope.Observe], [], DateTimeOffset.UtcNow.AddYears(1))
                {
                    UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                },
                new PrincipalPolicy("guarded", new string('c', 64), operatorScopes, [], DateTimeOffset.UtcNow.AddYears(1))
                {
                    UiGuard = guard
                },
                new PrincipalPolicy("desktop", new string('d', 64), operatorScopes, [approvedProgram], DateTimeOffset.UtcNow.AddYears(1))
                {
                    UiGuard = desktopGuard
                }
            ];
        }

        public NodePolicy Policy
        {
            get;
        }
        public FakeUi Ui
        {
            get;
        }
        public string StateRoot
        {
            get;
        }
        public string ApprovedProgram
        {
            get;
        }
        public RequestContext Caller
        {
            get;
        }
        public RequestContext SecondSessionCaller
        {
            get;
        }
        public RequestContext ReaderCaller
        {
            get;
        }
        public RequestContext GuardedCaller
        {
            get;
        }
        public RequestContext DesktopCaller
        {
            get;
        }

        // A request id that carries the fixed clock time, so the retention window accepts it and the
        // text is the same on every run and every machine.
        public string NextRequestId()
        {
            _requestCounter++;
            long milliseconds = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            string time = milliseconds.ToString("x12");
            string id = $"{time[..8]}-{time[8..]}-7{_requestCounter:x3}-8000-000000000000";
            _pinnedIds.Add(id);
            return id;
        }

        public void RequireLocalConsent()
        {
            _visibility = new AccessVisibility(
                new VisibilityOptions(),
                new SilentDisplay(),
                entry => _visibilityAudit.Add(entry.ToJsonString(PlainText)),
                _clock);
        }

        public void UseClassifier(IAdvisoryEvaluator advisor)
        {
            _classifier = new NodeRiskClassifier(advisor);
        }

        public string Observe()
        {
            JsonObject observation = Call("observe", "get_ui_tree", new JsonObject());
            if (observation["snapshot_id"] == null)
            {
                throw new InvalidOperationException(
                    "The scenario could not observe the fake desktop: " + observation.ToJsonString(PlainText));
            }
            return observation["snapshot_id"]!.ToString();
        }

        public JsonObject ClickArguments(string snapshot, string requestId)
        {
            return new JsonObject { ["id"] = Ui.Id, ["snapshot_id"] = snapshot, ["request_id"] = requestId };
        }

        public JsonObject Call(string label, string action, JsonObject arguments, RequestContext? caller = null)
        {
            if (ToolContract.Get(action)["mutates"]!.GetValue<bool>() && !arguments.ContainsKey("request_id"))
            {
                arguments["request_id"] = NextRequestId();
            }
            var request = new JsonObject { ["action"] = action, ["args"] = arguments };
            string requestHash = HashOfRequest(action, arguments);
            _dispatcher ??= new ActionDispatcher(Ui, Policy, _visibility, _classifier, _clock);
            JsonObject response = _dispatcher.Execute(request, caller ?? Caller);
            _hashPlaceholders[requestHash] = "<hash of request " + (_hashPlaceholders.Count + 1) + ">";
            _steps.Add(new JsonObject
            {
                ["step"] = label,
                ["action"] = action,
                ["response"] = Normalize(response),
                ["journal"] = Normalize(ReadNewJournalEntries()),
                ["desktop"] = DescribeDesktop()
            });
            return response;
        }

        public JsonObject ToJson()
        {
            var result = new JsonObject { ["steps"] = _steps.DeepClone() };
            if (_visibilityAudit.Count > 0)
            {
                result["visibility_events"] = Normalize(new JsonArray(_visibilityAudit.Select(line => JsonNode.Parse(line)).ToArray()));
            }
            return result;
        }

        public void Dispose()
        {
            _dispatcher?.Dispose();
            _visibility?.Dispose();
        }

        // The same formula the dispatcher uses. Computing it here means a change of the formula leaves a
        // raw hash in the output and fails the comparison.
        private static string HashOfRequest(string action, JsonObject arguments)
        {
            string text = new JsonObject { ["action"] = action, ["args"] = arguments.DeepClone() }.ToJsonString();
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private JsonArray ReadNewJournalEntries()
        {
            var entries = new JsonArray();
            string journalPath = Path.Combine(StateRoot, "audit.jsonl");
            if (!File.Exists(journalPath))
            {
                return entries;
            }
            string[] lines = File.ReadAllLines(journalPath);
            for (int index = _auditLinesSeen; index < lines.Length; index++)
            {
                entries.Add(JsonNode.Parse(lines[index])!["payload"]!.DeepClone());
            }
            _auditLinesSeen = lines.Length;
            return entries;
        }

        private JsonObject DescribeDesktop()
        {
            return new JsonObject
            {
                ["clicks"] = Ui.Clicks,
                ["screenshots"] = Ui.ScreenshotCalls,
                ["prepare_calls"] = Ui.PrepareCalls,
                ["launches"] = Ui.LaunchCalls,
                ["trace_files"] = CountTraceFiles()
            };
        }

        private int CountTraceFiles()
        {
            string directory = Path.Combine(StateRoot, "action-traces");
            return Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0;
        }

        // Replaces everything that differs from run to run with a stable placeholder. Identifiers that
        // the scenario chose itself stay readable, because they are part of what is being pinned.
        private JsonNode Normalize(JsonNode node)
        {
            string text = node.ToJsonString(PlainText);
            text = text.Replace(JsonEncodedText.Encode(_temporaryRoot, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(), "<temp>");
            foreach ((string hash, string placeholder) in _hashPlaceholders)
            {
                text = text.Replace(hash, placeholder);
            }
            text = Regex.Replace(text, @"\d{4}-\d{2}-\d{2}T[0-9:.]+(?:\+00:00|Z)?", "<timestamp>");
            text = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", ReplaceUnpinnedId);
            text = Regex.Replace(text, "\"duration_ms\":\\d+", "\"duration_ms\":0");
            return JsonNode.Parse(text)!;
        }

        private string ReplaceUnpinnedId(Match match)
        {
            if (_pinnedIds.Contains(match.Value))
            {
                return match.Value;
            }
            if (!_idPlaceholders.TryGetValue(match.Value, out string? placeholder))
            {
                placeholder = "<id " + (_idPlaceholders.Count + 1) + ">";
                _idPlaceholders[match.Value] = placeholder;
            }
            return placeholder;
        }
    }

    // The golden scenarios need a working indicator but must not open windows, so frames are accepted and dropped.
    private sealed class SilentDisplay : IVisibilityDisplay
    {
        public void Render(VisibilityState state)
        {
        }

        public void Dispose()
        {
        }
    }
}
