using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport;

// The cartridge and compact gateways route the four backup functions to the node tools of the same name; a change gets a
// request id when the caller sent none, and an id the caller sent is kept so a retry is recognized.
internal static class GatewayBackupFunctionCases
{
    public static int Run()
    {
        var checks = new Checks("Gateway backup functions");
        checks.That(GatewayBackupFunctions.TryTool("backup_create", out Tool create) && create == Tool.BackupCreate,
            "backup_create routes to the node tool");
        checks.That(GatewayBackupFunctions.TryTool("backup_plan", out Tool plan) && plan == Tool.BackupPlan, "backup_plan routes");
        checks.That(!GatewayBackupFunctions.TryTool("cartridge_node_status", out _) && !GatewayBackupFunctions.TryTool(null, out _),
            "other functions are not backup functions");

        JsonObject planned = GatewayBackupFunctions.Arguments(Tool.BackupPlan, null);
        checks.That(planned.Count == 0, "planning reads and gets no request id");
        JsonObject generated = GatewayBackupFunctions.Arguments(Tool.BackupRestore, new JsonObject { ["name"] = "copy" });
        checks.That(Guid.TryParse(generated["request_id"]?.ToString(), out Guid id) && id.Version == 7, "a change gets a UUIDv7");
        string given = Guid.CreateVersion7().ToString();
        var sent = new JsonObject { ["request_id"] = given };
        checks.That(GatewayBackupFunctions.Arguments(Tool.BackupCreate, sent)["request_id"]?.ToString() == given,
            "a request id the caller sent is kept");
        checks.That(sent.Count == 1, "the caller's arguments are not changed");

        return checks.Passed;
    }
}
