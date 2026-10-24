using System.Text.Json.Nodes;
using KLaniBot.Transport;

namespace KLaniBot.Fleet;

// Use case: the master asks every connected node that understands it for its vital signs, right after it connects and then
// every 30 seconds, and keeps the last answer for the controller tray and list_nodes. A poll is sent only while the node's
// relay is idle, so it never delays an action. Last contact is the node's last answer to any call. It lives in memory: after
// a master restart a node that has not reconnected reports no contact since the master started.
internal sealed class MasterNodeVitals
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    // The node answers from memory without waiting; a dispatched call that times out ends the relay, so the limit is generous.
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeVitals> _vitals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _contacts = new(StringComparer.Ordinal);

    public async Task PollAsync(string node, ReverseRelay relay, CancellationToken cancellation)
    {
        JsonObject? reply = await relay.CallIfIdleAsync(new JsonObject { ["kind"] = "vitals" }, cancellation, AnswerTimeout);
        bool answered = reply?["success"] is JsonValue success && success.TryGetValue(out bool succeeded) && succeeded;
        if (answered && NodeVitals.TryRead(reply!["vitals"]) is { } vitals)
        {
            lock (_gate)
            {
                _vitals[node] = vitals;
            }
        }
    }

    // Offline nodes keep their last contact time but no vitals, which would be stale.
    public void Disconnected(string node, DateTimeOffset lastContact)
    {
        lock (_gate)
        {
            _contacts[node] = lastContact;
            _vitals.Remove(node);
        }
    }

    public ControllerNodeRow Row(string node, string version, ReverseRelay? relay, DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset? contact = relay?.LastContact ?? (_contacts.TryGetValue(node, out DateTimeOffset last) ? last : null);
            long? seconds = contact is { } heard ? Math.Max(0, (long)(now - heard).TotalSeconds) : null;
            NodeVitals? vitals = relay != null ? _vitals.GetValueOrDefault(node) : null;
            return new ControllerNodeRow(node, relay != null, version, vitals, seconds);
        }
    }
}
