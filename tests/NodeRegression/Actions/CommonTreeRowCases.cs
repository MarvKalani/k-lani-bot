using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// The compact gateway asks for common rows with row_format. Without the argument a backend keeps its own text, and a
// value outside the declared schema enum is refused before any backend is called.
internal static class CommonTreeRowCases
{
    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Common tree rows: " + name);
            }
            passed++;
        }
        RoutesCommonRowsOnlyOnRequest(Check, temporaryRoot);
        RefusesValueOutsideSchema(Check, temporaryRoot);
        return passed;
    }

    private static void RoutesCommonRowsOnlyOnRequest(Action<bool, string> check, string temporaryRoot)
    {
        using var dispatcher = new ActionDispatcher(new FakeUi(), DesktopPolicy(temporaryRoot, "rows-route"));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject plain = dispatcher.Execute(TreeRequest(new JsonObject()), caller);
        JsonObject common = dispatcher.Execute(TreeRequest(new JsonObject { ["row_format"] = "common" }), caller);

        check(plain["success"]?.GetValue<bool>() == true, "a plain observation succeeds");
        check(plain["tree"]?.ToString().Contains("Button button") == true, "a plain observation keeps the backend text");
        check(common["success"]?.GetValue<bool>() == true, "a common observation succeeds");
        check(common["tree"]?.ToString().Contains("Root \"Desktop\"") == true, "a common observation asks for common rows");
    }

    private static void RefusesValueOutsideSchema(Action<bool, string> check, string temporaryRoot)
    {
        using var dispatcher = new ActionDispatcher(new FakeUi(), DesktopPolicy(temporaryRoot, "rows-schema"));
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        JsonObject refused = dispatcher.Execute(TreeRequest(new JsonObject { ["row_format"] = "condensed" }), caller);

        check(refused["success"]?.GetValue<bool>() == false, "an undeclared row_format is refused");
        check(refused["tree"] == null, "a refused row_format returns no tree");
    }

    private static JsonObject TreeRequest(JsonObject arguments)
    {
        return new JsonObject
        {
            ["action"] = "get_ui_tree",
            ["args"] = arguments
        };
    }

    private static NodePolicy DesktopPolicy(string temporaryRoot, string name)
    {
        string state = Path.Combine(temporaryRoot, name + "-state");
        string workspace = Path.Combine(temporaryRoot, name + "-workspace");
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
