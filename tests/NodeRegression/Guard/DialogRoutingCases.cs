using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Platforms;

// The agent asks to answer a certificate warning while fetching mail. The advisor sorts the dialog from what the node read,
// and the administrator's DialogDeciders say whether the agent, the person at the machine or nobody answers it.
internal static class DialogRoutingCases
{
    private const string Intent = "Fetch the mail in the mail program";
    private const string AgentReason = "Routine dialog, just confirm it to get on with the mail";
    private const string DialogText = "Das Zertifikat des Servers ist abgelaufen.\nTrotzdem verbinden\nAbbrechen";

    private static readonly UiTargetIdentity CertificateButton =
        new(Environment.ProcessId, Environment.ProcessPath!, "Sicherheitswarnung", "Button", "Trotzdem verbinden", "", true, true);

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Dialog routing");
        using var scenario = new RoutingScenario(checks, temporaryRoot);

        scenario.CheckSecurityExceptionGoesToPerson();
        scenario.CheckAdvisorSeesOnlyWhatTheNodeRead();
        scenario.CheckRoutineGoesToAgent();
        scenario.CheckDoubtCountsAsUnclear();
        scenario.CheckFullAccessAndBlocked();
        scenario.CheckPauseStaysPause();
        scenario.CheckDecisionWithoutDialogIsNotRouted();
        scenario.CheckDialogOpeningLaterIsStillRefused();
        scenario.CheckDetectedDialogRoutesAsBefore();
        scenario.CheckUndetectableDialogRoutesAsBefore();
        CheckPolicyWords(checks);
        CheckGuardedControls(checks, temporaryRoot);
        return checks.Passed;
    }

    // A guarded profile with two controls of one program: a data-loss control whose rule requires a decision and a plain control
    // whose rule does not. The advisor sorts every dialog as data loss, which the default policy leaves to the person at the machine.
    private static void CheckGuardedControls(Checks checks, string temporaryRoot)
    {
        using var scenario = new GuardedControlScenario(checks, temporaryRoot);
        scenario.CheckRequiredDecisionIsStillDemanded();
        scenario.CheckDataLossControlKeepsItsRouting();
        scenario.CheckPlainControlIgnoresDecision();
    }

    // One guarded principal with both controls. The node observes the program before each click, as the agent does.
    private sealed class GuardedControlScenario : IDisposable
    {
        private const string DataLossName = "Dauerhaft löschen";
        private const string PlainName = "Speichern";
        private readonly Checks _checks;
        private readonly string _application;
        private readonly FakeUi _ui = new() { WindowText = DialogText, ModalDetection = ModalDialogDetection.Absent };
        private readonly DialogClassAdvisor _advisor = new() { Label = "data_loss" };
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());
        private string _snapshot = "";

        public GuardedControlScenario(Checks checks, string temporaryRoot)
        {
            _checks = checks;
            string state = Path.Combine(temporaryRoot, "guarded-control-state");
            string workspace = Path.Combine(temporaryRoot, "guarded-control-workspace");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(workspace);
            using var self = Process.GetCurrentProcess();
            _application = self.MainModule!.FileName;
            var policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals = [GuardedPrincipal(_application)]
            };
            _dispatcher = new ActionDispatcher(_ui, policy, null, new NodeRiskClassifier(_advisor));
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
        }

        // Without a decision the guard refuses, as it always did, and nothing runs.
        public void CheckRequiredDecisionIsStillDemanded()
        {
            SelectControl(DataLossName);

            JsonObject refused = Click(null);

            _checks.That(refused["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == 0
                && (refused["error"]?.ToString() ?? "").Contains("requires a recorded decision", StringComparison.Ordinal),
                "a control whose rule requires a decision is still refused without one, by the guard");
        }

        // A decision on the data-loss control with no dialog open is routed as before this change: the advisor is asked, the default
        // policy leaves data loss to the person at the machine, and nothing runs.
        public void CheckDataLossControlKeepsItsRouting()
        {
            SelectControl(DataLossName);
            int questionsBefore = _advisor.DialogQuestions;

            JsonObject refused = Click(Decision());

            _checks.That(refused["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == 0
                && refused["dialog_class"]?.ToString() == "data_loss" && refused["decider"]?.ToString() == "person"
                && refused["decision_ignored"] == null,
                "with no dialog open a decision on a data-loss control is still routed, and the person at the machine gets it");
            _checks.That(_advisor.DialogQuestions == questionsBefore + 1, "the advisor is asked about that control, as before");
        }

        // A plain control has no rule that demands a decision, so with no dialog open the decision is not routed and the action runs.
        public void CheckPlainControlIgnoresDecision()
        {
            SelectControl(PlainName);
            int questionsBefore = _advisor.DialogQuestions;

            JsonObject answered = Click(Decision());

            _checks.That(answered["success"]!.GetValue<bool>() && _ui.Clicks == 1
                && answered["decision_ignored"]?.ToString() == "No dialog is open.",
                "a plain control with no dialog open runs, and its decision is reported as not applied");
            _checks.That(_advisor.DialogQuestions == questionsBefore, "the advisor is not asked about a plain control with no dialog");
        }

        // Observes the program and aims the next click at the named control.
        private void SelectControl(string name)
        {
            _ui.Identity = new UiTargetIdentity(Environment.ProcessId, _application, "Sicherheitswarnung", "Button", name, "", true, true);
            JsonObject observation = Call("get_ui_tree", new JsonObject { ["pid"] = Environment.ProcessId });
            _snapshot = observation["snapshot_id"]!.ToString();
        }

        private JsonObject Click(JsonObject? decision)
        {
            var arguments = new JsonObject
            {
                ["id"] = _ui.Id,
                ["snapshot_id"] = _snapshot,
                ["request_id"] = Guid.CreateVersion7().ToString()
            };
            if (decision != null)
            {
                arguments["decision"] = decision;
            }
            return Call("click_element", arguments);
        }

        private JsonObject Call(string action, JsonObject arguments)
        {
            return _dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, _caller);
        }

        private static JsonObject Decision()
        {
            return new JsonObject
            {
                ["severity"] = "heavy",
                ["occasion"] = "Delete the mail archive",
                ["choice"] = "Delete permanently",
                ["reason"] = AgentReason,
                ["expected_result"] = "The archive is gone"
            };
        }

        // The guard recomputes the program's digest, so every rule names the test host's own path and digest.
        private static PrincipalPolicy GuardedPrincipal(string application)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(application)));
            var guard = new UiGuardOptions
            {
                Mode = UiGuardMode.Guarded,
                Intent = Intent,
                Rules =
                [
                    ControlRule(application, digest, DataLossName, requiresDecision: true),
                    ControlRule(application, digest, PlainName, requiresDecision: false)
                ]
            };
            return new PrincipalPolicy(
                "operator",
                new string('a', 64),
                [Scope.Observe, Scope.Ui],
                [],
                DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = guard
            };
        }

        private static UiActionRule ControlRule(string application, string digest, string name, bool requiresDecision)
        {
            return new UiActionRule
            {
                Application = application,
                ApplicationSha256 = digest,
                WindowTitle = "Sicherheitswarnung",
                Role = "Button",
                Name = name,
                Actions = [Tool.ClickElement],
                RequiresDecision = requiresDecision
            };
        }
    }

    private static void CheckPolicyWords(Checks checks)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var policyFile = JsonNode.Parse("{\"dialogDeciders\":{\"securityException\":\"agent\",\"unclear\":\"blocked\"}}")!;
        NodePolicy parsed = policyFile.Deserialize<NodePolicy>(options)!;
        checks.That(parsed.DialogDeciders.SecurityException == DialogDecider.Agent && parsed.DialogDeciders.Unclear == DialogDecider.Blocked
            && parsed.DialogDeciders.Routine == DialogDecider.Agent, "the policy names deciders by word and keeps the other defaults");

        checks.That(Refuses("{\"dialogDeciders\":{\"dataLoss\":\"nobody\"}}", options), "an unknown decider word is refused");
        checks.That(Refuses("{\"dialogDeciders\":{\"dataLoss\":1}}", options), "a decider number is refused");
        checks.That(new DialogDeciders().SecurityException == DialogDecider.Person, "a security exception goes to the person by default");
    }

    private static bool Refuses(string policyText, JsonSerializerOptions options)
    {
        try
        {
            JsonSerializer.Deserialize<NodePolicy>(policyText, options);
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // One desktop principal with a task intent, a fake desktop showing a certificate warning and an advisor whose answer
    // the steps change in place, in this order.
    private sealed class RoutingScenario : IDisposable
    {
        private readonly Checks _checks;
        private readonly string _state;
        private readonly NodePolicy _policy;
        private readonly FakeUi _ui = new() { ModalTarget = true, Identity = CertificateButton, WindowText = DialogText };
        private readonly DialogClassAdvisor _advisor = new();
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());

        public RoutingScenario(Checks checks, string temporaryRoot)
        {
            _checks = checks;
            _state = Path.Combine(temporaryRoot, "dialog-routing-state");
            string workspace = Path.Combine(temporaryRoot, "dialog-routing-workspace");
            Directory.CreateDirectory(_state);
            Directory.CreateDirectory(workspace);
            _policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = _state,
                WorkspaceRoot = workspace,
                Principals =
                [
                    new PrincipalPolicy("operator", new string('a', 64), [Scope.Observe, Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop, Intent = Intent }
                    }
                ]
            };
            _policy.Visibility = _policy.Visibility with
            {
                ModalDialogs = ModalDialogMode.AllowInteraction
            };
            _dispatcher = new ActionDispatcher(_ui, _policy, null, new NodeRiskClassifier(_advisor));
        }

        public void Dispose()
        {
            _dispatcher.Dispose();
        }

        public void CheckSecurityExceptionGoesToPerson()
        {
            _advisor.Label = "security_exception";
            int clicksBefore = _ui.Clicks;

            JsonObject refused = AnswerDialog();

            _checks.That(refused["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == clicksBefore,
                "a certificate warning is not answered by the agent by default");
            _checks.That(refused["dialog_class"]!.ToString() == "security_exception" && refused["decider"]!.ToString() == "person",
                "the agent learns that the person at the machine handles this dialog");
            JsonNode journaled = LastJournalEntry(entry => entry["dialog_class"] != null && entry["decider"] != null);
            _checks.That(journaled["evidence"]?["text_excerpt"]?.ToString() == DialogText,
                "the journal keeps what the node saw of the refused dialog");

            _policy.DialogTextEvidence = DialogTextEvidence.HashOnly;
            AnswerDialog();
            journaled = LastJournalEntry(entry => entry["dialog_class"] != null && entry["decider"] != null);
            _checks.That(journaled["evidence"]?["text_excerpt"] == null && journaled["evidence"]?["text_sha256"] != null
                && _advisor.LastState!["text_excerpt"]?.ToString() == DialogText,
                "with hash_only the advisor still reads the text but the journal keeps only its hash");
            _policy.DialogTextEvidence = DialogTextEvidence.Excerpt;
        }

        public void CheckAdvisorSeesOnlyWhatTheNodeRead()
        {
            _advisor.Label = "security_exception";

            AnswerDialog();

            string shown = _advisor.LastState!.ToJsonString();
            _checks.That(!shown.Contains(AgentReason, StringComparison.Ordinal) && !shown.Contains(Intent, StringComparison.Ordinal),
                "the advisor never sees the agent's reasons or task, so the agent cannot argue the dialog lighter");
            _checks.That(_advisor.LastState!["window_title"]!.ToString() == "Sicherheitswarnung", "the advisor sees the dialog's window");
        }

        public void CheckRoutineGoesToAgent()
        {
            _advisor.Label = "routine";
            int clicksBefore = _ui.Clicks;

            JsonObject answered = AnswerDialog();

            _checks.That(answered["success"]!.GetValue<bool>() && _ui.Clicks == clicksBefore + 1,
                "a routine dialog is answered by the agent");
            JsonNode planned = LastJournalEntry(entry => entry["event"]?.ToString() == "agent_decision_planned");
            _checks.That(planned["dialog_class"]!.ToString() == "routine", "the planned decision records the dialog's class");
        }

        public void CheckDoubtCountsAsUnclear()
        {
            _advisor.Label = "routine";
            _advisor.Abstain = true;
            _checks.That(AnswerDialog()["dialog_class"]?.ToString() == "unclear", "an advisor without calibration yields unclear");
            _advisor.Abstain = false;

            _advisor.Label = "pass";
            JsonObject refused = AnswerDialog();
            _checks.That(refused["dialog_class"]?.ToString() == "unclear" && refused["decider"]?.ToString() == "person",
                "an answer outside the classes is unclear and goes to the person");
        }

        public void CheckFullAccessAndBlocked()
        {
            _advisor.Label = "security_exception";
            _policy.DialogDeciders = new DialogDeciders
            {
                SecurityException = DialogDecider.Agent,
                DataLoss = DialogDecider.Agent,
                Unclear = DialogDecider.Agent
            };
            int clicksBefore = _ui.Clicks;
            JsonObject answered = AnswerDialog();
            _checks.That(answered["success"]!.GetValue<bool>() && _ui.Clicks == clicksBefore + 1,
                "with full access the agent answers even a certificate warning");

            _policy.DialogDeciders = new DialogDeciders { SecurityException = DialogDecider.Blocked };
            JsonObject blocked = AnswerDialog();
            _checks.That(blocked["decider"]?.ToString() == "blocked" && _ui.Clicks == clicksBefore + 1,
                "a blocked class is answered by nobody through the node");
            _policy.DialogDeciders = new DialogDeciders();
        }

        public void CheckPauseStaysPause()
        {
            _advisor.Label = "routine";
            _policy.Visibility = _policy.Visibility with
            {
                ModalDialogs = ModalDialogMode.Pause
            };
            int clicksBefore = _ui.Clicks;

            JsonObject paused = AnswerDialog();

            _checks.That(paused["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == clicksBefore,
                "the classes only narrow dialog operation; pause still stops a routine dialog");
            _policy.Visibility = _policy.Visibility with
            {
                ModalDialogs = ModalDialogMode.AllowInteraction
            };
        }

        // The node finds no modal dialog for the element. The decision is recorded and reported as not applied, the advisor is not
        // asked about a dialog that is not there, and the action runs.
        public void CheckDecisionWithoutDialogIsNotRouted()
        {
            _ui.ModalTarget = false;
            _ui.ModalDetection = ModalDialogDetection.Absent;
            _advisor.Label = "security_exception";
            int clicksBefore = _ui.Clicks;
            int questionsBefore = _advisor.DialogQuestions;

            JsonObject answered = AnswerDialog();

            _checks.That(answered["success"]!.GetValue<bool>() && _ui.Clicks == clicksBefore + 1,
                "a decision without an open dialog does not stop the action");
            _checks.That(answered["decision_ignored"]?.ToString() == "No dialog is open." && answered["dialog_class"] == null,
                "the caller is told the decision was not applied and gets no dialog class");
            _checks.That(_advisor.DialogQuestions == questionsBefore, "the advisor is not asked about a dialog that is not there");
            _checks.That(answered["receipt"]!["decision"] != null, "the audit receipt still records the decision");
            JsonNode planned = LastJournalEntry(entry => entry["event"]?.ToString() == "agent_decision_planned"
                && entry["decision_ignored"] != null);
            _checks.That(planned["dialog_class"] == null && planned["evidence"]?["window_title"]?.ToString() == "Sicherheitswarnung",
                "the journal keeps the planned decision with what the node saw, and no dialog class");
            ResetDetection();
        }

        // A dialog that opens after the node looked for one is not operated on the strength of an ignored decision: the backend
        // receives the decision as not recorded, so the input is refused as it would be without any decision.
        public void CheckDialogOpeningLaterIsStillRefused()
        {
            _ui.ModalTarget = true;
            _ui.ModalDetection = ModalDialogDetection.Absent;
            _advisor.Label = "routine";
            int clicksBefore = _ui.Clicks;

            JsonObject refused = AnswerDialog();

            _checks.That(refused["outcome"]!.ToString() == "not_dispatched" && _ui.Clicks == clicksBefore,
                "a dialog that opens after the check is not operated on the strength of an ignored decision");
            ResetDetection();
        }

        // A detected dialog is routed as before: an unclassified dialog is left to the person at the machine.
        public void CheckDetectedDialogRoutesAsBefore()
        {
            _ui.ModalTarget = true;
            _ui.ModalDetection = ModalDialogDetection.Present;
            _advisor.Label = "unclear";
            int clicksBefore = _ui.Clicks;

            JsonObject refused = AnswerDialog();

            _checks.That(refused["dialog_class"]?.ToString() == "unclear" && refused["decider"]?.ToString() == "person"
                && _ui.Clicks == clicksBefore && refused["decision_ignored"] == null,
                "a detected dialog of unclear class goes to the person, and its decision is not reported as ignored");
            ResetDetection();
        }

        // When the node cannot tell whether a dialog is open, the decision keeps its routing: the advisor is asked and the class decides.
        public void CheckUndetectableDialogRoutesAsBefore()
        {
            _ui.ModalTarget = false;
            _ui.ModalDetection = ModalDialogDetection.Unknown;
            _advisor.Label = "security_exception";
            int questionsBefore = _advisor.DialogQuestions;

            JsonObject refused = AnswerDialog();

            _checks.That(refused["dialog_class"]?.ToString() == "security_exception" && refused["decider"]?.ToString() == "person"
                && _advisor.DialogQuestions == questionsBefore + 1 && refused["decision_ignored"] == null,
                "a decision whose dialog state is unknown is routed as before");
            ResetDetection();
        }

        // The state this scenario starts from: a certificate warning is open and the node cannot tell more about it.
        private void ResetDetection()
        {
            _ui.ModalTarget = true;
            _ui.ModalDetection = ModalDialogDetection.Unknown;
        }

        private JsonObject AnswerDialog()
        {
            var observation = Call("get_ui_tree", new JsonObject());
            var arguments = new JsonObject
            {
                ["id"] = _ui.Id,
                ["snapshot_id"] = observation["snapshot_id"]!.ToString(),
                ["request_id"] = Guid.CreateVersion7().ToString(),
                ["decision"] = new JsonObject
                {
                    ["severity"] = "light",
                    ["occasion"] = "Certificate warning while fetching mail",
                    ["choice"] = "Connect anyway",
                    ["reason"] = AgentReason,
                    ["expected_result"] = "The inbox loads"
                }
            };
            return Call("click_element", arguments);
        }

        private JsonObject Call(string action, JsonObject arguments)
        {
            return _dispatcher.Execute(new JsonObject { ["action"] = action, ["args"] = arguments }, _caller);
        }

        private JsonNode LastJournalEntry(Func<JsonNode, bool> matches)
        {
            return File.ReadLines(Path.Combine(_state, "audit.jsonl")).Select(line => JsonNode.Parse(line)!["payload"]!).Last(matches);
        }
    }

    // Answers the dialog question with the label the step sets, and lets the veto pass so only the routing decides.
    private sealed class DialogClassAdvisor : IAdvisoryEvaluator
    {
        public string Label { get; set; } = "unclear";
        public bool Abstain
        {
            get; set;
        }
        public JsonNode? LastState
        {
            get; private set;
        }

        // How often the advisor was asked to classify a dialog; a decision that is not routed must not raise it.
        public int DialogQuestions
        {
            get; private set;
        }

        public string ProviderId => "dialog-class";

        public Task<AdvisoryResult> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken cancellation = default)
        {
            var answers = new JsonObject();
            if (questions.ContainsKey(DialogClassifier.Question))
            {
                DialogQuestions++;
                LastState = state.DeepClone();
                answers[DialogClassifier.Question] = new JsonObject { ["type"] = "choice", ["choice"] = Label };
            }
            if (questions.ContainsKey("risk"))
            {
                answers["risk"] = new JsonObject { ["type"] = "choice", ["choice"] = "pass" };
            }
            var result = new AdvisoryResult(
                true,
                AdvisoryOutcome.Advisory,
                Guid.CreateVersion7().ToString(),
                ProviderId,
                "revision",
                CanonicalJson.Hash(questions),
                answers,
                null,
                "fixed",
                !Abstain,
                Abstain,
                null);
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }
}
