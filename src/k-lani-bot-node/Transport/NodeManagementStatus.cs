using System.Text.Json.Nodes;
using KLaniBot.Cartridges;
using KLaniBot.Guard;
using KLaniBot.Startup;

namespace KLaniBot.Transport;

// Management reads only the node's fixed update/bootstrap evidence. Existing observe authorization and transport
// keys apply; no caller-selected path, desktop worker or new wire command is required.
internal static class NodeManagementStatus
{
    public static JsonObject Read(NodePolicy policy, bool workerAvailable)
    {
        return new JsonObject
        {
            ["supervisor_version"] = ProductIdentity.DisplayVersion,
            ["worker_available"] = workerAvailable,
            ["last_update"] = ReadFile(Path.Combine(policy.StateRoot, "last-update.json")),
            ["startup_failure"] = ReadFile(Path.Combine(policy.StateRoot, "actuator", "worker-startup-failure.json")),
            ["supervisor_startup_failure"] = ReadFile(Path.Combine(policy.StateRoot, "worker-startup-failure.json"))
        };
    }

    public static JsonObject Capabilities(NodePolicy policy)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["outcome"] = "completed",
            ["management"] = Read(policy, false),
            ["capabilities"] = new JsonObject
            {
                ["nodeId"] = policy.NodeId,
                ["version"] = ProductIdentity.DisplayVersion,
                ["platform"] = "management-only",
                ["features"] = new JsonArray(),
                ["activeTools"] = new JsonArray("get_machine_capabilities", "get_action_status"),
                ["worker_available"] = false
            }
        };
    }

    private static JsonObject? ReadFile(string path)
    {
        const int MaximumStatusBytes = 16 * 1024;
        try
        {
            CartridgeManifest.RejectLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumStatusBytes)
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new JsonObject { ["unavailable"] = true, ["diagnostic"] = Diagnostics.Describe(failure) };
        }
    }
}
