using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Fleet;

// Whether the master forwards MCP clients' calls at all; the operator switches it in the controller tray.
public enum BotSwitch
{
    On,
    Off
}

// What one MCP client row shows: working right now, connected without a recent call, or paused by the operator.
public enum ControllerClientState
{
    Active,
    Idle,
    Paused
}

// One registered node as the master's controller-status reports it.
public sealed record ControllerNodeRow(string Name, bool Connected, string Version);

// One MCP client (one gateway process) as the master's controller-status reports it. Client and purpose are the client's
// own claim; Instance is what pause and resume name.
public sealed record ControllerClientRow(
    string Instance,
    string Client,
    string Purpose,
    IReadOnlyList<string> Nodes,
    ControllerClientState State,
    int SecondsSinceLastRequest);

// Use case: the master answers controller-status with this, and the controller tray reads it back to draw its menu. The
// words on the wire are fixed here, so both sides of the local administration pipe agree. A list cut to the pipe's frame
// budget says how many entries it left out.
public sealed record ControllerStatus(
    BotSwitch Bot,
    IReadOnlyList<ControllerNodeRow> Nodes,
    int MoreNodes,
    IReadOnlyList<ControllerClientRow> Clients,
    int MoreClients)
{
    public static string WireName(BotSwitch value)
    {
        return value switch
        {
            BotSwitch.On => "on",
            BotSwitch.Off => "off",
            _ => throw new UnreachableException()
        };
    }

    public static bool TryParseBot(string? word, out BotSwitch value)
    {
        value = word == "off" ? BotSwitch.Off : BotSwitch.On;
        return word is "on" or "off";
    }

    public static string WireName(ControllerClientState value)
    {
        return value switch
        {
            ControllerClientState.Active => "active",
            ControllerClientState.Idle => "idle",
            ControllerClientState.Paused => "paused",
            _ => throw new UnreachableException()
        };
    }

    private static bool TryParseState(string? word, out ControllerClientState value)
    {
        value = word switch
        {
            "active" => ControllerClientState.Active,
            "paused" => ControllerClientState.Paused,
            _ => ControllerClientState.Idle
        };
        return word is "active" or "idle" or "paused";
    }

    public JsonObject ToJson()
    {
        var nodes = new JsonArray();
        foreach (ControllerNodeRow node in Nodes)
        {
            nodes.Add(NodeJson(node));
        }
        var clients = new JsonArray();
        foreach (ControllerClientRow client in Clients)
        {
            clients.Add(ClientJson(client));
        }

        return new JsonObject
        {
            ["success"] = true,
            ["bot"] = WireName(Bot),
            ["nodes"] = nodes,
            ["more_nodes"] = MoreNodes,
            ["clients"] = clients,
            ["more_clients"] = MoreClients
        };
    }

    public static JsonObject NodeJson(ControllerNodeRow node)
    {
        return new JsonObject { ["name"] = node.Name, ["connected"] = node.Connected, ["version"] = node.Version };
    }

    public static JsonObject ClientJson(ControllerClientRow client)
    {
        var nodes = new JsonArray();
        foreach (string node in client.Nodes)
        {
            nodes.Add(node);
        }

        return new JsonObject
        {
            ["instance"] = client.Instance,
            ["client"] = client.Client,
            ["purpose"] = client.Purpose,
            ["nodes"] = nodes,
            ["state"] = WireName(client.State),
            ["seconds_since_last_request"] = client.SecondsSinceLastRequest
        };
    }

    // A reply of another shape (an older master, a failed command) is not a status.
    public static bool TryRead(JsonObject reply, out ControllerStatus status)
    {
        status = new ControllerStatus(BotSwitch.On, [], 0, [], 0);
        if (!IsTrue(reply["success"]) || !TryParseBot(reply["bot"]?.ToString(), out BotSwitch bot))
        {
            return false;
        }
        if (reply["nodes"] is not JsonArray nodes || reply["clients"] is not JsonArray clients)
        {
            return false;
        }
        bool rowsRead = TryReadNodes(nodes, out List<ControllerNodeRow> nodeRows);
        rowsRead &= TryReadClients(clients, out List<ControllerClientRow> clientRows);
        if (!rowsRead)
        {
            return false;
        }

        int moreNodes = CountOrZero(reply["more_nodes"]);
        int moreClients = CountOrZero(reply["more_clients"]);
        status = new ControllerStatus(bot, nodeRows, moreNodes, clientRows, moreClients);
        return true;
    }

    private static bool IsTrue(JsonNode? value)
    {
        return value is JsonValue flag && flag.TryGetValue(out bool isTrue) && isTrue;
    }

    private static int CountOrZero(JsonNode? value)
    {
        if (value is JsonValue number && number.TryGetValue(out int count) && count >= 0)
        {
            return count;
        }
        return 0;
    }

    private static bool TryReadNodes(JsonArray nodes, out List<ControllerNodeRow> rows)
    {
        rows = new List<ControllerNodeRow>();
        foreach (JsonNode? entry in nodes)
        {
            string? name = entry?["name"]?.ToString();
            if (name == null || entry!["connected"] is not JsonValue connected || !connected.TryGetValue(out bool isConnected))
            {
                return false;
            }
            rows.Add(new ControllerNodeRow(name, isConnected, entry["version"]?.ToString() ?? ""));
        }
        return true;
    }

    private static bool TryReadClients(JsonArray clients, out List<ControllerClientRow> rows)
    {
        rows = new List<ControllerClientRow>();
        foreach (JsonNode? entry in clients)
        {
            string? instance = entry?["instance"]?.ToString();
            if (instance == null || !TryParseState(entry!["state"]?.ToString(), out ControllerClientState state))
            {
                return false;
            }
            var nodes = (entry["nodes"] as JsonArray)?.Select(node => node?.ToString() ?? "").ToList() ?? [];
            int seconds = CountOrZero(entry["seconds_since_last_request"]);
            string client = entry["client"]?.ToString() ?? "";
            string purpose = entry["purpose"]?.ToString() ?? "";
            rows.Add(new ControllerClientRow(instance, client, purpose, nodes, state, seconds));
        }
        return true;
    }
}
