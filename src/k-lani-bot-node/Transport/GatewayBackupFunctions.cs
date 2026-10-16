using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport;

// Use case: an AI working through the cartridge or compact gateway gets backup_required and creates the backup through the
// same gateway, and uses a program's own MCP server (Programmable App MCPs) there too. These node tools are routed functions
// of cartridge_call, not extra top-level tools; the node still checks scopes, the policy and the request id of each change.
internal static class GatewayBackupFunctions
{
    private static readonly Dictionary<string, Tool> Functions = new(StringComparer.Ordinal)
    {
        ["backup_plan"] = Tool.BackupPlan,
        ["backup_verify"] = Tool.BackupVerify,
        ["backup_create"] = Tool.BackupCreate,
        ["backup_restore"] = Tool.BackupRestore,
        ["app_mcp_read"] = Tool.ApplicationMcpRead,
        ["app_mcp_call"] = Tool.ApplicationMcpCall
    };

    public static bool TryTool(string? function, out Tool tool)
    {
        tool = default;
        return function != null && Functions.TryGetValue(function, out tool);
    }

    // A change gets a fresh request id when the caller sent none, as the gateway does for every other mutation.
    public static JsonObject Arguments(Tool tool, JsonNode? arguments)
    {
        var copy = arguments is JsonObject given ? (JsonObject)given.DeepClone() : new JsonObject();
        if (tool is Tool.BackupCreate or Tool.BackupRestore or Tool.ApplicationMcpCall && copy["request_id"] == null)
        {
            copy["request_id"] = Guid.CreateVersion7().ToString();
        }
        return copy;
    }
}
