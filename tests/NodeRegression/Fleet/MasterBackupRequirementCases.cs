using System.Text.Json.Nodes;
using KLaniBot.Fleet;

// The master keeps the administrator's backup requirement per node: it names the nodes an order reaches, refuses unknown
// nodes and words, survives a restart, and counts only the confirmed order as applied.
internal static class MasterBackupRequirementCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Master backup requirement");
        string state = Directory.CreateDirectory(Path.Combine(temporaryRoot, "master-backup-" + Guid.CreateVersion7())).FullName;
        string[] paired = ["alpha", "beta", "gamma"];
        var orders = new MasterBackupRequirements(state);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        checks.That(orders.Request("all", "off", paired, now).Count == 3, "all reaches every paired node");
        checks.That(orders.Request("alpha,beta", "required", paired, now.AddSeconds(1)).SequenceEqual(["alpha", "beta"]),
            "a list reaches exactly the named nodes");
        checks.That(Refused(() => orders.Request("delta", "off", paired, now)), "an unknown node is refused");
        checks.That(Refused(() => orders.Request("alpha", "sometimes", paired, now)), "an unknown word is refused");

        JsonObject message = orders.PendingMessage("alpha")!;
        checks.That(message["kind"]?.ToString() == "backup_requirement" && message["requirement"]?.ToString() == "required"
            && message.Count == 3, "the pending message carries the latest order and no id yet");
        orders.MarkApplied("alpha", "an older order");
        checks.That(orders.PendingMessage("alpha") != null, "an older confirmation does not apply a newer order");
        orders.MarkApplied("alpha", message["requested_at"]!.ToString());
        checks.That(orders.PendingMessage("alpha") == null, "the confirmed order is applied");

        var restarted = new MasterBackupRequirements(state);
        checks.That(restarted.PendingMessage("alpha") == null && restarted.PendingMessage("gamma")?["requirement"]?.ToString() == "off",
            "orders and their state survive a master restart");
        restarted.Forget("gamma");
        checks.That(restarted.Status()["nodes"]?["gamma"] == null, "a revoked node's order is forgotten");

        return checks.Passed;
    }

    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
