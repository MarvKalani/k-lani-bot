using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;

// A tool in the contract without a handler would answer "Unknown action" at run time, and a handler
// without a contract entry could never be called. The two lists must match.
internal static class ActionHandlerTableCases
{
    // Answered by the gateway in front of the nodes, not by a node's dispatcher.
    private static readonly string[] GatewayTools = ["list_nodes", "evaluate_trace"];

    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Action handler table: " + name);
            }
            passed++;
        }
        string state = Path.Combine(temporaryRoot, "handler-table-state");
        string workspace = Path.Combine(temporaryRoot, "handler-table-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);
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
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy);

        string[] contractTools = ToolContract.Tools.OfType<JsonObject>().Select(tool => tool["name"]!.ToString()).ToArray();
        string[] handled = dispatcher.HandledActions.Select(ToolNames.Of).ToArray();
        string[] expected = contractTools.Where(name => !GatewayTools.Contains(name)).ToArray();

        Check(handled.Length == handled.Distinct().Count(), "no tool has two handlers");
        Check(
            expected.All(handled.Contains),
            "every tool of the contract has a handler: missing " + string.Join(", ", expected.Except(handled)));
        Check(
            handled.All(contractTools.Contains),
            "every handler belongs to a tool of the contract: extra " + string.Join(", ", handled.Except(contractTools)));
        Check(
            GatewayTools.All(contractTools.Contains) && GatewayTools.All(name => !handled.Contains(name)),
            "the gateway tools are in the contract and not handled by a node");
        return passed;
    }
}
