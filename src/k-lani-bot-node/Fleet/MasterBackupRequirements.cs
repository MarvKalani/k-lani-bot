using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Fleet;

// Use case: the administrator switches the backup requirement for one node, a list or all paired nodes from the master
// console instead of visiting each machine. The master keeps the wanted state per node in its private state, so a node
// that is offline receives it when it connects, and reports which nodes applied it and which are still pending.
internal sealed class MasterBackupRequirements
{
    private const string StateFile = "backup-requirements.json";
    private readonly string _path;
    private readonly JsonObject _orders;

    public MasterBackupRequirements(string stateDirectory)
    {
        _path = Path.Combine(stateDirectory, StateFile);
        _orders = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path))!.AsObject() : new JsonObject();
    }

    // Returns the nodes the order applies to. Throws ArgumentException for an unknown node or a word other than
    // required or off; nothing is stored then.
    public IReadOnlyList<string> Request(string target, string word, IReadOnlyCollection<string> pairedNodes, DateTimeOffset now)
    {
        if (word is not ("required" or "off"))
        {
            throw new ArgumentException("The backup requirement must be required or off.");
        }
        string[] nodes = target == "all" ? pairedNodes.ToArray() : target.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (nodes.Length == 0 || nodes.Any(node => !pairedNodes.Contains(node)))
        {
            throw new ArgumentException("Name paired nodes, a comma-separated list of them, or all.");
        }

        foreach (string node in nodes)
        {
            _orders[node] = new JsonObject { ["requirement"] = word, ["requested_at"] = now.ToString("O"), ["applied"] = false };
        }
        Save();
        return nodes;
    }

    // The message the master sends to a node whose order is not yet applied; null when nothing is pending.
    public JsonObject? PendingMessage(string node)
    {
        if (_orders[node] is not JsonObject order || order["applied"]?.GetValue<bool>() == true)
        {
            return null;
        }
        return new JsonObject
        {
            ["kind"] = "backup_requirement",
            ["requirement"] = order["requirement"]!.ToString(),
            ["requested_at"] = order["requested_at"]!.ToString()
        };
    }

    // Only the order that was sent counts as applied; a newer order for the same node stays pending.
    public void MarkApplied(string node, string requestedAt)
    {
        if (_orders[node] is JsonObject order && order["requested_at"]?.ToString() == requestedAt)
        {
            order["applied"] = true;
            Save();
        }
    }

    public void Forget(string node)
    {
        if (_orders.Remove(node))
        {
            Save();
        }
    }

    public JsonObject Status()
    {
        return new JsonObject { ["success"] = true, ["nodes"] = _orders.DeepClone() };
    }

    private void Save()
    {
        PairingProtocol.WritePrivate(_path, _orders.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), true);
    }
}
