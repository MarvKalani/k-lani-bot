using System.Text.Json.Nodes;
using KLaniBot.Platforms.Windows;

// The update helper reads a plan it refused only as far as it can, to restart the installed node and keep the version from
// being offered again. A plan of a node from before the current version must still yield the node's state root.
internal static class UpdatePlanRecoveryCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Update plan recovery");
        string stateRoot = Path.Combine(temporaryRoot, "update-recovery-state");
        string updates = Path.Combine(stateRoot, "updates");
        Directory.CreateDirectory(updates);
        string planPath = Path.Combine(updates, "update-plan.json");

        var plan = new JsonObject
        {
            ["version"] = 1,
            ["parent_pid"] = 4242,
            ["config"] = Path.Combine(stateRoot, "node.json"),
            ["manifest"] = new JsonObject { ["Version"] = "0.2610.0501" }
        };
        File.WriteAllText(planPath, plan.ToJsonString());
        UpdatePlanRecovery recovery = UpdatePlanRecovery.Read(planPath);
        checks.That(recovery.ParentId == 4242 && recovery.ConfigPath == Path.Combine(stateRoot, "node.json")
            && recovery.Version == "0.2610.0501" && recovery.StateRoot == Path.GetFullPath(stateRoot),
            "a readable plan names the waiting node, its configuration and the refused version");
        JsonObject status = recovery.RolledBackStatus("InvalidDataException: Invalid update plan.");
        checks.That(status["status"]!.ToString() == "rolled_back" && status["version"]!.ToString() == "0.2610.0501",
            "the refused version is recorded as rolled back, so the node does not take it again");

        File.WriteAllText(planPath, "{ not json");
        UpdatePlanRecovery unreadable = UpdatePlanRecovery.Read(planPath);
        checks.That(unreadable.ParentId == null && unreadable.ConfigPath == null && unreadable.StateRoot == Path.GetFullPath(stateRoot),
            "an unreadable plan still yields the state root from its own location");
        return checks.Passed;
    }
}
