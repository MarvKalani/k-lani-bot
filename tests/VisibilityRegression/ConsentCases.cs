using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Visibility;

// The approval flow and the stored settings are what a person at the machine relies on to stay in
// control. These cases pin every transition, the refusal of changes made by our own injected input,
// and the spelling of settings in files that already exist on installed nodes.
internal static class ConsentCases
{
    // Files written by earlier versions. They are kept as literals so that renaming an enum member
    // or a property fails here instead of on a customer's machine after the next update.
    private const string EarlierPreferencesFile =
        "{\"ScreenBorder\":false,\"RecordActionScreenshots\":true,\"WindowInteraction\":\"BackgroundSemantic\","
        + "\"ModalDialogs\":\"AllowInteraction\",\"ConsentMode\":\"unattended\",\"ActivityHoldMs\":900}";

    private const string EarlierPolicyFile =
        "{\"nodeId\":\"earlier\",\"visibility\":{\"screenBorder\":true,\"consentMode\":\"unattended\",\"activityHoldMs\":750}}";

    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Consent flow: " + name);
            }
            passed++;
        }
        EveryTransitionIsReachable(Check);
        UnattendedModeSkipsOnlyTheQuestion(Check);
        LockingHoldsUntilLiftedLocally(Check);
        EscapePausesTheAgent(Check);
        ContinuingNeverDispatchesAHeldStep(Check);
        SlowMotionShowsTheSteps(Check);
        StepHistoryKeepsTheRun(Check);
        StepCompletionUsesTheActualResult(Check);
        SpeedingUpIsRefusedDuringInjectedInput(Check);
        ReadingTheStateHasNoSideEffect(Check);
        InjectedInputIsRefusedAndRecorded(Check);
        EarlierFilesStillLoad(Check);
        UnknownValuesAreErrors(Check);
        ConsentModeNamesMatchTheProtocol(Check);
        return passed;
    }

    private static void EveryTransitionIsReachable(Action<bool, string> check)
    {
        using var scenario = new Scenario();
        AccessVisibility gate = scenario.Gate;

        check(scenario.RunAction(Tool.TakeScreenshot) == "consent_required", "a new session is asked before its first action");
        ConsentPrompt prompt = gate.State.Prompt!;

        check(gate.Decide(prompt.Key, prompt.Challenge, SessionConsent.Denied), "pending to denied");
        check(scenario.RunAction(Tool.TakeScreenshot) == "consent_denied", "denied sessions are refused");
        check(gate.State.Prompt == null, "a denied session shows no question");

        gate.RequestApprovalLocally();
        prompt = gate.State.Prompt!;
        check(prompt != null, "denied to pending by the person at the machine");

        check(gate.Decide(prompt!.Key, prompt.Challenge, SessionConsent.Deferred), "pending to deferred");
        JsonObject deferred = gate.Begin(Tool.TakeScreenshot, scenario.Caller).Rejection!;
        int retryAfter = deferred["retry_after_seconds"]!.GetValue<int>();
        check(
            deferred["code"]!.ToString() == "consent_deferred" && retryAfter is >= 1 and <= 300,
            "deferred names when the question returns");

        scenario.Clock.Advance(300);
        gate.Touch(scenario.Caller);
        ConsentPrompt reminder = gate.State.Prompt!;
        check(reminder != null && reminder.Challenge != prompt.Challenge, "deferred to pending after five minutes with a new challenge");
        check(!gate.Decide(prompt.Key, prompt.Challenge, SessionConsent.Allowed), "the answer to the old question is refused");

        check(gate.Decide(reminder!.Key, reminder.Challenge, SessionConsent.Allowed), "pending to allowed");
        check(scenario.RunAction(Tool.TakeScreenshot) == null, "allowed sessions act");
        RevocationAndChangedAssignment(check, scenario);
    }

    // Continues EveryTransitionIsReachable from an allowed session.
    private static void RevocationAndChangedAssignment(Action<bool, string> check, Scenario scenario)
    {
        AccessVisibility gate = scenario.Gate;
        gate.RevokeAll();
        check(scenario.RunAction(Tool.TakeScreenshot) == "consent_denied", "allowed to denied by revocation");

        gate.RequestApprovalLocally();
        ConsentPrompt again = gate.State.Prompt!;
        check(gate.Decide(again.Key, again.Challenge, SessionConsent.Allowed), "denied to pending to allowed");
        scenario.Gate.SetSessionPresentation(scenario.Caller, SessionPresentation.Create("Client", "First assignment", "master"));
        scenario.Gate.SetSessionPresentation(scenario.Caller, SessionPresentation.Create("Client", "Second assignment", "master"));
        check(scenario.RunAction(Tool.TakeScreenshot) == "consent_required", "allowed to new when the assignment changes");

        bool pendingRejected = false;
        try
        {
            gate.Decide(again.Key, again.Challenge, SessionConsent.Pending);
        }
        catch (ArgumentException)
        {
            pendingRejected = true;
        }
        check(pendingRejected, "a host cannot answer with a state that is not an answer");
        check(!gate.Decide("nobody:none", again.Challenge, SessionConsent.Allowed), "unknown sessions cannot be decided");
    }

    private static void UnattendedModeSkipsOnlyTheQuestion(Action<bool, string> check)
    {
        using var scenario = new Scenario(new VisibilityOptions { ConsentMode = ConsentMode.Unattended });
        check(scenario.RunAction(Tool.TakeScreenshot) == null, "unattended sessions act without a question");
        check(
            scenario.Audit.Any(entry => entry["event"]?.ToString() == "consent_allowed_by_operator_config"),
            "unattended consent is journalled");
        check(scenario.Gate.State.Unattended && scenario.Gate.State.ScreenBorder, "the border stays on in unattended mode");
        check(scenario.Gate.SetUnattended(false), "the person can switch the question back on");
        check(scenario.RunAction(Tool.TakeScreenshot) == "consent_required", "switching back asks again");
    }

    // "Alle Zugriffe sperren" was tried on a Mac in unattended mode while no client was connected: the click was
    // journalled, and the next client connected and acted without a question. A lock has to hold for clients that
    // connect later, against unattended mode and across a restart, until the person lifts it.
    private static void LockingHoldsUntilLiftedLocally(Action<bool, string> check)
    {
        VisibilityOptions? saved = null;
        using (var scenario = new Scenario(new VisibilityOptions { ConsentMode = ConsentMode.Unattended }, value => saved = value))
        {
            AccessVisibility gate = scenario.Gate;
            gate.LockAccess();
            check(scenario.RunAction(Tool.TakeScreenshot) == "consent_denied", "a client that connects after the lock is refused");
            check(gate.State.AccessLocked && saved?.AccessLocked == true, "the lock is shown and saved");
            gate.RequestApprovalLocally();
            check(gate.State.Prompt == null, "no question is offered while access is locked");
            bool unattendedAccepted = gate.SetUnattended(true);
            check(unattendedAccepted && scenario.RunAction(Tool.TakeScreenshot) == "consent_denied", "unattended mode does not open it");
            check(
                scenario.Audit.Any(entry => entry["event"]?.ToString() == "access_locked_locally"),
                "locking is journalled");

            check(gate.UnlockAccess() && !gate.State.AccessLocked && saved?.AccessLocked == false, "the person lifts the lock");
            check(scenario.RunAction(Tool.TakeScreenshot) == null, "after lifting it, unattended mode admits again");
            check(
                scenario.Audit.Any(entry => entry["event"]?.ToString() == "access_unlocked_locally"),
                "unlocking is journalled");

            var action = scenario.Gate.Begin(Tool.TakeScreenshot, scenario.Caller);
            gate.LockAccess();
            check(gate.State.AccessLocked, "locking is accepted even while an action runs");
            check(!gate.UnlockAccess() && gate.State.AccessLocked, "our own injected input cannot lift the lock");
            action.Activity?.Dispose();
        }

        using (var restarted = new Scenario(new VisibilityOptions { ConsentMode = ConsentMode.Unattended, AccessLocked = true }))
        {
            check(restarted.RunAction(Tool.TakeScreenshot) == "consent_denied", "a saved lock holds after a restart");
        }
        check(
            AccessVisibility.ParseOptions("{\"AccessLocked\":true}").AccessLocked
                && !AccessVisibility.ParseOptions("{}").AccessLocked,
            "the lock is read from the preferences file and absent means unlocked");
    }

    private static void ReadingTheStateHasNoSideEffect(Action<bool, string> check)
    {
        using var scenario = new Scenario();
        scenario.Gate.Touch(scenario.Caller);
        int entriesBefore = scenario.Audit.Count;

        scenario.Clock.Advance(100);
        int connections = scenario.Gate.State.Connections;
        connections += scenario.Gate.State.Connections;
        check(
            connections == 2 && scenario.Audit.Count == entriesBefore,
            "reading the state neither expires sessions nor writes the journal");

        scenario.Gate.Refresh();
        check(scenario.Gate.State.Connections == 0, "Refresh expires the silent session");
        check(scenario.Audit.Any(entry => entry["event"]?.ToString() == "session_expired"), "expiry is journalled");
    }

    private static void InjectedInputIsRefusedAndRecorded(Action<bool, string> check)
    {
        VisibilityOptions? saved = null;
        using var scenario = new Scenario(new VisibilityOptions(), value => saved = value);
        AccessVisibility gate = scenario.Gate;
        scenario.Approve();

        var action = gate.Begin(Tool.TakeScreenshot, scenario.Caller);
        check(action.Rejection == null, "the approved session starts an action");

        check(!gate.SetBorder(false), "the border cannot be switched off during an action");
        check(!gate.SetBorder(false) && !gate.SetBorder(false), "repeating it is still refused");
        check(!gate.SetUnattended(true), "unattended mode cannot be enabled during an action");
        check(!gate.SetScreenshotRecording(true), "screenshot recording cannot be enabled during an action");
        check(
            !gate.SetWindowInteraction(WindowInteractionMode.BackgroundSemantic, ModalDialogMode.AllowInteraction),
            "window policy cannot change during an action");
        check(saved == null && gate.State.ScreenBorder && !gate.State.Unattended, "nothing was saved or applied");

        JsonObject[] rejections = RejectionEntries(scenario);
        check(rejections.Length == 4, "each refused setting is journalled once, not once per attempt");
        string[] names = rejections.Select(entry => entry["action"]!.ToString()).Order().ToArray();
        check(
            names.SequenceEqual(["screen_border", "screenshot_recording", "unattended_mode", "window_interaction"]),
            "the journal names the refused setting");

        action.Activity!.Dispose();
        scenario.Clock.Advance(1);
        gate.Refresh();
        check(gate.SetBorder(false) && saved?.ScreenBorder == false, "the same change is accepted once the action is over");
        check(RejectionEntries(scenario).Length == 4, "accepted changes add no refusal");

        gate.SetBorder(true);
        var second = gate.Begin(Tool.TakeScreenshot, scenario.Caller);
        check(!gate.SetBorder(false), "the next action blocks it again");
        check(RejectionEntries(scenario).Length == 5, "a new activity period is journalled again");
        second.Activity!.Dispose();
    }

    private static JsonObject[] RejectionEntries(Scenario scenario)
    {
        return scenario.Audit.Where(entry => entry["event"]?.ToString() == "local_setting_rejected_during_action").ToArray();
    }

    private static void EarlierFilesStillLoad(Action<bool, string> check)
    {
        VisibilityOptions preferences = AccessVisibility.ParseOptions(EarlierPreferencesFile);
        preferences.Validate();
        check(
            preferences.ConsentMode == ConsentMode.Unattended && !preferences.ScreenBorder && preferences.RecordActionScreenshots,
            "earlier preferences keep their values");
        check(
            preferences.WindowInteraction == WindowInteractionMode.BackgroundSemantic && preferences.ActivityHoldMs == 900,
            "earlier window settings keep their values");

        var format = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        NodePolicy policy = JsonSerializer.Deserialize<NodePolicy>(EarlierPolicyFile, format)!;
        check(
            policy.Visibility.ConsentMode == ConsentMode.Unattended && policy.Visibility.ActivityHoldMs == 750,
            "an earlier policy file keeps its consent mode");

        string written = JsonSerializer.Serialize(new VisibilityOptions { ConsentMode = ConsentMode.Unattended });
        check(
            written.Contains("\"ConsentMode\":\"unattended\"", StringComparison.Ordinal),
            "the stored spelling is still the lower-case word");
        string askWritten = JsonSerializer.Serialize(new VisibilityOptions());
        check(askWritten.Contains("\"ConsentMode\":\"ask\"", StringComparison.Ordinal), "ask is still stored as ask");

        DirectoryInfo directory = Directory.CreateTempSubdirectory("k-lani-bot-visibility-");
        try
        {
            var stored = new NodePolicy { StateRoot = directory.FullName };
            File.WriteAllText(Path.Combine(directory.FullName, "visibility-preferences.json"), EarlierPreferencesFile);
            check(
                AccessVisibility.LoadOptions(stored).ConsentMode == ConsentMode.Unattended,
                "LoadOptions reads an earlier preferences file");
            AccessVisibility.SaveOptions(stored, preferences with
            {
                ConsentMode = ConsentMode.Ask
            });
            check(AccessVisibility.LoadOptions(stored).ConsentMode == ConsentMode.Ask, "SaveOptions and LoadOptions agree");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void UnknownValuesAreErrors(Action<bool, string> check)
    {
        // A typo must not turn a node that asks into one that does not, or the other way round.
        check(
            Throws<InvalidDataException>(() => AccessVisibility.ParseOptions("{\"ConsentMode\":\"sometimes\"}")),
            "an unknown consent word is refused");
        check(
            Throws<InvalidDataException>(() => AccessVisibility.ParseOptions("{\"WindowInteraction\":\"Sideways\"}")),
            "an unknown window mode is refused");
        check(
            Throws<InvalidDataException>(() => AccessVisibility.ParseOptions("{\"ConsentMode\":7}").Validate()),
            "a number outside the enum is refused");
        check(Throws<InvalidDataException>(() => AccessVisibility.ParseOptions("null")), "an empty document is refused");

        var format = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        check(
            Throws<JsonException>(() =>
                JsonSerializer.Deserialize<NodePolicy>("{\"visibility\":{\"consentMode\":\"always\"}}", format)),
            "an unknown consent word in policy.json is refused");
    }

    private static void ConsentModeNamesMatchTheProtocol(Action<bool, string> check)
    {
        check(ConsentModeNames.TryParse("ask", out ConsentMode ask) && ask == ConsentMode.Ask, "ask parses");
        check(
            ConsentModeNames.TryParse("unattended", out ConsentMode unattended) && unattended == ConsentMode.Unattended,
            "unattended parses");
        check(
            !ConsentModeNames.TryParse("Unattended", out _)
                && !ConsentModeNames.TryParse("", out _)
                && !ConsentModeNames.TryParse(null, out _),
            "other spellings are not guessed");
        check(
            ConsentModeNames.ToProtocolName(ConsentMode.Ask) == "ask"
                && ConsentModeNames.ToProtocolName(ConsentMode.Unattended) == "unattended",
            "names written to invitations are unchanged");
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private const int ShortStepMilliseconds = 500;
    private const int ShortHoldMilliseconds = 1000;
    private const int QuickAnswerMilliseconds = 300;
    private const int ResumeAfterMilliseconds = 200;

    private static readonly VisibilityOptions ShortPace = new()
    {
        SlowMotionStepMs = ShortStepMilliseconds,
        PauseHoldMs = ShortHoldMilliseconds
    };

    private static readonly StepView Click = new(StepKind.Click, "Trotzdem verbinden");

    // ESC is armed only while the person sees the border and the agent uses the shared keyboard and mouse; it pauses.
    private static void EscapePausesTheAgent(Action<bool, string> check)
    {
        var working = new VisibilityState(VisibilityStatus.Active, 1, true, null, false, true);
        check(AccessVisibility.EscapePausesAgent(working), "ESC pauses while the border shows foreground work");
        check(!AccessVisibility.EscapePausesAgent(working with
        {
            Border = false
        }), "ESC without a border pauses nothing");
        check(!AccessVisibility.EscapePausesAgent(working with
        {
            WindowInteraction = WindowInteractionMode.BackgroundSemantic
        }),
            "in background mode the person's own ESC pauses nothing");

        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        var waited = Stopwatch.StartNew();
        check(scenario.Gate.AwaitPace(scenario.Caller, Click, null) == PaceDecision.Proceed
            && waited.ElapsedMilliseconds < QuickAnswerMilliseconds, "at normal pace a step passes at once");

        scenario.Gate.PauseByEscape();
        waited.Restart();
        PaceDecision held = scenario.Gate.AwaitPace(scenario.Caller, Click, null);
        check(held == PaceDecision.PausedByPerson && waited.ElapsedMilliseconds >= ShortHoldMilliseconds,
            "a paused step waits for the person and is not dispatched when the hold time ends");
        check(scenario.Gate.State.Pace == Pace.Paused && scenario.Audit.Any(entry => entry["event"]?.ToString() == "paused_by_escape"),
            "the journal shows that the person pressed ESC, and the session stays approved");
    }

    // Continuing releases a held step without dispatching it, because the screen may have changed while the person looked.
    private static void ContinuingNeverDispatchesAHeldStep(Action<bool, string> check)
    {
        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        scenario.Gate.SetPace(Pace.Paused);
        bool resumed = false;
        var person = new Thread(() =>
        {
            Thread.Sleep(ResumeAfterMilliseconds);
            resumed = scenario.Gate.SetPace(Pace.Normal);
        });
        var waited = Stopwatch.StartNew();
        person.Start();

        PaceDecision held = scenario.Gate.AwaitPace(scenario.Caller, Click, null);
        person.Join();

        check(resumed && held == PaceDecision.PausedByPerson && waited.ElapsedMilliseconds < ShortHoldMilliseconds,
            "continuing from the menu is possible while a step waits, and that step reports the pause");
        check(scenario.Gate.AwaitPace(scenario.Caller, Click, null) == PaceDecision.Proceed, "the next step runs at normal pace");
    }

    // Slow motion shows each step with what comes next and lets it run after the step time.
    private static void SlowMotionShowsTheSteps(Action<bool, string> check)
    {
        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        scenario.Gate.SetPace(Pace.SlowMotion);
        StepView[] plan = [new(StepKind.Announced, "Posteingang öffnen"), new(StepKind.Announced, "Neue Mail lesen")];
        var waited = Stopwatch.StartNew();

        PaceDecision first = scenario.Gate.AwaitPace(scenario.Caller, Click, plan);

        check(first == PaceDecision.Proceed && waited.ElapsedMilliseconds >= ShortStepMilliseconds, "slow motion holds each step");
        StepList shown = scenario.Gate.State.Steps!;
        check(shown.Now == Click && shown.NextFromAgent && shown.Next.Count == 2, "the box shows the step now and the agent's plan");
        scenario.Gate.AwaitPace(scenario.Caller, new StepView(StepKind.Click, "Posteingang"), null);
        shown = scenario.Gate.State.Steps!;
        check(shown.Before.Single() == Click && shown.Next.Single().Detail == "Neue Mail lesen",
            "the next step moves the last one before it and uses up the first planned step");
    }

    // Speeding up is a loosening: the agent's own input cannot do it, the person can while the agent waits at the gate.
    private static void SpeedingUpIsRefusedDuringInjectedInput(Action<bool, string> check)
    {
        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        var (activity, _) = scenario.Gate.Begin(Tool.TypeText, scenario.Caller);

        check(scenario.Gate.SetPace(Pace.Paused), "pausing is possible during the agent's input");
        check(!scenario.Gate.SetPace(Pace.Normal) && scenario.Gate.State.Pace == Pace.Paused,
            "speeding up during the agent's input is refused, so the agent cannot click itself free");
        activity?.Dispose();
        scenario.Clock.Advance(1);
        scenario.Gate.Refresh();
        check(scenario.Gate.SetPace(Pace.Normal), "the person continues once no input of the agent runs");
        check(StepText.Clean("Weiter\u202Eklicken\nJetzt: alles") == "Weiter klicken Jetzt: alles",
            "the agent's words lose line breaks and direction overrides before they reach the box");
    }

    private static void StepHistoryKeepsTheRun(Action<bool, string> check)
    {
        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        const int StepsInRun = 8;
        for (int step = 0; step < StepsInRun; step++)
        {
            scenario.Gate.AwaitPace(scenario.Caller, new StepView(StepKind.Read, "Step " + step), null);
        }

        check(scenario.Gate.State.Steps!.Before.Count == StepsInRun - 1,
            "a scrollable list keeps every step of the run rather than only the last three");
    }

    private static void StepCompletionUsesTheActualResult(Action<bool, string> check)
    {
        using var scenario = new Scenario(ShortPace);
        scenario.Approve();
        scenario.Gate.AwaitPace(scenario.Caller, Click, null, "first");
        scenario.Gate.FinishStep("first", StepStatus.Completed, "Version 16.0");
        scenario.Gate.AwaitPace(scenario.Caller, Click, null, "second");
        scenario.Gate.FinishStep("first", StepStatus.Completed, "late result");

        check(scenario.Gate.State.Steps!.Before.Single().Status == StepStatus.Completed,
            "the completed step keeps its checked status in the history");
        check(scenario.Gate.State.Steps!.Now!.Status == StepStatus.Running,
            "a late result for another operation never checks the current step");
        check(scenario.Gate.State.Steps!.Before.Single().Result == "Version 16.0"
            && scenario.Gate.State.Steps!.Now!.Result.Length == 0,
            "the actual result stays with its completed step and a late result cannot replace it");
        scenario.Gate.FinishStep("second", StepStatus.Uncertain);
        check(scenario.Gate.State.Steps!.Now!.Status == StepStatus.Uncertain,
            "an unknown result stays unchecked rather than being reported as completed");
    }

    // One visibility gate with a fake clock, display and journal, so a consent case sees every frame and entry it causes.
    private sealed class Scenario : IDisposable
    {
        public FakeClock Clock { get; } = new();
        public Display Display { get; } = new();
        public List<JsonObject> Audit { get; } = new();
        public RequestContext Caller { get; } = new("operator", Guid.CreateVersion7().ToString());
        public AccessVisibility Gate
        {
            get;
        }

        public Scenario(VisibilityOptions? options = null, Action<VisibilityOptions>? save = null)
        {
            Gate = new AccessVisibility(options ?? new VisibilityOptions(), Display, Audit.Add, Clock, save);
        }

        // Runs one action to completion and returns the refusal code, or null when it was admitted.
        // The clock moves past the hold time so that the next call starts from a quiet desktop.
        public string? RunAction(Tool action)
        {
            var (activity, rejection) = Gate.Begin(action, Caller);
            activity?.Dispose();
            Clock.Advance(1);
            Gate.Refresh();
            return rejection?["code"]?.ToString();
        }

        public void Approve()
        {
            Gate.Begin(Tool.TakeScreenshot, Caller);
            ConsentPrompt prompt = Gate.State.Prompt!;
            Gate.Decide(prompt.Key, prompt.Challenge, SessionConsent.Allowed);
        }

        public void Dispose()
        {
            Gate.Dispose();
        }
    }
}
