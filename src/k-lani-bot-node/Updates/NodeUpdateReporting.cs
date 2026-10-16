using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Guard;
using KLaniBot.Transport;
using KLaniBot.Startup;

namespace KLaniBot.Updates;

// Update helpers report their own bounded outcome with the existing node credential and pinned CA, including after
// rollback to an older worker. An older master may ignore this additive endpoint; local recovery never waits for it.
internal static class NodeUpdateReporting
{
    public static async Task SendAsync(NodePolicy policy)
    {
        try
        {
            string path = Path.Combine(policy.StateRoot, "paired-master.json");
            CartridgeManifest.RejectLinks(path);
            JsonObject paired = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            using HttpClient client = NodeUpdateDownload.PinnedClient(paired, policy.NodeId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            JsonObject status = NodeManagementStatus.Read(policy, false);
            status["worker_available"] = null;
            status["report_origin"] = "update_helper";
            using HttpResponseMessage response = await client.PostAsJsonAsync(ProtocolIdentity.UpdateStatusRoute, status, deadline.Token);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or HttpRequestException
            or OperationCanceledException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("Update outcome retained locally; upload unavailable. " + Diagnostics.Describe(failure));
        }
    }
}
