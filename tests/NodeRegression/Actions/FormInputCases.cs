using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms;

// Dispatcher tests count native writes and preparation to prove stale form metadata fails before input and focus.
internal static class FormInputCases
{
    public static int Run(string root)
    {
        var checks = new Checks("Native form input");
        string workspace = Path.Combine(root, "form-workspace");
        Directory.CreateDirectory(workspace);
        var policy = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = Path.Combine(root, "form-state"),
            WorkspaceRoot = workspace,
            Principals = [new("operator", new string('a', 64), [Scope.Observe, Scope.Ui], [], DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
            }]
        };
        Directory.CreateDirectory(policy.StateRoot);
        var ui = new FakeUi();
        using var dispatcher = new ActionDispatcher(ui, policy);
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        CheckRefusals(checks, dispatcher, caller, ui);
        CheckSelection(checks, dispatcher, caller, ui);
        CheckTextInterface(checks, dispatcher, caller, ui);

        return checks.Passed;
    }

    private static UiFieldMetadata Metadata()
    {
        return new(UiFormSupport.Available, UiFieldMetadataSource.ApplicationDeclaration, true, true, false, true, "current", "field", [],
            new(UiFormSupport.Available, "options", [new("choice", "Choice", true)], true, false, "choice"));
    }

    private static JsonObject Target(ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        var observed = dispatcher.Execute(new JsonObject { ["action"] = "get_ui_tree", ["args"] = new JsonObject() }, caller);
        return new JsonObject
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = observed["snapshot_id"]!.ToString(),
            ["request_id"] = Guid.CreateVersion7().ToString(),
            ["choice_action"] = "select",
            ["option_id"] = "choice",
            ["options_revision"] = "options",
            ["field_context"] = "current"
        };
    }

    private static void CheckRefusals(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        foreach (var metadata in new[] { UiFieldMetadata.Unsupported, Metadata() with { Visible = false },
            Metadata() with { Enabled = false }, Metadata() with { ReadOnly = true }, Metadata() with { Context = "changed" },
            Metadata() with { Choices = new(UiFormSupport.Available, "changed", [], true, false, null) } })
        {
            ui.FormMetadata = metadata;
            var args = Target(dispatcher, caller, ui);
            var result = dispatcher.Execute(new JsonObject { ["action"] = "click_element", ["args"] = args }, caller);
            checks.That(result["success"]!.GetValue<bool>() == false, "Invalid native form state is refused.");
            checks.That(ui.PrepareCalls == 0 && ui.FormSelections == 0, "Refusal occurs before focus or selection.");
        }
        var invalid = Target(dispatcher, caller, ui);
        invalid["choice_action"] = "invalid";
        checks.That(dispatcher.Execute(new JsonObject { ["action"] = "click_element", ["args"] = invalid }, caller)
            ["success"]!.GetValue<bool>() == false && ui.FormSelections == 0, "Invalid mode sends no native input.");
    }

    private static void CheckSelection(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        ui.FormMetadata = Metadata();
        var args = Target(dispatcher, caller, ui);
        var result = dispatcher.Execute(new JsonObject { ["action"] = "click_element", ["args"] = args }, caller);

        checks.That(result["success"]!.GetValue<bool>(), "A valid observed option reaches the native route.");
        checks.That(ui.FormSelections == 1 && ui.PrepareCalls == 1, "Native selection prepares and dispatches once.");
    }
    private static void CheckTextInterface(Checks checks, ActionDispatcher dispatcher, RequestContext caller, FakeUi ui)
    {
        int prepared = ui.PrepareCalls;
        ui.NativeTextSupport = UiNativeTextSupport.Unavailable;
        var args = Target(dispatcher, caller, ui);
        foreach (string name in new[] { "choice_action", "option_id", "options_revision", "field_context" })
        {
            args.Remove(name);
        }
        args["text"] = "not sent";
        var refused = dispatcher.Execute(new JsonObject { ["action"] = "type_text", ["args"] = args }, caller);
        checks.That(refused["outcome"]!.ToString() == "not_dispatched" && refused["success"]!.GetValue<bool>() == false,
            "A proved absent native setter returns not-dispatched.");
        checks.That(ui.PrepareCalls == prepared && ui.PhysicalTexts == 0 && ui.SemanticTexts == 0,
            "The native interface probe refuses before focus or text input.");
        checks.That(refused["code"] == null && refused["error"] == null,
            "An interface limitation is distinguishable from policy or unknown failures.");
        ui.NativeTextSupport = UiNativeTextSupport.Unknown;
    }

}
