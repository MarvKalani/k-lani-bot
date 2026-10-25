using System.Text.Json.Nodes;
using KLaniBot.Fleet;

// End to end on this machine: the real node answers the master's vitals request right after it connects, controller-status
// carries what it measured, list_nodes in a real gateway shows only crossed limits through the master without becoming a
// client, and the master keeps the node's history.
internal static partial class Program
{
    private const string ObserverName = "observer-e2e";

    private static async Task TestNodeVitalsAsync(string binary, string masterState)
    {
        ControllerNodeRow node = await ConnectedNodeWithVitalsAsync(masterState);
        NodeVitals vitals = node.Vitals!;
        Check(vitals.System.Length > 0 && vitals.MemoryTotalBytes > 0 && vitals.MemoryAvailableBytes <= vitals.MemoryTotalBytes
            && vitals.DiskVolume == "/" && vitals.DiskFreeBytes > 0 && vitals.UptimeSeconds > 0,
            "The node reports its system, memory, root volume and uptime measured on this machine: " + vitals.ToJson().ToJsonString());
        Check(node.SecondsSinceContact is >= 0 and < 60, "A connected node was heard from within the last poll interval.");

        string registry = Path.Combine(masterState, "nodes.json");
        using GatewayProcess observer = await GatewayProcess.StartAsync(binary, registry, ObserverName, null);
        JsonObject? entry = await ListedNodeAsync(observer);
        Check(entry?["status"]?.ToString() == "connected" && entry.Count == 3,
            "list_nodes shows a healthy node only by name, URL and state, so a model spends no tokens on its vitals: "
            + entry?.ToJsonString());
        string settings = Path.Combine(masterState, NodeVitalsSettings.FileName);
        File.WriteAllText(settings, "{\"disk_free_percent\": 100}");
        JsonObject? strained = await ListedNodeAsync(observer);
        Check(strained?["alerts"]?.AsArray().Single()?.ToString().StartsWith("disk_low: / ", StringComparison.Ordinal) == true,
            "a crossed limit from vitals.json reaches list_nodes at once as one short alert: " + strained?.ToJsonString());
        File.Delete(settings);

        JsonObject history = await MasterAdministration.CallAsync(masterState, "vitals-history managed 1", CancellationToken.None);
        Check(history["samples"]?.GetValue<int>() >= 1 && history["disk_free_bytes"]?["min"] != null
            && File.Exists(Path.Combine(masterState, "vitals-history", "managed.jsonl")),
            "the master wrote the node's vitals to its history and names the period's peaks: " + history.ToJsonString());
        JsonObject report = await MasterAdministration.CallAsync(masterState, "vitals-report 1", CancellationToken.None);
        string reportPath = report["path"]?.ToString() ?? "";
        string page = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "";
        Check(reportPath == Path.Combine(masterState, "vitals-report.html") && page.Contains("<h2>managed</h2>")
            && page.Contains("<svg ") && !page.Contains("<script", StringComparison.OrdinalIgnoreCase),
            "the master writes the report the tray opens, with charts of the node and no script: " + report.ToJsonString());

        JsonObject status = await MasterAdministration.CallAsync(masterState, "controller-status", CancellationToken.None);
        Check(ControllerStatus.TryRead(status, out ControllerStatus after) && after.Clients.All(client => client.Client != ObserverName),
            "Reading node status does not make the gateway a client of the node.");
        await observer.CloseAsync();
    }

    private static async Task<JsonObject?> ListedNodeAsync(GatewayProcess gateway)
    {
        JsonObject listed = await gateway.CallToolAsync("list_nodes", new JsonObject());
        return listed["structuredContent"]?["nodes"]?.AsArray().SingleOrDefault(item => item?["name"]?.ToString() == "managed")
            as JsonObject;
    }

    // The master asks right after the node connects, long before this step; only the answer may still be on its way.
    private static async Task<ControllerNodeRow> ConnectedNodeWithVitalsAsync(string masterState)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            JsonObject status = await MasterAdministration.CallAsync(masterState, "controller-status", CancellationToken.None);
            ControllerNodeRow? node = ControllerStatus.TryRead(status, out ControllerStatus read)
                ? read.Nodes.SingleOrDefault(row => row.Name == "managed")
                : null;
            if (node is { Connected: true, Vitals: not null })
            {
                return node;
            }
            Check(DateTimeOffset.UtcNow < deadline, "The master holds vitals of the connected node: " + status.ToJsonString());
            await Task.Delay(250);
        }
    }
}
