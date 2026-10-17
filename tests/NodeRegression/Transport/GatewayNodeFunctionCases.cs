using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport;

// The cartridge and compact gateways route the four backup functions and the two app MCP functions to the node tools of the
// same name; a change gets a request id when the caller sent none, and an id the caller sent is kept so a retry is recognized.
internal static class GatewayNodeFunctionCases
{
    public static int Run()
    {
        var checks = new Checks("Gateway node functions");
        checks.That(GatewayNodeFunctions.TryTool("backup_create", out Tool create) && create == Tool.BackupCreate,
            "backup_create routes to the node tool");
        checks.That(GatewayNodeFunctions.TryTool("backup_plan", out Tool plan) && plan == Tool.BackupPlan, "backup_plan routes");
        checks.That(!GatewayNodeFunctions.TryTool("cartridge_node_status", out _) && !GatewayNodeFunctions.TryTool(null, out _),
            "other functions are not backup functions");

        JsonObject planned = GatewayNodeFunctions.Arguments(Tool.BackupPlan, null);
        checks.That(planned.Count == 0, "planning reads and gets no request id");
        JsonObject generated = GatewayNodeFunctions.Arguments(Tool.BackupRestore, new JsonObject { ["name"] = "copy" });
        checks.That(Guid.TryParse(generated["request_id"]?.ToString(), out Guid id) && id.Version == 7, "a change gets a UUIDv7");
        string given = Guid.CreateVersion7().ToString();
        var sent = new JsonObject { ["request_id"] = given };
        checks.That(GatewayNodeFunctions.Arguments(Tool.BackupCreate, sent)["request_id"]?.ToString() == given,
            "a request id the caller sent is kept");
        checks.That(sent.Count == 1, "the caller's arguments are not changed");

        checks.That(GatewayNodeFunctions.TryTool("app_mcp_call", out Tool appCall) && appCall == Tool.ApplicationMcpCall
            && GatewayNodeFunctions.TryTool("app_mcp_read", out Tool appRead) && appRead == Tool.ApplicationMcpRead,
            "the app MCP functions route to the node tools");
        checks.That(GatewayNodeFunctions.Arguments(Tool.ApplicationMcpCall, new JsonObject())["request_id"] != null
            && GatewayNodeFunctions.Arguments(Tool.ApplicationMcpRead, new JsonObject())["request_id"] == null,
            "an app MCP change gets a request id, a read does not");

        return checks.Passed;
    }
}
