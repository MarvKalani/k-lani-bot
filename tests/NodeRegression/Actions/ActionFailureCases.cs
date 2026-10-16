using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// A failure after a mutation was admitted used to escape the dispatcher when the journal could not take
// the rejection either. The stdio worker then reported it as "not dispatched", although the click had
// already reached the desktop.
internal static class ActionFailureCases
{
    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Action failure: " + name);
            }
            passed++;
        }
        JournalFailureAfterDispatchIsUnknown(Check, temporaryRoot);
        FocusOnlyMovesFocusWithoutClicking(Check, Path.Combine(temporaryRoot, "focus-only"));
        return passed;
    }

    private static void JournalFailureAfterDispatchIsUnknown(Action<bool, string> check, string temporaryRoot)
    {
        NodePolicy policy = DesktopPolicy(temporaryRoot);
        var ui = new FakeUi();
        using var dispatcher = new ActionDispatcher(ui, policy);
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject tree = dispatcher.Execute(new JsonObject { ["action"] = "get_ui_tree", ["args"] = new JsonObject() }, caller);
        string journalFile = Path.Combine(policy.StateRoot, "audit.jsonl");
        ui.AfterClick = () =>
        {
            // The journal file turns into a directory: every later append fails.
            File.Delete(journalFile);
            Directory.CreateDirectory(journalFile);
        };
        JsonObject? result = null;
        Exception? escaped = null;
        try
        {
            result = dispatcher.Execute(ClickRequest(tree["snapshot_id"]!.ToString()), caller);
        }
        catch (Exception exception)
        {
            escaped = exception;
        }

        check(escaped == null, "a journal failure after the click does not escape the dispatcher: " + escaped?.Message);
        check(ui.Clicks == 1, "the click reached the desktop");
        check(result?["outcome"]?.ToString() == "unknown", "the answer says the click may have run");
        check(result?["journal_error"] != null, "the answer says the journal could not record it");
    }

    private static void FocusOnlyMovesFocusWithoutClicking(Action<bool, string> check, string temporaryRoot)
    {
        var ui = new FakeUi();
        using var dispatcher = new ActionDispatcher(ui, DesktopPolicy(temporaryRoot));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject tree = dispatcher.Execute(new JsonObject { ["action"] = "get_ui_tree", ["args"] = new JsonObject() }, caller);
        string snapshot = tree["snapshot_id"]!.ToString();

        JsonObject focused = dispatcher.Execute(ClickRequest(snapshot, true), caller);
        check(focused["success"]?.GetValue<bool>() == true, "focus_only succeeds");
        check(ui.Focuses == 1 && ui.Clicks == 0, "focus_only focuses and does not click");

        dispatcher.Execute(ClickRequest(snapshot, null), caller);
        check(ui.Focuses == 1 && ui.Clicks == 1, "an absent focus_only still clicks");

        dispatcher.Execute(ClickRequest(snapshot, false), caller);
        check(ui.Focuses == 1 && ui.Clicks == 2, "focus_only false still clicks");
    }

    private static NodePolicy DesktopPolicy(string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "failure-state");
        string workspace = Path.Combine(temporaryRoot, "failure-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);
        return new NodePolicy
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
    }

    private static JsonObject ClickRequest(string snapshot, bool? focusOnly = null)
    {
        var arguments = new JsonObject
        {
            ["id"] = 1,
            ["snapshot_id"] = snapshot,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        if (focusOnly != null)
        {
            arguments["focus_only"] = focusOnly;
        }

        return new JsonObject
        {
            ["action"] = "click_element",
            ["args"] = arguments
        };
    }
}
