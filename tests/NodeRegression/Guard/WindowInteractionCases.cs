using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// The local window and modal settings decide how input reaches a program: foreground input is prepared first,
// background mode uses only semantic setters, and a modal dialog is touched only with a recorded decision whose
// screenshots and journal entries are kept as evidence.
internal static class WindowInteractionCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Window interaction");
        using var scenario = new WindowScenario(checks, temporaryRoot);

        scenario.CheckForeground();
        scenario.CheckBackground();
        scenario.CheckModalDecision();
        scenario.CheckHashOnlyEvidence();
        scenario.CheckFailureEvidence();
        CheckTraceLinks(checks, temporaryRoot, scenario.Workspace);
        return checks.Passed;
    }

    private static void CheckTraceLinks(Checks checks, string temporaryRoot, string workspace)
    {
        var linkedRoot = Path.Combine(temporaryRoot, "trace-link-root");
        Directory.CreateDirectory(linkedRoot);
        Directory.CreateSymbolicLink(Path.Combine(linkedRoot, "action-traces"), workspace);
        bool linkDenied = false;
        try
        {
            new ActionTrace(linkedRoot, TraceRetention.Rotate, _ => { }).SaveScreenshot(Guid.CreateVersion7().ToString(), "before", [1]);
        }
        catch (IOException)
        {
            linkDenied = true;
        }
        checks.That(linkDenied && !Directory.EnumerateFiles(workspace).Any(), "trace links cannot write into the agent workspace");
    }

    // One desktop principal and a fake desktop whose window settings the steps change in place, in this order.
    private sealed class WindowScenario : IDisposable
    {
        private readonly Checks _checks;
        private readonly string _state;
        private readonly NodePolicy _policy;
        private readonly FakeUi _ui = new() { ScreenshotEnabled = true };
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());

        public WindowScenario(Checks checks, string temporaryRoot)
        {
            _checks = checks;
            _state = Path.Combine(temporaryRoot, "window-mode-state");
            Workspace = Path.Combine(temporaryRoot, "window-mode-workspace");
            Directory.CreateDirectory(_state);
            Directory.CreateDirectory(Workspace);
            _policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = _state,
                WorkspaceRoot = Workspace,
                Principals =
                [
                    new PrincipalPolicy(
                        "operator",
                        new string('a', 64),
                        [Scope.Observe, Scope.Ui, Scope.MacroWrite],
                        [],
                        DateTimeOffset.UtcNow.AddHours(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                    }
                ]
            };
            _dispatcher = new ActionDispatcher(_ui, _policy);
        }

        public string Workspace
        {
            get;
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
        }

        private JsonObject Call(string action, JsonObject? arguments = null)
        {
            arguments ??= new JsonObject();
            if (ToolContract.Get(action)["mutates"]!.GetValue<bool>())
            {
                arguments["request_id"] = Guid.CreateVersion7().ToString();
            }
            return _dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, _caller);
        }

        private JsonObject Target()
        {
            var observation = Call("get_ui_tree");
            return new JsonObject { ["id"] = _ui.Id, ["snapshot_id"] = observation["snapshot_id"]!.ToString() };
        }

        public void CheckForeground()
        {
            _checks.That(_policy.Visibility.WindowInteraction == WindowInteractionMode.Foreground, "foreground is the default");
            var target = Target();
            target["text"] = "synthetic";
            _checks.That(Call("type_text", target)["success"]!.GetValue<bool>() && _ui.PrepareCalls == 1,
                "foreground prepared before input");
            _ui.RefuseForeground = true;
            target = Target();
            target["text"] = "must not be sent";
            int before = _ui.PhysicalTexts;
            var failure = Call("type_text", target);
            _checks.That(failure["outcome"]!.ToString() == "not_dispatched" && _ui.PhysicalTexts == before,
                "refused foreground never sends input");
        }

        public void CheckBackground()
        {
            int before = _ui.PhysicalTexts;
            _policy.Visibility = _policy.Visibility with
            {
                WindowInteraction = WindowInteractionMode.BackgroundSemantic
            };
            var target = Target();
            target["text"] = "background";
            _checks.That(Call("type_text", target)["success"]!.GetValue<bool>() && _ui.SemanticTexts == 1 && _ui.PhysicalTexts == before,
                "background text uses only the semantic setter");
            target = Target();
            target["text"] = "command";
            target["enter"] = true;
            _checks.That(Call("type_text", target)["outcome"]!.ToString() == "not_dispatched", "background Enter denied before admission");
            string[] physicalActions =
                ["hotkey", "mouse_click", "paste_clipboard", "focus_window", "launch_process", "run_terminal_command"];
            foreach (string action in physicalActions)
            {
                _checks.That(Call(action)["outcome"]!.ToString() == "not_dispatched", action + " has no background physical fallback");
            }
        }

        public void CheckModalDecision()
        {
            _ui.ModalTarget = true;
            _checks.That(Call("click_element", Target())["outcome"]!.ToString() == "not_dispatched", "modal defaults to pause");
            _policy.Visibility = _policy.Visibility with
            {
                ModalDialogs = ModalDialogMode.AllowInteraction
            };
            _checks.That(Call("click_element", Target())["outcome"]!.ToString() == "not_dispatched", "modal decision must be recorded");
            var target = Target();
            target["decision"] = new JsonObject
            {
                ["severity"] = "medium",
                ["occasion"] = "Synthetic certificate dialog",
                ["choice"] = "Accept the approved fixture certificate",
                ["reason"] = "Fixture issuer matches. <script>alert(1)</script>",
                ["expected_result"] = "Fixture dialog closes"
            };
            _policy.Visibility = _policy.Visibility with
            {
                RecordActionScreenshots = true
            };
            _ui.Identity = DialogTarget;
            _ui.WindowText = DialogText;
            var unclear = Call("click_element", (JsonObject)target.DeepClone());
            _checks.That(unclear["dialog_class"]?.ToString() == "unclear" && unclear["decider"]?.ToString() == "person" && _ui.Clicks == 0,
                "without an advisor every dialog is unclear and goes to the person by default");
            // The administrator gives the agent the dialogs nobody classified.
            _policy.DialogDeciders = new DialogDeciders { Unclear = DialogDecider.Agent };
            target["request_id"] = Guid.CreateVersion7().ToString();
            var accepted = Call("click_element", target);
            _checks.That(accepted["success"]!.GetValue<bool>() && _ui.Clicks == 1, "delegated modal interaction executes");
            _checks.That(accepted["receipt"]!["decision"]!["severity"]!.ToString() == "Medium",
                "severity is an enum in the decision receipt");
            _checks.That(_ui.ScreenshotCalls == 2 && accepted["receipt"]!["screenshots"]?["before"]?["sha256"] != null,
                "before and after images are linked by digest");
            CheckDecisionEvidence(accepted);
        }

        private void CheckDecisionEvidence(JsonObject accepted)
        {
            string tracePath = Path.Combine(_state, "action-traces", accepted["trace_file"]!.ToString());
            var trace = JsonNode.Parse(File.ReadAllText(tracePath))!;
            _checks.That(trace["receipt"]!["decision"] != null && trace["receipt"]!["outcome"]!.ToString() == "dispatched",
                "trace contains decision and actual dispatch outcome without inventing verification");
            string viewer = File.ReadAllText(Path.Combine(_state, "action-traces", "index.html"));
            bool escaped = viewer.Contains("&lt;script&gt;", StringComparison.Ordinal)
                && !viewer.Contains("<script>", StringComparison.Ordinal);
            _checks.That(escaped,
                "agent rationale is escaped in the local screenshot viewer");

            var audit = File.ReadLines(Path.Combine(_state, "audit.jsonl")).Select(line => JsonNode.Parse(line)!["payload"]!).ToArray();
            string operation = accepted["receipt"]!["operation_id"]!.ToString();
            int planned = Array.FindIndex(
                audit,
                entry => entry["operation_id"]?.ToString() == operation && entry["event"]?.ToString() == "agent_decision_planned");
            int completed = Array.FindIndex(
                audit,
                entry => entry["operation_id"]?.ToString() == operation && entry["decision_result"] != null);
            _checks.That(planned >= 0 && completed > planned, "decision is journaled before its result");
            JsonNode evidence = audit[planned]["evidence"]!;
            _checks.That(evidence["available"]!.GetValue<bool>() && evidence["window_title"]!.ToString() == "Zertifikat prüfen"
                && evidence["program_sha256"]!.ToString().Length == 64, "the node records which program, window and element it saw");
            _checks.That(evidence["text_sha256"]!.ToString() == Sha256(DialogText) && evidence["text_excerpt"]!.ToString() == DialogText,
                "the dialog text is journaled as hash and excerpt");
        }

        private static readonly UiTargetIdentity DialogTarget =
            new(Environment.ProcessId, Environment.ProcessPath!, "Zertifikat prüfen", "Button", "Accept", "", true, true);

        private const string DialogText = "Zertifikat prüfen\nAussteller: Fixture CA\nAccept\nCancel";

        private static string Sha256(string text)
        {
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        }

        // With hash_only the journal proves which text was shown without keeping it.
        public void CheckHashOnlyEvidence()
        {
            _policy.DialogTextEvidence = DialogTextEvidence.HashOnly;
            var target = Target();
            target["decision"] = new JsonObject
            {
                ["severity"] = "light",
                ["occasion"] = "Synthetic certificate dialog",
                ["choice"] = "Accept again",
                ["reason"] = "Fixture",
                ["expected_result"] = "Fixture dialog closes"
            };
            Call("click_element", target);
            var planned = File.ReadLines(Path.Combine(_state, "audit.jsonl")).Select(line => JsonNode.Parse(line)!["payload"]!)
                .Last(entry => entry["event"]?.ToString() == "agent_decision_planned");
            JsonNode evidence = planned["evidence"]!;
            _checks.That(evidence["text_sha256"]!.ToString() == Sha256(DialogText) && evidence["text_excerpt"] == null,
                "hash_only keeps the hash and no text");
            _policy.DialogTextEvidence = DialogTextEvidence.Excerpt;
        }

        public void CheckFailureEvidence()
        {
            _ui.ModalTarget = false;
            _ui.ThrowAfterClick = true;
            var failed = Call("click_element", Target());
            bool evidenceKept = failed["success"]!.GetValue<bool>() == false && failed["trace_file"] != null
                && failed["receipt"]!["error"] != null;
            _checks.That(evidenceKept,
                "failure retains screenshots, error and unknown execution outcome");
        }
    }
}
