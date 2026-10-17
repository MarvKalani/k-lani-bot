using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms;

// The node side of the last-resort input route: a window's rectangle by handle, a typed code for a window without a tree, and
// the reason a raw input names, journaled before dispatch.
internal static class LastResortInputCases
{
    private const string Reason = "no_accessibility_tree: egui canvas";

    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Last-resort input: " + name);
            }
            passed++;
        }
        BoundsByHandle(Check, Path.Combine(temporaryRoot, "last-resort-bounds"));
        NoTreeHasItsCode(Check, Path.Combine(temporaryRoot, "last-resort-tree"));
        ReasonIsJournaled(Check, Path.Combine(temporaryRoot, "last-resort-journal"));
        return passed;
    }

    private static void BoundsByHandle(Action<bool, string> check, string temporaryRoot)
    {
        var ui = new FakeUi { Bounds = new WindowScreenBounds(4, 8, 1280, 690) };
        ui.ExtraFeatures.Add(ToolFeature.WindowControl);
        using var dispatcher = new ActionDispatcher(ui, DesktopPolicy(temporaryRoot));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());

        JsonObject read = dispatcher.Execute(Request("get_window_geometry", new JsonObject { ["window"] = 7L }), caller);
        check(read["success"]?.GetValue<bool>() == true && read["bounds"]?["width"]?.GetValue<int>() == 1280
            && read["bounds"]?["x"]?.GetValue<int>() == 4, "a window handle answers with its rectangle");
        check(ui.LastWindowRequest?.Handle == 7 && ui.LastWindowRequest.TitleQuery == null, "the window is selected by handle only");

        ui.Bounds = null;
        JsonObject unsupported = dispatcher.Execute(Request("get_window_geometry", new JsonObject { ["window"] = 7L }), caller);
        check(unsupported["success"]?.GetValue<bool>() == false && unsupported["code"]?.ToString() == "unsupported",
            "a backend without a proved scale answers unsupported");
    }

    private static void NoTreeHasItsCode(Action<bool, string> check, string temporaryRoot)
    {
        var ui = new FakeUi { NoTree = true };
        using var dispatcher = new ActionDispatcher(ui, DesktopPolicy(temporaryRoot));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());

        var arguments = new JsonObject { ["window"] = 7L, ["row_format"] = "common" };
        JsonObject tree = dispatcher.Execute(Request("get_ui_tree", arguments), caller);

        check(tree["success"]?.GetValue<bool>() == false && tree["code"]?.ToString() == "no_accessibility_tree",
            "a window without a tree gets the no_accessibility_tree code");
    }

    private static void ReasonIsJournaled(Action<bool, string> check, string temporaryRoot)
    {
        var ui = new FakeUi();
        ui.ExtraFeatures.Add(ToolFeature.PhysicalInput);
        NodePolicy policy = DesktopPolicy(temporaryRoot);
        using var dispatcher = new ActionDispatcher(ui, policy);
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());

        var arguments = new JsonObject
        {
            ["keys"] = new JsonArray("ctrl", "v"),
            ["last_resort_reason"] = Reason,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        dispatcher.Execute(Request("hotkey", arguments), caller);

        string journal = File.ReadAllText(Path.Combine(policy.StateRoot, "audit.jsonl"));
        check(ui.KeyCalls == 1, "the key chord reached the backend");
        check(journal.Contains("\"last_resort_reason\":\"" + Reason + "\"", StringComparison.Ordinal),
            "the admission entry journals the reason");
    }

    private static JsonObject Request(string action, JsonObject arguments)
    {
        return new JsonObject { ["action"] = action, ["args"] = arguments };
    }

    private static NodePolicy DesktopPolicy(string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "state");
        string workspace = Path.Combine(temporaryRoot, "workspace");
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
}
