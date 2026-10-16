using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Inventory;

// collect_inventory answers in pages; the first page takes a snapshot and later pages read from it, so pages never mix two states of the
// machine.
public sealed class InventoryPages
{
    private readonly Dictionary<string, SystemInventory> _snapshots = new();

    // Throws ArgumentException when a later page lacks the inventory id or the inventory expired.
    public JsonObject Read(JsonObject arguments)
    {
        int offset = arguments["offset"]?.GetValue<int>() ?? 0;
        int limit = arguments["limit"]?.GetValue<int>() ?? 50;
        string? snapshotId = arguments["inventory_id"]?.ToString();
        SystemInventory inventory;
        if (snapshotId == null)
        {
            if (offset != 0)
            {
                throw new ArgumentException("Additional pages require the original inventory_id.");
            }
            inventory = SystemInventoryCollector.Collect();
            while (_snapshots.Count >= 4)
            {
                _snapshots.Remove(_snapshots.Keys.First());
            }
            _snapshots.Add(inventory.SnapshotId, inventory);
        }
        else if (!_snapshots.TryGetValue(snapshotId, out inventory!) || DateTimeOffset.UtcNow > inventory.CapturedAt.AddMinutes(15))
        {
            throw new ArgumentException("Inventory expired; collect a fresh snapshot.");
        }
        return new JsonObject
        {
            ["success"] = true,
            ["inventory_id"] = inventory.SnapshotId,
            ["captured_at"] = inventory.CapturedAt.ToString("O"),
            ["fingerprint"] = inventory.Fingerprint,
            ["machine"] = JsonSerializer.SerializeToNode(inventory.Machine, SystemInventory.JsonOptions),
            ["sources"] = JsonSerializer.SerializeToNode(inventory.Sources, SystemInventory.JsonOptions),
            ["total"] = inventory.Applications.Length,
            ["truncated"] = inventory.Truncated,
            ["applications"] = JsonSerializer.SerializeToNode(
                inventory.Applications.Skip(offset).Take(limit).ToArray(),
                SystemInventory.JsonOptions),
            ["next_offset"] = offset + limit < inventory.Applications.Length ? offset + limit : null,
            ["data_trust"] = "untrusted"
        };
    }
}
