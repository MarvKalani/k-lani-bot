using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Platforms.Windows;

// Use case: the update helper refused a plan before it touched the installed node. The node that staged the plan has already
// exited, so the helper reads what it can from the plan without trusting it, waits for that node, records the version as rolled
// back so it is not offered again, and starts the unchanged installed node again.
public sealed record UpdatePlanRecovery(int? ParentId, string? ConfigPath, string? Version, string StateRoot)
{
    // The plan lies in <state root>/updates/update-plan.json; the state root follows from that, whatever the plan says.
    public static UpdatePlanRecovery Read(string planPath)
    {
        string fullPath = Path.GetFullPath(planPath);
        string stateRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullPath)!)!;
        JsonObject? plan = ParseOrNull(fullPath);

        int? parentId = plan?["parent_pid"] is JsonValue parent && parent.TryGetValue(out int parentValue) ? parentValue : null;
        string? config = TextOrNull(plan?["config"]);
        string? version = TextOrNull(plan?["manifest"]?["Version"]);
        return new UpdatePlanRecovery(parentId, config, version, stateRoot);
    }

    private static string? TextOrNull(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
    }

    // The record that keeps the refused version from being installed again, with the reason for the person who looks.
    public JsonObject RolledBackStatus(string reason)
    {
        return new JsonObject { ["version"] = Version, ["status"] = "rolled_back", ["reason"] = reason };
    }

    private static JsonObject? ParseOrNull(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
