using System.Text.Json.Nodes;
using KLaniBot.Fleet;

// End to end on this machine: the real node answers the master's vitals request right after it connects, controller-status
// carries what it measured, and list_nodes in a real gateway shows the same through the master without becoming a client.
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
        JsonObject listed = await observer.CallToolAsync("list_nodes", new JsonObject());
        JsonNode? entry = listed["structuredContent"]?["nodes"]?.AsArray().SingleOrDefault(item => item?["name"]?.ToString() == "managed");
        Check(entry?["status"]?.ToString() == "connected" && entry["vitals"]?["system"]?.ToString() == vitals.System
            && entry["seconds_since_contact"] != null,
            "list_nodes shows the node as connected with the vitals its master holds: " + listed.ToJsonString());

        JsonObject status = await MasterAdministration.CallAsync(masterState, "controller-status", CancellationToken.None);
        Check(ControllerStatus.TryRead(status, out ControllerStatus after) && after.Clients.All(client => client.Client != ObserverName),
            "Reading node status does not make the gateway a client of the node.");
        await observer.CloseAsync();
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
